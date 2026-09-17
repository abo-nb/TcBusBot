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
                .WithFooter($"共 {lines.Count}/{GuildPersonaStore.MaxLinesPerGuild} 條｜只對這個伺服器生效｜/rest 可以重設")
                .Build(),
            ephemeral: true);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

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
