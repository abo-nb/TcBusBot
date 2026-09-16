namespace TcBusBot.Core.Storage;

/// <summary>
/// 要開哪一種儲存後端（由設定決定，不再散落在程式各處）。
/// </summary>
public sealed record StorageSettings(
    string DatabasePath,
    string? MongoConnectionString = null,
    string? MongoDatabase = null,
    SavedGroupStore.StorageMode Mode = SavedGroupStore.StorageMode.Auto)
{
    /// <summary>測試與乾跑用的記憶體模式。</summary>
    public static StorageSettings Memory => new(":memory:");

    public bool WantsMongo => !string.IsNullOrWhiteSpace(MongoConnectionString);

    /// <summary>連線字串的遮罩預覽（**絕對不印出帳密**）——啟動橫幅與 log 用。</summary>
    public string MongoPreview => WantsMongo
        ? MongoSavedGroupRepository.Mask(MongoConnectionString!)
        : "未設定（訂閱組存在本機）";

    public string Describe()
        => Mode switch
        {
            SavedGroupStore.StorageMode.Text => $"強制文字檔（{DatabasePath}）",
            SavedGroupStore.StorageMode.Mongo => $"強制 MongoDB（{MongoPreview}）",
            _ => WantsMongo ? $"自動：MongoDB（{MongoPreview}）優先" : $"自動：本機（{DatabasePath}）"
        };
}

/// <summary>
/// 決定「用哪一個儲存後端」：**MongoDB → SQLite → 文字檔 → 記憶體**。
///
/// 為什麼要獨立成一個類別（原本藏在 <c>SavedGroupStore.Open</c> 的 private static 裡）：
/// 這段邏輯就是「儲存方案」本身，把它抽出來之後
///   1. DI 容器可以把它當成一般的工廠註冊（見 <see cref="StorageServiceCollectionExtensions"/>）
///   2. 不需要真的建立 <see cref="SavedGroupStore"/> 就能測「挑後端的規則」
/// 而退回時**一定要留下警告**（連不上 Mongo 卻默默用本機，是最糟的結果）。
/// </summary>
internal static class StorageBackendFactory
{
    /// <summary>MongoDB 連不上時的背景重試（只提醒，不自動切換）。</summary>
    private static MongoRecheckLoop? _mongoRecheck;

    public static ISavedGroupRepository Open(StorageSettings settings, Action<string>? log = null)
    {
        log ??= BotLog.Warn;

        var path = string.IsNullOrWhiteSpace(settings.DatabasePath) ? "tcbus.db" : settings.DatabasePath;
        var textPath = TextPathFor(path);
        var mongoDb = string.IsNullOrWhiteSpace(settings.MongoDatabase)
            ? MongoSavedGroupRepository.DefaultDatabase
            : settings.MongoDatabase!;

        if (settings.Mode == SavedGroupStore.StorageMode.Mongo && !settings.WantsMongo)
            throw new InvalidOperationException("指定了 StorageMode.Mongo 但沒有給連線字串");

        if (settings.WantsMongo)
        {
            // 先講清楚「現在要連 MongoDB，最多等 15 秒」——
            // 否則雲端上看到啟動卡住十幾秒會以為當掉了。
            log($"[儲存] 正在連線 MongoDB（{settings.MongoPreview}，最多等 15 秒）…");

            try
            {
                var repo = new MongoSavedGroupRepository(settings.MongoConnectionString!, mongoDb);
                log($"[儲存] 使用 MongoDB：{repo.Describe()}");
                return repo;
            }
            catch (Exception ex)
            {
                if (settings.Mode == SavedGroupStore.StorageMode.Mongo) throw;

                // 不硬撐：連不上就退回本機儲存，但一定要讓使用者看到**為什麼**。
                log($"[儲存] ❌ MongoDB 連不上（{Short(ex)}）→ 改用本機儲存" +
                    "（這次的訂閱組不會同步到其他機器）");
                log(MongoDiagnostics.Checklist(settings.MongoConnectionString!));

                _mongoRecheck = new MongoRecheckLoop(settings.MongoConnectionString!, mongoDb);
            }
        }

        // 明確要求記憶體（乾跑與測試用）：SQLite 的記憶體資料庫最理想，
        // 沒有 SQLite 就退回「不寫檔的文字儲存」—— 行為一致，只是重啟就沒有。
        if (path == ":memory:" && settings.Mode == SavedGroupStore.StorageMode.Auto)
        {
            if (SqliteBackends.IsAvailable)
            {
                try { return new SqliteSavedGroupRepository(SqliteBackends.Open(":memory:"), path); }
                catch (Exception) { /* 往下退回記憶體儲存 */ }
            }

            return new TextSavedGroupRepository(null);
        }

        if (settings.Mode == SavedGroupStore.StorageMode.Text)
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
                log($"[儲存] SQLite 開不起來（{Short(ex)}），改用文字檔：{textPath}");
            }
        }

        try
        {
            return new TextSavedGroupRepository(textPath);
        }
        catch (Exception ex)
        {
            log($"[儲存] 文字檔也寫不進去（{Short(ex)}），訂閱組只好放記憶體（重啟後會消失）");
            return new TextSavedGroupRepository(null);
        }
    }

    /// <summary>例外訊息只取一行並截短（MongoDB 的例外很長，整段印出來只會洗版）。</summary>
    internal static string Short(Exception ex)
    {
        var reason = (ex.Message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return $"{ex.GetType().Name}: {(reason.Length > 120 ? reason[..120] + "…" : reason)}";
    }

    /// <summary>`tcbus.db` → 同目錄的 `saved_groups.txt`。</summary>
    internal static string TextPathFor(string path)
    {
        if (path == ":memory:") return Path.Combine(Path.GetTempPath(), SavedGroupStore.TextFileName);

        var dir = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(dir) ? SavedGroupStore.TextFileName : Path.Combine(dir, SavedGroupStore.TextFileName);
    }

    /// <summary>關掉背景的 MongoDB 重試迴圈（容器釋放時呼叫）。</summary>
    public static void DisposeShared()
    {
        _mongoRecheck?.Dispose();
        _mongoRecheck = null;
    }
}
