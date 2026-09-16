using System.Diagnostics;
using System.Reflection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace TcBusBot.Core.Chat;

/// <summary>
/// 用 **Semantic Kernel** 接 OpenAI 相容 API。
///
/// 為什麼是 SK ＋ OpenAI 連接器：連接器支援自訂 `endpoint`，
/// 所以同一份程式碼可以接 DeepSeek、OpenAI、OpenRouter、自架的 Ollama／vLLM ——
/// 只要有 `/v1/chat/completions` 就行，換服務只要改 `LLM_BASE_URL` 與 `LLM_MODEL`。
///
/// 這裡刻意只用到 SK 的最上層（<see cref="Kernel"/> ＋
/// <see cref="IChatCompletionService"/>）：沒有 plugin、沒有 function calling，
/// 因為那些在其他 OpenAI 相容服務上支援程度不一，會讓「換一個服務就壞掉」。
///
/// 用量回報：SK 會把 API 的 `usage` 放進 <c>ChatMessageContent.Metadata["Usage"]</c>，
/// 但型別隨連接器版本而異（實測 1.66 是 <c>OpenAI.Chat.ChatTokenUsage</c>，
/// 舊版可能是 <c>PromptTokens</c>／<c>CompletionTokens</c>），
/// 所以用**屬性名稱**反射讀取，讀不到才退回估算。實測值：
/// InputTokenCount=47 / OutputTokenCount=137 / TotalTokenCount=184。
/// </summary>
public sealed class SemanticKernelLlmClient : ILlmClient, IDisposable
{
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chat;
    private readonly LlmOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    private SemanticKernelLlmClient(Kernel kernel, IChatCompletionService chat, LlmOptions options,
                                    HttpClient http, bool ownsHttp)
    {
        _kernel = kernel;
        _chat = chat;
        _options = options;
        _http = http;
        _ownsHttp = ownsHttp;
    }

    public bool IsConfigured => true;

    public string Describe() => _options.Describe();

    /// <summary>
    /// 建立客戶端。**不丟例外**：設定有問題時回傳 (null, 原因)，
    /// 讓 Bot 可以「照常啟動、只是沒有 AI 功能」—— 額外的功能壞掉不該拖垮主要功能。
    /// </summary>
    public static (SemanticKernelLlmClient? Client, string Message) Create(LlmOptions options)
    {
        if (!options.IsConfigured)
            return (null, "沒有設定 LLM_API_KEY／LLM_MODEL，AI 聊天未啟用");

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            return (null, $"LLM_BASE_URL 不是有效的 http(s) 網址：{options.BaseUrl}");
        }

