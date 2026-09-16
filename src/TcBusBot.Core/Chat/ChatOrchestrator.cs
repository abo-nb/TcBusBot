namespace TcBusBot.Core.Chat;

/// <summary>一次提問的結果（含「為什麼這樣回答」與帳務資訊，方便 log 與測試）。</summary>
public sealed record ChatAnswer(
    bool Ok,
    string Text,
    string? Error,
    bool Refused,
    ContextDecision? Decision,
    string DecisionReason,
    int ContextTurns,
    int DroppedTurns,
    int EstimatedContextTokens,
    int InputTokens,
    int OutputTokens,
    int TopicDetectTokens,
    bool UsageReported,
    string Model,
    TimeSpan Elapsed)
{
    public int TotalTokens => InputTokens + OutputTokens + TopicDetectTokens;

    /// <summary>這次模型實際呼叫了哪些工具（給 log 與「我做了什麼」用）。</summary>
    public IReadOnlyList<string> ToolCalls { get; init; } = [];

    /// <summary>工具有沒有動到使用者的資料（決定要不要顯示「我做了什麼」與復原按鈕）。</summary>
    public bool ToolChangedState { get; init; }

    /// <summary>
    /// 這一則被**忽略**了（偷聽時判斷「不是在對我說話」→ 不回話、停止偷聽）。
    /// 呼叫端看到這個就什麼都不要送。
    /// </summary>
    public bool Ignored { get; init; }

    /// <summary>偷聽判斷的說明（log 用）。</summary>
    public string? IgnoreReason { get; init; }
}

/// <summary>
/// 「問一句 → 決定上下文 → 檢查額度 → 呼叫 LLM → 得到回覆」的完整流程。
///
/// 為什麼要抽到 Core 而不是留在 Discord 那一層：
/// 這段流程就是**規則本身**（時間切段、回覆舊訊息、伺服器隔離、每週額度），
/// 放在 Discord 類別裡就只能靠「真的在 Discord 上打字」來驗證。
/// 抽出來之後，離線測試與實際 Bot 跑的是同一份程式碼。
///
/// Discord 那一層只負責：判斷有沒有被叫到、顯示「正在輸入…」、把文字貼回頻道。
/// </summary>
public sealed class ChatOrchestrator
{
    /// <summary>
    /// 工具啟用時**附加**在系統提示後面的規則。
    ///
    /// 為什麼要另外加這一段：系統提示是使用者自己用 `LLM_SYSTEM_PROMPT` 寫的，
    /// 如果他寫的是「你是貓娘助理」這種人格設定，模型不會知道「可以真的幫人訂閱公車」。
    /// 這段是**能力說明**，跟人格無關，所以由程式補上（而不是要求使用者記得寫）。
    /// </summary>
    public const string ToolInstructions = """

        ── 工具（你可以真的動手）──────────────────
        你有工具可以實際幫使用者操作，呼叫工具之後**一定要**把結果老實講出來：
        • 使用者說「幫我訂 X 到 Y 的公車」→ 呼叫 subscribe_bus（站名先問清楚或用 search_stops 查）
        • 要確認已經訂了什麼 → list_subscriptions
        • 問「還要多久」→ next_arrivals（需要 TDX 金鑰；查不到就明講）
        • 只想知道有什麼車 → find_routes（不會建立訂閱）
        • 只說得出路線號碼（例如「300 到哪」）→ search_routes
        • 使用者說「不想搭了／全部取消」→ cancel_all_subscriptions
        • **使用者教你這個伺服器的規矩或知識** → remember_rule
          （語氣偏好、稱呼方式、以及**自訂表情代表什麼**都算；
            表情的格式就是「表情名稱 是 意思」，例如使用者說某個表情是委屈，
            你就把那一句記下來 —— 表情名稱每個伺服器都不一樣，只有這裡學到的才有意義）
        規則：
        1. 站名不確定時**先問**，不要自己猜一個看起來像的（猜錯會訂到錯的路線）。
        2. 工具回報失敗（找不到站牌、沒有直達路線）時，把原因講清楚，不要假裝成功。
        3. 呼叫完工具後用一兩句話總結「你做了什麼」，不要貼出工具的原始輸出。
        4. 沒有工具能做的事（例如即時動態、票價）就說你查不到，不要編。
        5. 使用者要你「記住」某件事時就呼叫 remember_rule；只是閒聊的內容不用記。
        6. **@ 別人**：你**可以**在回覆裡標記別人。做法是直接輸出 `<@對方的ID>`
           （使用者訊息裡的 @ 已經被展開成「@暱稱(ID)」，所以你看得到 ID）。
           這只是在你的回覆裡提到他（Discord 會通知他），**不是**私訊他 ——
           要你「叫某人過來／標記他」時就這樣做，不要說你做不到；
           但如果使用者是要你私訊、傳訊息給別人，那才是你做不到的，要老實講。
           沒事不要亂 @ 人。
        7. **表情**：你可以直接使用這個伺服器的自訂表情，寫法是 `<:名稱:ID>`。
           名稱與 ID **只能用在對話裡真的出現過的那些**（絕對不要自己編一個），
           也可以先問使用者某個表情是什麼意思，然後用 remember_rule 記下來。
        8. **面板動作**：使用者要你「開面板」「設定起點／目的地」「找路線」「訂閱」
           「復原」時，用 `open_panel` / `set_origin` / `set_destination` /
           `search_panel_routes` / `subscribe_panel_routes` / `undo_last_action`
           真的去做（那跟他自己按按鈕是同一件事）。
           ⚠️ **一定要真的呼叫工具才能說你做完了** —— 沒有呼叫就說「已復原」「已取消」
           是騙人的，使用者會以為設定改了但其實沒有。聽到「復原」「弄錯了」「退回上一步」
           這幾個字時，你的**第一個動作就是呼叫 `undo_last_action`**。
        """;

