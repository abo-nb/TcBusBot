namespace TcBusBot.Core.Chat;

using Microsoft.SemanticKernel;

/// <summary>一次要送給 LLM 的內容。</summary>
public sealed record LlmRequest(
    string SystemPrompt,
    IReadOnlyList<ChatTurn> History,
    ChatTurn Incoming,
    int MaxTokens,
    double Temperature,
    string? Tag = null)
{
    /// <summary>
    /// 這次可以用的工具（Semantic Kernel 的 plugin）。
    ///
    /// 為什麼放在 request 而不是 kernel：工具需要**這一次互動的上下文**
    /// （誰在問、在哪個頻道），是同一個使用者一次性的東西 ——
    /// 塞進共用的 kernel 會變成跨使用者污染（A 的訂閱被 B 的工具呼叫建立）。
    /// </summary>
    public IReadOnlyList<KernelPlugin> Plugins { get; init; } = [];

    /// <summary>這次呼叫大概會用掉多少輸入 token（事前估算，用來擋額度）。</summary>
    public int EstimatedInputTokens
        => TokenEstimator.Estimate(SystemPrompt)
           + TokenEstimator.Estimate(History)
           + TokenEstimator.Estimate(Incoming.ToPromptText())
           + TokenEstimator.PerMessageOverhead;
}

/// <summary>LLM 的回覆。</summary>
public sealed record LlmReply(
    string Text,
    int InputTokens,
    int OutputTokens,
    bool UsageReported,
    string Model,
    TimeSpan Elapsed,
    string? FinishReason = null)
{
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>用量是 API 給的還是我們自己估的（`/ai status` 與 log 會標示）。</summary>
    public string UsageSource => UsageReported ? "API 回報" : "估算";
}

/// <summary>呼叫 LLM 失敗（訊息一定是「人看得懂」的那種，不會把金鑰印出來）。</summary>
public sealed class LlmException : Exception
{
    public LlmException(string message, bool retryable = false, Exception? inner = null)
        : base(message, inner) => Retryable = retryable;

    public bool Retryable { get; }
}

/// <summary>
/// 聊天用的 LLM 介面。
///
/// 為什麼要抽這一層：Discord 那一層不該知道 Semantic Kernel 的存在 ——
/// 這樣「對話切段」「每週額度」這些規則都可以在沒有網路、沒有金鑰的情況下測，
/// 換模型或換服務也不會動到業務邏輯。
/// </summary>
public interface ILlmClient
{
    bool IsConfigured { get; }

    /// <summary>給使用者看的一行說明（模型 ＋ 服務 host，不含金鑰）。</summary>
    string Describe();

    Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}
