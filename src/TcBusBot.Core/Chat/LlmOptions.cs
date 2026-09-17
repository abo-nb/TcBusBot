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

    /// <summary>要不要讓 LLM 判斷「是不是換話題了」。</summary>
    public bool TopicDetect { get; set; } = true;

    /// <summary>
    /// 要不要讓模型看到「傳訊息的人是誰」：**伺服器暱稱 ＋ @帳號名 ＋ Discord ID**。
    ///
    /// 為什麼要給 ID：不然它分不出同名的人，也沒辦法把規則綁在特定人身上
    /// （「某某是管理員，對他要有禮貌」）。代價是它可能把 ID 貼到頻道上。
    /// 關掉的話就只給暱稱。
    /// </summary>
    public bool ExposeUserIds { get; set; } = true;

    /// <summary>
    /// 要不要讓模型在回覆裡 @ 別人（它會輸出 <c>&lt;@ID&gt;</c>）。
    ///
    /// ⚠️ 只允許 @ **使用者**；@everyone／@here／@身分組一律擋掉（見 LlmChatService）。
    /// </summary>
    public bool AllowMentions { get; set; } = true;

    /// <summary>
    /// 「主人」的 Discord 使用者 ID（逗號分隔）。**要下指令還必須在訊息裡帶上
    /// <see cref="AdminKey"/>**，兩個條件都成立才會被當成授權指令。
    /// </summary>
    public ulong[] AdminUserIds { get; set; } = [];

    /// <summary>
    /// 授權用的特殊 key。訊息裡出現這個字串（且發話者在名單裡）→ 這一則就是主人的命令。
    ///
    /// ⚠️ 訊息裡的 key 會被**移除**之後才送進模型與對話記憶，log 也不會印出來 ——
    ///    否則 key 會留在歷史與 console 裡，等於沒有保護。
    /// </summary>
    public string? AdminKey { get; set; }

    /// <summary>主人機制是否可用（名單與 key 都設定了才算）。**沒設 key 就整個關閉**（fail closed）。</summary>
    public bool AdminEnabled
        => AdminUserIds.Length > 0 && !string.IsNullOrWhiteSpace(AdminKey);

    /// <summary>給橫幅與 `/ai status` 看的一行說明（**絕對不印出 key**）。</summary>
    public string AdminDescription
        => !AdminEnabled
            ? "未啟用"
            : AdminUserIds.Length == 1
                ? $"1 位主人（ID …{AdminUserIds[0] % 100000:00000}）＋ 需要特殊 key"
                : $"{AdminUserIds.Length} 位主人 ＋ 需要特殊 key";

    /// <summary>
    /// 要不要讓 LLM 用工具**真的動手**（訂閱公車、取消訂閱…）。
    /// 關掉就只剩聊天 —— 模型不會有任何改變資料的能力。
    /// </summary>
    public bool ToolsEnabled { get; set; } = true;

    /// <summary>
    /// 要不要讓模型「思考」（reasoning／thinking）。
    ///
    /// 這個用途（聊天、訂閱公車、判斷換話題）不需要思考，而思考會**吃掉輸出額度又算錢**
    /// （實測：`1+1=?` 這種問題也花了 31 個 reasoning tokens，設 `max_tokens=100` 時
    /// 80 個都被思考用掉、回覆還被截斷）。
    ///
    /// | 值 | 行為 |
    /// | --- | --- |
    /// | `off`（預設） | 送出 `reasoning_effort: "none"` ＋ `thinking: {"type":"disabled"}`（實測有效） |
    /// | `auto` | 完全不動，用服務端的預設值（換到不認識這些欄位的服務時設這個） |
    /// | `low` / `medium` / `high` | 只送 `reasoning_effort: <值>`（明確要思考時） |
    /// </summary>
    public string Reasoning { get; set; } = "off";

    /// <summary>轉成要注入請求 JSON 的欄位（純函式，可離線測試；空 = 什麼都不加）。</summary>
    public static IReadOnlyDictionary<string, object?> ReasoningFields(string? reasoning)
        => (reasoning ?? "off").Trim().ToLowerInvariant() switch
        {
            // 兩個都送：實測這個端點兩種寫法都認（OpenAI 系與 Anthropic 系各一種）
            "off" or "none" or "false" or "0" => new Dictionary<string, object?>
            {
                ["reasoning_effort"] = "none",
                ["thinking"] = new Dictionary<string, object?> { ["type"] = "disabled" }
            },

            "low" or "medium" or "high" => new Dictionary<string, object?>
            {
                ["reasoning_effort"] = reasoning!.Trim().ToLowerInvariant()
            },

            // auto / 其他任何值 → 不注入
            _ => new Dictionary<string, object?>()
        };

    /// <summary>給橫幅與 `/ai status` 看的一行說明。</summary>
    public string ReasoningDescription => Reasoning.Trim().ToLowerInvariant() switch
    {
        "off" or "none" or "false" or "0" => "已關閉（省 token）",
        "low" or "medium" or "high" => $"reasoning_effort={Reasoning.Trim().ToLowerInvariant()}",
        _ => "服務端預設"
    };


    /// <summary>
    /// 要不要讓模型操作「面板的動作」（等於幫使用者按按鈕）。
    ///
    /// 使用者要的是「不用自己點那一串 設定起點→搜尋→勾選→確認→找路線→訂閱」，
    /// 直接說「幫我把起點設成台中車站、然後訂 300 跟 304」。
    /// 開啟後會多一組 `ui` 工具（開面板／設起訖／搜路線／訂閱／復原上一動作），
    /// 而且回覆會**附上真正的按鈕**（面板、路線選單），使用者想接手點也可以。
    /// </summary>
    public bool UiActions { get; set; } = true;

    /// <summary>
    /// **偷聽模式**：回完話之後，接下來「沒有 @ 它」的訊息也聽一下，
    /// 由 LLM 判斷要不要接話（<see cref="Addressee"/>：接話／先不出聲繼續聽／退出）。
    ///
    /// ⚠️ 需要 Message Content 特權意圖才做得到（沒有意圖時，非提及訊息的內容是空的）。
    /// ⚠️ 每一則需要判斷的訊息都會多花一次判斷的錢（約 200~300 tokens），
    ///    所以有「最多判斷幾則」與「安靜幾秒後停止」兩個上限，而且會記進每週額度。
    /// </summary>
    public bool Eavesdrop { get; set; } = true;

    /// <summary>
    /// 偷聽期間最多**判斷**幾則（每一則都要問一次模型）。
    ///
    /// ⚠️ 這個數字要當成「成本上限」，不是「對話長度上限」：
    /// 一群人在聊天的時候，Bot 在中間被 @ 一次就會重新開窗，
    /// 太小（例如 3）會讓它講兩句就退出，使用者看到的是「後面的訊息全被忽略」。
    /// </summary>
    public int EavesdropMaxMessages { get; set; } = 12;

    /// <summary>
    /// 偷聽的**閒置**時間窗：最後一則訊息之後幾秒沒人講話就停止偷聽、回到等 @。
    ///
    /// ⚠️ 是滑動的（每一則訊息都往後延），不是「從開窗起算」——
    /// 否則一群人聊超過這個秒數之後，Bot 就會中途退出。
    /// </summary>
    public int EavesdropSeconds { get; set; } = 120;

    /// <summary>
    /// 偷聽到的訊息要不要**留下來當上下文**。
    ///
    /// 使用者要的是「我們剛剛聊的事情，之後 @ 它時它接得上」：
    /// 只記 Bot 有回覆的訊息會讓上下文缺一大塊（一群人在聊、Bot 中間插一句，
    /// 之後再 @ 它，它完全不知道大家在聊什麼）。
    /// 這些訊息在對話裡標成 <see cref="ChatTurn.Ambient"/>，提示詞中以 `[閒聊]` 呈現，
    /// 讓模型知道那是背景、不要回它們。
    ///
    /// 關掉只影響「要不要記」，不影響判斷與回話。
    /// </summary>
    public bool EavesdropContext { get; set; } = true;

    /// <summary>帶進提示詞的歷史上限（則）。</summary>
    public int MaxContextTurns { get; set; } = 20;

    /// <summary>帶進提示詞的歷史上限（估算 token）。</summary>
    public int MaxContextTokens { get; set; } = 3000;

    /// <summary>單次回覆的輸出上限。</summary>
    public int MaxOutputTokens { get; set; } = 800;

    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// 一個段落最多留幾則訊息（超過就丟掉最舊的）。環境變數：`LLM_MAX_TURNS_PER_SEGMENT`（預設 24）。
    ///
    /// 這是**儲存**上限（記憶體），跟「送幾則給模型」（<see cref="MaxContextTurns"/>）是兩件事：
    /// 存得比送得多沒關係，多的那一些留著給「回覆舊訊息」用。
    /// </summary>
    public int MaxTurnsPerSegment { get; set; } = 24;

    /// <summary>
    /// 每個頻道最多留幾個段落（給「回覆舊訊息」用）。環境變數：`LLM_MAX_SEGMENTS_PER_CHANNEL`（預設 4）。
    /// </summary>
    public int MaxSegmentsPerChannel { get; set; } = 4;

    /// <summary>
    /// 同時追蹤幾個頻道（超過就淘汰最久沒動的）。環境變數：`LLM_MAX_CHANNELS`（預設 500）。
    ///
    /// 這是整台 Bot 的記憶體上限：總量 ≈ MaxChannels × MaxSegmentsPerChannel × MaxTurnsPerSegment 則。
    /// </summary>
    public int MaxChannels { get; set; } = 500;

    /// <summary>
    /// 頻道多久沒動就整個忘掉。環境變數：`LLM_CHANNEL_TTL_HOURS`（預設 12 小時，可給小數）。
    /// </summary>
    public TimeSpan ChannelTtl { get; set; } = TimeSpan.FromHours(12);

    // ─────────────────────────────────────────────────────
    //  「學到的提示詞」（每個伺服器一份的 overlay）容量上限
    //
    //  為什麼這幾個要可調：它們同時決定**提示詞會多長**（＝每次對話的錢）
    //  與**任何人都能叫 Bot 記多少東西**（公開伺服器的濫用風險）。
    //  私人小伺服器可以放寬，公開大伺服器建議收緊。
    // ─────────────────────────────────────────────────────

    /// <summary>每個伺服器最多學幾條規則（環境變數 `LLM_MAX_GUILD_RULES`，預設 40）。</summary>
    public int MaxGuildRules { get; set; } = 40;

    /// <summary>每一條規則最多幾個字（環境變數 `LLM_MAX_RULE_CHARS`，預設 300）。</summary>
    public int MaxRuleChars { get; set; } = 300;

    /// <summary>最多幾個伺服器可以有自己的規則（環境變數 `LLM_MAX_PERSONA_GUILDS`，預設 200）。</summary>
    public int MaxPersonaGuilds { get; set; } = 200;

    /// <summary>規則接進提示詞的總長上限（環境變數 `LLM_MAX_OVERLAY_CHARS`，預設 2000）。</summary>
    public int MaxOverlayChars { get; set; } = 2000;

    /// <summary>主人操作紀錄最多留幾筆（環境變數 `LLM_MAX_AUDIT_ENTRIES`，預設 200）。</summary>
    public int MaxAuditEntries { get; set; } = 200;

    /// <summary>把上面五個組成 <see cref="PersonaLimits"/>（給 `GuildPersonaStore` 用）。</summary>
    public PersonaLimits BuildPersonaLimits() => new()
    {
        MaxLinesPerGuild = MaxGuildRules,
        MaxLineLength = MaxRuleChars,
        MaxGuilds = MaxPersonaGuilds,
        MaxOverlayLength = MaxOverlayChars,
        MaxAuditEntries = MaxAuditEntries
    };

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
              (WeeklyTokenLimit > 0 ? $"{WeeklyTokenLimit:N0} tokens" : "不限") +
              $"，思考：{ReasoningDescription}）"
            : "未啟用（沒有 LLM_API_KEY）";

    /// <summary>
    /// 對話記憶的容量摘要（啟動 log、`--dryrun`、`/ai status` 用）。
    ///
    /// 為什麼要印出來：這幾個數字決定「這台 Bot 會吃掉多少記憶體」，
    /// 而且全部都是環境變數可調 —— 沒有印出來的話，改了也不知道有沒有生效。
    /// </summary>
    public string DescribeMemory()
        => $"每頻道最多 {MaxSegmentsPerChannel} 段 × {MaxTurnsPerSegment} 則、" +
           $"最多追蹤 {MaxChannels} 個頻道、閒置 {ChannelTtl.TotalHours:0.##} 小時忘記" +
           $"（全部加起來最壞約 {MaxChannels * (long)MaxSegmentsPerChannel * MaxTurnsPerSegment:N0} 則訊息）";

    public LlmOptions Clone() => (LlmOptions)MemberwiseClone();
}
