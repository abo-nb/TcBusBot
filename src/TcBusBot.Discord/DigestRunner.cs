using Discord;
using Discord.WebSocket;
using TcBusBot.Core.Chat;
using TcBusBot.Core.Chat.Digest;

namespace TcBusBot.Discord;

/// <summary>一次執行的結果（給排程器與 `/digest now` 用）。</summary>
public sealed record DigestRunReport(
    bool Posted,
    string Message,
    DigestRecord? Record = null,
    bool Skipped = false,
    bool BudgetBlocked = false)
{
    public bool Ok => Posted || Skipped;
}

/// <summary>
/// 「每週自動整理頻道」的 Discord 這一半：**把訊息抓下來**、**把摘要發出去**。
///
/// 為什麼規則不寫在這裡：分批、@ 誰、游標怎麼推進、失敗時怎麼辦，全部在
/// <see cref="ChannelDigestService"/>（Core，可離線測）。這裡只做三件 Discord 才做得到的事：
///   1. 用 REST 讀歷史訊息（**含權限檢查** —— 沒有權限時要講清楚缺哪一個，不是默默失敗）
///   2. 把游標之後的訊息翻成 <see cref="DigestMessage"/>
///   3. 把結果發到頻道（**@ 人只 @ 核對過的那幾個**，而且絕不 @everyone／@here）
///
/// ⚠️ 讀歷史訊息**不受** Message Content Intent 影響（那個限制是閘道事件），
///    但要的是頻道權限：`ViewChannel` ＋ `ReadMessageHistory`（發訊息另外要 `SendMessages`）。
/// </summary>
public sealed class DigestRunner
{
    private readonly DiscordSocketClient _client;
    private readonly ChannelDigestService _digests;
    private readonly DigestStore _store;
    private readonly DigestOptions _options;
    private readonly LlmOptions _llm;

    public DigestRunner(
        DiscordSocketClient client,
        ChannelDigestService digests,
        DigestStore store,
        DigestOptions options,
        LlmOptions llm)
    {
        _client = client;
        _digests = digests;
        _store = store;
        _options = options;
        _llm = llm;
    }

    /// <summary>跑一個頻道（排程或手動）。</summary>
    public async Task<DigestRunReport> RunAsync(ulong channelId, bool manual, CancellationToken ct = default)
    {
        var channel = _client.GetChannel(channelId) as SocketTextChannel;

        if (channel is null)
        {
            return Fail(channelId, manual, $"找不到頻道 `{channelId}`（我可能已經不在那個伺服器，或那不是文字頻道）。",
                retry: true);
        }

        var missing = MissingPermissions(channel);

        if (missing is not null)
            return Fail(channelId, manual, $"我在 #{channel.Name} 缺少權限：{missing}。", retry: true);

        var config = _store.Config(channelId);

        if (config is null)
            return Fail(channelId, manual, $"這個頻道還沒有啟用每週摘要（`/digest enable`）。", retry: false);

        if (!_options.Enabled)
            return Fail(channelId, manual, "每週摘要功能已停用（`LLM_DIGEST=false`）。", retry: false);

        // ── 抓訊息（游標之後的）──────────────────────────
        var (messages, truncated) = await FetchAsync(channel, config, ct);

        if (messages.Count == 0)
        {
            var nothing = DigestOutcome.Skip("沒有新訊息。");
            _store.MarkRun(channelId, null, null, DateTimeOffset.UtcNow, _options.ZoneOffset);

            // 排程時不打擾頻道（PostEmpty 打開才會發一句）
            if (_options.PostEmpty && !manual)
                await channel.SendMessageAsync("📭 這一週這個頻道沒有新訊息，所以沒有週報。");

            return new DigestRunReport(false, nothing.Message, Skipped: true);
        }

        var participants = Participants(messages);
        var run = new DigestRun(channel.Guild.Id, channelId, channel.Name, messages, participants, manual, truncated);

        // ── 先放一則「正在整理」的訊息 ─────────────────────
        //    為什麼要這個：摘要開著思考（預設 high），一次要跑 30～90 秒以上。
        //    沒有這一則，使用者看到的是「打了 /digest now 之後什麼都沒發生」——
        //    他會以為壞掉了，然後再打一次（於是又花一次錢）。
        var placeholder = await PostPlaceholderAsync(channel, messages.Count, manual);

        DigestOutcome outcome;

        try
        {
            outcome = await _digests.RunAsync(run, ct);
        }
        catch (Exception ex)
        {
            var reason = ex is LlmException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            await DiscardPlaceholderAsync(placeholder, $"❌ 這次摘要失敗：{reason}");
            return Fail(channelId, manual, $"呼叫模型失敗：{reason}", retry: true);
        }

        if (outcome.BudgetBlocked)
        {
            await DiscardPlaceholderAsync(placeholder, $"⛔ 這次摘要沒有跑：{outcome.Message}");
            return Fail(channelId, manual, outcome.Message, retry: true);
        }

        if (!outcome.HasDigest)
        {
            await DiscardPlaceholderAsync(placeholder, $"📭 {outcome.Message}");
            return Fail(channelId, manual, outcome.Message, retry: true);
        }

        // ── 發出去（把剛剛那一則改成成品，而不是多一則訊息）──
        await PostAsync(channel, outcome.Record!, placeholder, ct);

        Console.WriteLine($"[摘要] #{channel.Name}（{channelId}）{(manual ? "手動" : "排程")}：" +
                          $"{outcome.Record!.MessageCount} 則訊息、{outcome.Record.Calls} 次呼叫、" +
                          $"{outcome.Record.TotalTokens:N0} tokens、@ {outcome.Record.Mentions.Count} 人");

        return new DigestRunReport(true, outcome.Message, outcome.Record);
    }

