using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using TcBusBot.Core.Chat;
using TcBusBot.Core.Subscriptions;

namespace TcBusBot.Discord;

/// <summary>
/// AI 聊天的接線：**@ 它、或回覆它的訊息**就會回話。
///
/// 這個類別只做「Discord 的事」——判斷有沒有被叫到、顯示「正在輸入…」、
/// 把文字貼回頻道。所有規則（時間切段、回覆舊訊息、伺服器隔離、每週額度、估 token）
/// 都在 <see cref="ChatOrchestrator"/>，因為那些必須能在沒有網路、
/// 沒有金鑰的情況下測試。
///
/// ── 幾個刻意的決定 ─────────────────────────────────────
///
/// 1. **只認 @ 提及與回覆**：不做「看到訊息就回」。除了洗頻，也因為 Bot 讀得到
///    訊息內容是有隱私代價的（需要 Message Content 特權意圖），
///    必須讓使用者明確表達「這句是對 Bot 說的」。
///
/// 2. **不同伺服器不相通**：對話記憶的 key 是 (伺服器, 頻道)，
///    連索引都是分開的，所以 A 伺服器的話題不可能出現在 B 伺服器的上下文裡。
///
/// 3. **同一頻道一次只處理一則**：正在想的時候再問會直接告訴使用者「還在想」，
///    而不是排隊等 30 秒 —— 排隊會讓 token 用量失控（排了十則就燒十次）。
///
/// 4. **回覆用 Discord 的回覆**：這樣使用者「回覆 Bot 的那則訊息」就能自然接話。
/// </summary>
public sealed class LlmChatService : IDisposable
{
    /// <summary>Discord 單一訊息上限 2000 字；留一點空間。</summary>
    private const int MaxChunkChars = 1900;

    private readonly DiscordSocketClient _client;
    private readonly ChatOrchestrator _chat;
    private readonly LlmOptions _options;
    private readonly BusSessionStore _sessions;

    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _channelGates = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastAskedAt = new();

    /// <summary>「訊息內容是空的」只提醒一次（那幾乎一定是沒開 Message Content 意圖）。</summary>
    private static int _emptyContentWarned;

    private int _handled;
    private int _failed;
    private int _refused;

    public LlmChatService(
        DiscordSocketClient client,
        ChatOrchestrator chat,
        LlmOptions options,
        BusSessionStore sessions)
    {
        _client = client;
        _chat = chat;
        _options = options;
        _sessions = sessions;
    }

    public int Handled => _handled;
    public int Failed => _failed;
    public int Refused => _refused;
    public int ChannelCount => _chat.Conversations.ChannelCount;

    private int _started;

