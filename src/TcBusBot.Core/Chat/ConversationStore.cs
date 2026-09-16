using System.Collections.Concurrent;

namespace TcBusBot.Core.Chat;

/// <summary>
/// 一個「對話段落」：時間上連續、話題上相連的一串訊息。
///
/// 什麼時候會開新的一段：
///   * 超過 <see cref="LlmOptions.SegmentGapMinutes"/> 沒人說話（不必問 LLM 就知道）
///   * 時間沒超過，但 LLM 判斷「這是在講別的事」
///   * 使用者**回覆了舊訊息**時，反而是回到那一則所屬的段落（見 <see cref="ConversationStore"/>）
/// </summary>
public sealed class ConversationSegment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>0 = 私訊。</summary>
    public ulong GuildId { get; init; }

    public ulong ChannelId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>為什麼開這一段（顯示給使用者看，例如「超過 30 分鐘沒說話」）。</summary>
    public string Reason { get; init; } = "";

    public List<ChatTurn> Turns { get; } = new();

    public int TurnCount => Turns.Count;

    public string Describe()
        => $"{Id}（{Turns.Count} 則，{StartedAt.ToLocalTime():MM-dd HH:mm} 開始，" +
           $"最後 {LastActivityAt.ToLocalTime():HH:mm}）" + (Reason.Length > 0 ? $"｜{Reason}" : "");
}

/// <summary>某個頻道的所有段落（由新到舊都可以查，但「目前這一段」= 最後有動靜的那一段）。</summary>
public sealed class ChannelConversation
{
    public ulong GuildId { get; init; }
    public ulong ChannelId { get; init; }
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>由舊到新（加入順序）；「目前這一段」看 <see cref="LastActivityAt"/> 決定。</summary>
    public List<ConversationSegment> Segments { get; } = new();

    public ConversationSegment? Current
        => Segments.Count == 0 ? null : Segments.MaxBy(s => s.LastActivityAt);

    /// <summary>
    /// 這一則訊息屬於哪一段？
    ///
    /// ⚠️ 只在**這個頻道**裡找 —— 跨伺服器或跨頻道一律找不到，
    /// 這是「不同伺服器不相通」最底層的保證（連索引都不共用）。
    /// </summary>
    public ConversationSegment? FindByMessage(ulong messageId)
        => Segments.FirstOrDefault(s => s.Turns.Any(t => t.MessageId == messageId));

    public ChatTurn? FindTurn(ulong messageId)
        => Segments.SelectMany(s => s.Turns).FirstOrDefault(t => t.MessageId == messageId);
}

/// <summary>決定上下文之前先收集到的事實（純規則，不呼叫 LLM）。</summary>
public sealed record ContextDraft(
    ChannelConversation Conversation,
    ConversationSegment? Current,
    ConversationSegment? ReplySegment,
    ChatTurn? ReplyTarget,
    TimeSpan Gap,
    bool GapExceeded,
    bool IsFirstEver);

/// <summary>最後決定：新訊息要寫進哪一段、要帶哪些歷史給 LLM。</summary>
public sealed record ContextDecision(
    ChannelConversation Conversation,
    ConversationSegment Segment,
    IReadOnlyList<ChatTurn> Context,
    bool NewSegment,
    string Reason,
    ChatTurn? ReplyTarget);

/// <summary>某個頻道目前的狀態（`/ai status` 顯示用）。</summary>
public sealed record ConversationSnapshot(
    int SegmentCount,
    int CurrentTurnCount,
    DateTimeOffset? CurrentStartedAt,
    DateTimeOffset? CurrentLastActivityAt,
    string CurrentReason,
    TimeSpan? Idle);

