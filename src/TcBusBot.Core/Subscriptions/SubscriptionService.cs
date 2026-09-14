using System.Collections.Concurrent;
using TcBusBot.Core.Bus;

namespace TcBusBot.Core.Subscriptions;

/// <summary>
/// 訂閱的記憶體儲存。依需求：**不使用資料庫**，Bot 重啟後訂閱消失是可接受的。
///
/// 只需要維護兩個字典，沒有 repository / unit of work / EF。
/// </summary>
public sealed class SubscriptionService
{
    private readonly ConcurrentDictionary<string, SubscriptionGroup> _groups = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Subscription> _subs = new(StringComparer.Ordinal);

    public int GroupCount => _groups.Count;
    public int SubscriptionCount => _subs.Count;

    /// <summary>
    /// 建立一個「從 A 到 B」的訂閱群組。
    ///
    /// ★ 每個 RouteOption 的**每一個候選上車站**都會變成一個獨立訂閱，
    ///   因為使用者勾了多個站牌就代表「這幾站我都可以上車」。
    ///   通知時再由群組層級挑「最快到的那一班」。
    /// </summary>
    public SubscriptionGroup CreateGroup(
        ulong userId,
        LocationTarget origin,
        LocationTarget destination,
        IReadOnlyList<RouteOption> options,
        int notifyBeforeMinutes,
        ulong? guildId = null,
        ulong? channelId = null,
        GroupNotifyMode notifyMode = GroupNotifyMode.FastestPerWaitingPeriod)
        => CreateMultiLegGroup(
            userId,
            [new LegPlan(origin, destination, options)],
            notifyBeforeMinutes, guildId, channelId, notifyMode);

    /// <summary>
    /// 建立一個**涵蓋多段行程**的訂閱群組（把多個訂閱組合併成一個通知流）。
    ///
    /// 為什麼要合併成一個群組而不是多個群組：
    ///   「挑最快的那一班」的決策在群組層級做（見 <see cref="GroupNotifyState"/>），
    ///   所以只有把它們放進同一個群組，才會得到「從所有路線裡挑最快的一班、只通知一次」。
    ///   分成多個群組就會各通知一次（那是另一個選項：各自獨立）。
    /// </summary>
    public SubscriptionGroup CreateMultiLegGroup(
        ulong userId,
        IReadOnlyList<LegPlan> legs,
        int notifyBeforeMinutes,
        ulong? guildId = null,
        ulong? channelId = null,
        GroupNotifyMode notifyMode = GroupNotifyMode.FastestPerWaitingPeriod)
    {
        if (legs.Count == 0) throw new ArgumentException("至少要有一段行程", nameof(legs));

        var first = legs[0];
        var group = new SubscriptionGroup
        {
            Id = NewId(),
            UserId = userId,
            GuildId = guildId,
            ChannelId = channelId,
            Origin = first.Origin,
            Destination = first.Destination,
            NotifyBeforeMinutes = notifyBeforeMinutes,
            NotifyMode = notifyMode
        };

        for (var legIndex = 0; legIndex < legs.Count; legIndex++)
        {
            var leg = legs[legIndex];
            group.Legs.Add(new SubscriptionLeg(leg.Origin, leg.Destination));

            foreach (var opt in leg.Options)
            {
                foreach (var board in opt.BoardChoices)
                {
                    var sub = new Subscription
                    {
                        Id = NewId(),
                        GroupId = group.Id,
                        LegIndex = legIndex,
                        RouteUid = opt.RouteUid,
                        RouteName = opt.RouteName,
                        Direction = opt.Direction,
                        Headsign = opt.Headsign,
                        BoardStopUid = board.BoardStopUid,
                        BoardStopName = board.BoardStopName,
                        BoardSequence = board.BoardSequence,
                        AlightStopUid = board.AlightStopUid,
                        AlightStopName = board.AlightStopName,
                        AlightSequence = board.AlightSequence
                    };

                    _subs[sub.Id] = sub;
                    group.SubscriptionIds.Add(sub.Id);
                }
            }
        }

        _groups[group.Id] = group;
        return group;
    }

    public SubscriptionGroup? GetGroup(string groupId)
        => _groups.TryGetValue(groupId, out var g) ? g : null;

    public Subscription? GetSubscription(string subscriptionId)
        => _subs.TryGetValue(subscriptionId, out var s) ? s : null;

    public IReadOnlyList<Subscription> GetSubscriptions(SubscriptionGroup group)
        => group.SubscriptionIds
                .Select(id => _subs.TryGetValue(id, out var s) ? s : null)
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

    public IEnumerable<SubscriptionGroup> GetGroups()
        => _groups.Values.OrderBy(g => g.CreatedAt);

    public IEnumerable<SubscriptionGroup> GetGroupsByUser(ulong userId)
        => _groups.Values.Where(g => g.UserId == userId).OrderBy(g => g.CreatedAt);

    public bool RemoveGroup(string groupId)
    {
        if (!_groups.TryRemove(groupId, out var group)) return false;
        foreach (var id in group.SubscriptionIds) _subs.TryRemove(id, out _);
        return true;
    }

    public void SetNotifyBeforeMinutes(string groupId, int minutes)
    {
        if (_groups.TryGetValue(groupId, out var g)) g.NotifyBeforeMinutes = minutes;
    }

    public bool SetMode(string groupId, GroupNotifyMode mode)
    {
        if (!_groups.TryGetValue(groupId, out var g)) return false;
        g.NotifyMode = mode;
        return true;
    }

    public void Clear()
    {
        _groups.Clear();
        _subs.Clear();
    }

    /// <summary>
    /// 輪詢迴圈用的查詢：所有訂閱的「上車站」UID（去重）。
    ///
    /// 這是全案最重要的效能性質：不論使用者的起點有 2 個還是 25 個候選站牌，
    /// 這裡的數量只取決於「不重複的上車站」有幾個 —— 候選集合不會增加 API 呼叫。
    /// </summary>
    public IReadOnlyCollection<string> GetAllEnabledBoardStopUids()
        => _subs.Values
                .Where(s => s.Enabled)
                .Select(s => s.BoardStopUid)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

    private static string NewId()
        => Guid.NewGuid().ToString("N")[..8];
}
