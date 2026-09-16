using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcBusBot.Core.Storage;

/// <summary>訂閱組裡儲存的一條路線（用 RouteUid + Direction 當鍵，重載時會對照最新資料）。</summary>
public sealed record SavedRoute(
    [property: JsonPropertyName("u")] string RouteUid,
    [property: JsonPropertyName("d")] int Direction,
    [property: JsonPropertyName("n")] string RouteName);

/// <summary>訂閱組裡的一段行程：從 A 到 B，以及這一段要訂閱的路線。</summary>
public sealed record SavedLeg(
    [property: JsonPropertyName("o")] SavedTarget Origin,
    [property: JsonPropertyName("d")] SavedTarget Destination,
    [property: JsonPropertyName("r")] IReadOnlyList<SavedRoute> Routes);

/// <summary>訂閱組的完整內容（以 JSON 存在 SQLite 的 TEXT 欄位）。</summary>
public sealed record SavedGroupPayload(
    [property: JsonPropertyName("legs")] IReadOnlyList<SavedLeg> Legs,
    [property: JsonPropertyName("notify")] int NotifyMinutes)
{
    [JsonIgnore] public int LegCount => Legs.Count;

    [JsonIgnore] public int RouteCount => Legs.Sum(l => l.Routes.Count);

    /// <summary>第一段行程的起訖（單段時就是全部；多段時請逐段處理）。</summary>
    [JsonIgnore] public SavedTarget Origin => Legs[0].Origin;

    [JsonIgnore] public SavedTarget Destination => Legs[0].Destination;

    /// <summary>「臺中車站 → 靜宜大學」或「共 3 段行程」。</summary>
    public string DescribeRoute()
        => Legs.Count == 1
            ? $"{Origin.DisplayName} → {Destination.DisplayName}"
            : $"{Origin.DisplayName} → {Destination.DisplayName} 等 {Legs.Count} 段行程";
}

/// <summary>
/// 舊格式（單段）→ 新格式（多段）的相容讀取。
///
/// 為什麼要能讀舊資料：使用者已經存好的訂閱組不該因為程式改版就消失。
/// 舊的 JSON 長這樣：<c>{"origin":{…},"dest":{…},"routes":[…],"notify":10}</c>
/// —— 讀進來就變成「只有一段行程」。
/// </summary>
public sealed class SavedGroupPayloadConverter : JsonConverter<SavedGroupPayload>
{
    public override SavedGroupPayload Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var notify = root.TryGetProperty("notify", out var n) && n.TryGetInt32(out var nm) ? nm : 10;

        if (root.TryGetProperty("legs", out var legs) && legs.ValueKind == JsonValueKind.Array)
        {
            var parsed = legs.Deserialize<List<SavedLeg>>(options) ?? new List<SavedLeg>();
            return new SavedGroupPayload(parsed, notify);
        }

        var origin = ReadTarget(root, "origin");
        var destination = ReadTarget(root, "dest");
        var routes = root.TryGetProperty("routes", out var r)
            ? r.Deserialize<List<SavedRoute>>(options) ?? new List<SavedRoute>()
            : new List<SavedRoute>();

        return new SavedGroupPayload([new SavedLeg(origin, destination, routes)], notify);
    }

    private static SavedTarget ReadTarget(JsonElement root, string name)
        => root.TryGetProperty(name, out var el)
            ? el.Deserialize<SavedTarget>() ?? new SavedTarget("", Array.Empty<string>())
            : new SavedTarget("", Array.Empty<string>());

    public override void Write(Utf8JsonWriter writer, SavedGroupPayload value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("legs");
        JsonSerializer.Serialize(writer, value.Legs, options);
        writer.WriteNumber("notify", value.NotifyMinutes);
        writer.WriteEndObject();
    }
}

public sealed record SavedTarget(
    [property: JsonPropertyName("name")] string DisplayName,
    [property: JsonPropertyName("uids")] IReadOnlyList<string> StopUids);

public static class SavedGroupPayloadFactory
{
    /// <summary>
    /// 由一組訂閱建立可儲存的 payload。
    /// 放在 Core 讓 Bot 與離線驗證共用同一份邏輯（避免兩邊不一致）。
    /// </summary>
    public static SavedGroupPayload FromSubscriptions(
        SavedTarget origin, SavedTarget destination,
        IEnumerable<(string RouteUid, int Direction, string RouteName)> routes,
        int notifyMinutes)
        => new([new SavedLeg(origin, destination, Clean(routes))], notifyMinutes);