/// <summary>
/// 對話記憶（**只在記憶體**）。
///
/// 為什麼不存起來：這跟「訂閱重啟就消失」是同一個取捨 ——
/// 使用者重新問一次就好，而把每個頻道的對話都寫進資料庫反而是隱私負擔。
/// 唯一會被持久化的是**每週 token 用量**（那個不能因為重啟就歸零）。
/// </summary>
public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<(ulong GuildId, ulong ChannelId), ChannelConversation> _map = new();
    private readonly LlmOptions _options;

    public ConversationStore(LlmOptions options) => _options = options;

    public int ChannelCount => _map.Count;

    public int TrackedTurns => _map.Values.Sum(c => c.Segments.Sum(s => s.TurnCount));

    /// <summary>
    /// 收集事實：現在的段落是什麼、被回覆的訊息屬於哪一段、距離上次說話多久。
    ///
    /// 注意這裡**不做決定**（要不要開新段）：因為「換話題了沒」可能要問 LLM，
    /// 而這個方法必須保持同步、可離線測試。決定由呼叫端在
    /// <see cref="Commit"/> 時告訴我們。
    /// </summary>
    public ContextDraft Draft(ulong guildId, ulong channelId, ChatTurn incoming, ChatTurn? replyTarget)
    {
        var conv = _map.GetOrAdd((guildId, channelId), key => new ChannelConversation
        {
            GuildId = key.GuildId,
            ChannelId = key.ChannelId
        });

        var current = conv.Current;
        var replySegment = replyTarget is null ? null : conv.FindByMessage(replyTarget.MessageId);

        var gap = current is null ? TimeSpan.MaxValue : incoming.At - current.LastActivityAt;
        var gapExceeded = current is null || gap > _options.SegmentGap;

        return new ContextDraft(
            Conversation: conv,
            Current: current,
            ReplySegment: replySegment,
            ReplyTarget: replyTarget,
            Gap: gap,
            GapExceeded: gapExceeded,
            IsFirstEver: current is null);
    }

    /// <summary>
    /// 把新訊息寫進去，並回傳「這次要帶給 LLM 的歷史」。
    ///
    /// 三種情況：
    ///   * 回覆了某則舊訊息 → 回到那一段（即使它已經很舊、即使距離很遠）
    ///   * <paramref name="startNewSegment"/> → 開新的一段（舊的不帶）
    ///   * 否則 → 接續目前這一段
    /// </summary>
    public ContextDecision Commit(ContextDraft draft, ChatTurn incoming, bool startNewSegment, string reason)
    {
        var conv = draft.Conversation;
        ConversationSegment segment;
        var created = false;

        if (draft.ReplySegment is { } replySegment)
        {
            // ★「除非被回覆之前的訊息」：回覆就是明確指定「我要接著這個講」，
            //   所以即使它是好幾段以前、或是超過時間門檻，也回到那一段。
            segment = replySegment;
        }
        else if (startNewSegment || draft.Current is null)
        {
            segment = new ConversationSegment
            {
                GuildId = conv.GuildId,
                ChannelId = conv.ChannelId,
                StartedAt = incoming.At,
                LastActivityAt = incoming.At,
                Reason = reason
            };

            conv.Segments.Add(segment);
            created = true;
        }
        else
        {
            segment = draft.Current;
        }

        // 先抄下「加入新訊息之前」的內容 —— 那才是要給 LLM 的歷史
        var context = segment.Turns.ToList();

        // 被回覆的那一則一定要在上下文裡（可能已被 MaxTurnsPerSegment 擠掉）
        if (draft.ReplyTarget is { } target && context.All(t => t.MessageId != target.MessageId))
            context.Insert(0, target);

        segment.Turns.Add(incoming);
        segment.LastActivityAt = incoming.At;
        conv.LastActivityAt = incoming.At;

        TrimSegment(segment);
        TrimSegments(conv);
        TrimChannels();

        return new ContextDecision(
            Conversation: conv,
            Segment: segment,
            Context: context,
            NewSegment: created,
            Reason: reason,
            ReplyTarget: draft.ReplyTarget);
    }

    /// <summary>把 Bot 的回覆也記進同一段（下次才知道自己講過什麼）。</summary>
    public void RecordAssistant(ContextDecision decision, ChatTurn reply)
    {
        decision.Segment.Turns.Add(reply);
        decision.Segment.LastActivityAt = reply.At;
        decision.Conversation.LastActivityAt = reply.At;

        TrimSegment(decision.Segment);
    }

    /// <summary>`/ai forget`：忘掉這個頻道的全部對話。</summary>
    public bool Reset(ulong guildId, ulong channelId)
        => _map.TryRemove((guildId, channelId), out _);

    /// <summary>這一則訊息是不是我們記得過的（用來判斷「回覆」算不算是對 Bot 說話）。</summary>
    public bool HasMessage(ulong guildId, ulong channelId, ulong messageId)
        => _map.TryGetValue((guildId, channelId), out var conv) && conv.FindTurn(messageId) is not null;

    public ChatTurn? FindTurn(ulong guildId, ulong channelId, ulong messageId)
        => _map.TryGetValue((guildId, channelId), out var conv) ? conv.FindTurn(messageId) : null;

    public int ResetGuild(ulong guildId)
    {
        var keys = _map.Keys.Where(k => k.GuildId == guildId).ToList();
        return keys.Count(k => _map.TryRemove(k, out _));
    }

    public ConversationSnapshot? Snapshot(ulong guildId, ulong channelId, DateTimeOffset now)
    {
        if (!_map.TryGetValue((guildId, channelId), out var conv)) return null;

        var current = conv.Current;
        return new ConversationSnapshot(
            SegmentCount: conv.Segments.Count,
            CurrentTurnCount: current?.TurnCount ?? 0,
            CurrentStartedAt: current?.StartedAt,
            CurrentLastActivityAt: current?.LastActivityAt,
            CurrentReason: current?.Reason ?? "",
            Idle: current is null ? null : now - current.LastActivityAt);
    }

    /// <summary>清掉太久沒動的頻道；回傳清了幾個。</summary>
    public int Purge(DateTimeOffset now)
    {
        var cutoff = now - _options.ChannelTtl;
        var stale = _map.Where(kv => kv.Value.LastActivityAt < cutoff).Select(kv => kv.Key).ToList();
        return stale.Count(k => _map.TryRemove(k, out _));
    }

    private void TrimSegment(ConversationSegment segment)
    {
        if (segment.Turns.Count <= _options.MaxTurnsPerSegment) return;

        // 一次對話通常「最近的幾則」才重要；太舊的丟掉，記憶體也才不會長大
        segment.Turns.RemoveRange(0, segment.Turns.Count - _options.MaxTurnsPerSegment);
    }

    private void TrimSegments(ChannelConversation conv)
    {
        while (conv.Segments.Count > Math.Max(1, _options.MaxSegmentsPerChannel))
        {
            // 只丟「最舊而且不是目前這一段」的
            var current = conv.Current;
            var victim = conv.Segments.Where(s => !ReferenceEquals(s, current))
                                      .OrderBy(s => s.LastActivityAt)
                                      .FirstOrDefault();
            if (victim is null) break;
            conv.Segments.Remove(victim);
        }
    }

    private void TrimChannels()
    {
        var max = Math.Max(1, _options.MaxChannels);
        if (_map.Count <= max) return;

        var victims = _map.OrderBy(kv => kv.Value.LastActivityAt)
                          .Take(_map.Count - max)
                          .Select(kv => kv.Key)
                          .ToList();

        foreach (var key in victims) _map.TryRemove(key, out _);
    }
}
