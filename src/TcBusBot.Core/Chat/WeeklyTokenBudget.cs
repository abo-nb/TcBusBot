using System.Text.Json;
using System.Text.Json.Serialization;
using TcBusBot.Core.Storage;

namespace TcBusBot.Core.Chat;

/// <summary>一週的用量（會被持久化，所以欄位刻意少而穩定）。</summary>
public sealed record WeeklyUsage(
    [property: JsonPropertyName("week")] string WeekKey,
    [property: JsonPropertyName("total")] long TotalTokens,
    [property: JsonPropertyName("input")] long InputTokens,
    [property: JsonPropertyName("output")] long OutputTokens,
    [property: JsonPropertyName("calls")] int Calls,
    [property: JsonPropertyName("refused")] int Refusals,
    [property: JsonPropertyName("guilds")] Dictionary<string, long> ByGuild,
    [property: JsonPropertyName("updated")] DateTimeOffset UpdatedAt)
{
    public static WeeklyUsage Empty(string weekKey, DateTimeOffset now)
        => new(weekKey, 0, 0, 0, 0, 0, new Dictionary<string, long>(StringComparer.Ordinal), now);
}

/// <summary>能不能再花錢。</summary>
public sealed record BudgetCheck(
    bool Allowed,
    long Used,
    long Limit,
    long Remaining,
    int EstimatedInputTokens,
    DateTimeOffset ResetAt,
    string Reason)
{
    public bool Unlimited => Limit <= 0;

    public string Describe()
        => Unlimited
            ? $"不限額度（已用 {Used:N0} tokens／{ResetAt.ToLocalTime():MM-dd HH:mm} 重置）"
            : $"{Used:N0}／{Limit:N0} tokens（剩 {Remaining:N0}，{ResetAt.ToLocalTime():MM-dd HH:mm} 重置）";
}

/// <summary>
/// 每週 token 預算（**全域一份**：整個 Bot 共用）。
///
/// 為什麼是全域而不是每個伺服器：使用者選的。好處是「這個月最多花多少」一眼看得出來；
/// 代價是任何一個伺服器吵起來會把大家的額度用掉 —— 所以另外記了
/// <see cref="WeeklyUsage.ByGuild"/>，`/ai status` 看得出來是誰在花。
///
/// 重置點是 **UTC 週一 00:00**（固定不動，不需要使用者記得「上次重置是什麼時候」）。
///
/// ⚠️ 上限是**軟性**的：最後一次呼叫在送出前可能還在額度內，
/// 但回覆的輸出會讓總量稍微超過（最多超過一次回覆的量，也就是 MaxOutputTokens）。
/// 要在送出去之前就精準知道輸出量是不可能的。
/// </summary>
public sealed class WeeklyTokenBudget
{
    /// <summary>持久化用的鍵名（在共用的 blob 區裡）。</summary>
    public const string BlobKey = "llm_weekly_usage";

    private readonly LlmOptions _options;
    private readonly ILlmStateStore? _store;
    private readonly object _gate = new();

    private WeeklyUsage _usage;
    private DateTimeOffset _lastSavedAt = DateTimeOffset.MinValue;
    private bool _dirty;

    /// <summary>寫檔的最小間隔（每次回覆都寫檔太浪費；反正是「錢的紀錄」，掉幾秒可以接受）。</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);

    public WeeklyTokenBudget(LlmOptions options, ILlmStateStore? store = null, DateTimeOffset? now = null)
    {
        _options = options;
        _store = store;

        var current = now ?? DateTimeOffset.UtcNow;
        _usage = Load(current) ?? WeeklyUsage.Empty(WeekKey(current), current);
    }

    public long Limit => _options.WeeklyTokenLimit;

    public WeeklyUsage Usage
    {
        get { lock (_gate) return _usage; }
    }

    /// <summary>這一週的識別碼：用「週一那天的日期」，比 ISO 週數好讀。</summary>
    public static string WeekKey(DateTimeOffset now) => WeekStartUtc(now).ToString("yyyy-MM-dd");

    /// <summary>UTC 週一 00:00。</summary>
    public static DateTimeOffset WeekStartUtc(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var day = utc.Date;
        var sinceMonday = ((int)day.DayOfWeek + 6) % 7;   // 週一 = 0
        return new DateTimeOffset(day.AddDays(-sinceMonday), TimeSpan.Zero);
    }

    public static DateTimeOffset ResetAt(DateTimeOffset now) => WeekStartUtc(now).AddDays(7);

    /// <summary>
    /// 這次呼叫能不能送出去？
    ///
    /// 判斷式刻意包含「輸入量」：已經用到上限了就不准再送，
    /// 而不是送出去之後才發現超額（那樣額度就沒有意義了）。
    /// </summary>
    public BudgetCheck Check(int estimatedInputTokens, DateTimeOffset now)
    {
        lock (_gate)
        {
            RolloverIfNeeded(now);

            if (Limit <= 0)
                return new BudgetCheck(true, _usage.TotalTokens, 0, long.MaxValue,
                                       estimatedInputTokens, ResetAt(now), "未設定上限");

            var remaining = Math.Max(0, Limit - _usage.TotalTokens);
            var wouldUse = _usage.TotalTokens + estimatedInputTokens;

            if (wouldUse >= Limit)
            {
                return new BudgetCheck(
                    false, _usage.TotalTokens, Limit, remaining, estimatedInputTokens, ResetAt(now),
                    $"這一週的額度用完了（已用 {_usage.TotalTokens:N0}／{Limit:N0} tokens，" +
                    $"這次還要約 {estimatedInputTokens:N0}）");
            }

            return new BudgetCheck(true, _usage.TotalTokens, Limit, remaining,
                                   estimatedInputTokens, ResetAt(now), "額度足夠");
        }
    }

