namespace TcBusBot.Core.Chat;

/// <summary>
/// 「這句話是在跟我說話嗎？」—— 偷聽模式用。
///
/// 使用者要的是：回完話之後**順便聽幾句**，如果判斷不是對它說的就停止偷聽、回到等 @。
/// 為什麼要問模型而不是用規則判斷（例如「有沒有提到關鍵字」）：
/// 一群人聊天的時候，「那個公車幾點來」「所以你要搭 300 嗎」這種句子
/// 在字面上跟對 Bot 說話沒兩樣 —— 只有看上下文才分得出來。
///
/// 成本控制：
///   * 只看最近幾則（<see cref="MaxHistoryTurns"/>）＋ 新訊息
///   * 要求模型只回一個字（YES／NO），`MaxTokens = 8`、`Temperature = 0`
///   * 判斷結果會被記進每週額度（呼叫端負責）
///   * 就算判斷失敗（連不上、回答看不懂）→ **當成不是對它說的**並停止偷聽：
///     偷聽本來就是加分功能，不該因為它而讓 Bot 亂回話或一直花錢
/// </summary>
public sealed class AddresseeDetector
{
    public const string SystemPrompt =
        "你負責判斷『新訊息』是不是在對「你（機器人）」說話。\n" +
        "這是一個多人聊天頻道，訊息大多是**人之間**在講話。\n" +
        "判斷原則：\n" +
        "• 在對你提問、下指令、回你上一句、或延續你剛才回答的話題 → YES\n" +
        "• 兩個人互相約、互相問「你要不要…」「你要吃什麼」「你幾點到」這種\n" +
        "  **對另一個人的邀請或私人問句** → NO\n" +
        "• 聊到公車、時間、路線，但明顯不是要你回答 → NO\n" +
        "• 在講別人、感嘆、自言自語 → NO\n" +
        "• 不確定時 → NO（寧可少回一句，也不要插話打斷別人）\n" +
        "只輸出一個英文單字：YES 或 NO。不要輸出其他任何文字。";

    public const int MaxHistoryTurns = 4;

    private readonly ILlmClient _llm;
    private readonly LlmOptions _options;

    public AddresseeDetector(ILlmClient llm, LlmOptions options)
    {
        _llm = llm;
        _options = options;
    }

    public bool Enabled => _options.Eavesdrop;

    /// <summary>組出判斷用的提示詞（純函式，可離線測試）。</summary>
    public static string BuildUserPrompt(IReadOnlyList<ChatTurn> history, ChatTurn incoming, string botName)
    {
        var recent = history.Count <= MaxHistoryTurns
            ? history
            : history.Skip(history.Count - MaxHistoryTurns).ToList();

        var lines = recent.Select(t => $"  {(t.IsBot ? botName : t.AuthorName)}：{Short(t.Content)}");

        return
            $"你是「{botName}」，剛剛在這個頻道跟 {recent.LastOrDefault(t => !t.IsBot)?.AuthorName ?? "某人"} 講過話。\n" +
            "【最近的對話】\n" +
            (recent.Count == 0 ? "  （沒有）\n" : string.Join("\n", lines) + "\n") +
            $"\n【新訊息】（{incoming.AuthorName}）\n  {Short(incoming.Content)}\n" +
            "\n這則新訊息是在對你說話嗎？只回答 YES 或 NO。";
    }

    /// <summary>
    /// 解析模型的回答。
    /// ⚠️ 抓**最後出現**的 YES／NO（推理模型常多講幾句），
    /// 而且**兩種都找不到時回傳 null**（呼叫端會當成「不是對它說的」）。
    /// </summary>
    public static bool? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.ToUpperInvariant();
        var yes = text.LastIndexOf("YES", StringComparison.Ordinal);
        var no = text.LastIndexOf("NO", StringComparison.Ordinal);

        if (yes < 0 && no < 0) return null;
        if (no < 0) return true;
        if (yes < 0) return false;

        return yes > no;
    }

    /// <summary>判斷（永遠不丟例外；失敗＝不是對它說的）。</summary>
    public async Task<DetectResult> DetectAsync(
        IReadOnlyList<ChatTurn> history, ChatTurn incoming, string botName, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return new DetectResult(false, "偷聽未啟用", null);

        var request = new LlmRequest(
            SystemPrompt: SystemPrompt,
            History: [],
            Incoming: new ChatTurn(
                ChatRole.User, incoming.AuthorName, incoming.AuthorId, incoming.MessageId,
                BuildUserPrompt(history, incoming, botName), incoming.At),
            MaxTokens: 8,
            Temperature: 0,
            Tag: "addressee");

        try
        {
            var reply = await _llm.CompleteAsync(request, cancellationToken);
            var verdict = Parse(reply.Text);

            return verdict switch
            {
                true => new DetectResult(true, $"判斷是在對我說（「{Shorten(reply.Text)}」）", reply),
                false => new DetectResult(false, "判斷不是對我說 → 停止偷聽", reply),
                null => new DetectResult(false, $"回答看不懂（「{Shorten(reply.Text)}」）→ 當成不是對我說", reply)
            };
        }
        catch (LlmException ex)
        {
            return new DetectResult(false, $"判斷失敗（{ex.Message}）→ 當成不是對我說", null);
        }
    }

    public sealed record DetectResult(bool Addressed, string Detail, LlmReply? Usage);

    private static string Short(string text)
        => text.Length <= 160 ? text : text[..160] + "…";

    private static string Shorten(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim() is var t && t.Length <= 30 ? t : t[..30] + "…";
}
