using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcBusBot.Core.Storage;

/// <summary>
/// 訂閱組的「存放方式」。有兩種實作，對外行為完全一樣：
///
/// | 實作 | 什麼時候用 | 存到哪 |
/// | --- | --- | --- |
/// | <see cref="SqliteSavedGroupRepository"/> | 環境有 SQLite（Windows 內建／Android 系統的） | `tcbus.db` |
/// | <see cref="TextSavedGroupRepository"/> | **沒有 SQLite**，或資料庫開不起來 | 一行一筆的 `saved_groups.txt` |
///
/// 為什麼要有文字檔後備：訂閱可以重啟就消失（那是刻意的），
/// 但「訂閱組」是使用者自己整理出來的範本，**不該因為平台沒有 SQLite 就整個不見**。
/// 文字檔在這種情況下是最簡單也最可檢查的做法：
/// 一個檔案、一行一筆、用記事本就能看。
/// </summary>
internal interface ISavedGroupRepository : IDisposable
{
    /// <summary>給使用者看的說明（哪一種模式、檔案在哪）。</summary>
    string Describe();

    bool IsPersistent { get; }

    IReadOnlyList<SavedGroup> ListByUser(ulong userId);
    int CountByUser(ulong userId);
    SavedGroup? Get(long id, ulong userId);
    SavedGroup? GetByName(ulong userId, string name);
    SavedGroupPayload? GetPayload(long id, ulong userId);

    /// <summary>新增或同名覆蓋（呼叫端已經驗證過內容合法）。</summary>
    (SaveGroupResult Result, string?) Save(ulong userId, string name, SavedGroupPayload payload);

    bool Rename(long id, ulong userId, string newName);
    bool Delete(long id, ulong userId);
    void Touch(long id, ulong userId);
}

