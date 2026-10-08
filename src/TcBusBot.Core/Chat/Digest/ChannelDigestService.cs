using System.Text;

namespace TcBusBot.Core.Chat.Digest;

/// <summary>一次摘要要處理的東西（由 Discord 那一層抓好訊息之後傳進來）。</summary>
public sealed record DigestRun(
    ulong GuildId,
    ulong ChannelId,
    string ChannelName,
    IReadOnlyList<DigestMessage> Messages,
    IReadOnlyList<(ulong Id, string Name)> Participants,
    bool Manual,
    bool Truncated = false)
{
    public DateTimeOffset? From => Messages.Count == 0 ? null : Messages[0].At;

    public DateTimeOffset? To => Messages.Count == 0 ? null : Messages[^1].At;
}

/// <summary>
/// 「每週自動整理頻道」的**核心流程**：分批 → 抽取 → 彙整成週報 → 存檔 ＋ 推進游標。
///
/// 為什麼這一層在 Core（而不是寫在 Discord 的排程器裡）：
/// 排程、抓訊息、發訊息是 Discord 的事；而「只算新訊息」「分批不要漏」「@ 誰是安全的」
/// 「失敗不可以推進游標」這些是**規則**，必須能在沒有 Discord、沒有網路的情況下測。
///
/// ── 三個容易做錯、這裡刻意處理的地方 ─────────────────────
/// 1. **游標只在成功之後推進**：先推進再跑，一旦 API 失敗那段訊息就永遠不會被摘要到。
/// 2. **分批**：一週的訊息可能遠超過模型一次吃得下的量。分批抽取（map）＋最後彙整（reduce）
///    是唯一不會截斷的做法；而**只有最後那一次**開思考（省錢）。
/// 3. **@ 的人一定要核對**：模型很會照格式編 ID，見 <see cref="DigestPrompt.SanitizeMentions"/>。
/// </summary>
public sealed class ChannelDigestService
{
    private readonly ILlmClient _llm;
    private readonly DigestStore _store;
    private readonly WeeklyTokenBudget _budget;
    private readonly DigestOptions _options;

    public ChannelDigestService(
        ILlmClient llm,
        DigestStore store,
        WeeklyTokenBudget budget,
        DigestOptions options)
    {
        _llm = llm;
        _store = store;
        _budget = budget;
        _options = options;
    }

    public bool Enabled => _options.Enabled && _llm.IsConfigured;

    /// <summary>跑一個頻道。</summary>
    public async Task<DigestOutcome> RunAsync(DigestRun run, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return DigestOutcome.Failed("每週摘要功能已停用（`LLM_DIGEST=false`）。");

        if (!_llm.IsConfigured)
            return DigestOutcome.Failed("這台主機沒有設定 LLM 金鑰，所以沒辦法做摘要。");

        if (run.Messages.Count == 0)
            return DigestOutcome.Skip("沒有新訊息（上次整理之後這個頻道沒有人說話）。");

        var now = DateTimeOffset.UtcNow;
        var previous = _store.Latest(run.ChannelId);
        var previousOpen = previous?.OpenItems ?? [];
        var calls = 0;
        var inputTokens = 0;
        var outputTokens = 0;
        var model = "";

        // ── 1) 分批（map）─────────────────────────────────
        var batches = Batch(run.Messages);
        var partials = new List<DigestMessage>();
        var content = (IReadOnlyList<DigestMessage>)run.Messages;
        var bulkInput = 0;

        if (batches.Count > 1)
        {
            for (var i = 0; i < batches.Count; i++)
            {
                var prompt = DigestPrompt.BuildUserPrompt(
                    run.ChannelName, _options.ZoneOffset, batches[i], [], run.Participants,
                    isPartial: true, partialIndex: i + 1, partialCount: batches.Count);

                var (reply, refusal) = await CallAsync(
                    prompt, run, tag: "digest:map", digestCall: false,
                    maxTokens: Math.Clamp(_options.MaxOutputTokens / 3, 400, 1200),
                    cancellationToken);

                if (refusal is not null) return DigestOutcome.Refused(refusal);

                calls++;
                inputTokens += reply!.InputTokens;
                outputTokens += reply.OutputTokens;
                model = reply.Model;

                partials.Add(DigestPrompt.AsPartialTurn(
                    i + 1,
                    reply.Text,
                    batches[i].Count > 0 ? batches[i][^1].At : now));

                bulkInput += reply.TotalTokens;
            }

            content = partials;
        }

        // ── 2) 最後一次彙整（reduce，**這一步才開思考**）────
        var finalPrompt = DigestPrompt.BuildUserPrompt(
            run.ChannelName, _options.ZoneOffset, content, previousOpen, run.Participants, isPartial: false);

        var (final, finalRefusal) = await CallAsync(
            finalPrompt, run, tag: "digest", digestCall: true,
            maxTokens: _options.MaxOutputTokens,
            cancellationToken);

        if (finalRefusal is not null)
        {
            // 分批已經花掉的錢照記（那是真的花掉了），但**游標不推進** → 下次會重跑
            return DigestOutcome.Refused(
                $"{finalRefusal}（這一輪已經用掉 {inputTokens + outputTokens:N0} tokens，游標沒有推進，下次會重新整理同一段）");
        }

        calls++;
        inputTokens += final!.InputTokens;
        outputTokens += final.OutputTokens;
        model = final.Model;

        // ── 3) 解析 ＋ 核對（@ 的人一定要真的在這個頻道出現過）──
        var draft = DigestPrompt.Parse(final.Text, _options.MaxMentions);

        var mentions = DigestPrompt.SanitizeMentions(
            draft.Mentions, run.Participants, _options.MaxMentions);

        var openItems = DigestPrompt.SanitizeItems(draft.OpenItems, run.Participants);

        var record = new DigestRecord(
            Id: $"{run.ChannelId}-{now:yyyyMMddHHmmss}",
            ChannelId: run.ChannelId,
            GuildId: run.GuildId,
            At: now,
            From: run.From,
            To: run.To,
            MessageCount: run.Messages.Count,
            PeopleCount: run.Participants.Count,
            Summary: draft.Summary,
            Highlights: draft.Highlights,
            OpenItems: openItems,
            Mentions: mentions,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            Calls: calls,
            Manual: run.Manual,
            Truncated: run.Truncated,
            Model: model);

        // ── 4) 存檔 ＋ 推進游標（**只有在這裡**，也就是成功之後）──
        _store.Append(record);

        var last = run.Messages[^1];
        var effectiveCursor = run.Truncated ? null : last.Id;

        _store.MarkRun(
            run.ChannelId,
            effectiveCursor,
            run.Truncated ? record.To : last.At,
            now,
            _options.ZoneOffset);

        var note = run.Truncated
            ? "（這一輪讀到安全上限，游標只推到讀到的地方，剩下的下次再處理）"
            : "";

        return new DigestOutcome(
            true,
            $"✅ 已整理 {run.Messages.Count:N0} 則訊息（{run.Participants.Count} 人、{calls} 次呼叫、" +
            $"{inputTokens + outputTokens:N0} tokens）{note}",
            record,
            Truncated: run.Truncated ? run.Messages.Count : 0);
    }

