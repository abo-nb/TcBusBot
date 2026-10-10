using Discord;
using Discord.Interactions;
using TcBusBot.Core.Chat;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// `/stop`：**強制停止跟其他 App 的對話**，直到有真人找它。
///
/// 為什麼需要這個指令（使用者指定的停損方式）：
/// 要兩個機器人自由聊天（沒有題目、沒有腳本），就一定要有一個「當場喊停」的開關 ——
/// 因為「聊夠了」只有人知道，程式猜不出來。
///
/// ── 三個刻意的設計 ─────────────────────────────────────
///   * **只有「停」沒有「開始」**：要恢復只要**真人 @ 它**（使用者的說法：
///     「停到有真人找他」）。多一顆「開始」按鈕只會讓人不小心把迴圈又打開。
///   * **誰都能按**：這是「停止花錢」的方向，讓一般成員也能按是安全的
///     （反過來，如果是「開始聊天」的指令就一定要管理員）。
///   * **狀態存進資料庫**：正是「沒有人在看」的時候最需要它 ——
///     只放在記憶體的話，一次重啟就讓兩個機器人繼續聊。
/// </summary>
public sealed class StopModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly AppChatGuard _guard;
    private readonly LlmChatService _chat;

    public StopModule(AppChatGuard guard, LlmChatService chat)
    {
        _guard = guard;
        _chat = chat;
    }

    [SlashCommand("stop", "停止跟其他 App（機器人）的對話，直到有真人 @ 它")]
    public async Task StopAsync(
        [Summary("all", "連其他頻道也一起停（預設只停這個頻道）")] bool all = false)
    {
        var channelId = Context.Channel.Id;
        var now = DateTimeOffset.UtcNow;

        var state = _guard.Stop(channelId, Context.User.Id, now);
        var stoppedOthers = all ? _guard.StopAll(Context.User.Id, now) : 0;

        Console.WriteLine($"[App 對話] /stop：{Context.User.Username}（{Context.User.Id}）" +
                          $"停了 {channelId}{(all ? $"＋全部（{stoppedOthers} 個頻道）" : "")}");

        await RespondAsync(
            embed: new EmbedBuilder()
                .WithColor(new Color(0xC0, 0x39, 0x2B))
                .WithTitle("⏹ 已停止跟其他 App 的對話")
                .WithDescription(
                    (all ? "**所有頻道**都停了。\n" : $"停在 <#{channelId}>。\n") +
                    "它還是**看得到**其他 App 說的話（會記進上下文），但**不會回話**了。")
                .AddField("怎麼恢復",
                    "**真人 @ 它**（或回覆它）就會自動恢復 —— 這是刻意的：" +
                    "只有人知道「可以繼續聊了」。",
                    inline: false)
                .AddField("同時也會停的", 
                    "每小時上限（`LLM_APP_CHAT_PER_HOUR`）也在擋 —— " +
                    "那一道是給「沒有任何人記得按 /stop」的情況用的。",
                    inline: false)
                .WithFooter($"{now.ToLocalTime():MM-dd HH:mm}｜{_guard.Describe()}")
                .Build(),
            ephemeral: true);
    }
}
