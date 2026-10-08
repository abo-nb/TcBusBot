using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcBusBot.Core.Chat;

/// <summary>
/// 在 <c>/chat/completions</c> 這一層做兩件事：**補上思考欄位**（送出的 JSON）
/// 與**留下思考原文**（回應的 JSON）。
///
/// ── 為什麼要動到 HTTP 這一層（三條路都試過）──────────────────
///
/// 1. **SK 的 <c>ExtensionData</c> 在這個版本不會被送出去**
///    （決定性實驗：設 <c>ExtensionData["n"] = 2</c>，回應仍然只有 1 則）。
/// 2. **SK 的型別化屬性 <c>ReasoningEffort</c> 不接受 "none"**：
///    `NotSupportedException: The provided reasoning effort 'none' is not supported`。
/// 3. **端點實測**：<c>reasoning_effort="minimal"</c> 仍然會思考（24 reasoning tokens）。
///
/// 所以改成只改「送出去的 JSON」：SK 的 kernel、plugin、自動工具呼叫全部照舊，
/// 換服務時也只要改設定（或關掉這個 handler）。
///
/// ── 欄位是實測出來的（deepseek-flash，baseline 31 reasoning tokens）──
///
/// | 送出的欄位 | 結果 |
/// | --- | --- |
/// | `reasoning_effort: "none"` | ✅ reasoning tokens 消失（OpenAI 系的寫法） |
/// | `thinking: {"type":"disabled"}` | ✅ reasoning tokens 消失（Anthropic 系的寫法） |
/// | `enable_thinking: false` | ❌ 無效（仍 29） |
/// | `reasoning: {"enabled":false}` | ❌ 無效（仍 30） |
/// | `chat_template_kwargs` | ❌ 無效（仍 25） |
/// | `thinking_budget: 0` | ❌ 無效（仍 18） |
/// | `reasoning_effort: "minimal"` | ❌ 仍會思考（24） |
///
/// ⚠️ 兩個欄位都送是因為它們分別對應 OpenAI 系與 Anthropic 系的實作；
/// 換到「不認識這些欄位就回 400」的服務時，把 `LLM_REASONING` 設成 `auto` 即可。
///
/// ⚠️ 任何意外（不是 JSON、不是 chat 請求、解析失敗）都**原封不動送出去** ——
/// 關掉思考只是省錢、看思考只是除錯，都不該讓對話壞掉。
///
/// ── 為什麼連「回應」也要讀（`/ai think` 的來源）──────────────
/// SK 這個版本不保證會把非標準欄位（`reasoning_content`）映射到它的型別上，
/// 而那是「他到底在想什麼」的唯一來源。既然我們已經站在 HTTP 這一層，
/// 就在這裡順手把回應讀一遍，把思考原文交給 <see cref="ReasoningCapture"/>。
/// 讀完**一定**把內容放回去（不然 SK 會拿到空的 body）。
/// </summary>
public sealed class ReasoningHandler : DelegatingHandler
{
    private readonly IReadOnlyDictionary<string, object?> _fields;

    public ReasoningHandler(IReadOnlyDictionary<string, object?> fields) => _fields = fields;

    public ReasoningHandler(LlmOptions options) : this(LlmOptions.ReasoningFields(options.Reasoning)) { }

    /// <summary>
    /// 每一條連線都要掛（即使不需要補欄位）—— 因為「留不留思考原文」跟
    /// 「要不要關思考」是兩件事：前者是觀察，後者才是省錢。
    /// </summary>
    public static ReasoningHandler Create(LlmOptions options) => new(options);

    /// <summary>相容舊介面：沒有任何欄位要補時回 null（測試與舊呼叫端用）。</summary>
    public static ReasoningHandler? CreateIfNeeded(LlmOptions options)
    {
        var fields = LlmOptions.ReasoningFields(options.Reasoning);
        return fields.Count == 0 ? null : new ReasoningHandler(fields);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isChat = ShouldPatch(request);

        if (_fields.Count > 0 && isChat)
            await PatchRequestAsync(request, cancellationToken);

        var response = await base.SendAsync(request, cancellationToken);

        if (isChat) await CaptureReasoningAsync(response, cancellationToken);

        return response;
    }

    private async Task PatchRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var original = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var patched = Patch(original, _fields);

