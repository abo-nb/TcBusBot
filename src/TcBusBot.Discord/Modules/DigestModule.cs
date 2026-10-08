using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using TcBusBot.Core.Chat;
using TcBusBot.Core.Chat.Digest;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// `/digest`：**每週自動整理某個頻道**（摘要 ＋ 重點 ＋ 跨週事項）的管理指令。
///
/// 為什麼是獨立的指令群組而不是 `/ai digest`：
///   * `/ai` 是「聊天的查詢與維護」（模型、額度、記憶），這是**頻道內容的自動化**
///   * 它的選項多（頻道、時間、回顧範圍），獨立成群組在 Discord 上比較好按
///
/// 授權：**寫入類的動作只給 Bot 管理員**（`LLM_ADMIN_IDS`）——
/// 使用者的要求就是這樣，而且這會花錢（每一次摘要都是一次真實的 LLM 呼叫）。
/// 讀取類（`status`／`archive`）不需要管理員：看得到那個頻道的人就查得到，
/// 而且都是用 ephemeral 回覆（只有自己看得到）。
/// </summary>
[Group("digest", "每週自動整理頻道（摘要與重點）")]
public sealed class DigestModule : BusModuleBase
{
    private readonly DigestStore _store;
    private readonly DigestRunner _runner;
    private readonly DigestOptions _options;
    private readonly LlmOptions _llm;

    public DigestModule(DigestStore store, DigestRunner runner, DigestOptions options, LlmOptions llm)
    {
        _store = store;
        _runner = runner;
        _options = options;
        _llm = llm;
    }

    private bool IsAdmin => _llm.AdminUserIds.Contains(Context.User.Id);

    private string AdminOnlyMessage()
        => "這個動作只有 **Bot 管理員**（主機端 `LLM_ADMIN_IDS` 名單）可以做 ——\n" +
           "它會讓 Bot 每週自動花錢呼叫模型整理頻道內容，所以不能開放給所有人。\n" +
           "（`/digest status` 與 `/digest archive` 任何人都可以看，都是只有你看得到的回覆。）";

    [SlashCommand("status", "看這個頻道的每週摘要設定（時間、游標、上次跑了什麼）")]
    public async Task StatusAsync(
        [Summary("channel", "要查的頻道（留空＝目前這個頻道）")] SocketTextChannel? channel = null)
    {
        var target = channel ?? Context.Channel as SocketTextChannel;

        if (target is null)
        {
            await RespondAsync("這個指令要在文字頻道裡用，或指定一個頻道。", ephemeral: true);
            return;
        }

        var config = _store.Config(target.Id);
        var body = _store.DescribeChannel(target.Id);

        if (config is null)
        {
            body += "\n\n用法：`/digest enable`（用預設的每週一 09:00）" +
                    "，或用 `schedule:` 指定時間（例如 `週三 20:00`）。" +
                    (IsAdmin ? "" : "\n（啟用需要 Bot 管理員權限。）");
        }

        await RespondAsync(
            embed: new EmbedBuilder()
                .WithColor(config?.Enabled == true ? new Color(0x2E, 0x8B, 0x57) : new Color(0x5A, 0x5A, 0x5A))
                .WithTitle($"📋 每週摘要　#{target.Name}")
                .WithDescription(body)
                .AddField("設定",
                    $"模型：{(string.IsNullOrWhiteSpace(_options.Model) ? _llm.Model : _options.Model)}" +
                    $"｜思考：{_options.Reasoning}｜最多 @ {_options.MaxMentions} 人｜" +
                    $"一次最多 {_options.EffectiveMaxMessages:N0} 則訊息",
                    inline: false)
                .WithFooter("每週自動整理；也可以隨時用 `/digest now` 手動跑一次｜歸檔永久保留")
                .Build(),
            ephemeral: true);
    }