    /// <summary>
    /// 把訊息分成幾批（依估算的 token 數）。
    ///
    /// 邊界：單則訊息就超過上限時**自己一批**（不能切成兩半 —— 那會讓一句話被拆開，
    /// 摘要出來的意思會歪掉）。這種情況很少，但真的發生時不要當掉。
    /// </summary>
    public IReadOnlyList<List<DigestMessage>> Batch(IReadOnlyList<DigestMessage> messages)
    {
        var limit = Math.Max(1000, _options.BatchTokens);
        var batches = new List<List<DigestMessage>>();
        var current = new List<DigestMessage>();
        var used = 0;

        foreach (var message in messages)
        {
            var cost = TokenEstimator.Estimate(message.Text) + 12;

            if (current.Count > 0 && used + cost > limit)
            {
                batches.Add(current);
                current = [];
                used = 0;
            }

            current.Add(message);
            used += cost;
        }

        if (current.Count > 0) batches.Add(current);

        return batches;
    }

    /// <summary>
    /// 呼叫一次（含額度檢查與記帳）。
    ///
    /// 回傳的 <c>refusal</c> 不是 null 就代表**沒有送出去**（額度不足），
    /// 呼叫端必須停下來、而且**不可以推進游標**。
    /// </summary>
    private async Task<(LlmReply? Reply, string? Refusal)> CallAsync(
        string userPrompt,
        DigestRun run,
        string tag,
        bool digestCall,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var request = new LlmRequest(
            SystemPrompt: DigestPrompt.SystemPromptFor(_options.MaxMentions),
            History: [],
            Incoming: new ChatTurn(
                ChatRole.User,
                "每週摘要",
                0UL,
                0UL,
                userPrompt,
                now),
            MaxTokens: maxTokens,
            Temperature: 0.2,
            Tag: tag)
        {
            DigestCall = digestCall
        };

        var check = _budget.Check(request.EstimatedInputTokens, now);

        if (!check.Allowed)
        {
            _budget.RecordRefusal(now);
            return (null, $"這一週的 AI 額度不夠做摘要：{check.Reason}");
        }

        var reply = await _llm.CompleteAsync(request, cancellationToken);

        // 摘要花的是真錢，一定要算進每週額度（不然它就變成繞過上限的後門）
        _budget.Record(reply.InputTokens, reply.OutputTokens, run.GuildId, DateTimeOffset.UtcNow);

        return (reply, null);
    }

    /// <summary>要發到頻道的內容（Discord 那一層只負責排版）。</summary>
    public static string RenderText(DigestRecord record, TimeSpan zoneOffset, bool includeRange = true)
    {
        var body = new StringBuilder();

        if (includeRange && record.From is { } from && record.To is { } to)
            body.AppendLine($"📅 {from.ToOffset(zoneOffset):yyyy-MM-dd HH:mm} ～ {to.ToOffset(zoneOffset):MM-dd HH:mm}" +
                            $"（{record.MessageCount:N0} 則訊息、{record.PeopleCount} 人）");

        body.AppendLine();
        body.AppendLine(record.Summary);

        if (record.Highlights.Count > 0)
        {
            body.AppendLine();
            body.AppendLine("**重點**");
            foreach (var line in record.Highlights) body.AppendLine($"• {line}");
        }

        if (record.OpenItems.Count > 0)
        {
            body.AppendLine();
            body.AppendLine("**⏳ 還在進行中（跨週）**");
            foreach (var item in record.OpenItems)
                body.AppendLine($"• {item.Text}" +
                                (item.OwnerId == 0 ? "" : $"（<@{item.OwnerId}>）"));
        }

        return body.ToString().TrimEnd();
    }
}
