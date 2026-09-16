using System.Text.Json;

namespace TcBusBot.Core.Storage;

/// <summary>
/// SQLite 版的訂閱組儲存（原本的實作，只是從 <see cref="SavedGroupStore"/> 搬出來）。
///
/// 白名單、名稱長度、段數／路線數上限等驗證都在 <see cref="SavedGroupStore"/> 做過了，
/// 這裡只負責存取。
/// </summary>
internal sealed class SqliteSavedGroupRepository : ISavedGroupRepository
{
    private const string SelectColumns = """
        SELECT id, user_id, name, origin_name, destination_name, route_count,
               notify_minutes, created_at, last_used_at, use_count
          FROM saved_groups
        """;

    private readonly ISqliteBackend _db;

    public SqliteSavedGroupRepository(ISqliteBackend db, string path)
    {
        _db = db;
        DatabasePath = path;
        IsPersistent = db.IsPersistent && path != ":memory:";
        CreateSchema();
    }

    public string DatabasePath { get; }

    public bool IsPersistent { get; }
    public string Describe()
        => DatabasePath == ":memory:"
            ? "記憶體資料庫（SQLite :memory:，重啟後訂閱組會消失）"
            : $"{Path.GetFullPath(DatabasePath)}（SQLite {_db.Version ?? "?"}）";

    private void CreateSchema()
    {
        _db.Exec("""
            CREATE TABLE IF NOT EXISTS saved_groups (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id          TEXT    NOT NULL,
                name             TEXT    NOT NULL,
                origin_name      TEXT    NOT NULL,
                destination_name TEXT    NOT NULL,
                route_count      INTEGER NOT NULL,
                notify_minutes   INTEGER NOT NULL,
                payload          TEXT    NOT NULL,
                created_at       TEXT    NOT NULL,
                last_used_at     TEXT,
                use_count        INTEGER NOT NULL DEFAULT 0
            );
            """);

        _db.Exec("CREATE INDEX IF NOT EXISTS ix_saved_groups_user ON saved_groups(user_id);", null);

        // 通用小狀態（LLM 每週用量）——同樣在這個檔案裡，只是另一張表
        _db.Exec("""
            CREATE TABLE IF NOT EXISTS blobs (
                key        TEXT PRIMARY KEY,
                json       TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """, null);
    }

    // ── 查詢 ──────────────────────────────────────────────

    public IReadOnlyList<SavedGroup> ListByUser(ulong userId)
    {
        var r = _db.Query($"{SelectColumns} WHERE user_id = ? ORDER BY use_count DESC, created_at DESC",
                          userId.ToString());

        return r.Rows.Select(Map).ToList();
    }

    public int CountByUser(ulong userId)
        => (int)_db.Count("SELECT COUNT(*) FROM saved_groups WHERE user_id = ?", userId.ToString());

    public SavedGroup? Get(long id, ulong userId)
    {
        var r = _db.Query($"{SelectColumns} WHERE id = ? AND user_id = ?", id, userId.ToString());
        return r.Rows.Count > 0 ? Map(r.Rows[0]) : null;
    }

    public SavedGroup? GetByName(ulong userId, string name)
    {
        var r = _db.Query($"{SelectColumns} WHERE user_id = ? AND name = ?",
                          userId.ToString(), (name ?? "").Trim());

        return r.Rows.Count > 0 ? Map(r.Rows[0]) : null;
    }

    public SavedGroupPayload? GetPayload(long id, ulong userId)
    {
        var json = _db.Scalar("SELECT payload FROM saved_groups WHERE id = ? AND user_id = ?",
                              id, userId.ToString()) as string;

        if (string.IsNullOrEmpty(json)) return null;

        try { return JsonSerializer.Deserialize<SavedGroupPayload>(json, SavedGroupStore.PayloadJson); }
        catch { return null; }
    }

    // ── 異動 ──────────────────────────────────────────────

    public (SaveGroupResult Result, string?) Save(ulong userId, string name, SavedGroupPayload payload)
    {
        var existing = _db.Query("SELECT id FROM saved_groups WHERE user_id = ? AND name = ?",
                                 userId.ToString(), name);

        var json = JsonSerializer.Serialize(payload, SavedGroupStore.PayloadJson);
        var now = DateTimeOffset.UtcNow.ToString("O");

        // 清單顯示用的反正規化欄位：起點取第一段、終點取最後一段
        var originName = payload.Origin.DisplayName;
        var destinationName = payload.Legs[^1].Destination.DisplayName;

        if (existing.Rows.Count > 0)
        {
            var id = existing.GetLong(existing.Rows[0], "id");
            _db.Execute("""
                UPDATE saved_groups
                   SET origin_name = ?, destination_name = ?, route_count = ?,
                       notify_minutes = ?, payload = ?
                 WHERE id = ? AND user_id = ?
                """,
                originName, destinationName,
                payload.RouteCount, payload.NotifyMinutes, json, id, userId.ToString());

            return (SaveGroupResult.Updated, null);
        }

        _db.Execute("""
            INSERT INTO saved_groups
                (user_id, name, origin_name, destination_name, route_count,
                 notify_minutes, payload, created_at, use_count)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, 0)
            """,
            userId.ToString(), name,
            originName, destinationName,
            payload.RouteCount, payload.NotifyMinutes, json, now);

        return (SaveGroupResult.Created, null);
    }

    public bool Rename(long id, ulong userId, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return false;

        // 不能改成別組已經用掉的名字
        var clash = _db.Query("SELECT id FROM saved_groups WHERE user_id = ? AND name = ? AND id <> ?",
                              userId.ToString(), newName, id);
        if (clash.Rows.Count > 0) return false;

        return _db.Execute("UPDATE saved_groups SET name = ? WHERE id = ? AND user_id = ?",
                           newName, id, userId.ToString()) > 0;
    }

    public bool Delete(long id, ulong userId)
        => _db.Execute("DELETE FROM saved_groups WHERE id = ? AND user_id = ?", id, userId.ToString()) > 0;

    public void Touch(long id, ulong userId)
    {
        _db.Execute("""
            UPDATE saved_groups
               SET use_count = use_count + 1, last_used_at = ?
             WHERE id = ? AND user_id = ?
            """, DateTimeOffset.UtcNow.ToString("O"), id, userId.ToString());
    }

    public void Dispose() => _db.Dispose();

    // ── 小型狀態文件 ──────────────────────────────────────

    public string? LoadBlob(string key)
        => _db.Scalar("SELECT json FROM blobs WHERE key = ?", key) as string;

    public void SaveBlob(string key, string json)
        => _db.Execute("""
            INSERT INTO blobs (key, json, updated_at) VALUES (?, ?, ?)
            ON CONFLICT(key) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at
            """, key, json, DateTimeOffset.UtcNow.ToString("O"));

    private static SavedGroup Map(object?[] row)
    {
        static DateTimeOffset Parse(string s)
            => DateTimeOffset.TryParse(s, out var v) ? v : DateTimeOffset.MinValue;

        return new SavedGroup(
            Id: row[0] is long id ? id : 0,
            UserId: ulong.TryParse(row[1] as string, out var u) ? u : 0,
            Name: row[2] as string ?? "",
            OriginName: row[3] as string ?? "",
            DestinationName: row[4] as string ?? "",
            RouteCount: row[5] is long rc ? (int)rc : 0,
            NotifyMinutes: row[6] is long nm ? (int)nm : 10,
            CreatedAt: Parse(row[7] as string ?? ""),
            LastUsedAt: row[8] is string lu && lu.Length > 0 ? Parse(lu) : null,
            UseCount: row[9] is long uc ? (int)uc : 0);
    }
}