/// <summary>一行一筆的文字檔後備（JSON Lines）。</summary>
internal sealed class TextSavedGroupRepository : ISavedGroupRepository
{
    private sealed record Row
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("user")] public string User { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("origin")] public string OriginName { get; init; } = "";
        [JsonPropertyName("dest")] public string DestinationName { get; init; } = "";
        [JsonPropertyName("routes")] public int RouteCount { get; init; }
        [JsonPropertyName("notify")] public int NotifyMinutes { get; init; }
        [JsonPropertyName("created")] public string CreatedAt { get; init; } = "";
        [JsonPropertyName("lastUsed")] public string? LastUsedAt { get; init; }
        [JsonPropertyName("used")] public int UseCount { get; init; }
        [JsonPropertyName("payload")] public SavedGroupPayload Payload { get; init; } = null!;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        // 中文保留原樣：這個檔案的賣點就是「用記事本就看得到內容」
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new SavedGroupPayloadConverter() }
    };

    private readonly string? _path;
    private readonly object _gate = new();
    private List<Row> _rows;

    /// <summary>
    /// path = 文字檔路徑；<c>null</c> = 純記憶體（連檔案都寫不進去時的最後手段，
    /// 行為與文字檔一致，只是重啟後就沒有了）。
    /// </summary>
    public TextSavedGroupRepository(string? path)
    {
        _path = path;
        _rows = Read();
    }

    public bool IsPersistent => _path is not null;

    public string Describe()
        => _path is null
            ? "記憶體儲存（這個環境既沒有 SQLite 也寫不進檔案，重啟後訂閱組會消失）"
            : $"文字檔模式（沒有可用的 SQLite）：{_path}";

    // ── 讀寫 ──────────────────────────────────────────────

    private List<Row> Read()
    {
        var rows = new List<Row>();
        if (_path is null || !File.Exists(_path)) return rows;

        foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
        {
            // 去掉 BOM（用記事本之類的編輯器存檔可能會加）
            var trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            // 壞掉的行直接跳過：不要因為一行有問題就讓整個檔案讀不出來
            try
            {
                if (JsonSerializer.Deserialize<Row>(trimmed, Json) is { } row) rows.Add(row);
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        return rows;
    }

    /// <summary>整份重寫。資料量很小（每人上限 20 組），簡單比聰明可靠。</summary>
    private void WriteLocked()
    {
        if (_path is null) return;

        var sb = new StringBuilder();
        sb.AppendLine("# TcBusBot 訂閱組（一行一筆 JSON；這個環境沒有 SQLite，所以用文字檔）");
        foreach (var row in _rows) sb.AppendLine(JsonSerializer.Serialize(row, Json));

        // 先寫暫存檔再置換，避免寫到一半斷電就整個檔案壞掉
        var tmp = _path + ".tmp";
        // 刻意不用 Encoding.UTF8（它會寫入 BOM，讓第一行不再是 "#..."）
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, _path, overwrite: true);
    }

    private static SavedGroup ToSummary(Row row) => new(
        Id: row.Id,
        UserId: ulong.TryParse(row.User, out var u) ? u : 0,
        Name: row.Name,
        OriginName: row.OriginName,
        DestinationName: row.DestinationName,
        RouteCount: row.RouteCount,
        NotifyMinutes: row.NotifyMinutes,
        CreatedAt: DateTimeOffset.TryParse(row.CreatedAt, out var c) ? c : DateTimeOffset.MinValue,
        LastUsedAt: row.LastUsedAt is { Length: > 0 } lu && DateTimeOffset.TryParse(lu, out var l)
            ? l : null,
        UseCount: row.UseCount);

    private IEnumerable<Row> OfUser(ulong userId)
    {
        var key = userId.ToString();
        // 與 SQLite 版同一個排序：常用的排前面，其次新的排前面
        return _rows.Where(r => r.User == key)
                    .OrderByDescending(r => r.UseCount)
                    .ThenByDescending(r => r.CreatedAt, StringComparer.Ordinal);
    }

    // ── 查詢 ──────────────────────────────────────────────

    public IReadOnlyList<SavedGroup> ListByUser(ulong userId)
    {
        lock (_gate) return OfUser(userId).Select(ToSummary).ToList();
    }

    public int CountByUser(ulong userId)
    {
        var key = userId.ToString();
        lock (_gate) return _rows.Count(r => r.User == key);
    }

    public SavedGroup? Get(long id, ulong userId)
    {
        var key = userId.ToString();
        lock (_gate)
        {
            var row = _rows.FirstOrDefault(r => r.Id == id && r.User == key);
            return row is null ? null : ToSummary(row);
        }
    }

    public SavedGroup? GetByName(ulong userId, string name)
    {
        var key = userId.ToString();
        var trimmed = (name ?? "").Trim();

        lock (_gate)
        {
            var row = _rows.FirstOrDefault(r => r.User == key && r.Name == trimmed);
            return row is null ? null : ToSummary(row);
        }
    }

    public SavedGroupPayload? GetPayload(long id, ulong userId)
    {
        var key = userId.ToString();
        lock (_gate) return _rows.FirstOrDefault(r => r.Id == id && r.User == key)?.Payload;
    }

    // ── 異動 ──────────────────────────────────────────────

    public (SaveGroupResult Result, string?) Save(ulong userId, string name, SavedGroupPayload payload)
    {
        var key = userId.ToString();
        name = (name ?? "").Trim();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var origin = payload.Origin.DisplayName;
        var destination = payload.Legs[^1].Destination.DisplayName;

        lock (_gate)
        {
            var index = _rows.FindIndex(r => r.User == key && r.Name == name);

            if (index >= 0)
            {
                _rows[index] = _rows[index] with
                {
                    OriginName = origin,
                    DestinationName = destination,
                    RouteCount = payload.RouteCount,
                    NotifyMinutes = payload.NotifyMinutes,
                    Payload = payload
                };

                WriteLocked();
                return (SaveGroupResult.Updated, null);
            }

            var nextId = _rows.Count == 0 ? 1 : _rows.Max(r => r.Id) + 1;

            _rows.Add(new Row
            {
                Id = nextId,
                User = key,
                Name = name,
                OriginName = origin,
                DestinationName = destination,
                RouteCount = payload.RouteCount,
                NotifyMinutes = payload.NotifyMinutes,
                CreatedAt = now,
                LastUsedAt = null,
                UseCount = 0,
                Payload = payload
            });

            WriteLocked();
            return (SaveGroupResult.Created, null);
        }
    }

    public bool Rename(long id, ulong userId, string newName)
    {
        var key = userId.ToString();
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return false;

        lock (_gate)
        {
            // 不能改成別組已經用掉的名字
            if (_rows.Any(r => r.User == key && r.Name == newName && r.Id != id)) return false;

            var index = _rows.FindIndex(r => r.Id == id && r.User == key);
            if (index < 0) return false;

            _rows[index] = _rows[index] with { Name = newName };
            WriteLocked();
            return true;
        }
    }

    public bool Delete(long id, ulong userId)
    {
        var key = userId.ToString();

        lock (_gate)
        {
            var removed = _rows.RemoveAll(r => r.Id == id && r.User == key);
            if (removed == 0) return false;

            WriteLocked();
            return true;
        }
    }

    public void Touch(long id, ulong userId)
    {
        var key = userId.ToString();

        lock (_gate)
        {
            var index = _rows.FindIndex(r => r.Id == id && r.User == key);
            if (index < 0) return;

            _rows[index] = _rows[index] with
            {
                UseCount = _rows[index].UseCount + 1,
                LastUsedAt = DateTimeOffset.UtcNow.ToString("O")
            };

            WriteLocked();
        }
    }

    public void Dispose() { }
}
