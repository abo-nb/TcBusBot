using Discord;
using Discord.Interactions;
using TcBusBot.Core.Chat;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// <c>/rest</c>：把「這個伺服器學到的規矩」恢復成預設。
///
/// 為什麼要一個獨立的頂層指令（而不是 `/ai rest`）：
/// 「被教壞了想重來」是很直覺的動作，打三個字就要能用。
///
/// 重設的範圍（使用者要的）：
///   * **這個伺服器**學到的提示詞（語氣偏好、自訂表情的意思…）—— 清掉
///   * 其他伺服器完全不受影響
///   * 主機的 `LLM_SYSTEM_PROMPT` 不會被動到（那是主機端的設定）
///
/// 順便（可選）也可以清掉「目前這個頻道的對話記憶」——那跟學到的規矩是兩件事，
/// 所以做成選項而不是預設一起清。
///
/// ⚠️ 清掉的內容會**原文列出來**：教了很多條的人如果後悔，可以直接複製回去。
/// </summary>
public sealed class ResetModule : InteractionModuleBase<SocketInteractionContext>
{
    private const int MaxEchoLength = 3500;

    private readonly GuildPersonaStore _personas;
    private readonly ConversationStore _conversations;

    public ResetModule(GuildPersonaStore personas, ConversationStore conversations)
    {
        _personas = personas;
        _conversations = conversations;
    }

    [SlashCommand("rest", "重設這個伺服器學到的 AI 規矩（提示詞恢復預設）")]
    public async Task RestAsync(
        [Summary("forget_chat", "順便忘掉「這個頻道」的對話記憶（預設也會清）")]
        bool forgetChat = true)
    {
        var guildId = Context.Guild?.Id ?? 0;
        var removed = _personas.Reset(guildId);
        var clearedChat = forgetChat && _conversations.Reset(guildId, Context.Channel.Id);

        Console.WriteLine($"[rest] 使用者 {Context.User.Id} 在伺服器 {guildId} 重設：" +
                          $"{removed.Count} 條規矩（清對話記憶：{clearedChat}）");

        if (removed.Count == 0 && !clearedChat)
        {
            await RespondAsync(
                "這個伺服器本來就沒有學到任何規矩，這個頻道也沒有對話記憶 —— 不需要重設。",
                ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithColor(new Color(0x2D, 0x9C, 0x4F))
            .WithTitle("♻️ 已重設（只影響這個伺服器）")
            .WithDescription(
                $"清掉了 **{removed.Count}** 條學到的規矩" +
                (forgetChat ? "，並忘掉這個頻道的對話記憶。" : "。（對話記憶保留）") +
                "\n\nAI 現在回到預設的人格；其他伺服器完全不受影響。");

        if (removed.Count > 0)
        {
            var echo = string.Join("\n", removed.Select((l, i) => $"{i + 1}. {l}"));

            if (echo.Length > MaxEchoLength)
                echo = echo[..MaxEchoLength] + "\n…（以下省略）";

            embed.AddField("清掉的內容（想留就複製走）", Truncate(echo, 1024), inline: false);
        }

        await RespondAsync(embed: embed.Build(), ephemeral: true);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
