using Android.Content;
using TcBusBot.Core.Storage;

namespace TcBusBot.Mobile;

/// <summary>
/// 手機版的設定：寫成一個標準的 <c>.env</c> 檔，再交給既有的 <c>BotConfig</c> 讀。
///
/// 為什麼這樣做（而不是做一份手機專用的設定類別）：
///   桌面版與手機版就**共用同一套設定邏輯**（`--env` 指向資料夾、金鑰別名、
///   優先序、錯誤訊息），行為不會因為平台不同而分岔。
///   而且使用者可以直接用「匯出／匯入 .env」在兩邊搬移設定。
///
/// 檔案都在 App 的私有目錄（<c>/data/data/com.tcbusbot.mobile/files</c>）：
///   app.env        設定（權限 600，等同金鑰）
///   tcbus.db       訂閱組的 SQLite
///   cache/         TDX 靜態資料快取
///   fixtures/      沒有 TDX 金鑰時用的離線資料集
/// </summary>
public static class AppSettings
{
    public const string EnvFileName = "app.env";

    /// <summary>
    /// 工作目錄（可在 App 裡換，見 <see cref="WorkDir"/>）。
    /// 預設是「手機儲存空間 /TcBusBot」，因為那裡用檔案管理員或 USB 就看得到 ——
    /// App 私有目錄在沒 root 的手機上幾乎拿不出來。
    /// </summary>
    public static string Dir(Context ctx) => WorkDir.Resolve(ctx);

    /// <summary>私有目錄的前綴（UI 用來判斷「檔案是不是拿不出來」）。</summary>
    public static string PrivateDirHint => "/data/";

    public static string EnvPath(Context ctx) => Path.Combine(Dir(ctx), EnvFileName);
    public static string DbPath(Context ctx) => Path.Combine(Dir(ctx), "tcbus.db");
    public static string CacheDir(Context ctx) => Path.Combine(Dir(ctx), "cache");
    public static string FixturesDir(Context ctx) => Path.Combine(Dir(ctx), "fixtures");

    /// <summary>目前的設定（沒有檔案時回傳空值）。</summary>
    public static AppSettingsValues Load(Context ctx)
    {
        var map = TcBusBot.Core.Configuration.DotEnv.LoadFromFile(EnvPath(ctx));
        return new AppSettingsValues(
            Token: map.GetValueOrDefault("DISCORD_TOKEN") ?? "",
            TdxId: map.GetValueOrDefault("TDX_CLIENT_ID") ?? "",
            TdxSecret: map.GetValueOrDefault("TDX_CLIENT_SECRET") ?? "",
            MongoUri: map.GetValueOrDefault("TCBUS_MONGO") ?? "",
            PollSeconds: map.TryGetValue("TCBUS_POLL_INTERVAL", out var p) && int.TryParse(p, out var pv) ? pv : 30,
            NotifyMinutes: map.TryGetValue("TCBUS_NOTIFY_MINUTES", out var n) && int.TryParse(n, out var nv) ? nv : 10);
    }

    public static void Save(Context ctx, AppSettingsValues values)
    {
        AppSettingsValues.EnsureBootstrapped(ctx);

        var lines = new List<string>
        {
            "# TcBusBot 手機版設定（由 App 產生；格式與桌面版的 .env 完全相同）",
            "# 這個檔案含金鑰，請不要外流。",
            "",
            $"DISCORD_TOKEN={values.Token.Trim()}",
        };

        if (!string.IsNullOrWhiteSpace(values.TdxId)) lines.Add($"TDX_CLIENT_ID={values.TdxId.Trim()}");
        if (!string.IsNullOrWhiteSpace(values.TdxSecret)) lines.Add($"TDX_CLIENT_SECRET={values.TdxSecret.Trim()}");

        // 有設定就用 MongoDB（手機、電腦、雲端主機共用同一份訂閱組）
        if (!string.IsNullOrWhiteSpace(values.MongoUri)) lines.Add($"TCBUS_MONGO={values.MongoUri.Trim()}");

        lines.Add($"TCBUS_POLL_INTERVAL={values.PollSeconds}");
        lines.Add($"TCBUS_NOTIFY_MINUTES={values.NotifyMinutes}");

        File.WriteAllLines(EnvPath(ctx), lines);

        // 只給這個 App 讀
        try { File.SetUnixFileMode(EnvPath(ctx), UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception) { /* 有些檔案系統不支援，不影響功能 */ }

        BotConsole.Write($"[設定] 已寫入 {EnvPath(ctx)}\n");
    }

    /// <summary>
    /// 把 APK 裡打包的離線資料集複製到工作目錄（第一次啟動／換目錄時），
    /// 讓「沒有 TDX 金鑰也能用」這個桌面版能力在手機上同樣成立。
    ///
    /// 實際工作交給 <see cref="WorkDir.EnsureStructure"/> —— 那裡也會試寫一個檔案，
    /// 目錄存在不等於有寫入權限（Android 11+ 的公開目錄就是這樣）。
    /// </summary>
    public static void EnsureBootstrapped(Context ctx)
        => WorkDir.EnsureStructure(ctx, Dir(ctx), out _);

    /// <summary>
    /// 組出餵給 <c>Program.Main</c> 的參數。
    ///
    /// 全部走既有的命令列開關（桌面版也是同一組），所以沒有「手機版專屬行為」：
    ///   --env       指向放 app.env 的資料夾（BotConfig 支援資料夾）
    ///   --db        訂閱組的 SQLite（手機可寫路徑）
    ///   --cache     TDX 靜態資料快取
    ///   --fixtures  沒有金鑰時用的離線資料集
    /// </summary>
    public static string[] BuildBotArgs(Context ctx)
    {
        EnsureBootstrapped(ctx);

        var values = Load(ctx);
        var args = new List<string>
        {
            "--env", Dir(ctx),
            "--db", DbPath(ctx),
            "--cache", CacheDir(ctx),
            "--fixtures", FixturesDir(ctx),
            "--poll", values.PollSeconds.ToString(),
            "--notify", values.NotifyMinutes.ToString(),
        };

        // 沒有 TDX 金鑰 → 用離線資料集（桌面版是 auto，這裡明確一點，日誌才看得懂）
        args.Add("--data");
        args.Add(string.IsNullOrWhiteSpace(values.TdxId) ? "fixture" : "auto");

        // 訂閱組存到 MongoDB（有設定才加；沒設定就走本機的 SQLite／文字檔）
        if (!string.IsNullOrWhiteSpace(values.MongoUri))
        {
            args.Add("--mongo");
            args.Add(values.MongoUri.Trim());
        }

        return args.ToArray();
    }
}

/// <summary>手機版看得到的那幾個設定。</summary>
public sealed record AppSettingsValues(
    string Token,
    string TdxId,
    string TdxSecret,
    string MongoUri,
    int PollSeconds,
    int NotifyMinutes)
{
    /// <summary>第一次啟動時把預設檔案與資料集準備好。</summary>
    public static void EnsureBootstrapped(Context ctx) => AppSettings.EnsureBootstrapped(ctx);
}
