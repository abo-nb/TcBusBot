using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// <c>/say</c>：讓 Bot 幫你把一句話說出來。
///
/// 這是「無用小功能」—— 跟公車完全無關，純粹好玩。
/// 但它意外地有一個真實用途：**驗收通知通道**。
/// 想知道 Bot 到底能不能在這個頻道發訊息、Embed 會不會被權限擋掉，
/// 本來得等到公車真的快到；現在打一句話就知道。
///
/// 三個刻意的設計：
///   * <c>AllowedMentions.None</c> —— 不然任何人都能叫 Bot 去 @everyone 洗頻
///   * 內容打 <c>\n</c> 會變成真正的換行（斜線指令的輸入框打不出多行）
///   * 環境變數 <c>SAY_ALLOWED_USERS</c> 可以限制誰能用（沒設定 = 所有人都能用）
/// </summary>
public sealed class SayModule : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Discord 單一訊息的內容上限。</summary>
    private const int MaxContent = 2000;

    /// <summary>允許名單用的環境變數名稱（逗號分隔的使用者 ID）。</summary>
    public const string AllowListVariable = "SAY_ALLOWED_USERS";

    [SlashCommand("say", "讓 Bot 幫你說一句話（無用小功能）")]
    public async Task SayAsync(
        // ⚠️ [Summary] 的第一個參數是**參數名稱**（不是說明）。刻意用 ASCII 的 message：
        //    Discord 對指令名稱的字元集有限制，中文在文件上合法但沒必要冒險 ——
        //    只有一個參數時，使用者在 Discord 直接打內容就好，不會看到這個名字。
        [Summary("message", "要讓 Bot 說的內容；打 \\n 會變成換行")]
        [MaxLength(MaxContent)]
        string message)
    {
        var text = (message ?? "").Trim();

        if (text.Length == 0)
        {
            await RespondAsync("要說點什麼吧？例如 `/say 公車快到了`", ephemeral: true);
            return;
        }

        if (!IsAllowed(Context.User.Id))
        {
            await RespondAsync(
                $"❌ 你不在 `/say` 的允許名單裡。\n" +
                $"（環境變數 `{AllowListVariable}` 可以放允許的使用者 ID，用逗號分隔；" +
                $"你的 ID 是 `{Context.User.Id}`）",
                ephemeral: true);
            return;
        }

        // 斜線指令的輸入框沒辦法直接打多行，所以讓「打 \n 就換行」成立
        text = text.Replace("\\n", "\n", StringComparison.Ordinal);

        if (text.Length > MaxContent)
        {
            Console.WriteLine($"[say] 內容 {text.Length} 字超過 Discord 上限，已截斷");
            text = text[..(MaxContent - 1)] + "…";
        }

        var where = Context.Channel is SocketGuildChannel guildChannel
            ? $"#{guildChannel.Name}"
            : "私訊";

        Console.WriteLine($"[say] {Context.User.Username}（{Context.User.Id}）在 {where} 讓 Bot 說：" +
                          text.Replace("\n", " ⏎ "));

        // ⚠️ 一定要限制 mention：否則任何人都可以叫 Bot 去 @everyone
        await RespondAsync(text: text, allowedMentions: AllowedMentions.None);
    }

    /// <summary>
    /// 允許名單：環境變數 <c>SAY_ALLOWED_USERS</c>，用逗號／分號／空白分隔使用者 ID。
    ///
    /// **沒有設定 = 所有人都能用**（這是自己的 Bot，預設從寬）；
    /// 一旦設定了，就只有名單上的人能用 —— 在多人伺服器裡才不會被拿來洗頻。
    ///
    /// 刻意每次呼叫都重讀，不用 static 快取：.env 是在 <c>BotConfig.Load</c> 之後
    /// 才匯出到環境變數的，而模組是更早被掃描的，快取有可能讀到還沒匯出的值。
    /// 公開是為了讓離線驗證（<c>--dryrun</c>）能直接測這段解析。
    /// </summary>
    public static ulong[] AllowedUsers()
    {
        var raw = Environment.GetEnvironmentVariable(AllowListVariable);
        if (string.IsNullOrWhiteSpace(raw)) return [];

        return raw.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                  .Select(part => ulong.TryParse(part.Trim(), out var id) ? id : 0UL)
                  .Where(id => id != 0)
                  .ToArray();
    }

    public static bool IsAllowed(ulong userId)
    {
        var allowed = AllowedUsers();
        return allowed.Length == 0 || allowed.Contains(userId);
    }
}
