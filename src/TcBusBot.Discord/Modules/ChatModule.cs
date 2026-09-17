using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using TcBusBot.Core.Chat;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// <c>/ai</c>：AI 聊天的查詢與維護指令。
///
/// 聊天本身不用指令（@ 它、或回覆它的訊息就會回），這幾個指令是「看不到的東西要看得到」：
///   * <c>/ai status</c> — 用哪個模型、這一週花了多少 token、這個頻道的記憶有幾則
///   * <c>/ai forget</c> — 忘掉這個頻道的對話（換話題、或不想讓它記得時用）
///
/// ⚠️ 刻意**沒有**「開啟／關閉」指令：AI 有沒有開是**主機端**的設定（有沒有 LLM_API_KEY），
/// 在 Discord 上按一下就能改的東西會讓「到底有沒有開」變得不確定。
/// </summary>
[Group("ai", "AI 聊天（OpenAI 相容 API）")]
public sealed class ChatModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly LlmOptions _options;
    private readonly ConversationStore _conversations;
    private readonly WeeklyTokenBudget _budget;
    private readonly ILlmClient _llm;
    private readonly GuildPersonaStore _personas;

    /// <summary>
    /// ⚠️ 所有參數都**必填**：Discord.Net 挑的是「參數最多的建構子」，
    /// 給預設值會讓它挑到解析不到的那個（見 <see cref="DisabledLlmClient"/> 的說明）。
    /// 沒有啟用時容器會注入 <see cref="DisabledLlmClient"/>。
    /// </summary>
    public ChatModule(
        LlmOptions options,
        ConversationStore conversations,
        WeeklyTokenBudget budget,
        ILlmClient llm,
        GuildPersonaStore personas)
    {
        _options = options;
        _conversations = conversations;
        _budget = budget;
        _llm = llm;
        _personas = personas;
    }

    [SlashCommand("status", "看 AI 聊天的狀態：模型、這一週用掉多少 token、這個頻道的記憶")]
    public async Task StatusAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var guildId = Context.Guild?.Id ?? 0;
        var channelId = Context.Channel.Id;
        var snapshot = _conversations.Snapshot(guildId, channelId, now);
        var enabled = _llm.IsConfigured;

        var embed = new EmbedBuilder()
            .WithTitle("🤖 AI 聊天狀態")
            .WithColor(enabled ? new Color(0x2B, 0x6C, 0xB0) : new Color(0x5A, 0x5A, 0x5A))
            .AddField("啟用", enabled ? "✅ 已啟用" : "❌ 未啟用（主機沒有設定 LLM_API_KEY）", inline: false)
            .AddField("模型", enabled ? $"{_options.Model} @ {_options.EndpointHost}" : "—", inline: false)
            .AddField("工具（可以真的動手）",
                !enabled
                    ? "—"
                    : _options.ToolsEnabled
                        ? "✅ 已啟用：查站牌／查路線號碼／查路線／訂閱公車／列出訂閱／取消訂閱／到站時間／記住規矩"
                        : "❌ 已關閉（LLM_TOOLS=false）→ 只會聊天，不會動到你的訂閱",
                inline: false)
            .AddField("這個伺服器學到的規矩",
                _personas.Describe(guildId),
                inline: false)
            .AddField("每週額度",
                _budget.Limit <= 0
                    ? $"不限（已用 {_budget.Usage.TotalTokens:N0} tokens）"
                    : $"{_budget.Usage.TotalTokens:N0}／{_budget.Limit:N0} tokens" +
                      $"（剩 {Math.Max(0, _budget.Limit - _budget.Usage.TotalTokens):N0}）",
                inline: true)
            .AddField("重置時間",
                $"{WeeklyTokenBudget.ResetAt(now).ToLocalTime():MM-dd HH:mm}",
                inline: true)
            .AddField("本週呼叫",
                $"{_budget.Usage.Calls} 次（被額度擋下 {_budget.Usage.Refusals} 次）",
                inline: true)
            .AddField("這個頻道的記憶",
                snapshot is null
                    ? "（空的，還沒聊過）"
                    : $"{snapshot.SegmentCount} 段／目前 {snapshot.CurrentTurnCount} 則" +
                      (snapshot.AmbientTurnCount > 0 ? $"（含 {snapshot.AmbientTurnCount} 則偷聽到的閒聊）" : "") +
                      (snapshot.Idle is { } idle ? $"（最後一次 {idle.TotalMinutes:0} 分鐘前）" : ""),
                inline: false)
            .AddField("記憶容量（環境變數可調）",
                _options.DescribeMemory(),
                inline: false)
            .AddField("偷聽",
                !_options.Eavesdrop || _options.EavesdropMaxMessages <= 0 || _options.EavesdropSeconds <= 0
                    ? "❌ 已關閉（只回 @ 它或回覆它的訊息）"
                    : snapshot is { ListenRemaining: > 0 }
                        ? $"👂 正在偷聽：還能判斷 {snapshot.ListenRemaining} 則／" +
                          $"安靜 {snapshot.ListenTimeLeft?.TotalSeconds:0} 秒後停止"
                        : $"✅ 已啟用（回完話後開始聽：最多判斷 {_options.EavesdropMaxMessages} 則／" +
                          $"安靜 {_options.EavesdropSeconds} 秒後回到「等 @」）",
                inline: false)
            .WithFooter("用法：@ 我 或 回覆我的訊息就會回話｜/ai forget 可以清掉這個頻道的記憶");

        // 誰在花額度（全域額度的代價就是需要看得出來誰在花）
        var top = _budget.Usage.ByGuild
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"• {DescribeKey(kv.Key)}：{kv.Value:N0} tokens");

        if (_budget.Usage.ByGuild.Count > 0)
            embed.AddField("用量來源", string.Join("\n", top), inline: false);

        await RespondAsync(embed: embed.Build(), ephemeral: true);
    }

    [SlashCommand("learned", "看我（這個伺服器）學到了哪些規矩與自訂表情的意思")]
    public async Task LearnedAsync()
    {
        var guildId = Context.Guild?.Id ?? 0;
        var lines = _personas.Lines(guildId);

        if (lines.Count == 0)
        {
            await RespondAsync(
                "這個伺服器還沒有學到任何規矩 —— 是預設的樣子。\n\n" +
                "可以這樣教它：\n" +
                "• `@我 講話再簡短一點`（它會用 remember_rule 記下來）\n" +
                "• `@我 <自訂表情> 是 委屈`（表情名稱每個伺服器不一樣，只有這裡學到的才有意義）\n\n" +
                "學到的內容**只對這個伺服器生效**，用 `/rest` 可以整個重設。",
                ephemeral: true);
            return;
        }

        var numbered = lines.Select((l, i) => $"`{i + 1}.` {Truncate(l, 200)}");
        var description = string.Join("\n", numbered);

        if (description.Length > 4000) description = description[..4000] + "…";

        await RespondAsync(
            embed: new EmbedBuilder()
                .WithColor(new Color(0x2B, 0x6C, 0xB0))
                .WithTitle("📚 這個伺服器學到的規矩")
                .WithDescription(description)
                .WithFooter($"共 {lines.Count}/{_personas.Limits.MaxLinesPerGuild} 條｜只對這個伺服器生效｜/rest 可以重設")
                .Build(),
            ephemeral: true);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>`/ai pset` 的兩種模式。</summary>
    public enum PersonaMode
    {
        /// <summary>追加一條（跟模型自己學到的規則放在一起）。</summary>
        Append,

        /// <summary>覆蓋掉這個伺服器現有的全部自訂提示詞。</summary>
        Replace
    }

    /// <summary>
    /// `/ai pset`：**後台指定的管理員**（`LLM_ADMIN_IDS`）直接設定這個伺服器的自訂提示詞。
    ///
    /// 為什麼需要這個指令：模型自己學（`remember_rule`）很適合「使用者隨口教一句」，
    /// 但管理員想要的常常是**一開始就寫好、而且是精確的一整段**（例如「一律用繁體中文、
    /// 不要用條列、每次回答前先確認站名」）。用嘴巴講給模型聽會有不確定性，
    /// 也會佔用對話額度；這個指令就是「直接寫進去」。
    ///
    /// 寫的東西跟學到的是**同一份 overlay**（`GuildPersonaStore`）：
    ///   * `/ai learned` 看得到、`/rest` 清得掉（單一來源，不做第二套）
    ///   * 一樣受 `LLM_MAX_GUILD_RULES`／`LLM_MAX_RULE_CHARS` 的上限約束
    ///   * 每次都會記進稽核紀錄（`/ai audit`），因為這是「有人改了設定」
    ///
    /// ⚠️ 為什麼只認 `LLM_ADMIN_IDS` 而**不需要** `LLM_ADMIN_KEY`：
    ///    那把 key 是為了擋「模型被騙去打主人的指令」（訊息是模型讀得到的東西）。
    ///    斜線指令是 Discord 直接送到 Bot 的互動，**模型碰不到**，
    ///    而且少了 key 就不會出現在對話紀錄裡。真的被騙的風險在 `OwnerTools`（全域設定）那邊。
    /// </summary>
    [SlashCommand("pset", "設定這個伺服器的自訂提示詞（只有後台指定的管理員能用）")]
    public async Task PersonaSetAsync(
        [Summary("text", "要設定的內容（一句重點；留空＝只顯示目前的設定）")] string? text = null,
        [Summary("mode", "Append＝追加一條；Replace＝覆蓋掉這個伺服器現有的全部")] PersonaMode mode = PersonaMode.Append,
        [Summary("clear", "清空這個伺服器的自訂提示詞")] bool clear = false)
    {
        // ── 授權：後台指定的管理員（fail closed：沒設定名單＝沒人能用）──
        if (!_options.AdminUserIds.Contains(Context.User.Id))
        {
            await RespondAsync(
                "這個指令只有後台指定的管理員能用（`LLM_ADMIN_IDS` 名單上的人）。\n" +
                (Context.Guild is null ? "" : "你可以用 `@我 記住：…` 教它這個伺服器專屬的規矩。"),
                ephemeral: true);
            return;
        }

        var guildId = Context.Guild?.Id ?? 0;

        if (guildId == 0)
        {
            await RespondAsync("這個指令要在伺服器裡使用（自訂提示詞是「每個伺服器一份」的）。", ephemeral: true);
            return;
        }

        // ── 清空 ─────────────────────────────────────────
        if (clear)
        {
            var removed = _personas.Reset(guildId);

            if (removed.Count == 0)
            {
                await RespondAsync("這個伺服器本來就沒有自訂提示詞（不用清）。", ephemeral: true);
                return;
            }

            _personas.AuditAdmin(Context.User.Id, "清空這個伺服器的提示詞", $"{removed.Count} 條");

            await RespondAsync(
                $"🧹 已清空 {removed.Count} 條自訂提示詞（回到預設的樣子）。\n" +
                "下面是清掉的原文（想留就複製走）：\n" +
                Truncate(string.Join("\n", removed.Select(l => $"• {l}")), 3500),
                ephemeral: true);
            return;
        }

        // ── 沒給內容 → 顯示目前設定 ────────────────────────
        var current = _personas.Lines(guildId);

        if (string.IsNullOrWhiteSpace(text))
        {
            var body = current.Count == 0
                ? "（目前沒有自訂提示詞）"
                : string.Join("\n", current.Select((l, i) => $"`{i + 1}.` {Truncate(l, 300)}"));

            await RespondAsync(
                embed: new EmbedBuilder()
                    .WithColor(new Color(0x2B, 0x6C, 0xB0))
                    .WithTitle("🛠 這個伺服器的自訂提示詞")
                    .WithDescription(Truncate(body, 4000))
                    .WithFooter($"共 {current.Count}/{_personas.Limits.MaxLinesPerGuild} 條｜" +
                                $"用法：/ai pset text:<內容> mode:Append｜/ai pset clear:True")
                    .Build(),
                ephemeral: true);
            return;
        }

        // ── 設定（追加或覆蓋）─────────────────────────────
        var outcome = _personas.SetRules(guildId, Context.User.Id, text, replace: mode == PersonaMode.Replace);

        var message = outcome.Result switch
        {
            GuildPersonaStore.LearnResult.Added =>
                (mode == PersonaMode.Replace
                    ? $"✅ 已**覆蓋**這個伺服器的自訂提示詞（清掉 {outcome.Removed} 條舊的）。"
                    : "✅ 已**追加**一條自訂提示詞。") +
                $"\n目前共 {outcome.Lines.Count}/{_personas.Limits.MaxLinesPerGuild} 條：" +
                Truncate(string.Join("\n", outcome.Lines.Select(l => $"• {l}")), 3000),

            GuildPersonaStore.LearnResult.Duplicate =>
                "ℹ️ 這條已經在裡面了（沒有重複加）。目前內容：\n" +
                Truncate(string.Join("\n", outcome.Lines.Select(l => $"• {l}")), 3000),

            GuildPersonaStore.LearnResult.TooLong =>
                $"❌ 太長了（上限 {_personas.Limits.MaxLineLength} 字）。" +
                (mode == PersonaMode.Replace ? "舊的設定**沒有**被動到。" : "請縮短成一句重點。"),

            GuildPersonaStore.LearnResult.TooMany =>
                $"❌ 這個伺服器已經有 {_personas.Limits.MaxLinesPerGuild} 條（上限）了。" +
                (mode == PersonaMode.Replace ? "舊的設定**沒有**被動到。" : "可以先 `mode:Replace` 覆蓋，或清理一些。"),

            _ => "❌ 沒有內容可以設定。"
        };

        await RespondAsync(message, ephemeral: true);
    }

    [SlashCommand("audit", "看最近的主人操作紀錄（誰改了全域設定；只有主人看得到）")]
    public async Task AuditAsync()
    {
        // 唯讀、不洩漏 key，所以**不需要**在訊息裡帶 key ——
        // 主人在任何頻道都能查（但仍然只有名單上的人看得到）。
        var isAdmin = _options.AdminUserIds.Contains(Context.User.Id);

        if (!isAdmin)
        {
            await RespondAsync(
                "這個指令只有主人能看（`LLM_ADMIN_IDS` 名單上的人）。\n" +
                "它會揭露誰對**全域設定**做了什麼，屬於管理資訊。",
                ephemeral: true);
            return;
        }

        var entries = _personas.AuditLog(10);

        if (entries.Count == 0)
        {
            await RespondAsync("目前沒有任何主人操作紀錄（沒有人改過全域設定）。", ephemeral: true);
            return;
        }

        var lines = string.Join("\n", entries.Select(e => $"`{e.At.ToLocalTime():MM-dd HH:mm}` {e.Describe()}"));
        if (lines.Length > 4000) lines = lines[..4000] + "…";

        await RespondAsync(
            embed: new EmbedBuilder()
                .WithColor(new Color(0x5A, 0x5A, 0x5A))
                .WithTitle("📝 主人操作紀錄")
                .WithDescription(lines)
                .WithFooter($"最近 {entries.Count} 筆｜每次動到全域設定都會留下紀錄（含被拒絕的嘗試）")
                .Build(),
            ephemeral: true);
    }

    [SlashCommand("forget", "忘掉這個頻道的 AI 對話記憶（不影響公車訂閱）")]
    public async Task ForgetAsync()
    {
        var guildId = Context.Guild?.Id ?? 0;
        var removed = _conversations.Reset(guildId, Context.Channel.Id);

        await RespondAsync(
            removed
                ? "🧹 已經忘掉這個頻道的對話記憶。下一句會當成全新的對話。"
                : "這個頻道本來就沒有記憶（沒聊過，或被清過了）。",
            ephemeral: true);
    }

    private static string DescribeKey(string key)
        => key == "dm" ? "私訊" : $"伺服器 {key}";
}
