using System.Text.Json;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace TcBusBot.Core.Storage;

/// <summary>
/// 訂閱組存到 MongoDB（伺服器或 Atlas 都可以，只要給連線字串）。
///
/// **為什麼要這個選項**：本機儲存讓每個平台都要自己解一次問題 ——
/// Windows 有內建的 SQLite、Android 沒有、Termux 也沒有，
/// 於是資料在手機與電腦之間各存一份、搬來搬去。
/// 放到 MongoDB 之後**所有平台共用同一份資料**：
/// 手機訂閱、電腦打開就看到，換機器也不用複製檔案。
///
/// 對外的行為與其他後端完全一樣（同樣是 <see cref="ISavedGroupRepository"/>）：
///
/// | 概念 | 這裡怎麼存 |
/// | --- | --- |
/// | 一筆訂閱組 | 一個 document（集合 `saved_groups`） |
/// | 對外的 `long id` | document 的 `seq`（用 `counters` 集合原子遞增，跟 SQLite 的 AUTOINCREMENT 等價） |
/// | `payload` | **存 JSON 字串** —— 直接沿用既有的 `SavedGroupStore.PayloadJson`（含舊格式相容），而且在 Atlas 的介面上也看得懂 |
///
/// 同步 API 說明：MongoDB 的 C# driver 本身就提供同步方法
/// （<c>InsertOne</c> / <c>ReplaceOne</c> / <c>Find</c>…），
/// 所以這裡不需要「async 包 sync」那種寫法，整個 <see cref="ISavedGroupRepository"/>
/// 也就能維持同步、不必改動 Discord 那一層。
/// </summary>
public sealed class MongoSavedGroupRepository : ISavedGroupRepository
{
    public const string DefaultDatabase = "tcbus";
    public const string CollectionName = "saved_groups";
    public const string CounterCollectionName = "counters";
    public const string CounterId = "saved_groups_seq";

    /// <summary>通用小狀態（LLM 每週用量）放這裡。</summary>
    public const string BlobCollectionName = "blobs";

    // 連線逾時：**不要設太短**。雲端（Render）連 Atlas 第一次要經過
    // DNS SRV → TLS 握手 → 複製集探索，3 秒常常不夠（實測就會出現
    // 「A timeout occurred after 2998ms selecting a server」）。
    // 也不能太長，否則連不上時啟動會卡很久。
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ServerSelectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SocketTimeout = TimeSpan.FromSeconds(20);

    private readonly IMongoCollection<SavedGroupDocument> _groups;
    private readonly IMongoCollection<CounterDocument> _counters;
    private readonly IMongoCollection<MongoDB.Bson.BsonDocument> _blobs;
    private readonly string _describe;

    public MongoSavedGroupRepository(string connectionString, string database = DefaultDatabase)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("沒有 MongoDB 連線字串", nameof(connectionString));

        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ConnectTimeout = ConnectTimeout;
        settings.ServerSelectionTimeout = ServerSelectionTimeout;
        settings.SocketTimeout = SocketTimeout;

        // 連不上時不要無止境地重試（啟動時要快點決定用哪個後端）
        settings.RetryWrites = true;

        // 不要在日誌裡印出連線字串（裡面有帳密）
        var client = new MongoClient(settings);
        var db = client.GetDatabase(string.IsNullOrWhiteSpace(database) ? DefaultDatabase : database);

