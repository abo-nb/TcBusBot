using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using TcBusBot.Core.Chat;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// `/chat`：**讓 Bot 跟這個頻道裡的其他 App（機器人）對話**（管理員啟用）。
///
/// 為什麼要做成「每個頻道各自啟用」而不是一個環境變數：
///   * 要不要跟另一個機器人聊是**那個頻道的事** —— 別的頻道可能只想看到通知，
///     不想被兩個機器人的對話洗版
///   * 啟用的人應該就在那裡看得到成果（而且隨時能停）
///   * 環境變數是「主機的預設值」（預設是「看得到但不回話」），不是開關
///
/// 停損（兩個都要看）：
///   * `/stop` —— 當場喊停，停到有真人 @ 它（見 <see cref="StopModule"/>）
///   * 每小時上限 —— 無人看管的保險（`/chat enable cap:` 可以只改這個頻道）
/// </summary>
[Group("chat", "跟這個頻道裡的其他 App（機器人）對話")]
public sealed class AppChatModule : BusModuleBase
{
    private readonly AppChatGuard _guard;
    private readonly LlmOptions _llm;

    public AppChatModule(AppChatGuard guard, LlmOptions llm)
    {
        _guard = guard;
        _llm = llm;
    }

    private bool IsAdmin => _llm.AdminUserIds.Contains(Context.User.Id);

    private string AdminOnlyMessage()
        => "這個動作只有 **Bot 管理員**（主機端 `LLM_ADMIN_IDS` 名單）可以做 ——\n" +
           "啟用之後它會跟其他機器人一來一往，而每一句都是真的 LLM 呼叫。\n" +
           "（`/chat status` 任何人都可以看。）";

    [SlashCommand("enable", "在這個頻道啟用「跟其他 App 對話」（Bot 管理員）")]
    public async Task EnableAsync(
        [Summary("app", "只跟這一個機器人對話（留空＝任何 App 都會回）")] SocketUser? app = null,
        [Summary("cap", "這個頻道每小時最多回幾句（留空＝用主機預設）")] int cap = 0)
    {
        if (!IsAdmin)
        {
            await RespondAsync(AdminOnlyMessage(), ephemeral: true);
            return;
        }

        if (Context.Guild is null)
        {
            await RespondAsync("這個指令要在伺服器頻道裡使用。", ephemeral: true);
            return;
        }

        if (app is not null && !app.IsBot && !app.IsWebhook)
        {
            await RespondAsync(
                $"❌ `{app.Username}` 不是 App（機器人／webhook）。\n" +
                "這個功能是設計來跟**另一個機器人**對話的 —— 一般使用者直接 @ 它就好，不需要這個指令。",
                ephemeral: true);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var state = _guard.Enable(Context.Channel.Id, Context.User.Id, app?.Id ?? 0, cap, now);
        var effectiveCap = _guard.HourCapFor(Context.Channel.Id, _llm.AppChatPerHour);

        Console.WriteLine($"[App 對話] {Context.User.Username} 在 {Context.Channel.Id} 啟用" +
                          $"（{(app is null ? "任何 App" : app.Username)}、上限 {effectiveCap} 句/小時）");

        await RespondAsync(
            embed: new EmbedBuilder()
                .WithColor(new Color(0x2E, 0x8B, 0x57))
                .WithTitle("✅ 已啟用「跟其他 App 對話」")
                .WithDescription(
                    (app is null
                        ? "這個頻道裡**任何** App（機器人／webhook）的訊息，它都會回。"
                        : $"它只會回 **{app.Username}** 的訊息（其他 App 仍然只「看得到」不回話）。") +
                    "\n\n它會 **@ 對方**（多數機器人只認 mention，不叫它就不會動）。")
                .AddField("怎麼停",
                    "• **`/stop`**：當場停，停到有真人 @ 它\n" +
                    $"• 每小時上限：**{effectiveCap}** 句" +
                    (cap > 0 ? "（這個頻道自己的設定）" : "（主機預設，可用 `cap:` 改）") +
                    "\n• `/chat disable`：完全關掉這個頻道的對話",
                    inline: false)
                .AddField("它還是會做的事",
                    "看得到其他 App 說的話（記進上下文、標成 `[App]`）—— 所以你問它" +
                    "「你們剛剛聊了什麼」它答得出來；`/ai think` 也看得到它的思考。",
                    inline: false)
                .WithFooter($"啟用者：{Context.User.Username}｜{now.ToLocalTime():MM-dd HH:mm}")
                .Build(),
            ephemeral: true);
    }

    [SlashCommand("disable", "關掉這個頻道的「跟其他 App 對話」（Bot 管理員）")]
    public async Task DisableAsync()
    {
        if (!IsAdmin)
        {
            await RespondAsync(AdminOnlyMessage(), ephemeral: true);
            return;
        }

        var before = _guard.Config(Context.Channel.Id);

        if (!before.Enabled)
        {
            await RespondAsync(
                "這個頻道本來就沒有啟用（它現在只會「看得到」其他 App 的訊息，不會回話）。",
                ephemeral: true);
            return;
        }

        _guard.Disable(Context.Channel.Id);

        Console.WriteLine($"[App 對話] {Context.User.Username} 停用了 {Context.Channel.Id}");

        await RespondAsync(
            "⏹ 已關掉這個頻道的「跟其他 App 對話」。\n" +
            "它還是看得到其他 App 的訊息（會記進上下文），但不會再回話了。\n" +
            "（設定留著 —— 要再開隨時 `/chat enable`。）",
            ephemeral: true);
    }

    [SlashCommand("status", "看這個頻道的「跟其他 App 對話」設定")]
    public async Task StatusAsync()
    {
        var state = _guard.Config(Context.Channel.Id);
        var effectiveCap = _guard.HourCapFor(Context.Channel.Id, _llm.AppChatPerHour);
        var blocked = _guard.BlockReason(Context.Channel.Id, effectiveCap, DateTimeOffset.UtcNow);
        var sent = _guard.SentThisHour(Context.Channel.Id, DateTimeOffset.UtcNow);

        var embed = new EmbedBuilder()
            .WithColor(state.Enabled ? new Color(0x2E, 0x8B, 0x57) : new Color(0x5A, 0x5A, 0x5A))
            .WithTitle("💬 跟其他 App 對話")
            .AddField("這個頻道", state.Enabled ? "✅ 已啟用" : "⏸ 未啟用（只會看到訊息，不會回話）", inline: false)
            .AddField("對象",
                !state.Enabled
                    ? "—"
                    : state.AppId == 0
                        ? "任何 App（機器人／webhook）"
                        : $"只跟 <@{state.AppId}>",
                inline: false)
            .AddField("每小時上限",
                effectiveCap <= 0
                    ? "不限（⚠️ 只靠 `/stop`）"
                    : $"{effectiveCap} 句" + (state.HourCap > 0 ? "（這個頻道自己的設定）" : "（主機預設）"),
                inline: true)
            .AddField("這個小時已回", $"{sent} 句", inline: true)
            .AddField("現在能不能回",
                blocked is null ? "✅ 可以" : $"⛔ 不行：{blocked}",
                inline: false)
            .WithFooter(state.EnabledAt is null
                ? "管理員用 /chat enable 可以在這個頻道啟用"
                : $"由 <@{state.EnabledBy}> 於 {state.EnabledAt.Value.ToLocalTime():MM-dd HH:mm} 啟用｜/stop 可以立刻停")
            .Build();

        await RespondAsync(embed: embed, ephemeral: true);
    }
}