            if (patched is not null)
            {
                request.Content = new StringContent(patched, Encoding.UTF8, "application/json");
                request.Content.Headers.ContentType!.CharSet = "utf-8";
            }
        }
        catch (Exception)
        {
            // 改不動就照原樣送（例：body 不是 JSON）
        }
    }

    /// <summary>
    /// 把回應裡的思考原文與 reasoning token 數交給 <see cref="ReasoningCapture"/>。
    ///
    /// ⚠️ 一定要把 body 放回去：讀過之後 stream 就沒了，SK 會拿到空字串
    ///    （症狀是「模型回空話」而且完全沒有錯誤訊息）。
    /// </summary>
    private static async Task CaptureReasoningAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode || response.Content is null) return;

        string body;

        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception)
        {
            return;   // 讀不到就當作沒有（不要讓對話因為「想看思考」而壞掉）
        }

        try
        {
            Restore(response, body, cancellationToken);

            var (text, tokens) = ExtractReasoning(body);

            if (text.Length > 0 || tokens > 0) ReasoningCapture.Add(text, tokens);
        }
        catch (Exception)
        {
            // 解析失敗：body 已經放回去了，照原樣交給 SK
        }
    }

    private static void Restore(HttpResponseMessage response, string body, CancellationToken cancellationToken)
    {
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = "utf-8";

        response.Content = content;

        // 內容長度已經變了（而且是我們自己算的），不要留著舊的標頭
        response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(body);

        _ = cancellationToken;   // 目前不需要，但保留簽章讓未來改成串流時好改
    }

    /// <summary>
    /// 從回應 JSON 取出思考內容。
    ///
    /// 欄位名稱刻意寫得寬容：不同的服務商叫法不一樣
    /// （`reasoning_content` 是 DeepSeek／OpenAI 系的寫法、`reasoning` 是 OpenRouter 系、
    /// `thinking` 是 Anthropic 系），而**寫錯的代價是「功能看起來沒作用」**。
    /// </summary>
    public static (string Text, int Tokens) ExtractReasoning(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return ("", 0);

        try
        {
            if (JsonNode.Parse(body!) is not JsonObject root) return ("", 0);

            var text = "";
            var tokens = 0;

            if (root["choices"] is JsonArray choices && choices.Count > 0 && choices[0] is JsonObject choice)
            {
                // 非串流：message；串流：delta（我們目前不用串流，但先認得）
                var node = choice["message"] as JsonObject ?? choice["delta"] as JsonObject;

                if (node is not null)
                {
                    foreach (var name in new[] { "reasoning_content", "reasoning", "thinking", "reasoning_text" })
                    {
                        if (node[name] is JsonValue value && value.TryGetValue<string>(out var found)
                            && !string.IsNullOrWhiteSpace(found))
                        {
                            text = found;
                            break;
                        }
                    }
                }
            }

            if (root["usage"] is JsonObject usage)
            {
                if (usage["completion_tokens_details"] is JsonObject details
                    && details["reasoning_tokens"] is JsonValue rt && rt.TryGetValue<int>(out var rtValue))
                    tokens = rtValue;
                else if (usage["reasoning_tokens"] is JsonValue plain && plain.TryGetValue<int>(out var plainValue))
                    tokens = plainValue;
                // 有些服務商把推理算在輸出裡但不細分 → 保持 0（顯示時就不會多那一段）
            }

            return (text.Trim(), Math.Max(0, tokens));
        }
        catch (Exception)
        {
            return ("", 0);
        }
    }

    private static bool ShouldPatch(HttpRequestMessage request)
        => request.Method == HttpMethod.Post
           && request.Content is not null
           && (request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// 把欄位併進 JSON body（**已經有的一律不覆蓋** —— 呼叫端要自己指定時以呼叫端為準）。
    /// 回傳 null 代表不需要改（不是物件、或該有的都已經有了）。
    /// </summary>
    public static string? Patch(string body, IReadOnlyDictionary<string, object?> fields)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return null; }

        if (root is not JsonObject obj) return null;

        var changed = false;

        foreach (var (name, value) in fields)
        {
            if (obj.ContainsKey(name)) continue;

            obj[name] = value is null ? null : JsonSerializer.SerializeToNode(value);
            changed = true;
        }

        return changed ? obj.ToJsonString() : null;
    }
}
