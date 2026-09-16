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

    /// <summary>
    /// ⚠️ 所有參數都**必填**：Discord.Net 挑的是「參數最多的建構子」，
    /// 給預設值會讓它挑到解析不到的那個（見 <see cref="DisabledLlmClient"/> 的說明）。
    /// 沒有啟用時容器會注入 <see cref="DisabledLlmClient"/>。
    /// </summary>
    public ChatModule(
        LlmOptions options,
        ConversationStore conversations,
        WeeklyTokenBudget budget,
        ILlmClient llm)
    {
        _options = options;
        _conversations = conversations;
        _budget = budget;
        _llm = llm;
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
                        ? "✅ 已啟用：查站牌／查路線／訂閱公車／列出訂閱／取消訂閱／到站時間"
                        : "❌ 已關閉（LLM_TOOLS=false）→ 只會聊天，不會動到你的訂閱",
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
                      (snapshot.Idle is { } idle ? $"（最後一次 {idle.TotalMinutes:0} 分鐘前）" : ""),
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
