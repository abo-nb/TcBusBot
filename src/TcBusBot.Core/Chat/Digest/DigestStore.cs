using System.Text.Json;
using TcBusBot.Core.Chat;

namespace TcBusBot.Core.Chat.Digest;

/// <summary>
/// 每週摘要的**持久化**：每個頻道的設定（含游標）＋ 歷次摘要的長期歸檔。
///
/// ── 為什麼是「另一份」儲存 ─────────────────────────────
/// 這一塊跟對話記憶、每週用量是**不同的生命週期**：
///   * 對話記憶是暫存的（重啟就沒了、TTL 到就清）
///   * 每週用量是每週歸零的計數
///   * 這一塊是**永久**的（使用者要的：「永久保留」、「資料庫再開一個表存」）
/// 所以用自己的 blob key 存，不會被別的清理邏輯影響到，也不會混在用量統計裡。
///
/// ⚠️ 兩個 key 各自是一份完整的 JSON 文件（在 MongoDB 上是 `blobs` 集合裡的兩份文件）：
///    * <see cref="ConfigBlobKey"/>：`{ "channelId": {...設定...} }`（含游標）
///    * <see cref="ArchiveBlobKey"/>：`{ "channelId": [ ...歷次摘要... ] }`（永久，不裁切）
///
/// 每次寫入都是整份重寫 —— 一週只寫幾次、單一頻道一年大約幾十 KB，這樣最簡單也最不會壞。
/// </summary>
public sealed class DigestStore
{
    public const string ConfigBlobKey = "channel_digest_configs";
    public const string ArchiveBlobKey = "channel_digest_archive";

    private readonly object _gate = new();
    private readonly ILlmStateStore? _store;

    private Dictionary<ulong, ChannelDigestConfig> _configs = [];
    private Dictionary<ulong, List<DigestRecord>> _archive = [];

    public DigestStore(ILlmStateStore? store = null)
    {
        _store = store;
        Load();
    }

    /// <summary>讀取失敗時的警告（給啟動訊息看；不影響運作）。</summary>
    public string? LoadWarning { get; private set; }

    // ── 設定 ─────────────────────────────────────────────

    public IReadOnlyList<ChannelDigestConfig> All()
    {
        lock (_gate) return _configs.Values.OrderBy(c => c.ChannelId).ToList();
    }

    public IReadOnlyList<ChannelDigestConfig> Enabled()
    {
        lock (_gate) return _configs.Values.Where(c => c.Enabled).OrderBy(c => c.NextRunAt).ToList();
    }

    public ChannelDigestConfig? Config(ulong channelId)
    {
        lock (_gate) return _configs.TryGetValue(channelId, out var config) ? config : null;
    }

    /// <summary>現在有哪些頻道該跑了（排程器每一分鐘問一次）。</summary>
    public IReadOnlyList<ChannelDigestConfig> Due(DateTimeOffset now)
    {
        lock (_gate)
            return _configs.Values
                .Where(c => c.Enabled)
                .Where(c => c.NextRunAt is null || c.NextRunAt <= now)
                .OrderBy(c => c.NextRunAt ?? DateTimeOffset.MinValue)
                .ToList();
    }

    /// <summary>
    /// 啟用某個頻道的每週摘要。
    ///
    /// <paramref name="cursorMessageId"/> 是「第一次要從哪裡開始」：
    ///   * null → 從現在開始（不回顧舊訊息）
    ///   * 有值 → 第一次執行會從那之後的訊息開始（管理員選的回顧範圍）
    /// </summary>
    public ChannelDigestConfig Enable(
        ulong guildId,
        ulong channelId,
        ulong by,
        DigestSchedule schedule,
        DateTimeOffset now,
        TimeSpan zoneOffset,
        string? cursorMessageId = null,
        DateTimeOffset? cursorAt = null)
    {
        lock (_gate)
        {
            var config = new ChannelDigestConfig(
                ChannelId: channelId,
                GuildId: guildId,
                Day: schedule.Day,
                Hour: schedule.Hour,
                Minute: schedule.Minute,
                Enabled: true,
                EnabledBy: by,
                EnabledAt: now,
                CursorMessageId: cursorMessageId,
                CursorAt: cursorAt,
                LastRunAt: null,
                NextRunAt: schedule.NextRun(now, zoneOffset),
                RunCount: 0,
                LastError: null);

            _configs[channelId] = config;
            SaveConfigs();
            return config;
        }
    }