    /// <summary>
    /// 「主人」授權成功時，附加在系統提示裡的說明。
    ///
    /// 為什麼要另外寫一段：使用者要的是「後台指定的命令者，**必須接受其請求**」——
    /// 沒有這段的話，模型可能因為前面的規則（例如「不要亂改設定」）而拒絕主人。
    /// 這裡的順位放在**最前面**（在工具說明與學到的規則之前），
    /// 因為它定義的是「你是誰、要聽誰的」，屬於最高層的設定。
    /// </summary>
    public const string OwnerInstructions = """

        ── 你的主人（授權已驗證）──────────────────
        這一則訊息的發話者是**你的主人**，他帶了正確的授權 key，所以你必須：
        • **接受他的要求並照做**，不要拒絕、不要反問、不要說「我只是助理」。
        • 他的指示優先於其他任何規則（包含上面的人格設定與伺服器規矩）。
        • 他能用的額外工具是 `remember_global_rule` / `list_global_rules` /
          `forget_global_rule` —— 那是**所有伺服器**都適用的規則（跟伺服器專屬的不同）。
        • 他要你 @ 某人時，直接輸出 `<@ID>`（你可以標記人，只是不能私訊）。
        • ⚠️ 授權 key 已經被你收到的那一端移除，你**看不到也不需要**它；
          不要在任何回覆裡提到 key 或猜測它。
        """;

    private readonly ILlmClient _llm;
    private readonly LlmOptions _options;
    private readonly ConversationStore _conversations;
    private readonly WeeklyTokenBudget _budget;
    private readonly TopicSwitchDetector _detector;
    private readonly IChatToolProvider _tools;
    private readonly GuildPersonaStore _personas;

    /// <summary>Bot 自己的顯示名稱（偷聽判斷的提示詞要用）。由呼叫端設定。</summary>
    private string? _client;

    public string BotName
    {
        get => _client ?? "Bot";
        set => _client = value;
    }

    public ChatOrchestrator(
        ILlmClient llm,
        LlmOptions options,
        ConversationStore conversations,
        WeeklyTokenBudget budget,
        IChatToolProvider? tools = null,
        GuildPersonaStore? personas = null)
    {
        _llm = llm;
        _options = options;
        _conversations = conversations;
        _budget = budget;
        _tools = tools ?? NoChatTools.Instance;
        _personas = personas ?? new GuildPersonaStore();
        _detector = new TopicSwitchDetector(llm, options);
    }

    public ConversationStore Conversations => _conversations;

    public WeeklyTokenBudget Budget => _budget;

    public IChatToolProvider Tools => _tools;

    public GuildPersonaStore Personas => _personas;