    /// <summary>失敗時：記在設定裡（`/digest status` 看得到）＋ 排程失敗不吵頻道。</summary>
    private DigestRunReport Fail(ulong channelId, bool manual, string message, bool retry)
    {
        if (_store.Config(channelId) is not null)
            _store.MarkFailed(channelId, message, DateTimeOffset.UtcNow, _options.RetryDelay);

        Console.WriteLine($"[摘要] {(manual ? "手動" : "排程")}執行失敗（{channelId}）：{message}");

        return new DigestRunReport(false, message, BudgetBlocked: retry && message.Contains("額度", StringComparison.Ordinal));
    }

    /// <summary>哪些權限沒給（回 null 代表都給了）。</summary>
    public static string? MissingPermissions(SocketTextChannel channel)
    {
        var permissions = channel.Guild.CurrentUser.GetPermissions(channel);
        var missing = new List<string>();

        if (!permissions.ViewChannel) missing.Add("檢視頻道");
        if (!permissions.ReadMessageHistory) missing.Add("讀取訊息歷史");
        if (!permissions.SendMessages) missing.Add("傳送訊息");

        return missing.Count == 0 ? null : string.Join("、", missing);
    }

    /// <summary>
    /// 讀「游標之後」的訊息（**由新到舊逐頁讀，讀完再翻回時間順序**）。
    ///
    /// 為什麼要由新到舊：Discord 的歷史 API 只有「從某則往前往後」，
    /// 而我們要的是「最近的一批」—— 由新往舊讀可以在**任何時候停下來**，
    /// 而且停下來的位置正好是「最舊的已讀訊息」，游標語意很自然。
    /// </summary>
    private async Task<(List<DigestMessage> Messages, bool Truncated)> FetchAsync(
        SocketTextChannel channel, ChannelDigestConfig config, CancellationToken ct)
    {
        var limit = _options.EffectiveMaxMessages;

        var collected = new List<DigestMessage>();
        var truncated = false;
        ulong? before = null;

        while (collected.Count < limit)
        {
            ct.ThrowIfCancellationRequested();

            var page = before is null
                ? await channel.GetMessagesAsync(100).FlattenAsync()
                : await channel.GetMessagesAsync(before.Value, Direction.Before, 100).FlattenAsync();

            var batch = page
                .OrderByDescending(m => m.Id)      // 由新到舊
                .ToList();

            if (batch.Count == 0) break;

            var reachedCursor = false;

            foreach (var message in batch)
            {
                if (!DigestCursor.IsAfter(message.Id.ToString(), config.CursorMessageId))
                {
                    reachedCursor = true;
                    break;
                }

                if (TryConvert(message, out var converted)) collected.Add(converted);

                if (collected.Count >= limit)
                {
                    truncated = true;
                    break;
                }
            }

            if (reachedCursor || truncated) break;

            before = batch[^1].Id;

            if (batch.Count < 100) break;   // 沒有更舊的了

            // 保守節流：讀歷史是 REST 呼叫，爆量頻道不要打太快
            await Task.Delay(250, ct);
        }

        collected.Reverse();   // 翻回時間順序（摘要要照時間讀）
        return (collected, truncated);
    }

