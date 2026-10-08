namespace TcBusBot.Core.Chat.Digest;

/// <summary>
/// 每週頻道摘要的設定（環境變數可調）。
///
/// 為什麼獨立一個類別而不是塞進 <see cref="LlmOptions"/>：
/// 這一組設定只影響「每週摘要」那一條路徑（排程、批次、思考、上限），
/// 跟聊天本身的設定沒有關係 —— 混在一起會讓 `/ai status` 那張表越來越難讀，
/// 也會讓人以為改它會影響聊天。
/// </summary>
public sealed class DigestOptions
{
    /// <summary>功能總開關（`LLM_DIGEST=false` 時排程器不啟動，手動觸發也會被擋）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>一次摘要最多 @ 幾個人（`0` = 完全不 @）。</summary>
    public int MaxMentions { get; set; } = 5;

    /// <summary>
    /// 摘要那一次呼叫最多回幾個 token（摘要＋重點＋跨週清單的 JSON 外框一起算）。
    ///
    /// ⚠️ **思考的 token 算在這個額度裡**（實測：`max_tokens=60` 搭配思考時，
    ///    60 個 token 全被思考吃掉、回答是空的、`finish_reason=length`）。
    ///    所以開了思考（預設 `high`）時這個值要留得夠大 —— 5000 大約是
    ///    「一輪思考 ＋ 一份完整的週報」。
    /// </summary>
    public int MaxOutputTokens { get; set; } = 5000;

    /// <summary>單批訊息最多幾個 token；超過就分批（map）再彙整（reduce）。</summary>
    public int BatchTokens { get; set; } = 24_000;

    /// <summary>摘要用哪個模型（留白＝跟聊天同一個）。</summary>
    public string? Model { get; set; }

    /// <summary>
    /// 摘要那次呼叫的思考強度（`off`／`low`／`medium`／`high`／`auto`）。
    ///
    /// 為什麼是「那一次」而不是全部：思考讓**每一次**呼叫都變貴（見
    /// <see cref="ReasoningOffHandler"/> 的量測），而摘要一週只跑一次、要讀一整週的內容，
    /// 正好是「值得多想一下」的那種呼叫。所以預設 `high`，而且只給最後那次彙整用。
    /// </summary>
    public string Reasoning { get; set; } = "high";

    /// <summary>
    /// 每次執行最多讀幾則訊息（`0` = 不限，用 <see cref="SafetyMaxMessages"/>）。
    ///
    /// ⚠️ 就算設 0（不限），仍然有 <see cref="SafetyMaxMessages"/> 這道**內部安全上限** ——
    /// 沒有的話，一個爆量頻道會讓一次執行讀到天荒地老、把整週額度燒光。
    /// 超過安全上限時**游標只推進到讀到的地方**，剩下的下一輪再處理（不會漏訊息）。
    /// </summary>
    public int MaxMessagesPerRun { get; set; }

    /// <summary>內部安全上限（見 <see cref="MaxMessagesPerRun"/>）。</summary>
    public int SafetyMaxMessages { get; set; } = 10_000;

    /// <summary>排程檢查間隔（秒）；每分鐘問一次「有沒有人該跑了」很便宜。</summary>
    public int SchedulerIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// 排程時間用哪個時區解讀（分鐘；台灣 `+480`）。
    ///
    /// 為什麼一定要明講時區：主機在 Render 上是 **UTC**，
    /// 而「每週一早上九點」講的是**台灣時間** —— 用主機的本地時間算，
    /// 使用者看到的摘要會固定在早上五點出現（而且看起來像 bug）。
    /// </summary>
    public int TimeZoneOffsetMinutes { get; set; } = 480;

    /// <summary>沒有新訊息時要不要也發一則「這週沒有新訊息」（預設不發，省頻道版面）。</summary>
    public bool PostEmpty { get; set; }

    /// <summary>失敗之後隔多久再試（分鐘）。</summary>
    public int RetryMinutes { get; set; } = 15;

    public TimeSpan SchedulerInterval
        => TimeSpan.FromSeconds(Math.Clamp(SchedulerIntervalSeconds, 15, 3600));

    public TimeSpan ZoneOffset
        => TimeSpan.FromMinutes(Math.Clamp(TimeZoneOffsetMinutes, -720, 840));

    public TimeSpan RetryDelay
        => TimeSpan.FromMinutes(Math.Clamp(RetryMinutes, 1, 24 * 60));

    /// <summary>實際要讀幾則（設 0＝不限時用安全上限）。</summary>
    public int EffectiveMaxMessages
        => MaxMessagesPerRun > 0
            ? Math.Min(MaxMessagesPerRun, Math.Max(1, SafetyMaxMessages))
            : Math.Max(1, SafetyMaxMessages);

    /// <summary>思考強度是不是「跟主設定一樣」（一樣就不必多建一條連線）。</summary>
    public bool NeedsOwnConnection(LlmOptions main)
        => !string.Equals(Normalize(Reasoning), Normalize(main.Reasoning), StringComparison.Ordinal)
           || (!string.IsNullOrWhiteSpace(Model)
               && !string.Equals(Model!.Trim(), main.Model, StringComparison.Ordinal));

    public string Describe()
    {
        var model = string.IsNullOrWhiteSpace(Model) ? "跟聊天同一個" : Model!;
        var cap = MaxMessagesPerRun > 0
            ? $"{MaxMessagesPerRun:N0} 則"
            : $"不限（安全上限 {SafetyMaxMessages:N0} 則）";

        return $"每週摘要：{(Enabled ? "啟用" : "停用")}｜模型 {model}｜思考 {Reasoning}" +
               $"｜一次最多 {cap}｜最多 @ {MaxMentions} 人" +
               $"｜排程時區 UTC{(ZoneOffset >= TimeSpan.Zero ? "+" : "")}{ZoneOffset.TotalHours:0.#}";
    }

