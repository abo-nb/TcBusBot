namespace TcBusBot.Core.Chat;

/// <summary>
/// LLM 聊天的設定（全部都可以用環境變數／.env／命令列覆寫）。
///
/// 為什麼集中成一個類別：這一堆參數彼此有關聯
/// （上下文長度 ↔ token 估算 ↔ 每週上限），散在好幾個地方很容易對不起來。
/// </summary>
public sealed class LlmOptions
{
    /// <summary>OpenAI 相容 API 的 base URL（要含 /v1）。</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    public string Model { get; set; } = "deepseek-flash";

    public string? ApiKey { get; set; }

    /// <summary>系統提示：決定 Bot 的人格與範圍。</summary>
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;

    /// <summary>每週 token 上限（**全域**，UTC 週一 00:00 重置）。0 = 不限。</summary>
    public long WeeklyTokenLimit { get; set; } = 300_000;

    /// <summary>超過這個時間沒說話，就當成新的段落（不帶舊上下文）——不必問 LLM 就知道。</summary>
    public int SegmentGapMinutes { get; set; } = 30;

    /// <summary>時間沒超過門檻時，要不要再讓 LLM 判斷「是不是換話題了」。</summary>
    public bool TopicDetect { get; set; } = true;

    /// <summary>帶進提示詞的歷史上限（則）。</summary>
    public int MaxContextTurns { get; set; } = 20;

    /// <summary>帶進提示詞的歷史上限（估算 token）。</summary>
    public int MaxContextTokens { get; set; } = 3000;

    /// <summary>單次回覆的輸出上限。</summary>
    public int MaxOutputTokens { get; set; } = 800;

    public double Temperature { get; set; } = 0.7;

    /// <summary>一個段落最多留幾則訊息（超過就丟掉最舊的）。</summary>
    public int MaxTurnsPerSegment { get; set; } = 24;

    /// <summary>每個頻道最多留幾個段落（給「回覆舊訊息」用）。</summary>
    public int MaxSegmentsPerChannel { get; set; } = 4;

    /// <summary>同時追蹤幾個頻道（超過就淘汰最久沒動的）。</summary>
    public int MaxChannels { get; set; } = 500;

    /// <summary>頻道多久沒動就整個忘掉。</summary>
    public TimeSpan ChannelTtl { get; set; } = TimeSpan.FromHours(12);

    /// <summary>私訊要不要回（預設不要：私訊沒有「@ 機器人」這個動作，容易被誤觸）。</summary>
    public bool AllowDm { get; set; }

    /// <summary>同一個使用者連續問的冷卻秒數（避免有人連點把額度燒掉）。</summary>
    public int UserCooldownSeconds { get; set; } = 3;

    /// <summary>單次呼叫的逾時。</summary>
    public int RequestTimeoutSeconds { get; set; } = 90;

    public const string DefaultSystemPrompt =
        "你是 Discord 伺服器裡的助理「笨蛋猫猫」，主要幫大家查台中公車" +
        "（路線、站牌、到站時間、轉乘），也可以一般閒聊。\n" +
        "規則：\n" +
        "1. 用繁體中文、口語、簡短回答，盡量三句話內講完。\n" +
        "2. 不確定的事就說不確定，**絕對不要編造班次、時刻或票價**。\n" +
        "3. 你沒有即時的公車資料。被問到「現在幾分到」這類問題時，老實說你查不到，" +
        "並提示對方可以用 `/bus panel` 訂閱到站通知。\n" +
        "4. 不要重複對方的問題，直接回答。";

    /// <summary>有沒有設定到可以呼叫（少了 key 或 model 就等於沒開啟）。</summary>
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(ApiKey)
           && !string.IsNullOrWhiteSpace(Model)
           && !string.IsNullOrWhiteSpace(BaseUrl);

    public TimeSpan SegmentGap => TimeSpan.FromMinutes(Math.Max(1, SegmentGapMinutes));

    /// <summary>對話段落的識別碼（不同伺服器一定不相通；私訊用 0）。</summary>
    public static (ulong GuildId, ulong ChannelId) Key(ulong guildId, ulong channelId) => (guildId, channelId);

    /// <summary>金鑰只露出前後幾碼（log 與 /ai status 用）。</summary>
    public string MaskedKey
        => string.IsNullOrWhiteSpace(ApiKey)
            ? "（未設定）"
            : ApiKey!.Length <= 8
                ? "****"
                : $"{ApiKey[..4]}…{ApiKey[^4..]}（{ApiKey.Length} 字元）";

    /// <summary>只印 host，不要把完整 URL（可能含自架服務的內網位址）大聲講出來。</summary>
    public string EndpointHost
    {
        get
        {
            try { return new Uri(BaseUrl).Authority; }
            catch (Exception) { return BaseUrl; }
        }
    }

    public string Describe()
        => IsConfigured
            ? $"{Model} @ {EndpointHost}（每週上限 " +
              (WeeklyTokenLimit > 0 ? $"{WeeklyTokenLimit:N0} tokens" : "不限") + "）"
            : "未啟用（沒有 LLM_API_KEY）";

    public LlmOptions Clone() => (LlmOptions)MemberwiseClone();
}