    /// <summary>
    /// 一則 Discord 訊息 → <see cref="DigestMessage"/>。
    ///
    /// 刻意**跳過 Bot 自己的訊息**：那些是它自己的回覆與**它自己發的週報**。
    /// 把週報再餵回去摘要，下一週的摘要就會開始講「上一份摘要說…」——
    /// 一週一週地飄走，最後整份內容都在講摘要本身。
    /// </summary>
    private bool TryConvert(IMessage message, out DigestMessage converted)
    {
        converted = default!;

        if (message.Author.Id == _client.CurrentUser?.Id) return false;

        var author = message.Author as SocketGuildUser;
        var id = message.Author.Id;

        var name = MentionFormatter.SpeakerName(
            author?.DisplayName, author?.Username ?? message.Author.Username, id, _llm.ShowNicknames);

        var label = MentionFormatter.Label(
            author?.DisplayName, author?.Username ?? message.Author.Username, id, _llm.ExposeUserIds);

        // ⚠️ REST 回來的是 IMessage，但實際物件是 SocketMessage；
        //    只有後者才有 MentionedUsers（IMessage 這個介面沒有）。
        var known = (message as SocketMessage)?.MentionedUsers?
            .Where(u => u is SocketGuildUser)
            .GroupBy(u => u.Id)
            .ToDictionary(
                g => g.Key,
                g => MentionFormatter.SpeakerName(
                    (g.First() as SocketGuildUser)?.DisplayName, g.First().Username, g.Key, _llm.ShowNicknames))
            ?? [];

        var text = MentionFormatter.Expand(message.Content, known, _llm.ExposeUserIds).Trim();

        var images = message.Attachments.Count(a => LooksLikeImage(a.Filename))
                     + message.Stickers.Count
                     + message.Embeds.Count(e => e.Image is not null || e.Thumbnail is not null);

        if (text.Length == 0)
        {
            // 只有圖／貼圖／嵌入的訊息也要留下一筆 —— 那是「有人貼了東西」這個事實
            if (images > 0) text = "（貼了圖片／貼圖）";
            else if (message.Embeds.Count > 0) text = "（貼了一個連結）";
            else return false;   // 系統訊息、加入通知之類的
        }

        converted = new DigestMessage(
            Id: message.Id.ToString(),
            AuthorId: id,
            AuthorName: name,
            PromptLabel: label,
            At: message.Timestamp,
            Text: text,
            ImageCount: images,
            IsBot: message.Author.IsBot);

        return true;
    }

    private static bool LooksLikeImage(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
    }