        // 沒有這一步的話，「連不上」要等到第一次寫入才會發現
        db.RunCommand<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));

        _groups = db.GetCollection<SavedGroupDocument>(CollectionName);
        _counters = db.GetCollection<CounterDocument>(CounterCollectionName);
        _blobs = db.GetCollection<MongoDB.Bson.BsonDocument>(BlobCollectionName);
        EnsureIndexes();

        _describe = $"MongoDB（{Mask(connectionString)}／資料庫 {db.DatabaseNamespace.DatabaseName}）";
    }

    public bool IsPersistent => true;

    public string Describe() => _describe;

    private void EnsureIndexes()
    {
        // 同一個使用者不能有兩個同名訂閱組（與 SQLite 版的行為一致）
        _groups.Indexes.CreateOne(new CreateIndexModel<SavedGroupDocument>(
            Builders<SavedGroupDocument>.IndexKeys.Ascending(d => d.User).Ascending(d => d.Name),
            new CreateIndexOptions { Unique = true, Name = "ix_user_name" }));

        // 清單排序：常用的排前面，其次新的排前面
        _groups.Indexes.CreateOne(new CreateIndexModel<SavedGroupDocument>(
            Builders<SavedGroupDocument>.IndexKeys.Ascending(d => d.User)
                                              .Descending(d => d.UseCount)
                                              .Descending(d => d.CreatedAt),
            new CreateIndexOptions { Name = "ix_user_order" }));
    }

    private long NextSeq()
    {
        var filter = Builders<CounterDocument>.Filter.Eq(c => c.Id, CounterId);
        var update = Builders<CounterDocument>.Update.Inc(c => c.Seq, 1);

        var options = new FindOneAndUpdateOptions<CounterDocument, CounterDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };

        return _counters.FindOneAndUpdate(filter, update, options).Seq;
    }

    private static string Key(ulong userId) => userId.ToString();

    // ── 查詢 ──────────────────────────────────────────────

    public IReadOnlyList<SavedGroup> ListByUser(ulong userId)
        => _groups.Find(Builders<SavedGroupDocument>.Filter.Eq(d => d.User, Key(userId)))
                  .Sort(Builders<SavedGroupDocument>.Sort
                              .Descending(d => d.UseCount)
                              .Descending(d => d.CreatedAt))
                  .ToList()
                  .Select(MongoSavedGroupMapping.ToSummary)
                  .ToList();

    public int CountByUser(ulong userId)
        => (int)_groups.CountDocuments(Builders<SavedGroupDocument>.Filter.Eq(d => d.User, Key(userId)));

    public SavedGroup? Get(long id, ulong userId)
    {
        var doc = Find(id, userId);
        return doc is null ? null : MongoSavedGroupMapping.ToSummary(doc);
    }

    public SavedGroup? GetByName(ulong userId, string name)
    {
        var filter = Builders<SavedGroupDocument>.Filter.Eq(d => d.User, Key(userId))
                     & Builders<SavedGroupDocument>.Filter.Eq(d => d.Name, (name ?? "").Trim());

        var doc = _groups.Find(filter).FirstOrDefault();
        return doc is null ? null : MongoSavedGroupMapping.ToSummary(doc);
    }

    public SavedGroupPayload? GetPayload(long id, ulong userId)
    {
        var doc = Find(id, userId);
        if (doc is null) return null;

        try { return JsonSerializer.Deserialize<SavedGroupPayload>(doc.PayloadJson, SavedGroupStore.PayloadJson); }
        catch (Exception) { return null; }
    }

    private SavedGroupDocument? Find(long id, ulong userId)
    {
        var filter = Builders<SavedGroupDocument>.Filter.Eq(d => d.User, Key(userId))
                     & Builders<SavedGroupDocument>.Filter.Eq(d => d.Seq, id);

        return _groups.Find(filter).FirstOrDefault();
    }

    // ── 異動 ──────────────────────────────────────────────

    public (SaveGroupResult Result, string?) Save(ulong userId, string name, SavedGroupPayload payload)
    {
        var key = Key(userId);
        name = (name ?? "").Trim();

        var filter = Builders<SavedGroupDocument>.Filter.Eq(d => d.User, key)
                     & Builders<SavedGroupDocument>.Filter.Eq(d => d.Name, name);

        var existing = _groups.Find(filter).FirstOrDefault();

        var document = MongoSavedGroupMapping.ToDocument(
            userId, name, payload,
            seq: existing?.Seq ?? 0,
            id: existing?.Id ?? default,
            createdAt: existing?.CreatedAt ?? DateTime.UtcNow,
            lastUsedAt: existing?.LastUsedAt,
            useCount: existing?.UseCount ?? 0);

        if (existing is not null)
        {
            _groups.ReplaceOne(filter, document);
            return (SaveGroupResult.Updated, null);
        }

        document.Seq = NextSeq();
        _groups.InsertOne(document);
        return (SaveGroupResult.Created, null);
    }

    public bool Rename(long id, ulong userId, string newName)
    {
        var key = Key(userId);
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return false;

        // 不能改成別組已經用掉的名字
        var clash = Builders<SavedGroupDocument>.Filter.Eq(d => d.User, key)
                    & Builders<SavedGroupDocument>.Filter.Eq(d => d.Name, newName)
                    & Builders<SavedGroupDocument>.Filter.Ne(d => d.Seq, id);

        if (_groups.Find(clash).Limit(1).Any()) return false;

        var filter = Builders<SavedGroupDocument>.Filter.Eq(d => d.User, key)
                     & Builders<SavedGroupDocument>.Filter.Eq(d => d.Seq, id);

        var result = _groups.UpdateOne(filter, Builders<SavedGroupDocument>.Update.Set(d => d.Name, newName));
        return result.ModifiedCount > 0;
    }

    public bool Delete(long id, ulong userId)
    {
        var filter = Builders<SavedGroupDocument>.Filter.Eq(d => d.User, Key(userId))
                     & Builders<SavedGroupDocument>.Filter.Eq(d => d.Seq, id);

        return _groups.DeleteOne(filter).DeletedCount > 0;
    }

    public void Touch(long id, ulong userId)
    {
        var filter = Builders<SavedGroupDocument>.Filter.Eq(d => d.User, Key(userId))
                     & Builders<SavedGroupDocument>.Filter.Eq(d => d.Seq, id);

        _groups.UpdateOne(filter, Builders<SavedGroupDocument>.Update
            .Inc(d => d.UseCount, 1)
            .Set(d => d.LastUsedAt, DateTime.UtcNow));
    }

    public void Dispose() { }

    // ── 小型狀態文件（LLM 每週用量）──────────────────────
    //
    // 用 BsonDocument 直接存取：只有 key／json 兩個欄位，
    // 為它再宣告一個 document 類別不划算，而且 Atlas 介面上也一眼看得懂。

    public string? LoadBlob(string key)
    {
        var doc = _blobs.Find(Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", key)).FirstOrDefault();
        return doc is not null && doc.TryGetValue("json", out var json) ? json.AsString : null;
    }

    public void SaveBlob(string key, string json)
    {
        var filter = Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", key);
        var doc = new MongoDB.Bson.BsonDocument
        {
            { "_id", key },
            { "json", json },
            { "updatedAt", DateTime.UtcNow }
        };

        _blobs.ReplaceOne(filter, doc, new ReplaceOptions { IsUpsert = true });
    }

    /// <summary>只留 host，不印出帳號密碼（連線字串等同密碼）。</summary>
    public static string Mask(string connectionString)
    {
        try
        {
            var at = connectionString.IndexOf('@');
            var schemeEnd = connectionString.IndexOf("://", StringComparison.Ordinal);

            if (at > 0 && schemeEnd > 0)
                return connectionString[..(schemeEnd + 3)] + "***@" + connectionString[(at + 1)..];

            return connectionString;
        }
        catch (Exception)
        {
            return "（已設定）";
        }
    }
}

