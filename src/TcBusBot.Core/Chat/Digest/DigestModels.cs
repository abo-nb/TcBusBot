using System.Text.Json.Serialization;

namespace TcBusBot.Core.Chat.Digest;

/// <summary>
/// 要餵給模型的一則訊息（由 Discord 那一層抓好的**純資料**）。
///
/// 為什麼要轉成這個型別而不是直接用 <c>IMessage</c>：
/// 摘要邏輯（提示詞、分批、JSON 解析、游標）必須能在**沒有 Discord、沒有網路**的情況下測，
/// 而這一整段正是最容易出錯的地方（分批切錯、游標沒推進、@ 到不該 @ 的人）。
/// </summary>
public sealed record DigestMessage(
    string Id,
    ulong AuthorId,
    string AuthorName,
    string PromptLabel,
    DateTimeOffset At,
    string Text,
    int ImageCount = 0,
    bool IsBot = false)
{
    /// <summary>提示詞裡的一行（時間用頻道所在時區的「台灣時間」呈現，人才看得懂）。</summary>
    public string ToPromptLine(TimeSpan zoneOffset)
        => $"[{At.ToOffset(zoneOffset):MM-dd HH:mm}] {PromptLabel}：{Text}" +
           (ImageCount > 0 ? $"（附 {ImageCount} 張圖片）" : "");

    /// <summary>這個人是不是在這則訊息裡出現（用來收集「參與者」清單）。</summary>
    public bool Mentions(ulong userId) => AuthorId == userId;
}

/// <summary>一個「還在進行中」的事項（跨週會一直被帶下去，直到做完）。</summary>
public sealed record DigestItem(
    string Text,
    ulong OwnerId = 0,
    string OwnerName = "");

/// <summary>模型認為「這一週的事跟這個人有關」。</summary>
public sealed record DigestMention(
    ulong UserId,
    string Name,
    string Reason);

/// <summary>
/// 游標的比較規則（**抽出來是因為它決定「會不會漏訊息」**）。
///
/// Discord 的訊息 ID 是 snowflake（單調遞增的數字字串），所以「有沒有比游標新」
/// 就是數字比大小 —— 但字串比大小會錯（`"9" > "10"`），一定要轉成數字。
/// 不確定的一律當成「新」（寧可重複整理一則，也不要默默漏掉一段對話）。
/// </summary>
public static class DigestCursor
{
    public static ulong Parse(string? messageId)
        => ulong.TryParse(messageId, out var id) ? id : 0UL;

    /// <summary>這一則是不是在游標之後（＝還沒被整理過）。</summary>
    public static bool IsAfter(string? messageId, string? cursorMessageId)
    {
        var cursor = Parse(cursorMessageId);

        if (cursor == 0) return true;

        var id = Parse(messageId);

        return id == 0 || id > cursor;
    }
}

/// <summary>
/// 第一次啟用時要回顧多少（**由管理員決定**）。
///
/// 為什麼不自動決定：這取決於「這個頻道的歷史資料對你還有沒有意義」，
/// 以及「你願意為第一次付多少 token」—— 兩件事都只有管理員知道。
/// </summary>
public enum DigestBackfill
{
    /// <summary>從現在開始（不回顧）。</summary>
    Now,

    Last7Days,

    Last30Days,

    Last500Messages,

    Last1000Messages,

    /// <summary>能讀多少就讀多少（受安全上限限制）。</summary>
    Everything
}

/// <summary>
/// 某個頻道的摘要設定（**會被持久化**）。
///
/// 游標（<see cref="CursorMessageId"/>）是這個功能的核心：
/// 它記「上一次整理到哪一則」，所以每一週只算新訊息，不會重複計算同一段內容
/// （那不但浪費錢，摘要還會把同一件事講兩次）。
/// </summary>
public sealed record ChannelDigestConfig(
    [property: JsonPropertyName("channel")] ulong ChannelId,
    [property: JsonPropertyName("guild")] ulong GuildId,
    [property: JsonPropertyName("day")] DayOfWeek Day,
    [property: JsonPropertyName("hour")] int Hour,
    [property: JsonPropertyName("minute")] int Minute,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("by")] ulong EnabledBy,
    [property: JsonPropertyName("enabled_at")] DateTimeOffset EnabledAt,
    [property: JsonPropertyName("cursor")] string? CursorMessageId,
    [property: JsonPropertyName("cursor_at")] DateTimeOffset? CursorAt,
    [property: JsonPropertyName("last_run")] DateTimeOffset? LastRunAt,
    [property: JsonPropertyName("next_run")] DateTimeOffset? NextRunAt,
    [property: JsonPropertyName("runs")] int RunCount,
    [property: JsonPropertyName("last_error")] string? LastError)
{
    public DigestSchedule Schedule => new(Day, Hour, Minute);

    public ChannelDigestConfig WithSchedule(DigestSchedule schedule, DateTimeOffset nextRun)
        => this with
        {
            Day = schedule.Day,
            Hour = schedule.Hour,
            Minute = schedule.Minute,
            NextRunAt = nextRun
        };
}