    /// <summary>這一週有出現過的人（@ 人只能從這裡挑，見 <c>DigestPrompt.SanitizeMentions</c>）。</summary>
    private static IReadOnlyList<(ulong Id, string Name)> Participants(IReadOnlyList<DigestMessage> messages)
    {
        var byId = new Dictionary<ulong, string>();

        foreach (var message in messages)
        {
            if (message.AuthorId == 0) continue;

            byId[message.AuthorId] = message.AuthorName;
        }

        return byId.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// 把週報發到頻道。
    ///
    /// ⚠️ @ 人一定放在**訊息內容**（不是 embed）：Discord 的 embed 裡的 mention
    ///    不會觸發通知。而允許名單只放「核對過的本人」——
    ///    沒有 @everyone／@here／身分組這三種可能（見 <see cref="DigestPrompt.SanitizeMentions"/>）。
    /// </summary>
    /// <summary>
    /// 先發一則「正在整理…」的訊息（之後會被改成成品）。
    ///
    /// 為什麼要「改」而不是「刪掉重發」：改同一則訊息不會讓頻道跳兩次通知，
    /// 而且使用者回頭看時間軸時只會看到一份週報（不是「開始整理 + 週報」兩則）。
    /// 失敗時也把這一則改成錯誤訊息 —— 至少留下一條線索，而不是消失。
    /// </summary>
    private async Task<IUserMessage?> PostPlaceholderAsync(
        SocketTextChannel channel, int messageCount, bool manual)
    {
        try
        {
            var thinking = _options.Reasoning is "off" or "auto"
                ? ""
                : $"\n-# 模型正在思考（{_options.Reasoning}）—— 通常要 30～90 秒，思考內容可以用 `/ai think` 看";

            return await channel.SendMessageAsync(
                $"🔄 **正在整理這一週的訊息**（{messageCount:N0} 則{(manual ? "，手動觸發" : "")}）…{thinking}");
        }
        catch (Exception ex)
        {
            // 發不出來就照舊往下跑（成品會用新訊息發出去）
            Console.WriteLine($"[摘要] 進度訊息發送失敗（{ex.Message}）—— 摘要照常進行");
            return null;
        }
    }

    /// <summary>失敗時把進度訊息改成錯誤說明（沒有進度訊息就只寫 log）。</summary>
    private static async Task DiscardPlaceholderAsync(IUserMessage? placeholder, string text)
    {
        if (placeholder is null) return;

        try
        {
            await placeholder.ModifyAsync(m => m.Content = text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[摘要] 更新進度訊息失敗（{ex.Message}）");
        }
    }

    private async Task PostAsync(
        SocketTextChannel channel, DigestRecord record, IUserMessage? placeholder, CancellationToken ct)
    {
        var text = ChannelDigestService.RenderText(record, _options.ZoneOffset);
        var mentions = record.Mentions;

        var content = mentions.Count == 0
            ? "📋 **這個頻道的每週摘要**"
            : "📋 **這個頻道的每週摘要**　" +
              string.Join(" ", mentions.Select(m => $"<@{m.UserId}>")) +
              "\n-# " + string.Join("　", mentions.Select(m =>
                  $"{m.Name}：{(m.Reason.Length > 0 ? m.Reason : "這一週的事跟你相關")}"));

        var footer = $"{(record.Manual ? "手動觸發" : "每週自動整理")}｜{record.Model}｜" +
                     $"{record.TotalTokens:N0} tokens／{record.Calls} 次呼叫" +
                     (record.ReasoningTokens > 0 ? $"（思考 {record.ReasoningTokens:N0}）" : "") +
                     $"｜下一份：{NextRunText(record.ChannelId)}";

        var embed = new EmbedBuilder()
            .WithColor(new Color(0x2B, 0x6C, 0xB0))
            .WithTitle($"📋 每週摘要（{record.MessageCount:N0} 則訊息）")
            .WithDescription(text.Length > 4000 ? text[..4000] + "…" : text)
            .WithFooter(footer)
            .Build();

        // ⚠️ 一定要用 Mentions.OnlyUsers：`Users` 旗標與 `UserIds` 互斥，
        //    兩個一起設會在**送出訊息時**丟 ArgumentException ——
        //    而那是背景排程在跑的，使用者只會看到「這一週沒收到週報」。
        var allowed = Mentions.OnlyUsers(mentions.Select(m => m.UserId));

        // 有進度訊息就把它改成成品（不刪不重發：頻道只跳一次通知、時間軸只有一份週報）
        if (placeholder is not null)
        {
            try
            {
                await placeholder.ModifyAsync(m =>
                {
                    m.Content = content;
                    m.Embed = embed;
                    m.AllowedMentions = allowed;
                });

                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[摘要] 改寫進度訊息失敗（{ex.Message}）—— 改成另外發一則");
            }
        }

        await channel.SendMessageAsync(content, embed: embed, allowedMentions: allowed);
    }

    private string NextRunText(ulong channelId)
        => _store.Config(channelId)?.NextRunAt is { } next
            ? next.ToOffset(_options.ZoneOffset).ToString("MM-dd HH:mm")
            : "（已停用）";
}
