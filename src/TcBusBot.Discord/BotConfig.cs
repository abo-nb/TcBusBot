using TcBusBot.Core.Configuration;
using TcBusBot.Core.Storage;
using TcBusBot.Core.Tdx;

namespace TcBusBot.Discord;

/// <summary>
/// 設定來源優先序：**命令列參數 &gt; 環境變數 &gt; .env 檔**。
///
/// 刻意不引入 Microsoft.Extensions.Configuration（這個環境的 NuGet 快取沒有它），
/// 用 <see cref="DotEnv"/> 自己讀 .env。
/// .env 會從目前工作目錄往上找，所以 `dotnet run --project src\TcBusBot.Discord`
/// 在專案根目錄執行時就找得到。
/// </summary>
public sealed class BotConfig
{
    public string Token { get; private set; } = "";

    /// <summary>Token 從哪裡來的（只記錄來源，不記錄值），方便啟動時確認。</summary>
    public string TokenSource { get; private set; } = "（未設定）";

    public string EnvFile { get; private set; } = "（找不到 .env）";

    /// <summary>這個 .env 是怎麼找到的（命令列 / 環境變數 / 自動搜尋）—— 啟動時印出來，避免搞錯檔。</summary>
    public string EnvFileSource { get; private set; } = "";

    /// <summary>使用者明確指定了 .env 路徑卻找不到時的警告訊息（null = 沒事）。</summary>
    public string? EnvWarning { get; private set; }

    /// <summary>訂閱組的 SQLite 資料庫路徑（`:memory:` 表示不落地）。</summary>
    public string DatabasePath { get; private set; } = "tcbus.db";

    /// <summary>
    /// MongoDB 連線字串（空 = 不用 MongoDB，走本機的 SQLite／文字檔）。
    /// 設定之後訂閱組會存在 MongoDB，**所有平台共用同一份**（手機、電腦、雲端主機）。
    /// </summary>
    public string MongoUri { get; private set; } = "";

    /// <summary>MongoDB 資料庫名稱（預設 `tcbus`）。</summary>
    public string MongoDatabase { get; private set; } = "tcbus";

    /// <summary>從 .env 匯出到行程環境變數的鍵數（0 = 全部都已經由系統環境變數提供）。</summary>
    public int EnvExportedKeys { get; private set; }

    /// <summary>連線字串的遮罩預覽（**絕對不印出帳密**）。</summary>
    public string MongoPreview => string.IsNullOrWhiteSpace(MongoUri)
        ? "未設定（訂閱組存在本機）"
        : MongoSavedGroupRepository.Mask(MongoUri);

    /// <summary>auto（有金鑰就用 TDX，否則用內建資料集）| fixture | tdx</summary>
    public string DataSource { get; private set; } = "auto";

    public string FixturesPath { get; private set; } = "";
    public bool Refresh { get; private set; }
    public ulong? GuildId { get; private set; }
    public int DefaultNotifyMinutes { get; private set; } = 10;
    public int PollIntervalSeconds { get; private set; } = 30;
    public bool EnablePoller { get; private set; } = true;

    /// <summary>印出所有 Discord.Net 的連線日誌（排查連線問題用）。</summary>
    public bool Verbose { get; private set; }

    /// <summary>等 Ready 的秒數；超過就視為連線失敗並結束。</summary>
    public int ConnectTimeoutSeconds { get; private set; } = 30;

    public TdxOptions Tdx { get; } = new();

