using TcBusBot.Core.Chat;
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

    /// <summary>
    /// 健康檢查端點要聽的埠。Render 會用 `PORT` 環境變數指定，所以預設讀 `PORT`、沒有才用 8080。
    /// </summary>
    public int Port { get; private set; } = 8080;

    /// <summary>是否啟動健康檢查端點（`--no-health` 可關掉，例如手機版不需要）。</summary>
    public bool EnableHealthEndpoint { get; private set; } = true;

    /// <summary>防休眠要 ping 的網址（`APP_URL`／`--app-url`；Render 也會自動提供 `RENDER_EXTERNAL_URL`）。</summary>
    public string AppUrl { get; private set; } = "";

    /// <summary>防休眠的間隔分鐘數（預設 10）。</summary>
    public int KeepAliveMinutes { get; private set; } = 10;

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

    /// <summary>LLM 聊天設定（沒有 LLM_API_KEY 時 <see cref="LlmOptions.IsConfigured"/> 為 false，功能整組關掉）。</summary>
    public LlmOptions Llm { get; } = new();

    /// <summary>
    /// 要不要向 Discord 要 Message Content 特權意圖。
    ///
    /// ⚠️ 這是「讀取訊息內容」的權限，**必須同時在 Discord Developer Portal 開啟**
    /// （Bot → Privileged Gateway Intents → Message Content Intent），
    /// 否則閘道會用 4014（Disallowed intent）把連線踢掉，Bot 完全連不上。
    /// 所以：沒設定 LLM 時預設不要（維持原本只要 Guilds 就能跑），
    /// 需要時可以用 --no-message-intent 關掉。
    /// </summary>
    public bool EnableMessageContentIntent { get; private set; }

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

        // ── 部署到 Render（或其他 PaaS）用 ──────────────────
        // Render 會用 PORT 指定要監聽的埠；APP_URL 是「自己的公開網址」，用來防休眠。
        // RENDER_EXTERNAL_URL 是 Render 自動注入的，所以兩種寫法都支援。
        var port = SettingResolver.Resolve(args, "--port", file, ["PORT", "TCBUS_PORT"]).Value;
        if (int.TryParse(port, out var pv) && pv is > 0 and < 65536) cfg.Port = pv;

        var appUrl = SettingResolver.Resolve(args, "--app-url", file,
                                             ["APP_URL", "RENDER_EXTERNAL_URL"]).Value;
        cfg.AppUrl = appUrl ?? "";

        if (int.TryParse(SettingResolver.Resolve(args, "--keep-alive", file, ["KEEP_ALIVE_MINUTES"]).Value,
                         out var ka) && ka > 0)
            cfg.KeepAliveMinutes = ka;

        if (args.Contains("--no-health")) cfg.EnableHealthEndpoint = false;

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

        // ── LLM（OpenAI 相容 API，Semantic Kernel 實作）──────
        cfg.Llm.ApiKey = SettingResolver.Resolve(args, "--llm-key", file,
            ["LLM_API_KEY", "OPENAI_API_KEY", "OPENAI_KEY", "DEEPSEEK_API_KEY"]).Value;

        cfg.Llm.BaseUrl = SettingResolver.Resolve(args, "--llm-url", file,
            ["LLM_BASE_URL", "OPENAI_BASE_URL", "LLM_ENDPOINT"]).Value ?? cfg.Llm.BaseUrl;

        cfg.Llm.Model = SettingResolver.Resolve(args, "--llm-model", file,
            ["LLM_MODEL", "OPENAI_MODEL"]).Value ?? cfg.Llm.Model;

        var systemPrompt = SettingResolver.Resolve(args, "--llm-prompt", file, ["LLM_SYSTEM_PROMPT"]).Value;
        if (!string.IsNullOrWhiteSpace(systemPrompt)) cfg.Llm.SystemPrompt = systemPrompt!;

        if (long.TryParse(SettingResolver.Resolve(args, "--llm-weekly-tokens", file,
                ["LLM_WEEKLY_TOKENS", "LLM_WEEKLY_LIMIT"]).Value, out var weekly) && weekly >= 0)
            cfg.Llm.WeeklyTokenLimit = weekly;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-gap", file,
                ["LLM_SEGMENT_GAP_MINUTES", "LLM_GAP_MINUTES"]).Value, out var gap) && gap is > 0 and <= 1440)
            cfg.Llm.SegmentGapMinutes = gap;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-context-turns", file,
                ["LLM_MAX_CONTEXT_TURNS"]).Value, out var turns) && turns is > 0 and <= 100)
            cfg.Llm.MaxContextTurns = turns;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-context-tokens", file,
                ["LLM_MAX_CONTEXT_TOKENS"]).Value, out var ctxTokens) && ctxTokens >= 256)
            cfg.Llm.MaxContextTokens = ctxTokens;

        // ── 每個頻道「記多少」（記憶體用量 ↔ 上下文品質的取捨）──────
        //    ⚠️ 這幾個是**儲存**的上限，跟上面「送多少」（LLM_MAX_CONTEXT_*）不一樣：
        //       存得比送得多沒關係（多的只是留著給「回覆舊訊息」用）。
        if (int.TryParse(SettingResolver.Resolve(args, "--llm-turns-per-segment", file,
                ["LLM_MAX_TURNS_PER_SEGMENT"]).Value, out var perSegment) && perSegment is >= 4 and <= 500)
            cfg.Llm.MaxTurnsPerSegment = perSegment;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-segments-per-channel", file,
                ["LLM_MAX_SEGMENTS_PER_CHANNEL"]).Value, out var perChannel) && perChannel is >= 1 and <= 50)
            cfg.Llm.MaxSegmentsPerChannel = perChannel;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-max-channels", file,
                ["LLM_MAX_CHANNELS"]).Value, out var maxChannels) && maxChannels is >= 1 and <= 100_000)
            cfg.Llm.MaxChannels = maxChannels;

        if (double.TryParse(SettingResolver.Resolve(args, "--llm-channel-ttl-hours", file,
                ["LLM_CHANNEL_TTL_HOURS"]).Value, out var ttlHours) && ttlHours is >= 0.25 and <= 8760)
            cfg.Llm.ChannelTtl = TimeSpan.FromHours(ttlHours);

        // ── 「學到的提示詞」的容量（每伺服器幾條／每條幾個字／總長…）────
        //    ⚠️ 這些同時是「提示詞會多長」（＝每次的錢）與「別人能叫它記多少」的上限。
        if (int.TryParse(SettingResolver.Resolve(args, "--llm-guild-rules", file,
                ["LLM_MAX_GUILD_RULES"]).Value, out var guildRules) && guildRules is >= 1 and <= 500)
            cfg.Llm.MaxGuildRules = guildRules;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-rule-chars", file,
                ["LLM_MAX_RULE_CHARS"]).Value, out var ruleChars) && ruleChars is >= 20 and <= 4000)
            cfg.Llm.MaxRuleChars = ruleChars;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-persona-guilds", file,
                ["LLM_MAX_PERSONA_GUILDS"]).Value, out var personaGuilds) && personaGuilds is >= 1 and <= 10_000)
            cfg.Llm.MaxPersonaGuilds = personaGuilds;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-overlay-chars", file,
                ["LLM_MAX_OVERLAY_CHARS"]).Value, out var overlayChars) && overlayChars is >= 100 and <= 20_000)
            cfg.Llm.MaxOverlayChars = overlayChars;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-audit-entries", file,
                ["LLM_MAX_AUDIT_ENTRIES"]).Value, out var auditEntries) && auditEntries is >= 0 and <= 10_000)
            cfg.Llm.MaxAuditEntries = auditEntries;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-max-output", file,
                ["LLM_MAX_OUTPUT_TOKENS"]).Value, out var maxOut) && maxOut is > 0 and <= 8192)
            cfg.Llm.MaxOutputTokens = maxOut;

        if (double.TryParse(SettingResolver.Resolve(args, "--llm-temperature", file,
                ["LLM_TEMPERATURE"]).Value, out var temp) && temp is >= 0 and <= 2)
            cfg.Llm.Temperature = temp;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-cooldown", file,
                ["LLM_USER_COOLDOWN_SECONDS"]).Value, out var cooldown) && cooldown >= 0)
            cfg.Llm.UserCooldownSeconds = cooldown;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-timeout", file,
                ["LLM_TIMEOUT_SECONDS"]).Value, out var llmTimeout) && llmTimeout is >= 5 and <= 600)
            cfg.Llm.RequestTimeoutSeconds = llmTimeout;

        var topicDetect = SettingResolver.Resolve(args, "--llm-topic-detect", file,
            ["LLM_TOPIC_DETECT"]).Value;
        if (!string.IsNullOrWhiteSpace(topicDetect))
            cfg.Llm.TopicDetect = !IsFalsy(topicDetect!);

        var allowDm = SettingResolver.Resolve(args, "--llm-dm", file, ["LLM_ALLOW_DM"]).Value;
        if (!string.IsNullOrWhiteSpace(allowDm)) cfg.Llm.AllowDm = !IsFalsy(allowDm!);

        var tools = SettingResolver.Resolve(args, "--llm-tools", file, ["LLM_TOOLS"]).Value;
        if (!string.IsNullOrWhiteSpace(tools)) cfg.Llm.ToolsEnabled = !IsFalsy(tools!);
        if (args.Contains("--no-llm-tools")) cfg.Llm.ToolsEnabled = false;

        var reasoning = SettingResolver.Resolve(args, "--llm-reasoning", file, ["LLM_REASONING"]).Value;
        if (!string.IsNullOrWhiteSpace(reasoning)) cfg.Llm.Reasoning = reasoning!;
        if (args.Contains("--llm-think")) cfg.Llm.Reasoning = "auto";

        // ── 主人（後台指定的命令者 ＋ 特殊 key）─────────────
        var adminIds = SettingResolver.Resolve(args, "--llm-admin-ids", file,
            ["LLM_ADMIN_IDS", "LLM_OWNER_IDS"]).Value;

        if (!string.IsNullOrWhiteSpace(adminIds))
        {
            cfg.Llm.AdminUserIds = adminIds!
                .Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => ulong.TryParse(x.Trim(), out var id) ? id : 0UL)
                .Where(id => id != 0)
                .Distinct()
                .ToArray();
        }

        cfg.Llm.AdminKey = SettingResolver.Resolve(args, "--llm-admin-key", file,
            ["LLM_ADMIN_KEY", "LLM_OWNER_KEY"]).Value;

        var exposeIds = SettingResolver.Resolve(args, "--llm-expose-ids", file, ["LLM_EXPOSE_IDS"]).Value;
        if (!string.IsNullOrWhiteSpace(exposeIds)) cfg.Llm.ExposeUserIds = !IsFalsy(exposeIds!);

        var mentions = SettingResolver.Resolve(args, "--llm-mentions", file, ["LLM_ALLOW_MENTIONS"]).Value;
        if (!string.IsNullOrWhiteSpace(mentions)) cfg.Llm.AllowMentions = !IsFalsy(mentions!);

        // ── 讓模型看到 Discord 暱稱（預設開；關掉就一律用 @帳號）────
        var nicknames = SettingResolver.Resolve(args, "--llm-nicknames", file, ["LLM_SHOW_NICKNAMES"]).Value;
        if (!string.IsNullOrWhiteSpace(nicknames))
        {
            cfg.Llm.ShowNicknames = !IsFalsy(nicknames!);
            cfg.Llm.TellSelfName = cfg.Llm.ShowNicknames;
        }
        if (args.Contains("--no-llm-nicknames"))
        {
            cfg.Llm.ShowNicknames = false;
            cfg.Llm.TellSelfName = false;
        }

        // ── 面板動作與偷聽 ────────────────────────────────
        var uiActions = SettingResolver.Resolve(args, "--llm-ui", file, ["LLM_UI_ACTIONS"]).Value;
        if (!string.IsNullOrWhiteSpace(uiActions)) cfg.Llm.UiActions = !IsFalsy(uiActions!);
        if (args.Contains("--no-llm-ui")) cfg.Llm.UiActions = false;

        var eavesdrop = SettingResolver.Resolve(args, "--llm-eavesdrop", file, ["LLM_EAVESDROP"]).Value;
        if (!string.IsNullOrWhiteSpace(eavesdrop)) cfg.Llm.Eavesdrop = !IsFalsy(eavesdrop!);

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-eavesdrop-messages", file,
                ["LLM_EAVESDROP_MESSAGES"]).Value, out var evMsgs) && evMsgs is >= 0 and <= 100)
            cfg.Llm.EavesdropMaxMessages = evMsgs;

        if (int.TryParse(SettingResolver.Resolve(args, "--llm-eavesdrop-seconds", file,
                ["LLM_EAVESDROP_SECONDS"]).Value, out var evSec) && evSec is >= 0 and <= 3600)
            cfg.Llm.EavesdropSeconds = evSec;

        var eavesContext = SettingResolver.Resolve(args, "--llm-eavesdrop-context", file,
            ["LLM_EAVESDROP_CONTEXT"]).Value;
        if (!string.IsNullOrWhiteSpace(eavesContext)) cfg.Llm.EavesdropContext = !IsFalsy(eavesContext!);

        // 需要讀訊息內容才有 AI 聊天 → 有設 LLM 就預設要這個意圖
        cfg.EnableMessageContentIntent = cfg.Llm.IsConfigured;
        if (args.Contains("--no-message-intent")) cfg.EnableMessageContentIntent = false;
        if (args.Contains("--message-intent")) cfg.EnableMessageContentIntent = true;

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

    /// <summary>true／1／yes／on → true；false／0／no／off → false（大小寫不拘）。</summary>
    private static bool IsFalsy(string value)
        => value.Trim().ToLowerInvariant() is "0" or "false" or "no" or "off" or "n";

    // ─────────────────────────────────────────────────────
}