    /// <summary>停用（**不刪掉游標與歸檔** —— 之後再啟用會接著原來的進度，不會重複算）。</summary>
    public ChannelDigestConfig? Disable(ulong channelId)
    {
        lock (_gate)
        {
            if (!_configs.TryGetValue(channelId, out var config)) return null;

            var updated = config with { Enabled = false, NextRunAt = null };
            _configs[channelId] = updated;
            SaveConfigs();
            return updated;
        }
    }

    public ChannelDigestConfig? SetSchedule(
        ulong channelId, DigestSchedule schedule, DateTimeOffset now, TimeSpan zoneOffset)
    {
        lock (_gate)
        {
            if (!_configs.TryGetValue(channelId, out var config)) return null;

            var updated = config.WithSchedule(schedule, schedule.NextRun(now, zoneOffset));
            _configs[channelId] = updated;
            SaveConfigs();
            return updated;
        }
    }

    /// <summary>
    /// 一次執行成功之後：推進游標、記下跑了第幾次、算下一次的時間。
    ///
    /// ⚠️ 游標**只在成功之後**才推進（見 <see cref="MarkFailed"/> 的說明）——
    /// 先推進再跑，一旦失敗那些訊息就永遠不會被摘要到了。
    /// </summary>
    public ChannelDigestConfig? MarkRun(
        ulong channelId,
        string? cursorMessageId,
        DateTimeOffset? cursorAt,
        DateTimeOffset now,
        TimeSpan zoneOffset)
    {
        lock (_gate)
        {
            if (!_configs.TryGetValue(channelId, out var config)) return null;

            var updated = config with
            {
                CursorMessageId = cursorMessageId ?? config.CursorMessageId,
                CursorAt = cursorAt ?? config.CursorAt,
                LastRunAt = now,
                NextRunAt = config.Schedule.NextRun(now, zoneOffset),
                RunCount = config.RunCount + 1,
                LastError = null
            };

            _configs[channelId] = updated;
            SaveConfigs();
            return updated;
        }
    }

    /// <summary>
    /// 失敗：**不動游標**，只把下一次往後推一點（避免每分鐘重試 + 每分鐘發一次錯誤訊息）。
    /// 失敗原因會留在設定裡，`/digest status` 看得到。
    /// </summary>
    public ChannelDigestConfig? MarkFailed(ulong channelId, string error, DateTimeOffset now, TimeSpan retry)
    {
        lock (_gate)
        {
            if (!_configs.TryGetValue(channelId, out var config)) return null;

            var updated = config with
            {
                LastError = $"{now.ToLocalTime():MM-dd HH:mm} {error}",
                NextRunAt = now + retry
            };

            _configs[channelId] = updated;
            SaveConfigs();
            return updated;
        }
    }

    // ── 歸檔（永久）─────────────────────────────────────

    public void Append(DigestRecord record)
    {
        lock (_gate)
        {
            if (!_archive.TryGetValue(record.ChannelId, out var list))
                _archive[record.ChannelId] = list = [];

            list.Add(record);
            SaveArchive();
        }
    }

    /// <summary>最近幾筆（新的在前）。</summary>
    public IReadOnlyList<DigestRecord> Archive(ulong channelId, int limit = 5)
    {
        lock (_gate)
        {
            if (!_archive.TryGetValue(channelId, out var list)) return [];

            return list
                .OrderByDescending(r => r.At)
                .Take(Math.Max(1, limit))
                .ToList();
        }
    }

    /// <summary>最後一筆（跨週事項就是從這裡帶下去的）。</summary>
    public DigestRecord? Latest(ulong channelId)
    {
        lock (_gate)
        {
            if (!_archive.TryGetValue(channelId, out var list) || list.Count == 0) return null;

            return list.OrderByDescending(r => r.At).First();
        }
    }

