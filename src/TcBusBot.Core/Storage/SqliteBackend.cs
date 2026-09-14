namespace TcBusBot.Core.Storage;

/// <summary>
/// SQLite 後端介面。
///
/// 為什麼要有這層：**兩個平台都有自己的 SQLite，而且都不需要 NuGet 套件**
///
/// | 平台 | 後端 | 怎麼來 |
/// | --- | --- | --- |
/// | Windows | <see cref="SqliteDatabase"/> | P/Invoke 內建的 `winsqlite3.dll`（實測 3.51.1） |
/// | Android | `AndroidSqliteBackend`（`TcBusBot.Mobile`） | 系統的 `android.database.sqlite`（Java API，透過 .NET 繫結） |
///
/// Android **不能**沿用 Windows 那一套：`libsqlite3.so` 是 Android 的私有函式庫，
/// 從 API 24 起不在 `public.libraries.txt` 裡，應用程式 `dlopen` 不到。
/// 但 Android 自己有完整的 SQLite（`android.database.sqlite.SQLiteDatabase`），
/// 走它就不需要 NDK 編譯、也不需要任何原生套件。
///
/// 因此 <see cref="SavedGroupStore"/> 只依賴這個介面，
/// 由啟動端決定要用哪一個後端（見 <see cref="SqliteBackends.Factory"/>）。
/// </summary>
public interface ISqliteBackend : IDisposable
{
    /// <summary>SQLite 版本（顯示用；取不到就回 null）。</summary>
    string? Version { get; }

    /// <summary>是否真的寫進檔案（false = 記憶體，重啟就消失）。</summary>
    bool IsPersistent { get; }

    /// <summary>執行一句 SQL（可帶參數），回傳受影響的列數。</summary>
    int Execute(string sql, params object?[] args);

    /// <summary>執行 SQL，不在意受影響的列數（建表、PRAGMA 等）。</summary>
    void Exec(string sql, params object?[] args);

    /// <summary>查詢並取回全部結果。</summary>
    SqliteResult Query(string sql, params object?[] args);

    /// <summary>查詢單一純量值（第一列第一欄）。</summary>
    object? Scalar(string sql, params object?[] args)
    {
        var result = Query(sql, args);
        return result.Rows.Count > 0 && result.Rows[0].Length > 0 ? result.Rows[0][0] : null;
    }

    long Count(string sql, params object?[] args)
        => Scalar(sql, args) is long l ? l : 0;
}

/// <summary>
/// 取得 SQLite 後端。
///
/// Windows 上什麼都不用做（內建 DLL）；Android 這類平台在啟動時設定
/// <see cref="Factory"/>（例如 <c>SqliteBackends.Factory = path =&gt; new AndroidSqliteBackend(path);</c>），
/// 之後整包程式碼（含 <see cref="SavedGroupStore"/>）就照常運作。
/// </summary>
public static class SqliteBackends
{
    private static Func<string, ISqliteBackend>? _factory;

    /// <summary>平台自訂的後端工廠；null = 用 Windows 內建的 winsqlite3。</summary>
    public static Func<string, ISqliteBackend>? Factory
    {
        get => _factory;
        set => _factory = value;
    }

    /// <summary>這個環境有沒有可用的 SQLite（任何一種後端）。</summary>
    public static bool IsAvailable => _factory is not null || SqliteDatabase.IsAvailable;

    /// <summary>開啟資料庫；沒有可用後端時丟例外（呼叫端自己決定要不要退回記憶體模式）。</summary>
    public static ISqliteBackend Open(string path)
        => _factory is not null ? _factory(path) : new SqliteDatabase(path);
}