    private static string Normalize(string? reasoning)
        => (reasoning ?? "off").Trim().ToLowerInvariant();
}

/// <summary>
/// 每週摘要的排程（**用 <see cref="DigestOptions.ZoneOffset"/> 的時區解讀**）。
///
/// 只記「星期幾 ＋ 幾點幾分」，不記日期 —— 每一輪的下一次時間都是算出來的，
/// 這樣就不必存一個會過期的時間，也不會出現「跑完之後忘記算下一次」的狀態。
/// </summary>
public sealed record DigestSchedule(DayOfWeek Day, int Hour, int Minute)
{
    public static DigestSchedule Default { get; } = new(DayOfWeek.Monday, 9, 0);

    public string Describe() => $"每週{DayName(Day)} {Hour:00}:{Minute:00}";

    public static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日"
    };

    /// <summary>下一次該跑的時間（回傳 UTC）。</summary>
    public DateTimeOffset NextRun(DateTimeOffset now, TimeSpan zoneOffset)
    {
        var local = now.ToOffset(zoneOffset);
        var hour = Math.Clamp(Hour, 0, 23);
        var minute = Math.Clamp(Minute, 0, 59);

        var today = new DateTimeOffset(local.Year, local.Month, local.Day, hour, minute, 0, zoneOffset);
        var delta = ((int)Day - (int)local.DayOfWeek + 7) % 7;
        var candidate = today.AddDays(delta);

        // 今天就是那天、但時間已經過了（或正好現在）→ 下週同一時間
        if (candidate <= now) candidate = candidate.AddDays(7);

        return candidate.ToUniversalTime();
    }

    /// <summary>
    /// 解析使用者打的排程：`週一 09:00`／`星期一 9:00`／`mon 09:00`／`1 09:00`／`09:00`（＝每週一 09:00）。
    ///
    /// 為什麼要寬鬆：這是給人打的（`/digest enable schedule:…`），
    /// 打字方式一定會有好幾種，而**猜錯的代價只是時間不對**（看得出來、改一下就好），
    /// 所以認得越多種越好。真的看不懂時**回錯誤**，不要偷偷用預設值 ——
    /// 那會讓使用者以為設好了。
    /// </summary>
    public static bool TryParse(string? text, out DigestSchedule schedule, out string error)
    {
        schedule = Default;
        error = "";

        var raw = (text ?? "").Trim();

        if (raw.Length == 0) return true;   // 沒給 → 用預設（每週一 09:00）

        var parts = raw
            .Replace("星期", "週", StringComparison.Ordinal)
            .Replace("禮拜", "週", StringComparison.Ordinal)
            .Replace("週", " ", StringComparison.Ordinal)
            .Split([' ', '\t', ',', '，'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            error = "看不懂這個時間，請用「週一 09:00」這種寫法。";
            return false;
        }

        DayOfWeek? day = null;
        var hour = 9;
        var minute = 0;

        foreach (var part in parts)
        {
            if (IsDay(part, out var parsedDay))
            {
                day = parsedDay;
                continue;
            }

            if (TryParseClock(part, out var h, out var m))
            {
                hour = h;
                minute = m;
                continue;
            }

            error = $"看不懂「{part}」，請用「週一 09:00」這種寫法（也可以只寫 09:00）。";
            return false;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            error = "時間的範圍不對（小時 0-23、分鐘 0-59）。";
            return false;
        }

        schedule = new DigestSchedule(day ?? DayOfWeek.Monday, hour, minute);
        return true;
    }

    private static bool IsDay(string token, out DayOfWeek day)
    {
        day = DayOfWeek.Monday;

        if (token.Length == 0) return false;

        switch (token.ToLowerInvariant())
        {
            case "一" or "1" or "mon" or "monday": day = DayOfWeek.Monday; return true;
            case "二" or "2" or "tue" or "tues" or "tuesday": day = DayOfWeek.Tuesday; return true;
            case "三" or "3" or "wed" or "wednesday": day = DayOfWeek.Wednesday; return true;
            case "四" or "4" or "thu" or "thur" or "thurs" or "thursday": day = DayOfWeek.Thursday; return true;
            case "五" or "5" or "fri" or "friday": day = DayOfWeek.Friday; return true;
            case "六" or "6" or "sat" or "saturday": day = DayOfWeek.Saturday; return true;
            case "日" or "天" or "7" or "0" or "sun" or "sunday": day = DayOfWeek.Sunday; return true;
            default: return false;
        }
    }

    /// <summary>`09:00`／`9:00`／`0900`／`9` 都算。</summary>
    private static bool TryParseClock(string token, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;

        var text = token.Trim();

        if (text.Contains(':'))
        {
            var halves = text.Split(':', StringSplitOptions.TrimEntries);

            if (halves.Length == 2
                && int.TryParse(halves[0], out hour)
                && int.TryParse(halves[1], out minute))
                return true;

            return false;
        }

        if (!int.TryParse(text, out var digits)) return false;

        // 4 位數字當成 HHmm（0900），其他當成「幾點」
        if (digits is >= 100 and <= 2359)
        {
            hour = digits / 100;
            minute = digits % 100;
            return true;
        }

        hour = digits;
        minute = 0;
        return true;
    }
}
