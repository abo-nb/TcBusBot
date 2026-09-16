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
    private readonly ILlmClient _llm;
    private readonly LlmOptions _options;
    private readonly ConversationStore _conversations;
    private readonly WeeklyTokenBudget _budget;
    private readonly TopicSwitchDetector _detector;

    public ChatOrchestrator(
        ILlmClient llm,
        LlmOptions options,
        ConversationStore conversations,
        WeeklyTokenBudget budget)
    {
        _llm = llm;
        _options = options;
        _conversations = conversations;
        _budget = budget;
        _detector = new TopicSwitchDetector(llm, options);
    }

    public ConversationStore Conversations => _conversations;

    public WeeklyTokenBudget Budget => _budget;

    /// <summary>
    /// 回答一則訊息。
    ///
    /// ⚠️ **絕對不丟例外**（除了使用者取消）：失敗會變成 <see cref="ChatAnswer.Error"/>，
    /// 呼叫端只要把 <see cref="ChatAnswer.Text"/> 貼出去就好 ——
    /// Discord 的事件處理裡丟例外有可能影響連線。
    /// </summary>
    public async Task<ChatAnswer> AskAsync(
        ulong guildId,
        ulong channelId,
        ChatTurn incoming,
        ChatTurn? replyTarget,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
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
        var request = new LlmRequest(
            SystemPrompt: _options.SystemPrompt,
            History: trimmed.Turns,
            Incoming: incoming,
            MaxTokens: _options.MaxOutputTokens,
            Temperature: _options.Temperature,
            Tag: "chat");

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
                TopicDetectTokens: detectTokens,
                UsageReported: reply.UsageReported,
                Model: reply.Model,
                Elapsed: DateTimeOffset.UtcNow - startedAt);
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
                Elapsed: DateTimeOffset.UtcNow - startedAt);
        }
    }

    /// <summary>把 Bot 的回覆記進同一段（附上真正的訊息 ID）。</summary>
    public void RecordReply(
        ContextDecision decision, string text, ulong messageId, ulong botId, string botName, DateTimeOffset at)
        => _conversations.RecordAssistant(decision, new ChatTurn(
            ChatRole.Assistant, botName, botId, messageId, text, at));

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
