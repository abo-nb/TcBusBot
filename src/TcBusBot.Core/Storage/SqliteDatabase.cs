using System.Runtime.InteropServices;

namespace TcBusBot.Core.Storage;

/// <summary>
/// 最小 SQLite 封裝 —— 直接 P/Invoke **Windows 內建的 `winsqlite3.dll`**。
///
/// 為什麼不用 Microsoft.Data.Sqlite：
///   本專案的核心刻意保持「零外部套件相依」（見 README），
///   而 Windows 10/11 本來就內建完整的 SQLite（實測 3.51.1）。
///   直接呼叫它就能得到真正的 SQLite 檔案（任何 SQLite 工具都打得開），
///   不需要任何 NuGet 套件、離線也能建置與測試。
///
/// 限制：只能在 Windows 上跑。非 Windows（或找不到 DLL）時
/// <see cref="IsAvailable"/> 會是 false，呼叫端可改用記憶體模式。
/// </summary>
public sealed class SqliteDatabase : ISqliteBackend
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;

    private const int OpenReadWrite = 0x2;
    private const int OpenCreate = 0x4;

    private static readonly Lazy<bool> Availability = new(() =>
    {
        try
        {
            return sqlite3_libversion() != IntPtr.Zero;
        }
        catch
        {
            return false;   // 非 Windows，或沒有 winsqlite3.dll
        }
    });

    /// <summary>這個環境能不能用 SQLite（Windows 內建 DLL）。</summary>
    public static bool IsAvailable => Availability.Value;

    /// <summary>SQLite 版本字串（不可用時為 null）。</summary>
    public static string? Version
    {
        get
        {
            if (!IsAvailable) return null;
            try { return Marshal.PtrToStringUTF8(sqlite3_libversion()); }
            catch { return null; }
        }
    }

    private IntPtr _db;

    /// <summary>開啟（或建立）資料庫。path 傳 <c>:memory:</c> 就是純記憶體資料庫。</summary>
    public SqliteDatabase(string path)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(
                "此環境沒有可用的 SQLite（找不到 winsqlite3.dll）。");

        var rc = sqlite3_open_v2(path, out _db, OpenReadWrite | OpenCreate, IntPtr.Zero);
        if (rc != SqliteOk)
            throw new InvalidOperationException($"無法開啟 SQLite 資料庫「{path}」：{LastError()}");

        IsPersistent = path != ":memory:";

        // 併發寫入時不要立刻失敗（Bot 只有一個寫入者，但防禦一下）
        Exec("PRAGMA journal_mode = WAL;");
        Exec("PRAGMA busy_timeout = 3000;");
    }

    public bool IsOpen => _db != IntPtr.Zero;

    /// <summary>介面要求（靜態的 <see cref="Version"/> 不能直接滿足）。</summary>
    string? ISqliteBackend.Version => Version;

    /// <summary>Windows 後端一律寫檔（`:memory:` 例外）。</summary>
    public bool IsPersistent { get; private set; } = true;

    // ── 執行 ──────────────────────────────────────────────

    /// <summary>執行一句 SQL（可帶參數），回傳受影響的列數。</summary>
    public int Execute(string sql, params object?[] args)
    {
        using var stmt = Prepare(sql, args);
        var rc = sqlite3_step(stmt.Handle);
        return rc == SqliteDone ? sqlite3_changes(_db) : throw Error(sql, rc);
    }

    /// <summary>執行多句 SQL（用不到參數的場合，例如建表）。</summary>
    public void Exec(string sql, params object?[] args)
    {
        using var stmt = Prepare(sql, args);
        var rc = sqlite3_step(stmt.Handle);
        if (rc != SqliteDone && rc != SqliteRow) throw Error(sql, rc);
    }

    /// <summary>查詢並取回全部結果。</summary>
    public SqliteResult Query(string sql, params object?[] args)
    {
        using var stmt = Prepare(sql, args);

        var count = sqlite3_column_count(stmt.Handle);
        var columns = new string[count];
        for (var i = 0; i < count; i++)
            columns[i] = Marshal.PtrToStringUTF8(sqlite3_column_name(stmt.Handle, i)) ?? $"col{i}";

        var rows = new List<object?[]>();
        int rc;
        while ((rc = sqlite3_step(stmt.Handle)) == SqliteRow)
        {
            var row = new object?[count];
            for (var i = 0; i < count; i++)
            {
                row[i] = sqlite3_column_type(stmt.Handle, i) switch
                {
                    1 => sqlite3_column_int64(stmt.Handle, i),                          // INTEGER
                    2 => sqlite3_column_double(stmt.Handle, i),                         // FLOAT
                    5 => null,                                                          // NULL
                    _ => Marshal.PtrToStringUTF8(sqlite3_column_text(stmt.Handle, i))   // TEXT / BLOB
                };
            }
            rows.Add(row);
        }

        if (rc != SqliteDone) throw Error(sql, rc);
        return new SqliteResult(columns, rows);
    }

    /// <summary>查詢單一純量值（第一列第一欄）。</summary>
    public object? Scalar(string sql, params object?[] args)
    {
        var result = Query(sql, args);
        return result.Rows.Count > 0 && result.Rows[0].Length > 0 ? result.Rows[0][0] : null;
    }

    public long Count(string sql, params object?[] args)
        => Scalar(sql, args) is long l ? l : 0;

    // ── 交易 ──────────────────────────────────────────────

    public void BeginTransaction() => Exec("BEGIN;");
    public void Commit() => Exec("COMMIT;");
    public void Rollback() => Exec("ROLLBACK;");

    // ── 內部 ──────────────────────────────────────────────

    private Statement Prepare(string sql, object?[]? args)
    {
        // 注意：呼叫端若寫 Exec(sql, null)，params 陣列本身會是 null（不是空陣列）
        args ??= Array.Empty<object?>();

        var rc = sqlite3_prepare_v2(_db, sql, -1, out var handle, IntPtr.Zero);
        if (rc != SqliteOk) throw Error(sql, rc);

        var stmt = new Statement(handle);

        for (var i = 0; i < args.Length; i++)
        {
            var index = i + 1;
            var arg = args[i];

            var bindRc = arg switch
            {
                null => sqlite3_bind_null(handle, index),
                long l => sqlite3_bind_int64(handle, index, l),
                int n => sqlite3_bind_int64(handle, index, n),
                bool b => sqlite3_bind_int64(handle, index, b ? 1 : 0),
                double d => sqlite3_bind_double(handle, index, d),
                _ => sqlite3_bind_text(handle, index, arg.ToString() ?? "", -1, Transient)
            };

            if (bindRc != SqliteOk)
            {
                stmt.Dispose();
                throw Error($"綁定第 {index} 個參數（{sql}）", bindRc);
            }
        }

        return stmt;
    }

    private Exception Error(string sql, int rc)
        => new InvalidOperationException($"SQLite 錯誤（code {rc}）：{LastError()}\nSQL: {sql}");

    private string LastError()
    {
        if (_db == IntPtr.Zero) return "（資料庫未開啟）";
        return Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)) ?? "（未知錯誤）";
    }

    public void Dispose()
    {
        if (_db == IntPtr.Zero) return;
        sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }

    /// <summary>SQLITE_TRANSIENT：要 SQLite 自己複製一份字串（-1 轉成的指標）。</summary>
    private static readonly IntPtr Transient = new(-1);

    private sealed class Statement(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            sqlite3_finalize(Handle);
        }
    }

    // ── P/Invoke ──────────────────────────────────────────

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_libversion")]
    private static extern IntPtr sqlite3_libversion();

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(
        IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int numBytes,
        out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_changes(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(
        IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int numBytes, IntPtr destructor);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_null(IntPtr stmt, int index);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_name(IntPtr stmt, int index);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr stmt, int index);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int index);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int64")]
    private static extern long sqlite3_column_int64(IntPtr stmt, int index);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_double")]
    private static extern double sqlite3_column_double(IntPtr stmt, int index);
}

/// <summary>查詢結果：欄位名 + 每一列的原始值（依 SQLite 型別分別轉成 object）。</summary>
public sealed record SqliteResult(string[] Columns, List<object?[]> Rows)
{
    public int Count => Rows.Count;

    public int IndexOf(string column)
        => Array.FindIndex(Columns, c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));

    public string GetString(object?[] row, string column)
    {
        var i = IndexOf(column);
        return i >= 0 && row[i] is string s ? s : "";
    }

    public long GetLong(object?[] row, string column)
    {
        var i = IndexOf(column);
        return i >= 0 && row[i] is long l ? l : 0;
    }

    public int GetInt(object?[] row, string column) => (int)GetLong(row, column);
}
