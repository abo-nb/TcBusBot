using Android.Database;
using Android.Database.Sqlite;
using TcBusBot.Core.Storage;

// Android 的 Java 繫結沒有可為 null 的標註，這裡的兩個警告沒有意義：
//   CS8620  rawQuery(String[]) 允許 null 元素（代表 SQL NULL）
//   CS8604  execSQL 傳 null 代表「沒有繫結參數」（正常用法）
#pragma warning disable CS8620, CS8604

namespace TcBusBot.Mobile;

/// <summary>
/// Android 的 SQLite 後端 —— 走系統內建的 <c>android.database.sqlite.SQLiteDatabase</c>。
///
/// 為什麼不沿用 Windows 那套 P/Invoke：
///   `libsqlite3.so` 是 Android 的**私有**函式庫（不在 public.libraries.txt 裡），
///   從 Android 7（API 24）起應用程式 `dlopen` 不到它。
///   但 Android 自己就有完整的 SQLite，而且 .NET Android 已經把 Java API 繫結好了
///   （<c>Android.Database.Sqlite</c>），所以不需要 NDK、不需要原生套件、不需要 NuGet。
///
/// 產生的就是一般的 SQLite 檔案，跟 Windows 版互相複製也打得開。
/// </summary>
public sealed class AndroidSqliteBackend : ISqliteBackend
{
    private readonly SQLiteDatabase _db;

    public AndroidSqliteBackend(string path)
    {
        _db = path == ":memory:"
            ? SQLiteDatabase.OpenOrCreateDatabase(":memory:", null)
            : SQLiteDatabase.OpenOrCreateDatabase(path, null);

        IsPersistent = path != ":memory:";

        Exec("PRAGMA journal_mode = WAL;");
        Exec("PRAGMA busy_timeout = 3000;");
    }

    public string? Version
    {
        get
        {
            try
            {
                using var cursor = _db.RawQuery("SELECT sqlite_version();", null);
                return cursor.MoveToFirst() ? cursor.GetString(0) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public bool IsPersistent { get; }

    public int Execute(string sql, params object?[] args)
    {
        _db.ExecSQL(sql, BindArgs(args));

        using var cursor = _db.RawQuery("SELECT changes();", null);
        return cursor.MoveToFirst() ? cursor.GetInt(0) : 0;
    }

    public void Exec(string sql, params object?[] args)
        => _db.ExecSQL(sql, BindArgs(args));

    public SqliteResult Query(string sql, params object?[] args)
    {
        using var cursor = _db.RawQuery(sql, StringArgs(args));

        var columns = cursor.GetColumnNames() ?? Array.Empty<string>();
        var rows = new List<object?[]>();

        while (cursor.MoveToNext())
        {
            var row = new object?[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                row[i] = cursor.GetType(i) switch
                {
                    FieldType.Null => null,
                    FieldType.Integer => cursor.GetLong(i),
                    FieldType.Float => cursor.GetDouble(i),
                    _ => cursor.GetString(i)
                };
            }
            rows.Add(row);
        }

        return new SqliteResult(columns, rows);
    }

    public void Dispose() => _db.Close();

    /// <summary>
    /// 寫入用的繫結參數。<c>execSQL</c> 接受 Java 物件，
    /// 所以 long / int / bool / double 都會以原本的型別綁進去。
    /// </summary>
    private static Java.Lang.Object[]? BindArgs(object?[] args)
    {
        if (args.Length == 0) return null;

        var bound = new Java.Lang.Object?[args.Length];
        for (var i = 0; i < args.Length; i++)
            bound[i] = ToJava(args[i]);

        return bound!;
    }

    /// <summary>
    /// 查詢用的繫結參數。
    ///
    /// ⚠️ Android 的 <c>rawQuery(String, String[])</c> **只接受字串**，
    /// 數字要先轉成字串；SQLite 會依欄位的型別親和性（column affinity）
    /// 把 `'4'` 當成整數 4 來比對，所以 `WHERE id = ?` 仍然正確。
    /// （專案裡只有 id / notify_minutes / route_count 這幾個整數欄位會用到。）
    /// </summary>
    private static string?[]? StringArgs(object?[] args)
        => args.Length == 0 ? null : args.Select(a => a?.ToString()).ToArray();

    private static Java.Lang.Object? ToJava(object? value) => value switch
    {
        null => null,
        // 用 ValueOf 而不是 new Java.Lang.Long(...)：建構子在 API 33 之後已過時
        long l => Java.Lang.Long.ValueOf(l),
        int n => Java.Lang.Long.ValueOf(n),
        bool b => Java.Lang.Long.ValueOf(b ? 1 : 0),
        double d => Java.Lang.Double.ValueOf(d),
        _ => new Java.Lang.String(value.ToString())
    };
}
