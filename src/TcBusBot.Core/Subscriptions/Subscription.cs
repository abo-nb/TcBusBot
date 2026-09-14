using TcBusBot.Core.Bus;
using TcBusBot.Core.Models;

namespace TcBusBot.Core.Subscriptions;

/// <summary>
/// 一個訂閱 = 一條 (路線, 方向, 上車站) 的組合。
///
/// ★ 使用者在起點勾了多個候選站牌時，會建立**多個訂閱**（每個合法上車站一個），
///   而通知由 <see cref="SubscriptionGroup"/> 層級統一決定：
///   「從這些訂閱中挑公車最快到的那一班」通知一次。
/// </summary>
public sealed class Subscription
{
    public required string Id { get; init; }
    public required string GroupId { get; init; }

    /// <summary>
    /// 這個訂閱屬於群組裡的哪一段行程（<see cref="SubscriptionGroup.Legs"/> 的索引）。
    /// 「合併多個訂閱組成一個通知流」時，一個群組會有多段行程，通知要顯示對的那一段起訖。
    /// </summary>
    public int LegIndex { get; init; }

    public required string RouteUid { get; init; }
    public required string RouteName { get; init; }
    public required int Direction { get; init; }
    public required string Headsign { get; init; }

    public required string BoardStopUid { get; init; }
    public required string BoardStopName { get; init; }
    public required int BoardSequence { get; init; }

    public required string AlightStopUid { get; init; }
    public required string AlightStopName { get; init; }
    public required int AlightSequence { get; init; }

    public bool Enabled { get; set; } = true;

    public int StopsBetween => AlightSequence - BoardSequence;

    public override string ToString() =>
        $"{RouteName}({Direction}) {BoardStopName} → {AlightStopName} [{Id}]";
}

/// <summary>訂閱群組裡的一段行程（起點候選集合 → 目的地候選集合）。</summary>
public sealed record SubscriptionLeg(LocationTarget Origin, LocationTarget Destination);

/// <summary>
/// 一段行程「要訂閱什麼」——已對照目前的站序資料重新匹配過的結果。
/// 套用訂閱組時會產生這個，再交給 <see cref="SubscriptionService"/> 建立訂閱。
/// </summary>
public sealed record LegPlan(
    LocationTarget Origin,
    LocationTarget Destination,
    IReadOnlyList<RouteOption> Options);

/// <summary>通知策略。</summary>
public enum GroupNotifyMode
{
    /// <summary>
    /// （預設）每個「等車期間」只通知一次：
    /// 挑出最快到的那一班來通知，並在訊息裡附上其他選擇。
    /// 直到那班車過站/消失，才會通知下一班 —— 避免一次噴好幾則訊息。
    /// </summary>
    FastestPerWaitingPeriod,

    /// <summary>
    /// 每一班車各通知一次（資訊最完整，但訊息較多）。
    /// </summary>
    EveryBusOnce
}

/// <summary>群組層級的通知狀態（去重就靠這裡）。</summary>
public sealed class GroupNotifyState
{
    /// <summary>vehicleKey → 通知時間。同一班車只在同一群組通知一次。</summary>
    private readonly Dictionary<string, DateTimeOffset> _notified = new(StringComparer.Ordinal);

    public DateTimeOffset? LastNotifiedAt { get; set; }
    public string? CurrentFocusVehicleKey { get; set; }

    /// <summary>同一台車超過這個時間再出現，視為新班次（該車跑下一趟）。</summary>
    public static readonly TimeSpan Rearm = TimeSpan.FromMinutes(90);

    public bool WasNotified(string vehicleKey, DateTimeOffset now)
        => _notified.TryGetValue(vehicleKey, out var at) && now - at <= Rearm;

    public void MarkNotified(string vehicleKey, DateTimeOffset now)
    {
        _notified[vehicleKey] = now;
        LastNotifiedAt = now;

        // 清掉過期的紀錄，避免長時間執行後無限成長
        if (_notified.Count > 256)
        {
            foreach (var k in _notified.Where(kv => now - kv.Value > Rearm).Select(kv => kv.Key).ToList())
                _notified.Remove(k);
        }
    }

    public int NotifiedCount => _notified.Count;
}

/// <summary>
/// 一次「我要從 A 到 B」的完整意圖＝一個群組，內含多個訂閱。
/// 通知的「挑最快」決策在這一層做。
///
/// **一個群組可以有多段行程**（<see cref="Legs"/>）：那是「把多個訂閱組合併成一個通知流」
/// 的結果 —— 例如「上班」與「回家」合併後，通知時從所有路線裡挑最快到的那一班。
/// <see cref="Origin"/> / <see cref="Destination"/> 是**第一段**的起訖（顯示用），
/// 單段行程時就是全部。
/// </summary>
public sealed class SubscriptionGroup
{
    public required string Id { get; init; }
    public required ulong UserId { get; init; }
    public ulong? GuildId { get; init; }
    public ulong? ChannelId { get; init; }        // null = 私訊

    public required LocationTarget Origin { get; init; }
    public required LocationTarget Destination { get; init; }

    /// <summary>這個群組涵蓋的所有行程段（至少 1 段）。</summary>
    public List<SubscriptionLeg> Legs { get; } = new();

    public int NotifyBeforeMinutes { get; set; }
    public GroupNotifyMode NotifyMode { get; set; } = GroupNotifyMode.FastestPerWaitingPeriod;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public List<string> SubscriptionIds { get; } = new();
    public GroupNotifyState State { get; } = new();

    public int LegCount => Math.Max(1, Legs.Count);

    /// <summary>這個訂閱屬於哪一段行程的起訖（找不到時退回第一段）。</summary>
    public SubscriptionLeg LegOf(Subscription sub)
        => sub.LegIndex >= 0 && sub.LegIndex < Legs.Count
            ? Legs[sub.LegIndex]
            : new SubscriptionLeg(Origin, Destination);

    /// <summary>群組的起訖描述：單段是「A → B」，多段是「A → B 等 N 段行程」。</summary>
    public string DescribeRoute()
    {
        var first = $"{Origin.DisplayName} → {Destination.DisplayName}";
        return LegCount == 1 ? first : $"{first} 等 {LegCount} 段行程";
    }

    public override string ToString() =>
        $"{DescribeRoute()}（{SubscriptionIds.Count} 個訂閱，提前 {NotifyBeforeMinutes} 分）";
}

/// <summary>要發給使用者的通知內容（由 SubscriptionMatcher 產生，Discord 層只負責送出）。</summary>
public sealed record BusArrivalNotice(
    SubscriptionGroup Group,
    Subscription Subscription,
    BusEta Eta,
    double LiveSeconds,
    IReadOnlyList<BusArrivalNotice.Alternative> Alternatives)
{
    public sealed record Alternative(string RouteName, string StopName, double LiveSeconds);

    public int Minutes => (int)Math.Round(LiveSeconds / 60.0);

    public string DescribeTime() => LiveSeconds <= 30
        ? "即將進站"
        : $"預計約 {Minutes} 分鐘到站";

    public string DescribeAlternatives()
        => Alternatives.Count == 0
            ? ""
            : "（其他選擇：" + string.Join("、", Alternatives.Select(
                  a => $"{a.RouteName} {a.StopName} 約 {Math.Round(a.LiveSeconds / 60.0)} 分")) + "）";
}
