namespace TcBusBot.Core.Chat;

/// <summary>
/// 偷聽時「這則訊息要怎麼處理」的三種結果。
///
/// 為什麼不是 YES／NO 兩種：一開始只有「回話／停止偷聽」，
/// 但真實的多人頻道不是這樣運作的 —— 一群人聊天的時候，
/// 大部分句子既不是對 Bot 說的、也不代表「話題結束了」。
/// 只要判斷成「不是在跟我說話就退出」，就會出現兩個實際的抱怨：
///
///   1. **後面說的都被忽略**：一個人問了別的事、Bot 就退出，之後大家再聊它也不理。
///   2. **插不上話**：群組裡常有「@ 別人」的訊息，那更是直接退出。
///
/// 所以拆成三種：接話／先不出聲但留下來／真的退出。
/// </summary>
public enum Addressee
{
    /// <summary>在對 Bot 說話 → 回話。</summary>
    Reply,

    /// <summary>不是對 Bot 說話，但對話還在同一個主題上 → **不出聲，繼續聽**。</summary>
    Skip,

    /// <summary>明顯是別人之間的對話 → 停止偷聽，回到「等 @」。</summary>
    Stop
}

/// <summary>
/// 「這句話是在跟我說話嗎？」—— 偷聽模式用。
///
/// 使用者要的是：回完話之後**順便聽幾句**，判斷不是在對它說話時安靜下來、
/// 確定是別人之間的對話時才停止偷聽、回到等 @（三種結果見 <see cref="Addressee"/>）。
/// 為什麼要問模型而不是用規則判斷（例如「有沒有提到關鍵字」）：
/// 一群人聊天的時候，「那個公車幾點來」「所以你要搭 300 嗎」這種句子
/// 在字面上跟對 Bot 說話沒兩樣 —— 只有看上下文才分得出來。
///
/// 成本控制：
///   * 只看最近幾則（<see cref="MaxHistoryTurns"/>）＋ 新訊息
///   * 要求模型只回一個字（REPLY／SKIP／STOP），`MaxTokens = 8`、`Temperature = 0`
///   * 判斷結果會被記進每週額度（呼叫端負責）
///   * 就算判斷失敗（連不上、回答看不懂）→ **當成 SKIP**（不出聲但留下來）：
///     偷聽本來就是加分功能，不該因為它而亂回話；
///     但也不該因為一次判斷失敗就退出 —— 退出之後就再也接不上了
/// </summary>
public sealed class AddresseeDetector
{
    public const string SystemPrompt =
        "你是一個聊天機器人，正在一個多人聊天頻道裡。你剛剛回過話，現在要決定「新訊息」怎麼處理。\n" +
        "判斷原則：\n" +
        "• REPLY（接話）：在對你提問、下指令、回你上一句、延續你剛才回答的主題（即使沒 @ 你）\n" +
        "• SKIP（先不出聲，但留下來繼續聽）：沒 @ 你，但這群人還在聊**剛才那件事**、\n" +
        "  或你聽不出來是對誰說、或只是還沒輪到你\n" +
        "• STOP（退出，回去等 @）：明顯是**別人之間**的對話 —— @ 了別人、互相邀約、\n" +
        "  私人的問句（「你要不要一起去」「你幾點到」）、或在聊跟你完全無關的事\n" +
        "• 不確定時 → SKIP（不要插話，但也不要離開：離開之後你就接不上了）\n" +
        "只輸出一個英文單字：REPLY、SKIP 或 STOP。不要輸出其他任何文字。";

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
            "\n這則新訊息要 REPLY、SKIP 還是 STOP？只回答一個單字。";
    }

    /// <summary>
    /// 解析模型的回答。
    ///
    /// ⚠️ 抓**最後出現**的關鍵字（推理模型常多講幾句），
    /// 而且**三種都找不到時回傳 null**（呼叫端會當成 SKIP：不出聲但留在頻道裡）。
    /// </summary>
    public static Addressee? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.ToUpperInvariant();

        var reply = text.LastIndexOf("REPLY", StringComparison.Ordinal);
        var skip = text.LastIndexOf("SKIP", StringComparison.Ordinal);
        var stop = text.LastIndexOf("STOP", StringComparison.Ordinal);

        var last = Math.Max(reply, Math.Max(skip, stop));
        if (last < 0) return null;

        return last == stop ? Addressee.Stop
             : last == reply ? Addressee.Reply
             : Addressee.Skip;
    }

    /// <summary>判斷（永遠不丟例外；失敗＝SKIP）。</summary>
    public async Task<DetectResult> DetectAsync(
        IReadOnlyList<ChatTurn> history, ChatTurn incoming, string botName, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return new DetectResult(Addressee.Skip, "偷聽未啟用", null);

        var request = new LlmRequest(
            SystemPrompt: SystemPrompt,
            History: [],
            Incoming: new ChatTurn(
                ChatRole.User, incoming.AuthorName, incoming.AuthorId, incoming.MessageId,
                BuildUserPrompt(history, incoming, botName), incoming.At),
            MaxTokens: 8,
            Temperature: 0,
            Tag: "addressee")
        {
            // 這種判斷只要回一個單字、而且次數多 → 交給 RoutingLlmClient 導去便宜的小模型
            JudgeCall = _options.HasSeparateJudgeModel
        };

        try
        {
            var reply = await _llm.CompleteAsync(request, cancellationToken);
            var parsed = Parse(reply.Text);

            // 注意：Parse 只會回 REPLY／SKIP／STOP 或 null，
            // 但 default 還是寫成 SKIP ——「不出聲但留下來」是唯一安全的預設。
            return parsed switch
            {
                Addressee.Reply => new DetectResult(
                    Addressee.Reply, $"判斷是在對我說（「{Shorten(reply.Text)}」）→ 回話", reply),

                Addressee.Stop => new DetectResult(
                    Addressee.Stop, $"判斷是別人之間的對話（「{Shorten(reply.Text)}」）→ 停止偷聽、回到等 @", reply),

                Addressee.Skip => new DetectResult(
                    Addressee.Skip, $"判斷不是對我說（「{Shorten(reply.Text)}」）→ 不出聲，繼續聽", reply),

                _ => new DetectResult(
                    Addressee.Skip, $"回答看不懂（「{Shorten(reply.Text)}」）→ 當成先不出聲，繼續聽", reply)
            };
        }
        catch (LlmException ex)
        {
            return new DetectResult(Addressee.Skip, $"判斷失敗（{ex.Message}）→ 先不出聲，繼續聽", null);
        }
    }

    public sealed record DetectResult(Addressee Verdict, string Detail, LlmReply? Usage)
    {
        /// <summary>要不要真的回話。</summary>
        public bool Addressed => Verdict == Addressee.Reply;
    }

    private static string Short(string text)
        => text.Length <= 160 ? text : text[..160] + "…";

    private static string Shorten(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim() is var t && t.Length <= 30 ? t : t[..30] + "…";
}
