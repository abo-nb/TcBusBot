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
/// 別的 App 的訊息要怎麼對待。
///
/// 需求原文是「他似乎看不到其他 App 說的話」——
/// 「看不到」與「不回話」是兩件事：使用者要它**知道**別的 App 講了什麼
/// （例如另一個機器人貼的班次通知），但不希望它對著機器人回話。
/// </summary>
public enum AppMessageMode
{
    /// <summary>
    /// （預設）**看得到，但不回話**：記進對話上下文（提示詞裡標成 `[App]`），
    /// 不問模型、不花錢、不發訊息。之後有人問「剛剛那個通知說什麼」它就答得出來。
    /// </summary>
    Context,

    /// <summary>沒 @ 它也回話（還是有冷卻與「不發 ping」兩道煞車）。</summary>
    Reply,

    /// <summary>
    /// **跟另一個 App 對話**：沒 @ 也回，而且會 **@ 回去**（多數機器人只認 mention，
    /// 不叫它就完全不動 —— 那樣兩邊根本聊不起來）。
    ///
    /// ⚠️ 這個模式一定要搭配停損（`AppChatGuard`：`/stop` ＋ 每小時上限）——
    ///    兩個都會回話的機器人放著不管就是無限迴圈。
    /// </summary>
    Chat,

    /// <summary>完全忽略（連記都不記）—— 以前的舊行為。</summary>
    Off
}

/// <summary>這一則訊息要怎麼處理。</summary>
public enum MessageHandling
{
    /// <summary>照一般流程（真人：@ 它／回覆它／偷聽判斷）。</summary>
    Normal,

    /// <summary>別的 App 明確叫我們 → 回話。</summary>
    ReplyToApp,

    /// <summary>別的 App 在講話 → **只記下來當上下文**（看得到、不回話、不花錢）。</summary>
    ContextOnly,

    /// <summary>不理，而且不記（連上下文都不留）。</summary>
    Ignore
}

/// <summary>
/// 從 <see cref="MessageFacts"/> 算出「要不要處理這一則」。
///
/// 規則集中成一個純函式，是因為這裡的每一條都是**安全與禮貌**問題：
///   * 太寬 → 機器人互相回話（燒錢、洗頻）、@ 別人時亂插話
///   * 太嚴 → 「別的 App @ 它，它卻完全沒反應」或「別的 App 說的話它完全不知道」
/// </summary>
public static class MessageAddress
{
    /// <summary>
    /// 這一則要怎麼處理。
    ///
    /// 別的 App 的門檻刻意比真人高：
    ///   * 真人回一句話可以算「在對我說話」
    ///   * 兩個機器人之間只要有「回覆也算」就會互相觸發到天荒地老
    /// </summary>
    public static MessageHandling Decide(MessageFacts facts, AppMessageMode appMode = AppMessageMode.Context)
    {
        if (!facts.FromApp) return MessageHandling.Normal;

        // 別的 App 在私訊裡：連記都不記（機器人私訊沒有對話的意義）
        if (facts.DirectMessage) return MessageHandling.Ignore;

        // 明確 @ 我們 → 一定要回（三種模式都一樣：被叫了就要答）
        if (facts.MentionsBot) return MessageHandling.ReplyToApp;

        return appMode switch
        {
            AppMessageMode.Reply or AppMessageMode.Chat => MessageHandling.ReplyToApp,
            AppMessageMode.Off => MessageHandling.Ignore,
            _ => MessageHandling.ContextOnly
        };
    }

    /// <summary>
    /// 回傳 null ＝ 要處理；回傳字串 ＝ **忽略的原因**（會寫進 console，
    /// 這樣「它為什麼不理我」在 log 裡就有答案）。
    ///
    /// ⚠️ 只給「連記都不記」的情況用；「看得到但不回話」請看 <see cref="Decide"/>
    ///    （它會回 <see cref="MessageHandling.ContextOnly"/>）。
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

    /// <summary>要不要套用「App 專屬冷卻」（同一個 App 在同一個頻道連續叫我們的煞車）。</summary>
    public static bool NeedsAppCooldown(MessageFacts facts, AppMessageMode appMode = AppMessageMode.Context)
        => facts.FromApp && (facts.MentionsBot || appMode is AppMessageMode.Reply or AppMessageMode.Chat);

    /// <summary>
    /// 「跟另一個 App 對話」模式嗎（回話時要 **@ 回去**，否則多數機器人不會理我們）。
    /// </summary>
    public static bool IsAppChat(AppMessageMode appMode) => appMode == AppMessageMode.Chat;

    /// <summary>把使用者設定的字串轉成模式（看不懂就用預設的「看得到但不回話」）。</summary>
    public static AppMessageMode ParseAppMode(string? value)
        => (value ?? "").Trim().ToLowerInvariant() switch
        {
            "reply" or "回" or "回話" => AppMessageMode.Reply,
            "chat" or "聊天" or "talk" => AppMessageMode.Chat,
            "off" or "false" or "0" or "ignore" or "忽略" => AppMessageMode.Off,
            _ => AppMessageMode.Context
        };

    /// <summary>給 `/ai status` 與啟動訊息看的一行說明。</summary>
    public static string DescribeAppMode(AppMessageMode mode) => mode switch
    {
        AppMessageMode.Chat =>
            "別的 App 的訊息：**跟它對話**（會 @ 回去；用 `/stop` 停、有每小時上限）",
        AppMessageMode.Reply => "別的 App 的訊息：沒 @ 也會回（有冷卻、不發 ping）",
        AppMessageMode.Off => "別的 App 的訊息：完全忽略（連上下文都不記）",
        _ => "別的 App 的訊息：看得到（記進上下文並標成 [App]），但不會回話"
    };

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