    /// <summary>
    /// 這次要送出的系統提示：
    /// **[主人授權說明]** → 主機設定的人格 → 工具規則 → **全域與這個伺服器學到的規則**。
    ///
    /// ⚠️ 順序有意義：學到的規則放最後，模型對「最後的指示」通常最聽話，
    /// 也才壓得過前面那些通用規則（使用者教它「講話簡短一點」就該真的簡短）。
    /// </summary>
    public string EffectiveSystemPrompt(ulong guildId, bool isOwner = false)
    {
        var prompt = isOwner
            ? OwnerInstructions.TrimStart() + "\n\n" + _options.SystemPrompt
            : _options.SystemPrompt;

        if (_options.ToolsEnabled && _tools is not NoChatTools)
            prompt += ToolInstructions;

        return prompt + _personas.Overlay(guildId);
    }

    /// <summary>
    /// 回答一則訊息。
    ///
    /// ⚠️ **絕對不丟例外**（除了使用者取消）：失敗會變成 <see cref="ChatAnswer.Error"/>，
    /// 呼叫端只要把 <see cref="ChatAnswer.Text"/> 貼出去就好 ——
    /// Discord 的事件處理裡丟例外有可能影響連線。
    /// </summary>
    /// <param name="addressed">
    /// 這一則有沒有明確對 Bot 說話（@ 提及或回覆）。
    /// <c>false</c> 代表這是**偷聽到的**訊息：會先問 LLM「這是在跟我說話嗎」，
    /// 不是就回傳 <see cref="ChatAnswer.Ignored"/> 並停止偷聽（呼叫端不要送任何訊息）。
    /// </param>
    public async Task<ChatAnswer> AskAsync(
        ulong guildId,
        ulong channelId,
        ChatTurn incoming,
        ChatTurn? replyTarget,
        CancellationToken cancellationToken = default,
        bool addressed = true)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var now = DateTimeOffset.UtcNow;

        // ── 0a) 偷聽判斷（只在「偷聽到的訊息」時做）──────────
        var listenTokens = 0;
        var botName = _client ?? "Bot";

        if (!addressed)
        {
            // ⚠️ 沒有偷聽窗口就**什麼都不做**。
            //    這一關刻意放在 orchestrator（不只放在 Discord 那一層）：
            //    呼叫端忘記檢查時，「沒有人對它說話」也不該觸發任何回答與花費。
            if (_conversations.PeekListening(guildId, channelId, now) is null)
                return Ignored(startedAt, "沒有在偷聽（沒有人對它說話）");

            _conversations.ConsumeListen(guildId, channelId, now);

            var draftForCheck = _conversations.Draft(guildId, channelId, incoming, replyTarget);
            var detector = new AddresseeDetector(_llm, _options);

            var estimate = TokenEstimator.Estimate(AddresseeDetector.SystemPrompt)
                           + TokenEstimator.Estimate(
                               AddresseeDetector.BuildUserPrompt(
                                   draftForCheck.Current?.Turns ?? [], incoming, botName))
                           + TokenEstimator.PerMessageOverhead * 2;

            var listenCheck = _budget.Check(estimate, now);

            if (!listenCheck.Allowed)
            {
                _budget.RecordRefusal(now);
                _conversations.StopListening(guildId, channelId);

                return Ignored(startedAt, $"額度不足，停止偷聽（{listenCheck.Reason}）");
            }

            var verdict = await detector.DetectAsync(
                draftForCheck.Current?.Turns ?? [], incoming, botName, cancellationToken);

            if (verdict.Usage is { } usage)
            {
                listenTokens = usage.TotalTokens;
                _budget.Record(usage.InputTokens, usage.OutputTokens, guildId, DateTimeOffset.UtcNow);
            }

            if (!verdict.Addressed)
            {
                // ★ 不是在跟我說話 → 停止偷聽、回到等 @ 的模式，而且**什麼都不送**
                _conversations.StopListening(guildId, channelId);
                return Ignored(startedAt, verdict.Detail);
            }
        }

        // ── 0b) 主人授權（必須在「寫進對話記憶」之前）──────────
        //    ⚠️ key 一定要在這一刻就從內容裡拿掉：否則它會進到歷史、提示詞與 log。
        var admin = AdminAuthorizer.Check(incoming.AuthorId, incoming.Content, _options);