/// <summary>
/// 一次摘要的結果 —— **這一筆會被永久保存**（見 <see cref="DigestStore"/>）。
///
/// 為什麼連摘要原文都存：使用者要的是「每週自動整理」，而整理出來的東西
/// 一個月後一定有人想回頭查（「上個月是不是決定過什麼」）。
/// Discord 的訊息會被刪、會被洗掉，這一份不會。
/// </summary>
public sealed record DigestRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("channel")] ulong ChannelId,
    [property: JsonPropertyName("guild")] ulong GuildId,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("from")] DateTimeOffset? From,
    [property: JsonPropertyName("to")] DateTimeOffset? To,
    [property: JsonPropertyName("messages")] int MessageCount,
    [property: JsonPropertyName("people")] int PeopleCount,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("highlights")] IReadOnlyList<string> Highlights,
    [property: JsonPropertyName("open")] IReadOnlyList<DigestItem> OpenItems,
    [property: JsonPropertyName("mentions")] IReadOnlyList<DigestMention> Mentions,
    [property: JsonPropertyName("in_tokens")] int InputTokens,
    [property: JsonPropertyName("out_tokens")] int OutputTokens,
    [property: JsonPropertyName("reasoning_tokens")] int ReasoningTokens,
    /// <summary>
    /// 模型在整理之前想的內容（**節錄**，最多 2000 字）。
    ///
    /// 為什麼要存：使用者的要求是「看他在想什麼」，而週報是**一週後才看**的東西 ——
    /// 只在記憶體裡留著的思考，重啟或過幾天就沒了。存節錄（不是全文）
    /// 是因為思考常常比摘要長好幾倍，而看的人是為了知道「它怎麼判斷的」。
    /// </summary>
    [property: JsonPropertyName("reasoning")] string Reasoning,
    [property: JsonPropertyName("calls")] int Calls,
    [property: JsonPropertyName("manual")] bool Manual,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("model")] string Model)
{
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>有幾則訊息被整理過（給 `/digest status` 與 log 用）。</summary>
    public string DescribeRange()
        => From is null || To is null
            ? "（沒有時間範圍）"
            : $"{From.Value.ToLocalTime():yyyy-MM-dd HH:mm} → {To.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
}

/// <summary>一次摘要執行的結果（要發的內容 ＋ 統計）。</summary>
public sealed record DigestOutcome(
    bool Ok,
    string Message,
    DigestRecord? Record = null,
    bool NoNewMessages = false,
    bool BudgetBlocked = false,
    int Truncated = 0,
    /// <summary>
    /// 模型的輸出被 `max_tokens` 截斷（`finish_reason=length`）。
    ///
    /// ⚠️ 這個一定要講出來：思考的 token 也算在輸出額度裡，所以「太長」的實際症狀是
    ///    **摘要話講到一半就斷掉**（甚至 JSON 不完整而退回「整段當摘要」）。
    ///    不說的話，使用者只會覺得「摘要品質變差了」。
    /// </summary>
    bool OutputTruncated = false)
{
    /// <summary>這一輪是不是真的產出了摘要（要發到頻道）。</summary>
    public bool HasDigest => Ok && Record is not null;

    public static DigestOutcome Skip(string message)
        => new(false, message, NoNewMessages: true);

    public static DigestOutcome Refused(string message)
        => new(false, message, BudgetBlocked: true);

    public static DigestOutcome Failed(string message)
        => new(false, message);
}