    public static BotConfig Load(string[] args)
    {
        var cfg = new BotConfig();

        // .env 的位置有三種來源（與其他設定同一個優先序：命令列 > 環境變數 > 自動搜尋）
        //   1) --env <檔案或資料夾>
        //   2) TCBUS_ENV / TCBUS_ENV_FILE 環境變數
        //   3) 自動搜尋：目前工作目錄與執行檔目錄往上找 .env
        var envPath = SettingResolver.TakeOption(args, "--env");
        var envFrom = "命令列 --env";

        if (string.IsNullOrWhiteSpace(envPath))
        {
            envPath = Environment.GetEnvironmentVariable("TCBUS_ENV")
                      ?? Environment.GetEnvironmentVariable("TCBUS_ENV_FILE");
            envFrom = "環境變數 TCBUS_ENV";
        }

        var explicitlyAsked = !string.IsNullOrWhiteSpace(envPath);
        var found = DotEnv.FindFile(envPath);
        var file = DotEnv.LoadFromFile(found);

        if (found is not null)
        {
            cfg.EnvFile = found;
            cfg.EnvFileSource = explicitlyAsked ? $"（{envFrom}）" : "（自動搜尋）";
        }
        else
        {
            cfg.EnvFile = explicitlyAsked ? $"（找不到 {envPath}）" : "（找不到 .env）";
            if (explicitlyAsked)
                cfg.EnvWarning = $"找不到指定的 .env：{envPath}";
        }


        var token = SettingResolver.Resolve(args, "--token", file, ["DISCORD_TOKEN", "DISCORD_BOT_TOKEN", "BOT_TOKEN", "TOKEN"]);

        cfg.Token = token.Value ?? "";
        cfg.TokenSource = token.Source;

        var dataSource = SettingResolver.Resolve(args, "--data", file, ["TCBUS_DATA"]);
        cfg.DataSource = (dataSource.Value ?? "auto").ToLowerInvariant();

        cfg.FixturesPath = SettingResolver.Resolve(args, "--fixtures", file, ["TCBUS_FIXTURES"]).Value ?? "";
        cfg.DatabasePath = SettingResolver.Resolve(args, "--db", file, ["TCBUS_DB", "TCBUS_DATABASE"]).Value ?? "tcbus.db";
        cfg.MongoUri = SettingResolver.Resolve(args, "--mongo", file,
                               ["TCBUS_MONGO", "TCBUS_MONGO_URI", "MONGO_URI"]).Value ?? "";
        cfg.MongoDatabase = SettingResolver.Resolve(args, "--mongo-db", file, ["TCBUS_MONGO_DB"]).Value ?? "tcbus";
        cfg.Refresh = args.Contains("--refresh");

        var guild = SettingResolver.Resolve(args, "--guild", file, ["DISCORD_GUILD_ID"]).Value;
        if (ulong.TryParse(guild, out var gid)) cfg.GuildId = gid;

        cfg.Tdx.ClientId = SettingResolver.Resolve(args, "--tdx-id", file, ["TDX_CLIENT_ID", "TDX_ID"]).Value ?? "";
        cfg.Tdx.ClientSecret = SettingResolver.Resolve(args, "--tdx-secret", file, ["TDX_CLIENT_SECRET", "TDX_SECRET"]).Value ?? "";
        cfg.Tdx.BaseUrl = SettingResolver.Resolve(args, "--tdx-base", file, ["TDX_BASE_URL"]).Value ?? cfg.Tdx.BaseUrl;
        cfg.Tdx.City = SettingResolver.Resolve(args, "--city", file, ["TDX_CITY"]).Value ?? cfg.Tdx.City;
        cfg.Tdx.CacheDirectory = SettingResolver.Resolve(args, "--cache", file, ["TCBUS_CACHE"]).Value ?? "cache";

        if (int.TryParse(SettingResolver.Resolve(args, "--poll", file, ["TCBUS_POLL_INTERVAL"]).Value, out var poll))
            cfg.PollIntervalSeconds = poll;
        if (int.TryParse(SettingResolver.Resolve(args, "--notify", file, ["TCBUS_NOTIFY_MINUTES"]).Value, out var notify))
            cfg.DefaultNotifyMinutes = notify;
        if (args.Contains("--no-poller")) cfg.EnablePoller = false;
        cfg.Verbose = args.Contains("--verbose") || args.Contains("-v");

        if (int.TryParse(SettingResolver.Resolve(args, "--connect-timeout", file, ["TCBUS_CONNECT_TIMEOUT"]).Value, out var ct))
            cfg.ConnectTimeoutSeconds = ct;

        // ★ 最後才把 .env 的內容匯出到行程環境變數（讓程式內任何地方都能用
        //   Environment.GetEnvironmentVariable 讀到）。
        //   ⚠️ 一定要在 Resolve 之後：否則 Resolve 會先在環境變數裡找到 .env 的值，
        //      啟動橫幅就會把「來自 .env」誤報成「來自環境變數」。
        //   已存在的系統環境變數不會被覆蓋（維持「命令列 > 環境變數 > .env」的優先序）。
        cfg.EnvExportedKeys = DotEnv.ApplyToEnvironment(file);

        return cfg;
    }

    /// <summary>實際要用的資料來源。</summary>
    public string EffectiveDataSource => DataSource switch
    {
        "fixture" => "fixture",
        "tdx" => "tdx",
        _ => Tdx.HasCredentials ? "tdx" : "fixture"
    };

    /// <summary>Token 的遮罩預覽（絕對不印出完整值）。</summary>
    public string TokenPreview => string.IsNullOrEmpty(Token)
        ? "（未設定）"
        : $"{Token.Length} 字元，來源：{TokenSource}";

    // ─────────────────────────────────────────────────────
}