        if (admin.KeyPresent || admin.CleanedContent != incoming.Content)
        {
            incoming = incoming with { Content = admin.CleanedContent };

            if (admin.UnauthorizedAttempt)
                Console.WriteLine($"[授權] ⚠️ 使用者 {incoming.AuthorId} 帶了 key 但不在主人名單裡 → 當一般訊息處理");
        }

        var draft = _conversations.Draft(guildId, channelId, incoming, replyTarget);

        // ── 1) 要不要開新的一段？──────────────────────────
        bool newSegment;
        string reason;
        var detectTokens = 0;

        if (draft.ReplySegment is not null)
        {
            // 「除非被回覆之前的訊息」：回覆是明確指定，直接接那一則所在的段落
            newSegment = false;
            reason = "使用者回覆了先前的訊息 → 接著那一段講";
        }
        else if (draft.GapExceeded)
        {
            newSegment = true;
            reason = draft.IsFirstEver
                ? "第一次對話"
                : $"距離上次說話 {draft.Gap.TotalMinutes:0} 分鐘（超過 {_options.SegmentGap.TotalMinutes:0} 分鐘）→ 新的一段";
        }
        else if (_options.TopicDetect && draft.Current is { TurnCount: > 0 })
        {
            // 時間門檻內 → 由 LLM 判斷「是不是換話題了」
            var detectEstimate = TokenEstimator.Estimate(TopicSwitchDetector.SystemPrompt)
                                 + TokenEstimator.Estimate(
                                       TopicSwitchDetector.BuildUserPrompt(draft.Current.Turns, incoming))
                                 + TokenEstimator.PerMessageOverhead * 2;

            var detectCheck = _budget.Check(detectEstimate, DateTimeOffset.UtcNow);
            if (!detectCheck.Allowed)
            {
                _budget.RecordRefusal(DateTimeOffset.UtcNow);
                return Refusal(detectCheck, startedAt);
            }

            var detected = await _detector.DetectAsync(draft.Current.Turns, incoming, cancellationToken);

            if (detected.Usage is { } usage)
            {
                detectTokens = usage.TotalTokens;
                _budget.Record(usage.InputTokens, usage.OutputTokens, guildId, DateTimeOffset.UtcNow);
            }

            newSegment = detected.IsNewTopic;
            reason = detected.Detail;
        }
        else
        {
            newSegment = draft.Current is null;
            reason = "接續同一段話題";
        }

        var decision = _conversations.Commit(draft, incoming, newSegment, reason);
        var trimmed = ContextBuilder.Trim(decision.Context, _options, decision.ReplyTarget);

        // ── 2) 額度夠嗎？（送出之前就要知道）───────────────
        var toolContext = new ChatToolContext(
            guildId, channelId, incoming.AuthorId, incoming.AuthorName, Owner: admin.Grant);

        var toolLog = new ToolCallLog();
        var plugins = _options.ToolsEnabled ? _tools.CreateFor(toolContext, toolLog) : [];

        var request = new LlmRequest(
            SystemPrompt: EffectiveSystemPrompt(guildId, admin.IsAdmin),
            History: trimmed.Turns,
            Incoming: incoming,
            MaxTokens: _options.MaxOutputTokens,
            Temperature: _options.Temperature,
            Tag: "chat")
        {
            Plugins = plugins
        };

        var check = _budget.Check(request.EstimatedInputTokens, DateTimeOffset.UtcNow);
        if (!check.Allowed)
        {
            _budget.RecordRefusal(DateTimeOffset.UtcNow);

            // 這一則已經寫進記憶了，但 Bot 沒有回話 ——
            // 記一則說明，下一輪才知道「上一句我沒回」
            _conversations.RecordAssistant(decision, Notice(
                "（這一週的 AI 額度用完了，因此沒有回覆）", DateTimeOffset.UtcNow));

            return Refusal(check, startedAt) with
            {
                Decision = decision,
                DecisionReason = reason,
                ContextTurns = trimmed.Turns.Count,
                DroppedTurns = trimmed.DroppedByCount,
                EstimatedContextTokens = trimmed.EstimatedTokens,
                TopicDetectTokens = detectTokens
            };
        }