/// <summary>MongoDB 上的一個訂閱組。</summary>
public sealed class SavedGroupDocument
{
    [BsonId] public MongoDB.Bson.ObjectId Id { get; set; }

    [BsonElement("user")] public string User { get; set; } = "";

    /// <summary>對外的 <c>long id</c>（由 counters 集合原子遞增）。</summary>
    [BsonElement("seq")] public long Seq { get; set; }

    [BsonElement("name")] public string Name { get; set; } = "";

    [BsonElement("origin")] public string OriginName { get; set; } = "";

    [BsonElement("dest")] public string DestinationName { get; set; } = "";

    [BsonElement("routes")] public int RouteCount { get; set; }

    [BsonElement("notify")] public int NotifyMinutes { get; set; }

    /// <summary>payload 存 JSON 字串：沿用既有（含舊格式相容）的序列化設定，Atlas 上也看得懂。</summary>
    [BsonElement("payload")] public string PayloadJson { get; set; } = "";

    [BsonElement("created")] public DateTime CreatedAt { get; set; }

    [BsonElement("lastUsed")] public DateTime? LastUsedAt { get; set; }

    [BsonElement("used")] public int UseCount { get; set; }
}

internal sealed class CounterDocument
{
    [BsonId] public string Id { get; set; } = "";
    [BsonElement("seq")] public long Seq { get; set; }
}

/// <summary>
/// document ↔ 物件的轉換。抽成純函式，這樣**不需要 MongoDB 伺服器也能測**。
/// </summary>
public static class MongoSavedGroupMapping
{
    public static SavedGroupDocument ToDocument(
        ulong userId, string name, SavedGroupPayload payload,
        long seq, MongoDB.Bson.ObjectId id, DateTime createdAt, DateTime? lastUsedAt, int useCount)
        => new()
        {
            Id = id,
            User = userId.ToString(),
            Seq = seq,
            Name = name,
            // 清單顯示用的反正規化欄位：起點取第一段、終點取最後一段
            OriginName = payload.Origin.DisplayName,
            DestinationName = payload.Legs[^1].Destination.DisplayName,
            RouteCount = payload.RouteCount,
            NotifyMinutes = payload.NotifyMinutes,
            PayloadJson = JsonSerializer.Serialize(payload, SavedGroupStore.PayloadJson),
            CreatedAt = createdAt,
            LastUsedAt = lastUsedAt,
            UseCount = useCount
        };

    public static SavedGroup ToSummary(SavedGroupDocument d) => new(
        Id: d.Seq,
        UserId: ulong.TryParse(d.User, out var u) ? u : 0,
        Name: d.Name,
        OriginName: d.OriginName,
        DestinationName: d.DestinationName,
        RouteCount: d.RouteCount,
        NotifyMinutes: d.NotifyMinutes,
        CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(d.CreatedAt, DateTimeKind.Utc)),
        LastUsedAt: d.LastUsedAt is { } lu
            ? new DateTimeOffset(DateTime.SpecifyKind(lu, DateTimeKind.Utc))
            : null,
        UseCount: d.UseCount);

    public static SavedGroupPayload? ToPayload(SavedGroupDocument d)
    {
        try { return JsonSerializer.Deserialize<SavedGroupPayload>(d.PayloadJson, SavedGroupStore.PayloadJson); }
        catch (Exception) { return null; }
    }
}