    /// <summary>記一次用量（呼叫成功之後）。</summary>
    public void Record(int inputTokens, int outputTokens, ulong guildId, DateTimeOffset now)
    {
        lock (_gate)
        {
            RolloverIfNeeded(now);

            var key = guildId == 0 ? "dm" : guildId.ToString();

            _usage = _usage with
            {
                TotalTokens = _usage.TotalTokens + Math.Max(0, inputTokens) + Math.Max(0, outputTokens),
                InputTokens = _usage.InputTokens + Math.Max(0, inputTokens),
                OutputTokens = _usage.OutputTokens + Math.Max(0, outputTokens),
                Calls = _usage.Calls + 1,
                UpdatedAt = now,
                ByGuild = Bump(_usage.ByGuild, key, Math.Max(0, inputTokens) + Math.Max(0, outputTokens))
            };

            _dirty = true;
            SaveLocked(now, force: false);
        }
    }

    /// <summary>記一次「因為額度用完而被擋下來」（顯示用，不代表花錢）。</summary>
    public void RecordRefusal(DateTimeOffset now)
    {
        lock (_gate)
        {
            RolloverIfNeeded(now);
            _usage = _usage with { Refusals = _usage.Refusals + 1 };
            _dirty = true;
        }
    }

    /// <summary>重置（測試或 `/ai reset-usage` 用）。</summary>
    public void Reset(DateTimeOffset now)
    {
        lock (_gate)
        {
            _usage = WeeklyUsage.Empty(WeekKey(now), now);
            _dirty = true;
            SaveLocked(now, force: true);
        }
    }

    /// <summary>把還沒寫下去的用量寫進儲存區（關機時呼叫）。</summary>
    public void Flush(DateTimeOffset now)
    {
        lock (_gate) SaveLocked(now, force: true);
    }

    public string Describe()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var reset = ResetAt(now);
            var left = Limit <= 0 ? "不限" : $"剩 {Math.Max(0, Limit - _usage.TotalTokens):N0}";

            return $"本週（{_usage.WeekKey} 起）{_usage.TotalTokens:N0} tokens／" +
                   $"{_usage.Calls} 次呼叫／被擋 {_usage.Refusals} 次（{left}，" +
                   $"{reset.ToLocalTime():MM-dd HH:mm} 重置）";
        }
    }

    public string Serialize()
    {
        lock (_gate) return JsonSerializer.Serialize(_usage, Json);
    }

    // ── 內部 ─────────────────────────────────────────────

    private WeeklyUsage? Load(DateTimeOffset now)
    {
        if (_store is null) return null;

        try
        {
            var json = _store.GetBlob(BlobKey);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var usage = JsonSerializer.Deserialize<WeeklyUsage>(json, Json);
            if (usage is null) return null;

            // 上一個星期的紀錄 → 直接當成新的開始（不是錯誤，不用警告）
            if (usage.WeekKey != WeekKey(now)) return null;

            return usage with
            {
                ByGuild = usage.ByGuild is null
                    ? new Dictionary<string, long>(StringComparer.Ordinal)
                    : new Dictionary<string, long>(usage.ByGuild, StringComparer.Ordinal)
            };
        }
        catch (Exception ex)
        {
            BotLog.Warn($"[llm] 讀不到上週的用量紀錄（{ex.GetType().Name}），這一週從 0 開始");
            return null;
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private void RolloverIfNeeded(DateTimeOffset now)
    {
        var key = WeekKey(now);
        if (_usage.WeekKey == key) return;

        BotLog.Warn($"[llm] 新的星期（{key}）→ 每週 token 用量歸零" +
                    $"（上週 {_usage.TotalTokens:N0} tokens／{_usage.Calls} 次）");

        _usage = WeeklyUsage.Empty(key, now);
        _dirty = true;
        SaveLocked(now, force: true);
    }

    private void SaveLocked(DateTimeOffset now, bool force)
    {
        if (!_dirty || _store is null) return;
        if (!force && now - _lastSavedAt < SaveInterval) return;

        try
        {
            _store.SetBlob(BlobKey, JsonSerializer.Serialize(_usage, Json));
            _lastSavedAt = now;
            _dirty = false;
        }
        catch (Exception ex)
        {
            // 寫不進去不該讓聊天失敗：額度紀錄掉一次不影響功能，下次再寫
            BotLog.Warn($"[llm] 用量紀錄寫入失敗（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    private static Dictionary<string, long> Bump(
        IReadOnlyDictionary<string, long> source, string key, long delta)
    {
        var copy = new Dictionary<string, long>(source, StringComparer.Ordinal);
        copy[key] = copy.TryGetValue(key, out var old) ? old + delta : delta;
        return copy;
    }
}
