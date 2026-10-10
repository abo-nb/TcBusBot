namespace TcBusBot.Core.Chat;

/// <summary>
/// 一則訊息「在對誰說話」的**事實**（由 Discord 那一層填，每一項都是客觀的觀察）。
///
/// 為什麼要把這件事變成資料 ＋ 純函式（而不是寫在 Discord 的事件處理裡）：
/// 這裡的每一條規則錯了都**不會拋例外**，只會出現「它不回我」或「它亂回話」——
/// 而且都很難重現（要剛好有人 @ 別人、剛好另一個機器人在講話）。
/// 抽出來之後，各種組合都能在 `tcbus selftest` 裡直接餵進去測。
///
/// ── 兩個真實回報過的症狀，都出自這一塊 ──────────────────
/// 1. **別的 App @ 它，它吃不到**：以前整段「Bot／webhook 發的訊息」都被丟掉
///    （那是為了防止機器人互相回話），連明確 @ 它也一起被丟掉。
/// 2. **@ 別人被當成在對它說話**：以前「@ 了別人」只算**真人**
///    （`!u.IsBot`），所以「@ 某個 App」「@ 管理員身分組」「@everyone」
///    都不算「在跟別人說話」→ 交去問模型要不要接話 → 它就會插話。
/// </summary>
public sealed record MessageFacts(
    /// <summary>發話者是別的 App（Bot）或 webhook。</summary>
    bool FromApp,
    /// <summary>在私訊裡（沒有伺服器）。</summary>
    bool DirectMessage,
    /// <summary>明確 @ 了我們。</summary>
    bool MentionsBot,
    /// <summary>@ 了幾個**真人**（不含我們）。</summary>
    int OtherHumans,
    /// <summary>@ 了幾個**別的 App**（不含我們）。</summary>
    int OtherApps,
    /// <summary>@ 了幾個身分組（含 App 自己的身分組）。</summary>
    int Roles,
    /// <summary>@everyone／@here。</summary>
    bool MentionsEveryone,
    /// <summary>回覆了我們的訊息（或我們記得過的訊息）。</summary>
    bool RepliesToBot)
{
    /// <summary>@ 了**別人** —— 真人、別的 App、身分組、@everyone 都算。</summary>
    public bool MentionsSomeoneElse
        => OtherHumans > 0 || OtherApps > 0 || Roles > 0 || MentionsEveryone;

    /// <summary>
    /// 有沒有明確對 Bot 說話。
    ///
    /// ⚠️ 「回覆」那條路**不適用於別的 App**：A 回我、我回 A，
    ///    兩邊都覺得對方在對自己說話 → 無限循環。App 要叫我們就一定要 @。
    /// </summary>
    public bool AddressedToBot => MentionsBot || (!FromApp && RepliesToBot);

    /// <summary>可以偷聽嗎（App 之間的訊息不聽：那是機器人在講話，插話只會互相觸發）。</summary>
    public bool CanEavesdrop => !FromApp && !AddressedToBot;

    /// <summary>給 log 用的一行說明。</summary>
    public string Describe()
        => (FromApp ? "App" : "真人") +
           (DirectMessage ? "／私訊" : "") +
           (MentionsBot ? "／@了我" : "") +
           (OtherHumans > 0 ? $"／@了{OtherHumans}個真人" : "") +
           (OtherApps > 0 ? $"／@了{OtherApps}個App" : "") +
           (Roles > 0 ? $"／@了{Roles}個身分組" : "") +
           (MentionsEveryone ? "／@everyone" : "") +
           (RepliesToBot ? "／回覆我" : "");
}

/// <summary>
/// 從 <see cref="MessageFacts"/> 算出「要不要處理這一則」。
///
/// 規則集中成一個純函式，是因為這裡的每一條都是**安全與禮貌**問題：
///   * 太寬 → 機器人互相回話（燒錢、洗頻）、@ 別人時亂插話
///   * 太嚴 → 「別的 App @ 它，它卻完全沒反應」（使用者實際回報的那個）
/// </summary>
public static class MessageAddress
{
    /// <summary>
    /// 回傳 null ＝ 要處理；回傳字串 ＝ **忽略的原因**（會寫進 console，
    /// 這樣「它為什麼不理我」在 log 裡就有答案）。
    /// </summary>
    public static string? IgnoreReason(MessageFacts facts)
    {
        if (!facts.FromApp) return null;

        // 別的 App：**只有明確 @ 我們**才處理。
        // 這條門檻刻意比真人高 —— 真人回一句話可以算「在對我說話」，
        // 兩個機器人之間只要有「回覆也算」就會互相觸發到天荒地老。
        if (facts.DirectMessage)
            return "別的 App 在私訊裡 → 不理（機器人私訊沒有「@ 我」這件事，也沒有對話的意義）";

        return facts.MentionsBot
            ? null
            : "別的 App／webhook 的訊息（沒有 @ 我們）→ 不理（機器人互相回話會無限循環）";
    }

    /// <summary>要不要套用「App 專屬冷卻」（同一個 App 在同一個頻道連續 @ 我們的煞車）。</summary>
    public static bool NeedsAppCooldown(MessageFacts facts) => facts.FromApp && facts.MentionsBot;

    /// <summary>
    /// 這個被 @ 的身分組是「**我們這個 App 自己的**身分組」嗎？
    ///
    /// 為什麼要特別處理：Discord 幫每個 App 建一個**同名身分組**（託管身分組），
    /// 而使用者從成員清單 @ 它時，送出的可能是**身分組 mention**（`&lt;@&amp;roleId&gt;`）
    /// 而不是使用者 mention（`&lt;@userId&gt;`）。
    /// 只認使用者 mention 的話，那種 @ 會被當成「@ 了別人」→ 它完全不理你 ——
    /// 而使用者看到的是「我明明 @ 它了，它卻不回」。
    /// </summary>
    public static bool IsOwnAppRole(bool isManaged, ulong? roleBotId, ulong botId)
        => botId != 0 && isManaged && roleBotId == botId;
}
