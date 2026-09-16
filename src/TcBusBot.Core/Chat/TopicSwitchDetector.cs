namespace TcBusBot.Core.Chat;

/// <summary>
/// 「要不要開新的一段對話？」
///
/// 使用者的規則：**時間只是第一道過濾**，真正的判斷交給 LLM ——
/// 「超過一定時間就當別段」是快速路徑（不需要花錢問），
/// 但如果只是隔了幾分鐘（時間門檻內），卻明顯在講別的事，也應該換段（不要帶舊上下文）；
/// 反過來說，隔了一陣子但接著剛才的話題問，也應該接續。
///
/// 所以流程是：
///   1. 超過 <see cref="LlmOptions.SegmentGapMinutes"/> → 直接開新段（不問 LLM、不花錢）
///   2. 使用者回覆了某一則訊息 → 回到那一則所屬的段落（不問 LLM：這是明確的指定）
///   3. 其他情況 → 問 LLM「這則訊息是接續上一個話題，還是開新話題？」
///
/// 判斷器只用**最近幾則**訊息，並且要求模型只回一個字（NEW／SAME），
/// 成本極低（實測輸入約 100 tokens）；就算判斷失敗也只是沿用目前的段落，不會中斷對話。
/// </summary>
public sealed class TopicSwitchDetector
{
    public const string SystemPrompt =
        "你是一個話題切換判斷器，只做一件事：判斷『新訊息』是不是在接續『先前的對話』。\n" +
        "判斷原則：\n" +
        "• 新訊息在回應、補充、追問、延續先前對話的任何內容 → SAME\n" +
        "• 新訊息是打招呼、完全不同的主題、或先前對話已經明顯結束 → NEW\n" +
        "• 不確定時 → SAME（接續比較安全，突然失憶對使用者更糟）\n" +
        "只輸出一個英文單字：NEW 或 SAME。不要輸出其他任何文字。";

    /// <summary>判斷器最多看幾則歷史（少即是快、便宜、也比較不會被長上下文干擾）。</summary>
    public const int MaxHistoryTurns = 6;

    private readonly ILlmClient _llm;
    private readonly LlmOptions _options;

    public TopicSwitchDetector(ILlmClient llm, LlmOptions options)
    {
        _llm = llm;
        _options = options;
    }

    public bool Enabled => _options.TopicDetect;

    /// <summary>組出判斷用的提示詞（純函式，可離線測試）。</summary>
    public static string BuildUserPrompt(IReadOnlyList<ChatTurn> history, ChatTurn incoming)
    {
        var recent = history.Count <= MaxHistoryTurns
            ? history
            : history.Skip(history.Count - MaxHistoryTurns).ToList();

        var lines = recent.Select(t => $"  {(t.IsBot ? "Bot" : t.AuthorName)}：{Short(t.Content)}");

        return
            "【先前的對話】\n" +
            (recent.Count == 0 ? "  （沒有，這是第一則訊息）\n" : string.Join("\n", lines) + "\n") +
            $"\n【新訊息】（{incoming.AuthorName}）\n  {Short(incoming.Content)}\n" +
            "\n這則新訊息是接續先前的對話（SAME），還是開新話題（NEW）？只回答 NEW 或 SAME。";
    }

    /// <summary>
    /// 解析模型的回答。
    ///
    /// 為什麼寫得這麼寬鬆：推理型模型常常會多講幾句（「這則訊息看起來是...SAME」），
    /// 嚴格的 JSON／單字比對會直接失敗。這裡抓**最後出現**的 NEW／SAME，
    /// 因為模型的結論通常寫在最後。兩種都出現時以最後一個為準；
    /// 完全找不到就回 null（呼叫端會沿用目前段落）。
    /// </summary>
    public static bool? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.ToUpperInvariant();

        var lastNew = text.LastIndexOf("NEW", StringComparison.Ordinal);
        var lastSame = text.LastIndexOf("SAME", StringComparison.Ordinal);

        if (lastNew < 0 && lastSame < 0) return null;
        if (lastNew < 0) return false;
        if (lastSame < 0) return true;

        return lastNew > lastSame;
    }

    /// <summary>
    /// 問 LLM。**永遠不會丟例外**：判斷失敗就沿用目前的段落（回傳 false）。
    /// 判斷結果與用量會回報給呼叫端記帳。
    /// </summary>
    public async Task<DetectResult> DetectAsync(
        IReadOnlyList<ChatTurn> history, ChatTurn incoming, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return new DetectResult(false, "沒有開啟話題判斷", null);

        var request = new LlmRequest(
            SystemPrompt: SystemPrompt,
            History: [],
            Incoming: new ChatTurn(
                ChatRole.User, incoming.AuthorName, incoming.AuthorId, incoming.MessageId,
                BuildUserPrompt(history, incoming), incoming.At),
            MaxTokens: 16,
            Temperature: 0,
            Tag: "topic-detect");

        try
        {
            var reply = await _llm.CompleteAsync(request, cancellationToken);
            var verdict = Parse(reply.Text);

            return verdict switch
            {
                true => new DetectResult(true, $"LLM 判斷換話題了（回覆「{Shorten(reply.Text)}」）", reply),
                false => new DetectResult(false, "LLM 判斷是同一段話題", reply),
                null => new DetectResult(false, $"LLM 的回答看不懂（「{Shorten(reply.Text)}」）→ 沿用目前段落", reply)
            };
        }
        catch (LlmException ex)
        {
            return new DetectResult(false, $"話題判斷失敗（{ex.Message}）→ 沿用目前段落", null);
        }
    }

    public sealed record DetectResult(bool IsNewTopic, string Detail, LlmReply? Usage);

    private static string Short(string text)
        => text.Length <= 200 ? text : text[..200] + "…";

    private static string Shorten(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim() is var t && t.Length <= 40 ? t : t[..40] + "…";
}
