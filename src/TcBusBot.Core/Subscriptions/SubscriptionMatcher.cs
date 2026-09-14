using TcBusBot.Core.Models;
using TcBusBot.Core.Realtime;

namespace TcBusBot.Core.Subscriptions;

/// <summary>
/// 判斷「誰需要被通知」。這是整個系統的大腦，也是純函式最容易測試的部分。
///
/// 決策規則（對應需求：多個站 = 多個訂閱，通知取最快到的那一班）：
///   1. 逐一檢查群組內的每個訂閱，收集「已進入通知視窗且該班車尚未通知過」的候選
///   2. 從候選中挑 **LiveSeconds 最小** 的那一個 → 只發一則通知
///   3. 把該班車標記為已通知（同一班車在同一群組只通知一次，
///      即使它會依序經過多個候選上車站）
/// </summary>
public sealed class SubscriptionMatcher
{
    private readonly SubscriptionService _subs;
    private readonly RealtimeBusCache _cache;
    private readonly int _staleDataSeconds;

    public SubscriptionMatcher(SubscriptionService subs, RealtimeBusCache cache, int staleDataSeconds = 180)
    {
        _subs = subs;
        _cache = cache;
        _staleDataSeconds = staleDataSeconds;
    }

    public IReadOnlyList<BusArrivalNotice> EvaluateAll(DateTimeOffset now)
    {
        var notices = new List<BusArrivalNotice>();

        foreach (var group in _subs.GetGroups())
        {
            var notice = EvaluateGroup(group, now);
            if (notice is not null) notices.Add(notice);
        }

        return notices;
    }

    private BusArrivalNotice? EvaluateGroup(SubscriptionGroup group, DateTimeOffset now)
    {
        var candidates = new List<Candidate>();

        foreach (var sub in _subs.GetSubscriptions(group))
        {
            if (!sub.Enabled) continue;

            var eta = _cache.Get(sub.RouteUid, sub.Direction, sub.BoardStopUid);
            if (eta is null) continue;

            var live = LiveEstimateSeconds(eta, now);
            if (live is null) continue;

            if (live.Value > group.NotifyBeforeMinutes * 60) continue;

            var vehicleKey = VehicleKey(eta);

            // ★ 同一班車在同一群組只通知一次。
            //   沒有這一行的話，一台車依序經過「干城站」和「臺中車站(臺灣大道)」時會通知兩次。
            if (group.State.WasNotified(vehicleKey, now)) continue;

            candidates.Add(new Candidate(sub, eta, live.Value, vehicleKey));
        }

        if (candidates.Count == 0) return null;

        // ★ 挑「最快到的那一班」
        candidates.Sort((a, b) => a.LiveSeconds.CompareTo(b.LiveSeconds));
        var pick = candidates[0];

        // FastestPerWaitingPeriod：已經在等某一班車時，不再通知其他班車。
        // 那班車一旦過站/消失（StillApproaching 為 false），就會換下一班。
        if (group.NotifyMode == GroupNotifyMode.FastestPerWaitingPeriod &&
            group.State.CurrentFocusVehicleKey is { } focus &&
            focus != pick.VehicleKey &&
            StillApproaching(group, focus, now))
        {
            return null;
        }

        group.State.MarkNotified(pick.VehicleKey, now);
        group.State.CurrentFocusVehicleKey = pick.VehicleKey;

        var alternatives = candidates
            .Skip(1)
            .Where(c => c.VehicleKey != pick.VehicleKey)
            .Take(2)
            .Select(c => new BusArrivalNotice.Alternative(
                c.Subscription.RouteName,
                c.Subscription.BoardStopName,
                c.LiveSeconds))
            .ToList();

        return new BusArrivalNotice(group, pick.Subscription, pick.Eta, pick.LiveSeconds, alternatives);
    }

    private bool StillApproaching(SubscriptionGroup group, string vehicleKey, DateTimeOffset now)
    {
        foreach (var sub in _subs.GetSubscriptions(group))
        {
            if (!sub.Enabled) continue;
            var eta = _cache.Get(sub.RouteUid, sub.Direction, sub.BoardStopUid);
            if (eta is null) continue;
            if (VehicleKey(eta) != vehicleKey) continue;

            var live = LiveEstimateSeconds(eta, now);
            if (live is not null) return true;
        }
        return false;
    }

    /// <summary>
    /// 班次識別鍵。ETA 回應沒有班次 ID，所以用 (路線, 方向, 車牌)。
    /// 同一台車跑下一趟由 <see cref="GroupNotifyState.Rearm"/> 的 90 分鐘規則處理。
    /// </summary>
    public static string VehicleKey(BusEta eta)
        => $"{eta.RouteUID}|{eta.Direction}|{eta.PlateNumb}";

    /// <summary>
    /// ★ TDX 的 ETA **不會自動遞減** —— 官方 OAS 明載：
    ///   「N1 僅於該路線上有任一車輛離站時，來源端才會重新計算並發佈，
    ///     因此使用者需自行處理時間遞減機制」。
    ///
    /// 不做這件事，使用者會收到「還有 5 分鐘」但公車其實 3 分鐘就到了。
    /// </summary>
    public static double? LiveEstimateSeconds(BusEta eta, DateTimeOffset now, int? staleDataSeconds = null)
    {
        // 官方 schema：StopStatus 為 2~4 或 PlateNumb 為 -1 時 EstimateTime 為 null。
        // 注意**不要**寫成「StopStatus == 0 才有 ETA」—— 部分縣市在 StopStatus = 1
        // 且 EstimateTime > 0 時，代表「多久後開始發車」，屬正常情形。
        if (eta.EstimateTime is not int raw) return null;

        if (staleDataSeconds is int stale &&
            (now - eta.SrcUpdateTime).TotalSeconds > stale)
            return null;

        var elapsed = (now - eta.SrcUpdateTime).TotalSeconds;
        return Math.Max(0, raw - elapsed);
    }

    private double? LiveEstimateSeconds(BusEta eta, DateTimeOffset now)
        => LiveEstimateSeconds(eta, now, _staleDataSeconds);

    private readonly record struct Candidate(
        Subscription Subscription, BusEta Eta, double LiveSeconds, string VehicleKey);
}
