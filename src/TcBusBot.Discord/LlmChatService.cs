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
        => $"{_options.Describe()}｜已回 {_handled} 則／失敗 {_failed}／額度擋下 {_refused}" +
           $"／排隊 {_queued} 則（合併 {_merged}、丟棄 {_queueDropped}）｜" +
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
        if (guildId == 0 && !_options.AllowDm)
        {
            // 但**要說一聲**：很多人是用私訊測試的，完全沒有回應會被當成「壞掉了」。
            // 每個人最多講一次（6 小時內不重複），避免有人拿私訊洗頻。
            await ExplainDirectMessageAsync(userMessage);
            return;
        }

        var raw = message.Content ?? "";
        var referenced = userMessage.ReferencedMessage;

        var mentioned = botId != 0 && userMessage.MentionedUsers.Any(u => u.Id == botId);

        // 回覆也算「對 Bot 說話」：回覆 Bot 的訊息、回覆提到 Bot 的訊息、
        // 或回覆任何一則**我們記得過的**訊息（= 想把那段話題拉回來）
        var replyAddressed = referenced is not null
                             && (referenced.Author.Id == botId
                                 || (botId != 0 && referenced.MentionedUserIds.Contains(botId))
                                 || _chat.Conversations.HasMessage(guildId, message.Channel.Id, referenced.Id));

        var now = DateTimeOffset.UtcNow;

        // ── 偷聽中的訊息也要進來處理 ─────────────────────────
        //    ⚠️ 這裡出過大包：原本的判斷只有「@ 它」與「回覆它」，
        //    沒被叫到的訊息會在**到達偷聽那段程式之前**就 return，
        //    所以「回完話後繼續聽」從來沒有真的生效過（使用者看到的是後面的訊息全被忽略）。
        var listening = !mentioned && !replyAddressed
                        && _chat.Conversations.PeekListening(guildId, message.Channel.Id, now) is not null;

        if (!ShouldHandle(mentioned, replyAddressed, listening)) return;

        // 沒開 Message Content 意圖時，Discord 會把「沒有 @ 到 Bot」的訊息內容清空
        if (raw.Length == 0 && !mentioned)
        {
            if (Interlocked.Exchange(ref _emptyContentWarned, 1) == 0)
                Console.WriteLine("[llm] ⚠️ 收到「沒有 @ 它、內容卻是空的」的訊息 —— " +
                                  "幾乎一定是沒開 Message Content Intent（見啟動說明）");

            return;
        }

        var text = StripMention(raw, botId).Trim();

        // 把「@某人」展開成「@暱稱(ID)」：模型原本只看到 <@123456789>，不知道那是誰。
        // （Discord 的 mention 有 <@id>、<@!id> 兩種寫法，純函式在 Core 裡可測）
        var mentions = userMessage.MentionedUsers
            .Where(u => u.Id != botId)
            .GroupBy(u => u.Id)
            .ToDictionary(g => g.Key, g => NameOf(g.First()));

        text = MentionFormatter.Expand(text, mentions, _options.ExposeUserIds);

        // ── 偷聽模式：這一則沒有 @ 它、也不是回覆它 ──────────
        //   如果剛剛回過話（偷聽窗口還開著），就讓它聽一下並自己判斷要不要接話。
        var addressed = mentioned || replyAddressed;

        // 訊息 @ 了**別的真人**（不是 Bot）→ 那是在跟那個人說話，這是鐵證。
        // 交給 Core 處理：不插話、**不花錢判斷**，但**繼續偷聽**
        // （以前的版本在這裡直接停止偷聽 —— 一群人聊天時只要有人 @ 別人，
        //   之後的訊息就全部被忽略了）。
        var mentionsOtherHuman = userMessage.MentionedUsers.Any(u => u.Id != botId && !u.IsBot);

        if (!addressed)
        {
            var listen = _chat.Conversations.PeekListening(guildId, message.Channel.Id, now);

            if (listen is null) return;   // 沒在偷聽 → 當作沒看到（維持原本行為）

            Console.WriteLine($"[llm] 👂 偷聽中（還能判斷 {listen.RemainingMessages} 則，全程 {_options.EavesdropSeconds} 秒安靜就停）：" +
                              $"{Preview(text)}");
        }

        if (text.Length == 0)
        {
            // 偷聽到的空訊息（純貼圖、只有附件、或沒開 Message Content 意圖）
            // **不可以**回「要問什麼呢？」—— 那句話沒有人在問它，插話比不回答更糟。
            if (addressed)
                await SafeReplyAsync(userMessage, "要問什麼呢？（直接 @ 我然後打訊息就好）");
            else
                Console.WriteLine($"[llm] 👂 偷聽到一則沒有文字的訊息 → 忽略（{DescribeWhere(guildId, message)}）");

            return;
        }

        // 冷卻：同一個人連續問會把額度燒掉（偷聽的訊息不算，因為那是別人講的話）
        if (addressed && _options.UserCooldownSeconds > 0
            && _lastAskedAt.TryGetValue(userMessage.Author.Id, out var last)
            && now - last < TimeSpan.FromSeconds(_options.UserCooldownSeconds))
        {
            return;
        }

        if (addressed) _lastAskedAt[userMessage.Author.Id] = now;

        // ── 排隊（不是「忙就丟掉」）─────────────────────────
        //   以前是「同一頻道一次只處理一則，忙的時候直接丟掉」——
        //   多人頻道裡第二個 @ 它的人會被**默默吃掉**（console 只有一行 log，
        //   使用者什麼都看不到）。使用者要的是「盡量每個人都回」，
        //   所以改成排隊，只有佇列真的滿了才丟（而且先丟閒聊、不丟 @ 它的）。
        Enqueue(new QueuedMessage(userMessage, text, addressed, mentionsOtherHuman), now);
    }

    /// <summary>排隊中的一則（保留原訊息，回覆時要用它的頻道／訊息 ID）。</summary>
    private sealed record QueuedMessage(
        SocketUserMessage Message,
        string Text,
        bool Addressed,
        bool MentionsOtherHuman);

    /// <summary>每個頻道的待處理佇列 ＋ 它的工人。</summary>
    private sealed class ChannelQueue
    {
        public required ChatQueue<QueuedMessage> Queue { get; init; }

        /// <summary>正在跑的那個工人（null = 沒有人在處理這個頻道）。</summary>
        public Task? Worker { get; set; }
    }

    private readonly ConcurrentDictionary<ulong, ChannelQueue> _queues = new();
    private readonly object _workerGate = new();

    private int _queued;
    private int _merged;
    private int _queueDropped;

    /// <summary>目前幾個頻道有人在等回覆（`/ai status` 與 log 用）。</summary>
    public int BusyChannels
    {
        get
        {
            lock (_workerGate)
                return _queues.Count(kv => kv.Value.Worker is { IsCompleted: false });
        }
    }

    public int QueuedCount => Volatile.Read(ref _queued);

    /// <summary>排進這個頻道的佇列，並確保有一個工人在處理。</summary>
    private void Enqueue(QueuedMessage message, DateTimeOffset now)
    {
        var channelId = message.Message.Channel.Id;
        var where = DescribeWhere(GuildOf(message.Message), message.Message);

        ChatQueueOutcome outcome;

        lock (_workerGate)
        {
            var state = _queues.GetOrAdd(channelId, _ => new ChannelQueue
            {
                Queue = new ChatQueue<QueuedMessage>(
                    _options.ChannelQueueDepth,
                    TimeSpan.FromSeconds(_options.MergeWindowSeconds),
                    merge: (a, b) => a with { Text = $"{a.Text}\n{b.Text}" })
            });

            outcome = state.Queue.Enqueue(new ChatQueueItem<QueuedMessage>(
                AuthorId: message.Message.Author.Id,
                At: now,
                Addressed: message.Addressed,
                Payload: message));

            // 沒有人在做這個頻道 → 開一個工人
            if (state.Worker is not { IsCompleted: false })
                state.Worker = Task.Run(() => ProcessChannelAsync(channelId, state));
        }

        switch (outcome)
        {
            case ChatQueueOutcome.Added:
                Interlocked.Increment(ref _queued);
                Console.WriteLine($"[llm] ⏳ 排隊中（{where}）：{Preview(message.Text)}");
                break;

            case ChatQueueOutcome.Merged:
                Interlocked.Increment(ref _merged);
                Console.WriteLine($"[llm] ➕ 合併到上一題（{where}）：{Preview(message.Text)}");
                break;

            case ChatQueueOutcome.Dropped:
                Interlocked.Increment(ref _queueDropped);

                // 被丟掉的那一則**內容還是要留下來當上下文**（只記閒聊）——
                // 「沒有回話」跟「完全不知道發生什麼事」是兩件事。
                if (!message.Addressed)
                    _chat.RecordAmbient(GuildOf(message.Message), channelId,
                                        BuildTurn(message.Message, message.Text),
                                        "排隊滿了所以沒回，只留下上下文");

                Console.WriteLine($"[llm] ⚠️ 佇列滿了（{_options.ChannelQueueDepth} 則）→ 丟掉" +
                                  $"{(message.Addressed ? "一則 @ 它的訊息" : "一則閒聊")}（{where}）：" +
                                  Preview(message.Text));
                break;
        }
    }

    /// <summary>
    /// 一個頻道的工人：**一次處理一則**（順序＝使用者講話的順序），
    /// 閒下來 20 秒就收工（下次有訊息會再開一個）。
    /// </summary>
    private async Task ProcessChannelAsync(ulong channelId, ChannelQueue state)
    {
        while (true)
        {
            if (!state.Queue.WaitForWork(TimeSpan.FromSeconds(20)))
            {
                // 沒東西了 → 收工。⚠️ 一定要在鎖裡確認「真的一則都沒有」：
                //    不然剛好有人在這瞬間排進來，工人卻已經收工 → 那則訊息會被放到天荒地老。
                lock (_workerGate)
                {
                    if (state.Queue.HasWork) continue;

                    state.Worker = null;
                    _queues.TryRemove(channelId, out _);
                    return;
                }
            }

            while (state.Queue.TryDequeue(out var item))
            {
                var payload = item.Payload;
                var merged = item.Merged > 1 ? $"，合併了 {item.Merged} 則" : "";

                try
                {
                    await AnswerAsync(
                        payload.Message, GuildOf(payload.Message), payload.Text,
                        payload.Message.ReferencedMessage, payload.Addressed, payload.MentionsOtherHuman);

                    if (merged.Length > 0)
                        Console.WriteLine($"[llm] ✔ 回覆完成{merged}（{DescribeWhere(GuildOf(payload.Message), payload.Message)}）");
                }
                catch (Exception ex)
                {
                    // 一則失敗不可以讓整個頻道的佇列停掉
                    _failed++;
                    Console.WriteLine($"[llm] 處理一則時發生非預期錯誤（繼續處理下一則）：");
                    Console.WriteLine(ex.ToString());
                }
            }
        }
    }

    /// <summary>從訊息推回伺服器 ID（私訊是 0）。</summary>
    private static ulong GuildOf(SocketMessage message)
        => (message.Channel as SocketGuildChannel)?.Guild.Id ?? 0;

    /// <summary>
    /// 「私訊預設不回」的說明（每個人最多講一次）。
    ///
    /// 為什麼要講：使用者用私訊測試時什麼都收不到，很容易下結論「LLM 沒接上」——
    /// 但其實只是這個 Bot 刻意不在私訊回話（私訊沒有「@ 機器人」這個動作，容易被誤觸）。
    /// </summary>
    private async Task ExplainDirectMessageAsync(SocketUserMessage message)
    {
        var now = DateTimeOffset.UtcNow;

        if (_dmExplained.TryGetValue(message.Author.Id, out var last) && now - last < TimeSpan.FromHours(6))
            return;

        _dmExplained[message.Author.Id] = now;
        Console.WriteLine($"[llm] 收到私訊（{message.Author.Username}）→ 回一句說明（私訊預設不回）");

        await SafeReplyAsync(message,
            "📮 我預設**不在私訊裡回話**（私訊沒有「@ 我」這個動作，容易被誤觸），所以先前的訊息我都沒回。\n" +
            "請到**伺服器頻道**裡 @ 我，或回覆我的訊息。\n\n" +
            "（主機端要開放私訊的話：設定 `LLM_ALLOW_DM=true`。）");
    }

    /// <summary>「私訊預設不回」的說明有沒有講過的紀錄（每個人 6 小時一次）。</summary>
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _dmExplained = new();

    /// <summary>
    /// 這則訊息要不要進到 AI 流程？三個入口：**@ 它**、**回覆它**（含回覆記憶裡的訊息）、
    /// **正在偷聽**。

    /// ⚠️ 抽成公開的純函式是因為這裡真的出過大包：偷聽的程式碼寫好了，
    /// 但上面那行 `if (!mentioned && !replyAddressed) return;` 會在到達偷聽之前就返回，
    /// 所以「回完話後繼續聽」**從來沒有真的生效過** —— 使用者看到的是
    /// 「@ 它講一句之後，其他人再講什麼它都當作沒看到」。
    /// 現在這個判斷有離線測試與 `--dryrun` 的檢查盯著。
    /// </summary>
    public static bool ShouldHandle(bool mentioned, bool replyAddressed, bool listening)
        => mentioned || replyAddressed || listening;

    /// <summary>
    /// 把 Discord 的訊息轉成對話裡的一則。
    ///
    /// 名字有兩份，而且**兩份都有用**：
    ///   * <c>AuthorName</c>：模型平常讀到的名字（預設是**暱稱**，見 <see cref="NameOf"/>）。
    ///     判斷器、話題判斷、工具訊息都用這一個 —— 用暱稱它才認得出「小明」是誰。
    ///   * <c>PromptLabel</c>：主對話的標籤，一定同時帶帳號與 ID（`LLM_EXPOSE_IDS`），
    ///     所以就算兩個人剛好取同一個暱稱也分得出來。
    /// </summary>
    private ChatTurn BuildTurn(SocketUserMessage message, string text)
        => new(
            ChatRole.User,
            NameOf(message.Author),
            message.Author.Id,
            message.Id,
            text,
            message.Timestamp.ToUniversalTime(),
            PromptLabel: MentionFormatter.Label(
                _options.ShowNicknames ? HostNameOf(message.Author) : message.Author.Username,
                message.Author.Username,
                message.Author.Id,
                _options.ExposeUserIds));

    /// <summary>模型平常看到的名字：暱稱（預設）或帳號。</summary>
    private string NameOf(IUser user)
        => MentionFormatter.SpeakerName(
            HostNameOf(user), user.Username, user.Id, _options.ShowNicknames);

    /// <summary>Discord 上的顯示名稱（伺服器暱稱 → 全域顯示名稱 → 帳號名）。</summary>
    private static string HostNameOf(IUser user)
        => user is SocketGuildUser guildUser && !string.IsNullOrWhiteSpace(guildUser.DisplayName)
            ? guildUser.DisplayName
            : user.Username;

    /// <summary>
    /// **它在這個伺服器被叫什麼**（Discord 暱稱）。
    ///
    /// 為什麼要傳給 Core：伺服器把 Bot 改叫「貓貓」之後，有人喊「貓貓」它要知道那是在叫它 ——
    /// 判斷「這句話是不是在對我說話」時尤其重要（判斷器問的是「你是『誰』」）。
    /// </summary>
    private string SelfNameIn(ulong guildId, SocketMessage message)
    {
        if (!_options.ShowNicknames || !_options.TellSelfName) return _client.CurrentUser?.Username ?? "Bot";

        var guild = (message.Channel as SocketGuildChannel)?.Guild
                    ?? (guildId != 0 ? _client.GetGuild(guildId) : null);

        return HostNameOf(guild?.CurrentUser ?? (IUser?)_client.CurrentUser!);
    }

    private async Task AnswerAsync(
        SocketUserMessage message,
        ulong guildId,
        string text,
        IMessage? referenced,
        bool addressed = true,
        bool mentionsOtherHuman = false)
    {
        var incoming = BuildTurn(message, text);

        var replyTarget = referenced is null ? null : ToTurn(referenced, _client.CurrentUser?.Id ?? 0);
        var where = DescribeWhere(guildId, message);

        // 呼叫期間讓 Discord 顯示「正在輸入…」（推理模型可能要想十幾秒）。
        //
        // ⚠️ 但**偷聽到的訊息不能一開始就顯示**：那一步只是「判斷要不要插話」，
        //    判斷結果是 SKIP／STOP 時什麼都不會送出去 —— 頻道上就會出現
        //    「Bot 顯示正在輸入，然後什麼都沒說」，旁人看起來像它正在回應，
        //    或像它壞掉了。
        //    所以改成**確定要回話才開始顯示**（由 Core 在正確的時機回呼，見 onReplying）。
        using var typingCts = new CancellationTokenSource();
        Task? typing = null;

        void StartTypingOnce()
        {
            typing ??= KeepTypingAsync(message.Channel, typingCts.Token);
        }

        ChatAnswer answer;
        try
        {
            answer = await _chat.AskAsync(guildId, message.Channel.Id, incoming, replyTarget,
                                          cancellationToken: default, addressed: addressed,
                                          mentionsOtherHuman: mentionsOtherHuman,
                                          onReplying: StartTypingOnce,
                                          selfName: SelfNameIn(guildId, message));
        }
        finally
        {
            typingCts.Cancel();
            if (typing is not null)
            {
                try { await typing; } catch (Exception) { /* 只是打字動畫 */ }
            }
        }

        // ── 偷聽時判斷「不是在跟我說話」→ 什麼都不送，回到等 @ 的模式 ──
        if (answer.Ignored)
        {
            Console.WriteLine($"[llm] 👂 {answer.IgnoreReason}｜沒有送出訊息，" +
                              "也沒有顯示「正在輸入…」｜" + where);
            return;
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
        else if (answer.Ok && ClaimsAnAction(replyText))
            replyText += "\n\n-# ⚠️ 我這次其實沒有真的呼叫工具 —— 上面說的動作可能沒有生效，" +
                         "請再說一次（或直接告訴我要做什麼）。";

        // LLM 做的破壞性操作也要能一鍵還原（取消訂閱 → 掛「↩️ 復原」按鈕）
        var components = BuildUndoComponents(message);

        // 模型要求「畫面上要出現什麼元件」（開面板／路線清單／到站時間）→ 附上真正的按鈕
        var uiComponents = BuildUiComponents(guildId, message);

        if (uiComponents is not null) components = uiComponents;

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
    /// <summary>
    /// 模型要求「幫使用者按按鈕」時，把**真正的元件**附在回覆上。
    ///
    /// 為什麼一定要附真的元件：模型改的是同一個面板 session（<see cref="BusSession"/>），
    /// 所以使用者看到的面板狀態是真的 —— 附上元件之後他可以接手自己點，
    /// 不用再打一次 `/bus panel`。
    /// </summary>
    private MessageComponent? BuildUiComponents(ulong guildId, SocketUserMessage message)
    {
        if (_chat.Tools is not BotToolProvider provider) return null;
        if (provider.TakePendingUi() is not { } request) return null;

        var session = _sessions.GetOrCreate(message.Author.Id, message.Channel.Id);

        Console.WriteLine($"[llm] 🖱 附上{request.Note}元件（模型幫使用者按按鈕）");

        return request.Kind switch
        {
            UiRequest.Panel => BusUi.PanelComponents(session),
            UiRequest.Routes => BusUi.RouteComponents(session.LastRoutes),
            UiRequest.Etas => new ComponentBuilder()
                .WithButton("🔄 重新整理", Cid.ShowEtas, ButtonStyle.Primary)
                .WithButton("模擬一則通知", Cid.Simulate, ButtonStyle.Secondary)
                .WithButton("📂 我的訂閱組", Cid.OpenGroups, ButtonStyle.Secondary)
                .Build(),
            _ => null
        };
    }

    /// <summary>
    /// 回覆裡「聲稱自己做了一個動作」但其實**一個工具都沒呼叫**嗎？
    ///
    /// 為什麼需要這個：模型有時候會直接說「已經幫你復原了」「訂閱已取消」，
    /// 但根本沒有呼叫工具 —— 使用者看到會以為設定改了，實際上沒有。
    /// （實測真的發生過：使用者說「訂錯了幫我復原」，它回「復原好了」但沒呼叫任何工具。）
    ///
    /// 這裡刻意只比對**明確的完成式動作詞**，避免一般回覆被誤標。
    /// </summary>
    public static bool ClaimsAnAction(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;

        string[] claims =
        [
            "已復原", "已經復原", "復原好了", "已取消", "已經取消", "取消好了",
            "已訂閱", "已經訂閱", "訂好了", "已設定", "已經設定", "設定好了",
            "已刪除", "已經刪除", "刪掉了", "已記住", "已經記住", "記好了",
            "已經幫你", "已經幫您", "幫你訂了", "幫您訂了"
        ];

        return claims.Any(c => reply.Contains(c, StringComparison.Ordinal));
    }

    /// <summary>
    /// 偷聽時把「不是在跟 Bot 說話」的訊息留下來當上下文（設 `LLM_EAVESDROP_CONTEXT=false` 就什麼都不做）。
    /// </summary>
    public void RecordAmbient(ulong guildId, ulong channelId, ChatTurn turn, string reason)
        => _chat.RecordAmbient(guildId, channelId, turn, reason);

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

    private ChatTurn ToTurn(IMessage message, ulong botId)
        => new(
            message.Author.Id == botId ? ChatRole.Assistant : ChatRole.User,
            NameOf(message.Author),
            message.Author.Id,
            message.Id,
            message.Content ?? "",
            message.Timestamp.ToUniversalTime(),
            PromptLabel: MentionFormatter.Label(
                _options.ShowNicknames ? HostNameOf(message.Author) : message.Author.Username,
                message.Author.Username, message.Author.Id, includeId: true));

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
        // 佇列不算資源，但裡面的 SemaphoreSlim 要放掉（工人會在下次等不到東西時自己收工）
        foreach (var state in _queues.Values) state.Queue.Dispose();
        _queues.Clear();
    }
}