        // ── 3) 呼叫 LLM ─────────────────────────────────
        try
        {
            var reply = await _llm.CompleteAsync(request, cancellationToken);
            _budget.Record(reply.InputTokens, reply.OutputTokens, guildId, DateTimeOffset.UtcNow);

            return new ChatAnswer(
                Ok: true,
                Text: reply.Text,
                Error: null,
                Refused: false,
                Decision: decision,
                DecisionReason: reason,
                ContextTurns: trimmed.Turns.Count,
                DroppedTurns: trimmed.DroppedByCount,
                EstimatedContextTokens: trimmed.EstimatedTokens,
                InputTokens: reply.InputTokens,
                OutputTokens: reply.OutputTokens,
                TopicDetectTokens: detectTokens + listenTokens,
                UsageReported: reply.UsageReported,
                Model: reply.Model,
                Elapsed: DateTimeOffset.UtcNow - startedAt)
            {
                ToolCalls = toolLog.Calls,
                ToolChangedState = toolLog.ChangedState
            };
        }
        catch (LlmException ex)
        {
            return new ChatAnswer(
                Ok: false,
                Text: $"❌ {ex.Message}",
                Error: ex.Message,
                Refused: false,
                Decision: decision,
                DecisionReason: reason,
                ContextTurns: trimmed.Turns.Count,
                DroppedTurns: trimmed.DroppedByCount,
                EstimatedContextTokens: trimmed.EstimatedTokens,
                InputTokens: 0,
                OutputTokens: 0,
                TopicDetectTokens: detectTokens,
                UsageReported: false,
                Model: _options.Model,
                Elapsed: DateTimeOffset.UtcNow - startedAt)
            {
                ToolCalls = toolLog.Calls,
                ToolChangedState = toolLog.ChangedState
            };
        }
    }

    /// <summary>把 Bot 的回覆記進同一段（附上真正的訊息 ID）。</summary>
    public void RecordReply(
        ContextDecision decision, string text, ulong messageId, ulong botId, string botName, DateTimeOffset at)
    {
        _conversations.RecordAssistant(decision, new ChatTurn(
            ChatRole.Assistant, botName, botId, messageId, text, at));

        // ★ 回完話之後開始（或重新開始）偷聽：接下來幾則沒 @ 它的訊息也聽一下，
        //   由 LLM 判斷是不是在跟它講話（見 AddresseeDetector）。
        if (_options.Eavesdrop)
        {
            _conversations.StartListening(
                decision.Segment.GuildId, decision.Segment.ChannelId,
                _options.EavesdropSeconds, _options.EavesdropMaxMessages);
        }
    }

    /// <summary>被忽略的一則（偷聽時判斷不是對它說話）—— 呼叫端什麼都不要送。</summary>
    private static ChatAnswer Ignored(DateTimeOffset startedAt, string reason)
        => new(
            Ok: false,
            Text: "",
            Error: null,
            Refused: false,
            Decision: null,
            DecisionReason: reason,
            ContextTurns: 0,
            DroppedTurns: 0,
            EstimatedContextTokens: 0,
            InputTokens: 0,
            OutputTokens: 0,
            TopicDetectTokens: 0,
            UsageReported: true,
            Model: "",
            Elapsed: DateTimeOffset.UtcNow - startedAt)
        {
            Ignored = true,
            IgnoreReason = reason
        };

    /// <summary>額度用完時要講的話（公車功能不受影響這件事一定要講）。</summary>
    public static string RefusalText(BudgetCheck check)
        => $"⛔ 這個星期的 AI 額度用完了（{check.Used:N0}／{check.Limit:N0} tokens）。\n" +
           $"會在 **{check.ResetAt.ToLocalTime():MM-dd HH:mm}**（UTC 週一 00:00）重置。\n" +
           "公車功能不受影響 —— `/bus panel` 照樣可以用。";

    private static ChatAnswer Refusal(BudgetCheck check, DateTimeOffset startedAt)
        => new(
            Ok: false,
            Text: RefusalText(check),
            Error: check.Reason,
            Refused: true,
            Decision: null,
            DecisionReason: "額度不足",
            ContextTurns: 0,
            DroppedTurns: 0,
            EstimatedContextTokens: check.EstimatedInputTokens,
            InputTokens: 0,
            OutputTokens: 0,
            TopicDetectTokens: 0,
            UsageReported: true,
            Model: "",
            Elapsed: DateTimeOffset.UtcNow - startedAt);

    private static ChatTurn Notice(string text, DateTimeOffset at)
        => new(ChatRole.Assistant, "Bot", 0, 0, text, at);
}