    /// <summary>
    /// 接上 <c>MessageReceived</c>。
    ///
    /// 為什麼由服務自己訂閱（而不是 Program 幫它接）：這樣「AI 聊天怎麼被觸發」
    /// 只寫在一個地方，Program 只要在真的啟用時呼叫一次 <see cref="Start"/>。
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        _client.MessageReceived += message =>
        {
            // 事件處理絕對不能被例外中斷（Discord.Net 會把例外往上丟，可能影響連線）
            _ = Task.Run(() => HandleAsync(message));
            return Task.CompletedTask;
        };
    }

    public string Describe()
        => $"{_options.Describe()}｜已回 {_handled} 則／失敗 {_failed}／額度擋下 {_refused}｜" +
           $"{_chat.Budget.Describe()}";

    // ─────────────────────────────────────────────────────
    //  入口
    // ─────────────────────────────────────────────────────

    public async Task HandleAsync(SocketMessage message)
    {
        try
        {
            await HandleCoreAsync(message);
        }
        catch (Exception ex)
        {
            // Discord 的事件處理**絕對不能**把例外丟出去（可能影響連線）
            _failed++;
            Console.WriteLine("[llm] 非預期錯誤：");
            Console.WriteLine(ex.ToString());
            await SafeReplyAsync(message, "❌ 我這邊出錯了（詳細訊息在 Bot 的 console）。");
        }
    }

    private async Task HandleCoreAsync(SocketMessage message)
    {
        // 只處理真人打的訊息（Bot 之間互相回話會無限循環）
        if (message is not SocketUserMessage userMessage) return;
        if (userMessage.Author.IsBot || userMessage.Author.IsWebhook) return;

        var botId = _client.CurrentUser?.Id ?? 0;
        var guildId = (message.Channel as SocketGuildChannel)?.Guild.Id ?? 0;

        // 私訊預設不回（沒有「@ 機器人」這個動作，容易被誤觸）
        if (guildId == 0 && !_options.AllowDm) return;

        var raw = message.Content ?? "";
        var referenced = userMessage.ReferencedMessage;

        var mentioned = botId != 0 && userMessage.MentionedUsers.Any(u => u.Id == botId);

        // 回覆也算「對 Bot 說話」：回覆 Bot 的訊息、回覆提到 Bot 的訊息、
        // 或回覆任何一則**我們記得過的**訊息（= 想把那段話題拉回來）
        var replyAddressed = referenced is not null
                             && (referenced.Author.Id == botId
                                 || (botId != 0 && referenced.MentionedUserIds.Contains(botId))
                                 || _chat.Conversations.HasMessage(guildId, message.Channel.Id, referenced.Id));

        if (!mentioned && !replyAddressed) return;

        // 沒開 Message Content 意圖時，Discord 會把「沒有 @ 到 Bot」的訊息內容清空
        if (raw.Length == 0 && !mentioned)
        {
            if (Interlocked.Exchange(ref _emptyContentWarned, 1) == 0)
                Console.WriteLine("[llm] ⚠️ 收到「回覆了 Bot 但內容是空的」的訊息 —— " +
                                  "幾乎一定是沒開 Message Content Intent（見啟動說明）");

            return;
        }

        var text = StripMention(raw, botId).Trim();

        // 把「@某人」展開成「@暱稱(ID)」：模型原本只看到 <@123456789>，不知道那是誰。
        // （Discord 的 mention 有 <@id>、<@!id> 兩種寫法，純函式在 Core 裡可測）
        var mentions = userMessage.MentionedUsers
            .Where(u => u.Id != botId)
            .GroupBy(u => u.Id)
            .ToDictionary(g => g.Key, g => DisplayNameOf(g.First()));

        text = MentionFormatter.Expand(text, mentions, _options.ExposeUserIds);

        if (text.Length == 0)
        {
            await SafeReplyAsync(userMessage, "要問什麼呢？（直接 @ 我然後打訊息就好）");
            return;
        }

        // 冷卻：同一個人連續問會把額度燒掉
        var now = DateTimeOffset.UtcNow;
        if (_options.UserCooldownSeconds > 0
            && _lastAskedAt.TryGetValue(userMessage.Author.Id, out var last)
            && now - last < TimeSpan.FromSeconds(_options.UserCooldownSeconds))
        {
            return;
        }
        _lastAskedAt[userMessage.Author.Id] = now;

        // 同一頻道一次只回一則
        var gate = _channelGates.GetOrAdd(message.Channel.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero))
        {
            Console.WriteLine($"[llm] 略過一則（{DescribeWhere(guildId, message)} 還在想上一題）");
            return;
        }

        try
        {
            await AnswerAsync(userMessage, guildId, text, referenced);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task AnswerAsync(
        SocketUserMessage message, ulong guildId, string text, IMessage? referenced)
    {
        var incoming = new ChatTurn(
            ChatRole.User,
            message.Author.Username,
            message.Author.Id,
            message.Id,
            text,
            message.Timestamp.ToUniversalTime(),
            PromptLabel: MentionFormatter.Label(
                DisplayNameOf(message.Author),
                message.Author.Username,
                message.Author.Id,
                _options.ExposeUserIds));

        var replyTarget = referenced is null ? null : ToTurn(referenced, _client.CurrentUser?.Id ?? 0);
        var where = DescribeWhere(guildId, message);

        // 呼叫期間讓 Discord 顯示「正在輸入…」（推理模型可能要想十幾秒）
        using var typingCts = new CancellationTokenSource();
        var typing = KeepTypingAsync(message.Channel, typingCts.Token);

        ChatAnswer answer;
        try
        {
            answer = await _chat.AskAsync(guildId, message.Channel.Id, incoming, replyTarget);
        }
        finally
        {
            typingCts.Cancel();
            try { await typing; } catch (Exception) { /* 只是打字動畫 */ }
        }

        // ── log（每一則都留下「為什麼這樣回答」與用量）──────
        Console.WriteLine(
            $"[llm] {where}｜{answer.DecisionReason}｜上下文 {answer.ContextTurns} 則" +
            $"（丟掉 {answer.DroppedTurns}，約 {answer.EstimatedContextTokens} tokens）");

        if (answer.Refused)
        {
            _refused++;
            Console.WriteLine($"[llm] ⛔ 額度不足：{answer.Error}");
        }
        else if (answer.Ok)
        {
            _handled++;
            Console.WriteLine(
                $"[llm] → {answer.Model}｜in {answer.InputTokens} / out {answer.OutputTokens} tokens" +
                $"（{(answer.UsageReported ? "API 回報" : "估算")}）｜話題判斷 {answer.TopicDetectTokens} tokens｜" +
                $"{answer.Elapsed.TotalSeconds:0.0}s｜{_chat.Budget.Describe()}");

            if (answer.ToolCalls.Count > 0)
                Console.WriteLine($"[llm] 🔧 工具呼叫：{string.Join("、", answer.ToolCalls)}" +
                                  (answer.ToolChangedState ? "（有動到資料）" : ""));
        }
        else
        {
            _failed++;
            Console.WriteLine($"[llm] ✘ 失敗：{answer.Error}");
        }

        // ── 貼回頻道（分段；第一段用「回覆」讓使用者可以接著回）──
        // 模型如果**真的動了資料**，就在後面附一行「我實際做了什麼」：
        // 模型常常講得比做得多，這句話讓使用者能核對。
        var replyText = answer.Text;

        if (answer.Ok && answer.ToolChangedState)
            replyText += "\n\n-# 🔧 我實際做了：" + string.Join("、", answer.ToolCalls);

        // LLM 做的破壞性操作也要能一鍵還原（取消訂閱 → 掛「↩️ 復原」按鈕）
        var components = BuildUndoComponents(message);

        var chunks = Split(replyText, MaxChunkChars);
        IMessage? sent = null;
        var allowedMentions = ReplyMentions();

        for (var i = 0; i < chunks.Count; i++)
        {
            var isLast = i == chunks.Count - 1;

            sent = i == 0
                ? await message.ReplyAsync(chunks[i],
                    allowedMentions: allowedMentions,
                    components: isLast ? components : null)
                : await message.Channel.SendMessageAsync(chunks[i],
                    allowedMentions: allowedMentions,
                    components: isLast ? components : null);
        }

        // 把 Bot 的回覆記進同一段（附上真正的訊息 ID）
        if (answer.Ok && answer.Decision is { } decision && sent is not null)
        {
            _chat.RecordReply(
                decision,
                answer.Text,
                sent.Id,
                _client.CurrentUser?.Id ?? 0,
                _client.CurrentUser?.Username ?? "Bot",
                DateTimeOffset.UtcNow);
        }
    }

    // ─────────────────────────────────────────────────────
    //  小工具
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 如果模型剛剛取消了訂閱，就把它推進這個頻道的復原堆疊、並回傳「↩️ 復原」按鈕。
    ///
    /// 為什麼要這樣做：`/bus end` 有復原按鈕，但**模型自己決定要取消**的時候，
    /// 使用者只看到一句「已幫你取消全部訂閱」—— 那太無助了。
    /// 既然被移除的內容已經抄下來了（<see cref="BusTools.PendingUndo"/>），
    /// 就把它變成同一顆按鈕：AI 做的事情與人按按鈕做的事情，復原方式一致。
    /// </summary>
    private MessageComponent? BuildUndoComponents(SocketUserMessage message)
    {
        if (_chat.Tools is not BotToolProvider busTools) return null;
        if (busTools.TakePendingUndo() is not { Count: > 0 } removed) return null;

        var session = _sessions.GetOrCreate(message.Author.Id, message.Channel.Id);

        session.Undo.Push(new UndoEndedTracking(
            Description: $"AI 取消全部訂閱（{removed.Count} 組）",
            Removed: removed,
            PreviousSessionGroupId: session.CreatedGroupId));

        session.CreatedGroupId = null;
        session.Origin = null;
        session.Destination = null;
        session.LastRoutes.Clear();
        session.ResetPicks();

        Console.WriteLine($"[llm] 🔧 已把「取消 {removed.Count} 組訂閱」推進復原堆疊" +
                          $"（{removed.Sum(r => r.Subscriptions.Count)} 筆）");

        return new ComponentBuilder()
            .WithButton($"↩️ 復原（把 {removed.Count} 組訂閱放回來）", Cid.Undo, ButtonStyle.Primary)
            .Build();
    }

    private static ChatTurn ToTurn(IMessage message, ulong botId)
        => new(
            message.Author.Id == botId ? ChatRole.Assistant : ChatRole.User,
            message.Author.Username,
            message.Author.Id,
            message.Id,
            message.Content ?? "",
            message.Timestamp.ToUniversalTime(),
            PromptLabel: MentionFormatter.Label(
                DisplayNameOf(message.Author), message.Author.Username, message.Author.Id, includeId: true));

    /// <summary>伺服器暱稱優先，沒有就用帳號名（DM 沒有暱稱）。</summary>
    private static string DisplayNameOf(IUser user)
        => user is SocketGuildUser guildUser && !string.IsNullOrWhiteSpace(guildUser.DisplayName)
            ? guildUser.DisplayName
            : user.Username;

    /// <summary>
    /// 回覆時允許的 mention。
    ///
    /// 「讓它 @ 別人」是使用者要的，但**只開放 @ 使用者**：
    /// mention 的文字是模型產生的，開放 @everyone／@身分組等於讓它有機會洗頻整個伺服器。
    /// </summary>
    private AllowedMentions ReplyMentions()
        => _options.AllowMentions
            ? new AllowedMentions { AllowedTypes = AllowedMentionTypes.Users }
            : AllowedMentions.None;

    /// <summary>把「@Bot」那個 mention 拿掉（Discord 給的是 &lt;@123&gt; / &lt;@!123&gt;）。</summary>
    private static string StripMention(string content, ulong botId)
    {
        if (botId == 0) return content;

        return content
            .Replace($"<@{botId}>", "", StringComparison.Ordinal)
            .Replace($"<@!{botId}>", "", StringComparison.Ordinal);
    }

    private static string DescribeWhere(ulong guildId, SocketMessage message)
    {
        var where = guildId == 0
            ? "私訊"
            : $"#{(message.Channel as SocketGuildChannel)?.Name ?? message.Channel.Id.ToString()}";

        return $"{guildId}/{where} {message.Author.Username}：{Preview(message.Content ?? "")}";
    }

    private static string Preview(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 60 ? flat : flat[..60] + "…";
    }

    /// <summary>每隔幾秒戳一次「正在輸入…」，讓使用者知道 Bot 還在工作。</summary>
    private static async Task KeepTypingAsync(IMessageChannel channel, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await channel.TriggerTypingAsync();
                await Task.Delay(TimeSpan.FromSeconds(7), token);
            }
        }
        catch (Exception)
        {
            // 只是打字動畫，失敗不影響任何事
        }
    }

    /// <summary>超過 Discord 上限的長回覆切成幾段（盡量切在換行處）。</summary>
    public static List<string> Split(string text, int maxChars)
    {
        var chunks = new List<string>();
        var remaining = (text ?? "").Trim();

        while (remaining.Length > maxChars)
        {
            var cut = remaining.LastIndexOf('\n', Math.Min(maxChars, remaining.Length - 1));

            // 找不到適合的換行就硬切（總比送不出去好）
            if (cut < maxChars / 2) cut = maxChars;

            chunks.Add(remaining[..cut].TrimEnd());
            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0) chunks.Add(remaining);
        return chunks;
    }

    private async Task SafeReplyAsync(SocketMessage message, string text)
    {
        try
        {
            foreach (var chunk in Split(text, MaxChunkChars))
                await message.Channel.SendMessageAsync(chunk);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[llm] 回覆訊息失敗：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>清掉太久沒動的頻道記憶（由輪詢迴圈定時呼叫）。</summary>
    public int Purge(DateTimeOffset now)
    {
        var removed = _chat.Conversations.Purge(now);

        // 順便把冷卻表縮一下，長時間掛機才不會一直長大
        var stale = _lastAskedAt.Where(kv => now - kv.Value > TimeSpan.FromHours(1))
                                .Select(kv => kv.Key).ToList();
        foreach (var key in stale) _lastAskedAt.TryRemove(key, out _);

        return removed;
    }

    public void Dispose()
    {
        foreach (var gate in _channelGates.Values) gate.Dispose();
        _channelGates.Clear();
    }
}
