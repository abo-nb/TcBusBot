using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcBusBot.Core.Chat;

/// <summary>
/// 在送往 <c>/chat/completions</c> 的 JSON 裡補上「不要思考」的欄位。
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
/// 關掉思考只是省錢，不該讓對話壞掉。
/// </summary>
public sealed class ReasoningOffHandler : DelegatingHandler
{
    private readonly IReadOnlyDictionary<string, object?> _fields;

    public ReasoningOffHandler(IReadOnlyDictionary<string, object?> fields)
        => _fields = fields;

    public ReasoningOffHandler(LlmOptions options)
        : this(LlmOptions.ReasoningFields(options.Reasoning)) { }

    public static ReasoningOffHandler? CreateIfNeeded(LlmOptions options)
    {
        var fields = LlmOptions.ReasoningFields(options.Reasoning);
        return fields.Count == 0 ? null : new ReasoningOffHandler(fields);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_fields.Count == 0 || !ShouldPatch(request))
            return await base.SendAsync(request, cancellationToken);

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

        return await base.SendAsync(request, cancellationToken);
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