        try
        {
            // 自己管 HttpClient：逾時要能控制（預設 100 秒太久，使用者會以為當掉）
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds)) };

            var builder = Kernel.CreateBuilder();
            builder.AddOpenAIChatCompletion(
                modelId: options.Model,
                endpoint: endpoint,
                apiKey: options.ApiKey!,
                httpClient: http);

            var kernel = builder.Build();
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            return (new SemanticKernelLlmClient(kernel, chat, options, http, ownsHttp: true),
                    $"✅ AI 聊天已啟用：{options.Describe()}");
        }
        catch (Exception ex)
        {
            return (null, $"建立 LLM 客戶端失敗：{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var history = new ChatHistory(request.SystemPrompt);

        foreach (var turn in request.History)
            history.Add(ToMessage(turn));

        history.Add(ToMessage(request.Incoming));

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens
        };

        var sw = Stopwatch.StartNew();

        try
        {
            var result = await _chat.GetChatMessageContentsAsync(
                history, settings, _kernel, cancellationToken);

            sw.Stop();

            if (result is null || result.Count == 0)
                throw new LlmException("模型沒有回覆任何內容");

            var message = result[0];
            var text = (message.Content ?? "").Trim();

            var (input, output, reported) = ExtractUsage(message, request, text);

            if (text.Length == 0)
            {
                // 有些推理模型會把預算用完在思考上（finish_reason=length）而沒有輸出
                var reason = ReadFinishReason(message);
                throw new LlmException(reason == "length"
                    ? "模型把輸出額度用完在思考上，沒有產生回覆（可以調高 LLM_MAX_OUTPUT_TOKENS）"
                    : "模型回覆了空白內容");
            }

            return new LlmReply(
                Text: text,
                InputTokens: input,
                OutputTokens: output,
                UsageReported: reported,
                Model: message.ModelId ?? _options.Model,
                Elapsed: sw.Elapsed,
                FinishReason: ReadFinishReason(message));
        }
        catch (LlmException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new LlmException("使用者取消或 Bot 正在關閉", retryable: false);
        }
        catch (TaskCanceledException ex)
        {
            sw.Stop();
            throw new LlmException(
                $"等不到回應（超過 {_options.RequestTimeoutSeconds} 秒）", retryable: true, ex);
        }
        catch (Exception ex)
        {
            sw.Stop();
            throw new LlmException(Friendly(ex), retryable: IsRetryable(ex), ex);
        }
    }

    private static ChatMessageContent ToMessage(ChatTurn turn)
        => turn.IsBot
            ? new ChatMessageContent(AuthorRole.Assistant, turn.Content)
            : new ChatMessageContent(AuthorRole.User, turn.ToPromptText());

    /// <summary>把 API 的例外翻成看得懂的一句話（400／401／429 是最常見的三種）。</summary>
    private string Friendly(Exception ex)
    {
        var raw = (ex.Message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (raw.Length > 200) raw = raw[..200] + "…";

        var text = raw + " " + (ex.InnerException?.Message ?? "");
        var host = _options.EndpointHost;

        if (text.Contains("401") || text.Contains("Unauthorized") || text.Contains("invalid_api_key"))
            return $"金鑰被拒絕（{host}）：檢查 LLM_API_KEY 是否正確、有沒有過期";

        if (text.Contains("404") || text.Contains("model_not_found"))
            return $"找不到模型「{_options.Model}」（{host}）：檢查 LLM_MODEL 名稱";

        if (text.Contains("429") || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return $"服務端限流（429，{host}）：等一下再試，或換一個模型";

        if (text.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
            || text.Contains("quota", StringComparison.OrdinalIgnoreCase))
            return $"服務端餘額不足（{host}）：去服務商後台加值";

        if (ex is HttpRequestException || text.Contains("Name or service not known")
            || text.Contains("No such host") || text.Contains("Connection refused"))
            return $"連不上 {host}（網路或網址錯誤）";

        return $"呼叫失敗：{ex.GetType().Name}: {raw}";
    }

    private static bool IsRetryable(Exception ex)
        => ex is HttpRequestException or IOException or System.Net.Sockets.SocketException;

    private static string? ReadFinishReason(ChatMessageContent message)
        => message.Metadata is { } md && md.TryGetValue("FinishReason", out var value)
            ? value?.ToString()
            : null;

    /// <summary>
    /// 從 SK 的 metadata 撈 token 用量；撈不到就用估算（並且誠實標示）。
    /// </summary>
    private static (int Input, int Output, bool Reported) ExtractUsage(
        ChatMessageContent message, LlmRequest request, string replyText)
    {
        var usage = message.Metadata is { } md && md.TryGetValue("Usage", out var u) ? u : null;

        if (usage is not null)
        {
            // 各種版本／連接器的欄位名都試一輪（見類別註解）
            var input = ReadInt(usage, "InputTokenCount", "PromptTokens", "InputTokens");
            var output = ReadInt(usage, "OutputTokenCount", "CompletionTokens", "OutputTokens");
            var total = ReadInt(usage, "TotalTokenCount", "TotalTokens");

            if (input is null && output is null && total is not null)
            {
                // 只有總量時，用估算的輸入量回推輸出量
                var estimatedInput = request.EstimatedInputTokens;
                input = Math.Min(estimatedInput, total.Value);
                output = Math.Max(0, total.Value - input.Value);
            }

            if (input is not null || output is not null)
                return (input ?? 0, output ?? 0, true);
        }

        return (request.EstimatedInputTokens, TokenEstimator.Estimate(replyText), false);
    }

    private static int? ReadInt(object source, params string[] propertyNames)
    {
        var type = source.GetType();

        foreach (var name in propertyNames)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null) continue;

            var value = property.GetValue(source);
            if (value is null) continue;

            if (value is int i) return i;
            if (value is long l) return (int)Math.Min(int.MaxValue, l);
            if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        }

        return null;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