    /// <summary>
    /// 把多個訂閱組合併成一個（**多段行程**）。
    ///
    /// 起訖候選站牌相同的段落會再合併路線，所以「同一條通勤路線存了兩次」不會變成兩段。
    /// 提前通知時間預設取**最大**的那個（= 提醒最早）：晚通知會讓人錯過公車，
    /// 早通知只是多一則訊息。合併時可以在 Modal 裡自己改。
    /// </summary>
    public static SavedGroupPayload Merge(
        IEnumerable<SavedGroupPayload> payloads, int? notifyMinutes = null)
    {
        var list = payloads.ToList();
        var legs = new List<SavedLeg>();

        foreach (var leg in list.SelectMany(p => p.Legs))
        {
            var index = legs.FindIndex(l => SameTarget(l.Origin, leg.Origin)
                                            && SameTarget(l.Destination, leg.Destination));

            if (index < 0)
            {
                legs.Add(new SavedLeg(leg.Origin, leg.Destination, Clean(leg.Routes.Select(AsTuple))));
                continue;
            }

            var merged = legs[index].Routes.Concat(leg.Routes)
                                   .DistinctBy(r => (r.RouteUid, r.Direction))
                                   .ToList();

            legs[index] = legs[index] with { Routes = merged };
        }

        var notify = notifyMinutes
                     ?? (list.Count == 0 ? 10 : list.Max(p => p.NotifyMinutes));

        return new SavedGroupPayload(legs, notify);
    }

    private static bool SameTarget(SavedTarget a, SavedTarget b)
        => string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)
           && a.StopUids.OrderBy(x => x, StringComparer.Ordinal)
              .SequenceEqual(b.StopUids.OrderBy(x => x, StringComparer.Ordinal));

    private static (string, int, string) AsTuple(SavedRoute r) => (r.RouteUid, r.Direction, r.RouteName);

    private static List<SavedRoute> Clean(IEnumerable<(string RouteUid, int Direction, string RouteName)> routes)
        => routes.DistinctBy(r => (r.RouteUid, r.Direction))
                 .Select(r => new SavedRoute(r.RouteUid, r.Direction, r.RouteName))
                 .ToList();
}

/// <summary>清單顯示用的訂閱組摘要。</summary>
public sealed record SavedGroup(
    long Id,
    ulong UserId,
    string Name,
    string OriginName,
    string DestinationName,
    int RouteCount,
    int NotifyMinutes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    int UseCount)
{
    public string Describe()
        => $"{OriginName} → {DestinationName}（{RouteCount} 條路線，提前 {NotifyMinutes} 分）";
}

/// <summary>儲存失敗的原因（給 UI 顯示友善訊息）。</summary>
public enum SaveGroupResult
{
    Created,
    Updated,
    NameEmpty,
    NameTooLong,
    TooManyGroups,
    RouteCountMismatch,
    TooManyLegs,
    Failed
}

/// <summary>
/// 訂閱組的持久化儲存。
///
/// 為什麼要持久化：訂閱本身是記憶體（重啟就消失，這是刻意的），
/// 但「訂閱組」是使用者刻意整理出來的範本，重啟後還要能一鍵套用。
///
/// **四種模式，自動挑最好的那個**：
///
/// | 優先序 | 模式 | 什麼時候用 | 重啟後還在？ | 跨機器共用？ |
/// | --- | --- | --- | --- | --- |
/// | 1 | **MongoDB** | 有給連線字串（伺服器或 Atlas） | ✅ | ✅ 所有平台共用同一份 |
/// | 2 | SQLite | 環境有可用的 SQLite（Windows 內建／Android 系統的） | ✅ | ❌ 各機器一份 |
/// | 3 | 文字檔 | 沒有 SQLite，或資料庫開不起來 | ✅ | ❌（`saved_groups.txt`，一行一筆） |
/// | 4 | 記憶體 | path = `:memory:`，或連檔案都寫不進去 | ❌ | ❌ |
///
/// **MongoDB 排第一的理由**：本機儲存讓每個平台都要各解一次問題
/// （Windows 有內建 SQLite、Android 沒有、Termux 也沒有），資料還會散在好幾台機器上。
/// 給了連線字串之後，手機、電腦、雲端主機看的是同一份訂閱組。
/// 連不上時**不會硬撐**：記錄警告並退回下面的本機儲存，讓 Bot 還能用。
/// </summary>
public sealed class SavedGroupStore : IDisposable, TcBusBot.Core.Chat.ILlmStateStore
{
    public const int MaxGroupsPerUser = 20;
    public const int MaxNameLength = 40;
    public const int MaxRoutesPerGroup = 60;
    public const int MaxLegsPerGroup = 10;