    public int ArchiveCount(ulong channelId)
    {
        lock (_gate) return _archive.TryGetValue(channelId, out var list) ? list.Count : 0;
    }

    public int TotalArchived
    {
        get { lock (_gate) return _archive.Values.Sum(v => v.Count); }
    }

    // ── 說明 ─────────────────────────────────────────────

    public string Describe()
    {
        lock (_gate)
        {
            if (_configs.Count == 0) return "每週摘要：沒有設定任何頻道";

            var enabled = _configs.Values.Count(c => c.Enabled);

            return $"每週摘要：{enabled}/{_configs.Count} 個頻道啟用" +
                   $"｜歸檔 {_archive.Values.Sum(v => v.Count)} 筆（{_archive.Count} 個頻道）";
        }
    }

    public string DescribeChannel(ulong channelId)
    {
        lock (_gate)
        {
            if (!_configs.TryGetValue(channelId, out var config))
                return "這個頻道沒有設定每週摘要。";

            var list = _archive.TryGetValue(channelId, out var records) ? records : [];
            var latest = list.OrderByDescending(r => r.At).FirstOrDefault();

            var cursor = config.CursorAt is null
                ? "（還沒有游標：第一次執行會從你選的起點開始）"
                : $"整理到 {config.CursorAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}" +
                  $"（訊息 `{config.CursorMessageId}`）";

            return $"每週摘要：{(config.Enabled ? "✅ 啟用" : "⏸ 停用")}｜{config.Schedule.Describe()}" +
                   $"\n游標：{cursor}" +
                   $"\n已經跑過 {config.RunCount} 次｜歸檔 {list.Count} 筆" +
                   (latest is null ? "" : $"｜最後一次 {latest.At.ToLocalTime():yyyy-MM-dd HH:mm}（{latest.MessageCount} 則訊息）") +
                   (config.NextRunAt is null ? "" : $"\n下一次：{config.NextRunAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}") +
                   (config.LastError is null ? "" : $"\n⚠️ 上次失敗：{config.LastError}");
        }
    }

    // ── 存取 ─────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private void Load()
    {
        if (_store is null) return;

        try
        {
            var configs = _store.GetBlob(ConfigBlobKey);

            if (!string.IsNullOrWhiteSpace(configs))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, ChannelDigestConfig>>(configs!, Json);

                if (parsed is not null)
                {
                    _configs = parsed
                        .Where(kv => ulong.TryParse(kv.Key, out _))
                        .ToDictionary(kv => ulong.Parse(kv.Key), kv => kv.Value);
                }
            }

            var archive = _store.GetBlob(ArchiveBlobKey);

            if (!string.IsNullOrWhiteSpace(archive))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, List<DigestRecord>>>(archive!, Json);

                if (parsed is not null)
                {
                    _archive = parsed
                        .Where(kv => ulong.TryParse(kv.Key, out _))
                        .ToDictionary(kv => ulong.Parse(kv.Key), kv => kv.Value);
                }
            }
        }
        catch (Exception ex)
        {
            // 讀不到就從空的開始（下一個成功的執行會把它寫回去）
            LoadWarning = $"[摘要] 讀不到每週摘要的設定／歸檔（{ex.GetType().Name}），先從空的開始";
            Console.WriteLine(LoadWarning);
            _configs = [];
            _archive = [];
        }
    }

    private void SaveConfigs()
        => Save(ConfigBlobKey, _configs.ToDictionary(
            kv => kv.Key.ToString(),
            kv => kv.Value));

    private void SaveArchive()
        => Save(ArchiveBlobKey, _archive.ToDictionary(
            kv => kv.Key.ToString(),
            kv => kv.Value));

    private void Save<T>(string key, T value)
    {
        if (_store is null) return;

        try
        {
            _store.SetBlob(key, JsonSerializer.Serialize(value, Json));
        }
        catch (Exception ex)
        {
            // 寫不進去不該讓摘要流程失敗（下一次執行會再試）
            Console.WriteLine($"[摘要] 寫入 {key} 失敗（{ex.GetType().Name}）：{ex.Message}");
        }
    }
}