    [SlashCommand("enable", "啟用這個頻道的每週摘要（Bot 管理員）")]
    public async Task EnableAsync(
        [Summary("channel", "要啟用的頻道（留空＝目前這個頻道）")] SocketTextChannel? channel = null,
        [Summary("schedule", "每週哪時候跑，例如「週一 09:00」「週三 20:00」（留空＝每週一 09:00）")]
        string? schedule = null,
        [Summary("backfill", "第一次要回顧多少（之後每週只算新訊息）")]
        DigestBackfill backfill = DigestBackfill.Now)
    {
        if (!IsAdmin)
        {
            await RespondAsync(AdminOnlyMessage(), ephemeral: true);
            return;
        }

        if (Context.Guild is null)
        {
            await RespondAsync("這個指令要在伺服器裡使用。", ephemeral: true);
            return;
        }

        var target = channel ?? Context.Channel as SocketTextChannel;

        if (target is null)
        {
            await RespondAsync("請指定一個文字頻道（或在那個頻道裡打這個指令）。", ephemeral: true);
            return;
        }

        if (!DigestSchedule.TryParse(schedule, out var parsed, out var error))
        {
            await RespondAsync($"❌ {error}", ephemeral: true);
            return;
        }

        var missing = DigestRunner.MissingPermissions(target);

        if (missing is not null)
        {
            await RespondAsync(
                $"❌ 我在 #{target.Name} 缺少權限：**{missing}**。\n" +
                "讀訊息歷史要「檢視頻道」＋「讀取訊息歷史」，發週報要「傳送訊息」。",
                ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var now = DateTimeOffset.UtcNow;
        var (cursorId, cursorAt, describe) = await ResolveBackfillAsync(target, backfill, now);

        var config = _store.Enable(
            Context.Guild.Id, target.Id, Context.User.Id, parsed, now, _options.ZoneOffset, cursorId, cursorAt);

        await FollowupAsync(
            embed: new EmbedBuilder()
                .WithColor(new Color(0x2E, 0x8B, 0x57))
                .WithTitle($"✅ 已啟用每週摘要　#{target.Name}")
                .AddField("時間", $"{parsed.Describe()}（UTC{_options.ZoneOffset.TotalHours:+0;-0}，台灣時間）", inline: false)
                .AddField("第一次的起點", describe, inline: false)
                .AddField("下一次會跑", $"{config.NextRunAt!.Value.ToOffset(_options.ZoneOffset):yyyy-MM-dd HH:mm}", inline: true)
                .AddField("之後", "每週只整理**上次之後的新訊息**，不會重複計算同一段內容", inline: false)
                .WithFooter("想現在就跑一次：/digest now｜查設定：/digest status｜停用：/digest disable")
                .Build(),
            ephemeral: true);
    }

    [SlashCommand("disable", "停用這個頻道的每週摘要（Bot 管理員；游標與歸檔會保留）")]
    public async Task DisableAsync(
        [Summary("channel", "要停用的頻道（留空＝目前這個頻道）")] SocketTextChannel? channel = null)
    {
        if (!IsAdmin)
        {
            await RespondAsync(AdminOnlyMessage(), ephemeral: true);
            return;
        }

        var target = channel ?? Context.Channel as SocketTextChannel;

        if (target is null)
        {
            await RespondAsync("請指定一個文字頻道。", ephemeral: true);
            return;
        }

        var updated = _store.Disable(target.Id);

        await RespondAsync(
            updated is null
                ? $"這個頻道本來就沒有啟用每週摘要。"
                : $"⏸ 已停用 #{target.Name} 的每週摘要。\n" +
                  $"游標與 {_store.ArchiveCount(target.Id)} 筆歸檔都**保留著** —— " +
                  "之後再 `/digest enable` 會接著原來的進度，不會重複整理舊訊息。",
            ephemeral: true);
    }

    [SlashCommand("schedule", "改這個頻道的時間（Bot 管理員）")]
    public async Task ScheduleAsync(
        [Summary("when", "例如「週一 09:00」「週三 20:00」「09:00」")] string when,
        [Summary("channel", "要改的頻道（留空＝目前這個頻道）")] SocketTextChannel? channel = null)
    {
        if (!IsAdmin)
        {
            await RespondAsync(AdminOnlyMessage(), ephemeral: true);
            return;
        }

        var target = channel ?? Context.Channel as SocketTextChannel;

        if (target is null)
        {
            await RespondAsync("請指定一個文字頻道。", ephemeral: true);
            return;
        }

        if (!DigestSchedule.TryParse(when, out var parsed, out var error))
        {
            await RespondAsync($"❌ {error}", ephemeral: true);
            return;
        }

        var updated = _store.SetSchedule(target.Id, parsed, DateTimeOffset.UtcNow, _options.ZoneOffset);

        await RespondAsync(
            updated is null
                ? $"這個頻道還沒有啟用每週摘要（先用 `/digest enable`）。"
                : $"🕘 #{target.Name} 的每週摘要改成 **{parsed.Describe()}**" +
                  $"（台灣時間）\n下一次：**{updated.NextRunAt!.Value.ToOffset(_options.ZoneOffset):yyyy-MM-dd HH:mm}**",
            ephemeral: true);
    }

    [SlashCommand("now", "現在馬上整理一次（Bot 管理員；只整理上次之後的新訊息）")]
    public async Task NowAsync(
        [Summary("channel", "要整理的頻道（留空＝目前這個頻道）")] SocketTextChannel? channel = null)
    {
        if (!IsAdmin)
        {
            await RespondAsync(AdminOnlyMessage(), ephemeral: true);
            return;
        }

        var target = channel ?? Context.Channel as SocketTextChannel;

        if (target is null)
        {
            await RespondAsync("請指定一個文字頻道。", ephemeral: true);
            return;
        }

        if (_store.Config(target.Id) is null)
        {
            await RespondAsync(
                $"#{target.Name} 還沒有啟用每週摘要。先用 `/digest enable`（可以順便選要回顧多少）。",
                ephemeral: true);
            return;
        }

        // 摘要可能要讀幾千則訊息 ＋ 呼叫模型，超過 Discord 的 3 秒互動時限 → 先 defer
        await DeferAsync(ephemeral: true);

        var report = await _runner.RunAsync(target.Id, manual: true, CancellationToken.None);

        if (!report.Ok)
        {
            await FollowupAsync(
                $"❌ 沒有產生摘要：{report.Message}" +
                (report.BudgetBlocked ? "\n（額度不足時可以用 `/ai status` 的「♻️ 重置本週額度」立刻恢復）" : ""),
                ephemeral: true);
            return;
        }

        var record = report.Record;

        await FollowupAsync(
            record is null
                ? $"📭 {report.Message}"
                : $"✅ 摘要已發到 #{target.Name}（{report.Message}）\n" +
                  $"• 訊息：{record.MessageCount:N0} 則（{record.From?.ToOffset(_options.ZoneOffset):MM-dd HH:mm} ～ {record.To?.ToOffset(_options.ZoneOffset):MM-dd HH:mm}）\n" +
                  $"• 重點 {record.Highlights.Count} 條、跨週事項 {record.OpenItems.Count} 條、@ {record.Mentions.Count} 人\n" +
                  $"• 用量：{record.TotalTokens:N0} tokens／{record.Calls} 次呼叫（模型 {record.Model}）",
            ephemeral: true);
    }

    [SlashCommand("archive", "看歷次摘要（永久保留，只有你看得到）")]
    public async Task ArchiveAsync(
        [Summary("channel", "要查的頻道（留空＝目前這個頻道）")] SocketTextChannel? channel = null,
        [Summary("count", "要看幾筆（預設 3，最多 10）")] int count = 3)
    {
        var target = channel ?? Context.Channel as SocketTextChannel;

        if (target is null)
        {
            await RespondAsync("請指定一個文字頻道。", ephemeral: true);
            return;
        }

        var records = _store.Archive(target.Id, Math.Clamp(count, 1, 10));

        if (records.Count == 0)
        {
            await RespondAsync($"#{target.Name} 還沒有任何摘要紀錄。", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithColor(new Color(0x2B, 0x6C, 0xB0))
            .WithTitle($"🗄 摘要歸檔　#{target.Name}")
            .WithFooter($"共 {_store.ArchiveCount(target.Id)} 筆（永久保留）｜只顯示最近 {records.Count} 筆");

        foreach (var record in records)
        {
            var body = ChannelDigestService.RenderText(record, _options.ZoneOffset, includeRange: false);
            var when = record.At.ToOffset(_options.ZoneOffset).ToString("yyyy-MM-dd HH:mm");

            embed.AddField(
                $"{when}（{(record.Manual ? "手動" : "自動")}｜{record.MessageCount:N0} 則）",
                body.Length > 1000 ? body[..1000] + "…" : body,
                inline: false);
        }

        await RespondAsync(embed: embed.Build(), ephemeral: true);
    }

    /// <summary>
    /// 把「回顧多少」換成**游標**（第一次執行要從哪一則之後開始）。
    ///
    /// 語意要很清楚：游標是「已經處理完的最後一則」，所以第一次執行會整理**游標之後**的訊息。
    ///   * `Now` → 游標＝目前最新的一則（所以只有之後的新訊息會被整理）
    ///   * `Last7Days` / `Last30Days` → 往回找到第一個「比界線更舊」的訊息當游標
    ///   * `Last500Messages` / `Last1000Messages` → 往回數 N 則，第 N+1 則當游標
    ///   * `Everything` → 沒有游標（會從頻道能讀到的最舊訊息開始，受安全上限限制）
    /// </summary>
    private async Task<(string? CursorId, DateTimeOffset? CursorAt, string Describe)> ResolveBackfillAsync(
        SocketTextChannel channel, DigestBackfill backfill, DateTimeOffset now)
    {
        if (backfill == DigestBackfill.Everything)
            return (null, null, "從最舊讀得到的一則開始（受安全上限限制）");

        var newest = (await channel.GetMessagesAsync(1).FlattenAsync()).FirstOrDefault();

        if (newest is null)
            return (null, null, "這個頻道還沒有訊息（等有人說話之後才會整理）");

        if (backfill == DigestBackfill.Now)
            return (newest.Id.ToString(), newest.Timestamp, "從現在開始（不回顧過去的訊息）");

        var boundary = backfill switch
        {
            DigestBackfill.Last7Days => now.AddDays(-7),
            DigestBackfill.Last30Days => now.AddDays(-30),
            _ => now.AddDays(-3650)
        };

        var wanted = backfill switch
        {
            DigestBackfill.Last500Messages => 500,
            DigestBackfill.Last1000Messages => 1000,
            _ => 0
        };

        var found = 0;
        ulong? before = null;

        while (found < 6000)      // 安全上限：回顧最多掃 6000 則
        {
            var page = before is null
                ? await channel.GetMessagesAsync(100).FlattenAsync()
                : await channel.GetMessagesAsync(before.Value, Direction.Before, 100).FlattenAsync();

            var batch = page.OrderByDescending(m => m.Id).ToList();

            if (batch.Count == 0) break;

            foreach (var message in batch)
            {
                found++;

                var reached = wanted > 0
                    ? found > wanted
                    : message.Timestamp < boundary;

                if (reached)
                {
                    var describe = wanted > 0
                        ? $"從最近 {wanted:N0} 則之前開始"
                        : $"從 {boundary.ToOffset(_options.ZoneOffset):yyyy-MM-dd} 之後開始";

                    return (message.Id.ToString(), message.Timestamp, describe);
                }
            }

            before = batch[^1].Id;

            if (batch.Count < 100) break;

            await Task.Delay(250);
        }

        return (null, null, "這個頻道的訊息比指定的範圍還少 → 從最舊的一則開始");
    }
}