    /// <summary>payload 的序列化設定（含舊格式相容轉換器）；離線工具與測試共用同一份。</summary>
    public static readonly JsonSerializerOptions PayloadJson = new()
    {
        WriteIndented = false,
        // 中文不要被轉成 \uXXXX —— 存進文字檔／MongoDB 之後才看得懂
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new SavedGroupPayloadConverter() }
    };

    /// <summary>實際用哪一種儲存（使用者可以在 UI 上看到）。</summary>
    public enum StorageMode
    {
        /// <summary>有 MongoDB 就用 MongoDB → 否則 SQLite → 否則文字檔 → 最後記憶體。</summary>
        Auto,

        /// <summary>強制文字檔（測試與除錯用）。</summary>
        Text,

        /// <summary>強制 MongoDB；連不上就丟例外，不會偷偷退回本機儲存。</summary>
        Mongo
    }

    private readonly ISavedGroupRepository _repo;

    /// <summary>MongoDB 連不上時的背景重試（只提醒，不自動切換）。</summary>
    private static MongoRecheckLoop? _mongoRecheck;

    /// <summary>是否真的寫進檔案（false = 記憶體模式，重啟就消失）。</summary>
    public bool IsPersistent => _repo.IsPersistent;

    /// <summary>登錄的資料庫路徑（文字檔模式時，文字檔放在同一個目錄）。</summary>
    public string DatabasePath { get; }

    /// <summary>文字檔後備的檔名。</summary>
    public const string TextFileName = "saved_groups.txt";

    public SavedGroupStore(string path, StorageMode mode = StorageMode.Auto)
        : this(path, mongoConnectionString: null, mongoDatabase: null, mode) { }

    /// <param name="mongoConnectionString">MongoDB 連線字串；空 = 不用 MongoDB。</param>
    /// <param name="mongoDatabase">資料庫名稱（預設 `tcbus`）。</param>
    public SavedGroupStore(
        string path, string? mongoConnectionString, string? mongoDatabase,
        StorageMode mode = StorageMode.Auto)
    {
        DatabasePath = path;

        _repo = Open(path, mongoConnectionString, mongoDatabase, mode);
    }

    private static ISavedGroupRepository Open(
        string path, string? mongoConnectionString, string? mongoDatabase, StorageMode mode)
    {
        var textPath = TextPathFor(path);
        var wantsMongo = !string.IsNullOrWhiteSpace(mongoConnectionString);
        var mongoDb = string.IsNullOrWhiteSpace(mongoDatabase) ? MongoSavedGroupRepository.DefaultDatabase : mongoDatabase!;

        if (mode == StorageMode.Mongo && !wantsMongo)
            throw new InvalidOperationException("指定了 StorageMode.Mongo 但沒有給連線字串");

        if (wantsMongo)
        {
            // 先講清楚「現在要連 MongoDB，最多等 15 秒」——
            // 否則雲端上看到啟動卡住十幾秒會以為當掉了。
            BotLog.Warn($"[儲存] 正在連線 MongoDB（{MongoDiagnostics.Mask(mongoConnectionString!)}，" +
                        "最多等 15 秒）…");

            try
            {
                var repo = new MongoSavedGroupRepository(mongoConnectionString!, mongoDb);
                BotLog.Warn($"[儲存] 使用 MongoDB：{repo.Describe()}");
                return repo;
            }
            catch (Exception ex)
            {
                if (mode == StorageMode.Mongo) throw;

                // 不硬撐：連不上就退回本機儲存，但一定要讓使用者看到**為什麼**。
                // MongoDB 的例外訊息本身看不出原因，所以附上可執行的檢查清單。
                BotLog.Warn($"[儲存] ❌ MongoDB 連不上（{Short(ex)}）→ 改用本機儲存" +
                            "（這次的訂閱組不會同步到其他機器）");
                BotLog.Warn(MongoDiagnostics.Checklist(mongoConnectionString!));

                _mongoRecheck = new MongoRecheckLoop(mongoConnectionString!, mongoDb);
            }
        }

        // 明確要求記憶體（乾跑與測試用）：SQLite 的記憶體資料庫最理想，
        // 沒有 SQLite 就退回「不寫檔的文字儲存」—— 行為一致，只是重啟就沒有。
        if (path == ":memory:" && mode == StorageMode.Auto)
        {
            if (SqliteBackends.IsAvailable)
            {
                try { return new SqliteSavedGroupRepository(SqliteBackends.Open(":memory:"), path); }
                catch (Exception) { /* 往下退回記憶體儲存 */ }
            }

            return new TextSavedGroupRepository(null);
        }

        if (mode == StorageMode.Text)
            return new TextSavedGroupRepository(textPath);

        if (SqliteBackends.IsAvailable)
        {
            try
            {
                return new SqliteSavedGroupRepository(SqliteBackends.Open(path), path);
            }
            catch (Exception ex)
            {
                // 開不起來（權限、檔案損毀、平台不支援…）→ 退回文字檔，
                // 而不是默默變成記憶體模式（那樣使用者會以為資料存好了）
                BotLog.Warn($"[儲存] SQLite 開不起來（{Short(ex)}），改用文字檔：{textPath}");
            }
        }

        try
        {
            return new TextSavedGroupRepository(textPath);
        }
        catch (Exception ex)
        {
            BotLog.Warn($"[儲存] 文字檔也寫不進去（{Short(ex)}），" +
                        "訂閱組只好放記憶體（重啟後會消失）");
            return new TextSavedGroupRepository(null);
        }
    }

    /// <summary>例外訊息只取一行並截短（MongoDB 的例外很長，整段印出來只會洗版）。</summary>
    private static string Short(Exception ex)
    {
        var reason = (ex.Message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return $"{ex.GetType().Name}: {(reason.Length > 120 ? reason[..120] + "…" : reason)}";
    }

    /// <summary>`tcbus.db` → 同目錄的 `saved_groups.txt`。</summary>
    private static string TextPathFor(string path)
    {
        if (path == ":memory:") return Path.Combine(Path.GetTempPath(), TextFileName);

        var dir = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(dir) ? TextFileName : Path.Combine(dir, TextFileName);
    }

    public string Describe() => _repo.Describe();

    // ─────────────────────────────────────────────────────

    public IReadOnlyList<SavedGroup> ListByUser(ulong userId) => _repo.ListByUser(userId);

    public int CountByUser(ulong userId) => _repo.CountByUser(userId);

    public SavedGroup? Get(long id, ulong userId) => _repo.Get(id, userId);

    /// <summary>依名稱找一個（同名覆蓋時用來先備份，好讓「合併」可以復原）。</summary>
    public SavedGroup? GetByName(ulong userId, string name) => _repo.GetByName(userId, name);

    public SavedGroupPayload? GetPayload(long id, ulong userId) => _repo.GetPayload(id, userId);

    /// <summary>
    /// 新增或更新一個訂閱組。同名會覆蓋（回傳 <see cref="SaveGroupResult.Updated"/>）。
    /// 驗證集中在這裡，兩種後端就不用各寫一次。
    /// </summary>
    public (SaveGroupResult Result, string? Message) Save(
        ulong userId, string name, SavedGroupPayload payload)
    {
        name = (name ?? "").Trim();

        if (name.Length == 0) return (SaveGroupResult.NameEmpty, null);
        if (name.Length > MaxNameLength)
            return (SaveGroupResult.NameTooLong, $"名稱最多 {MaxNameLength} 個字");
        if (payload.LegCount == 0)
            return (SaveGroupResult.RouteCountMismatch, "這個訂閱沒有行程可以存");
        if (payload.LegCount > MaxLegsPerGroup)
            return (SaveGroupResult.TooManyLegs, $"一個訂閱組最多 {MaxLegsPerGroup} 段行程");
        if (payload.RouteCount == 0)
            return (SaveGroupResult.RouteCountMismatch, "這個訂閱沒有路線可以存");
        if (payload.RouteCount > MaxRoutesPerGroup)
            return (SaveGroupResult.RouteCountMismatch, $"一個訂閱組最多存 {MaxRoutesPerGroup} 條路線");

        // 同名覆蓋不算新增，所以只在「真的新增」時檢查數量上限
        if (_repo.GetByName(userId, name) is null && _repo.CountByUser(userId) >= MaxGroupsPerUser)
            return (SaveGroupResult.TooManyGroups, $"最多只能有 {MaxGroupsPerUser} 個訂閱組");

        return _repo.Save(userId, name, payload);
    }

    public bool Rename(long id, ulong userId, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0 || newName.Length > MaxNameLength) return false;

        return _repo.Rename(id, userId, newName);
    }

    public bool Delete(long id, ulong userId) => _repo.Delete(id, userId);

    /// <summary>記錄一次「被使用」，讓常用的組排在前面。</summary>
    public void Touch(long id, ulong userId) => _repo.Touch(id, userId);

    // ── 通用小型狀態（ILlmStateStore）─────────────────────
    //
    // 借用同一條後端鏈（MongoDB → SQLite → 文字檔 → 記憶體）來放「跨重啟要保留」的
    // 極少量資料。目前只有 LLM 的每週 token 用量 —— 那個絕對不能因為重啟就歸零，
    // 否則每週上限形同虛設。

    public string? GetBlob(string key) => _repo.LoadBlob(key);

    public void SetBlob(string key, string json) => _repo.SaveBlob(key, json);

    public void Dispose()
    {
        _repo.Dispose();
        _mongoRecheck?.Dispose();
        _mongoRecheck = null;
    }
}
