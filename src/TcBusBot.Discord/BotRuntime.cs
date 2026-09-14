using Discord;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Models;
using TcBusBot.Core.Realtime;
using TcBusBot.Core.Subscriptions;
using TcBusBot.Core.Tdx;

namespace TcBusBot.Discord;

/// <summary>
/// Bot 的共享狀態：啟動時間、資料來源說明、以及「產生一則通知預覽」的能力。
///
/// 刻意做成一個具體類別而不是介面 —— 只有一個實作，不需要抽象。
/// </summary>
public sealed class BotRuntime
{
    private readonly SubscriptionService _subs;
    private readonly TdxApiClient? _api;

    public BotRuntime(SubscriptionService subs, TdxApiClient? api)
    {
        _subs = subs;
        _api = api;
    }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public string DataSourceDescription { get; set; } = "（未知）";

    /// <summary>沒有 TDX 金鑰時查不到真實即時資料，UI 要據此給出可行動的說明。</summary>
    public bool CanQueryRealtime => _api is not null;

    public string PollerDescription { get; set; } = "未啟動";

    // ─────────────────────────────────────────────────────
    //  通知預覽
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 用「模擬的」到站資料產生一則通知長相。
    ///
    /// 為什麼要做這個：沒有 TDX 金鑰就沒有真實 ETA，
    /// 但使用者仍然需要看到「通知到底長什麼樣子」才能驗收整個流程。
    /// 模擬資料用站牌 UID 當種子，所以每次按都會得到一樣的結果（可重現）。
    /// </summary>
    public Embed PreviewWithSyntheticData(SubscriptionGroup group, DateTimeOffset now)
    {
        var subs = _subs.GetSubscriptions(group);
        if (subs.Count == 0)
            return new EmbedBuilder()
                .WithColor(new Color(0xE6, 0x7E, 0x22))
                .WithTitle("這個訂閱群組沒有任何訂閱")
                .WithDescription("請重新用 `/bus panel` 建立。")
                .Build();

        var scored = subs
            .Select(s => (Sub: s, Eta: SyntheticEta(s, now)))
            .Select(x => (x.Sub, x.Eta, Live: SubscriptionMatcher.LiveEstimateSeconds(x.Eta, now, 600) ?? 0))
            .OrderBy(x => x.Live)
            .ToList();

        var pick = scored[0];
        var alternatives = scored.Skip(1).Take(2)
            .Select(x => new BusArrivalNotice.Alternative(
                x.Sub.RouteName, x.Sub.BoardStopName, x.Live))
            .ToList();

        return BusUi.Notification(group, pick.Sub, pick.Eta, pick.Live, alternatives, now,
                                  simulation: true);
    }

    /// <summary>用真實 TDX ETA 產生預覽。需要 API 金鑰。</summary>
    public async Task<Embed> PreviewWithRealtimeDataAsync(
        SubscriptionGroup group, DateTimeOffset now, CancellationToken ct = default)
    {
        if (_api is null)
            return PreviewWithSyntheticData(group, now);

        var subs = _subs.GetSubscriptions(group);
        var stops = subs.Select(s => s.BoardStopUid).Distinct(StringComparer.Ordinal).ToList();
        if (stops.Count == 0) return PreviewWithSyntheticData(group, now);

        List<BusEta> etas;
        try
        {
            etas = await _api.GetEtasByStopsAsync(stops, ct);
        }
        catch (Exception ex)
        {
            return new EmbedBuilder()
                .WithColor(new Color(0xC0, 0x39, 0x2B))
                .WithTitle("查詢 TDX 失敗")
                .WithDescription(ex.Message)
                .Build();
        }

        var byKey = etas.ToDictionary(e => (e.RouteUID, e.Direction, e.StopUID));

        var scored = new List<(Subscription Sub, BusEta Eta, double Live)>();
        foreach (var s in subs)
        {
            if (!byKey.TryGetValue((s.RouteUid, s.Direction, s.BoardStopUid), out var eta)) continue;
            var live = SubscriptionMatcher.LiveEstimateSeconds(eta, now, 180);
            if (live is null) continue;
            scored.Add((s, eta, live.Value));
        }

        if (scored.Count == 0)
        {
            return new EmbedBuilder()
                .WithColor(new Color(0x5A, 0x5A, 0x5A))
                .WithTitle("目前查不到預估到站時間")
                .WithDescription("可能原因：\n" +
                                 "• 現在沒有營運中的班次（深夜／清晨常見）\n" +
                                 "• TDX 的 N1 資料還沒更新\n" +
                                 "• 你的候選站牌不在這條路線的行駛範圍\n\n" +
                                 "（台中 N1 資料在「有車輛離站」時才會重算）")
                .Build();
        }

        scored.Sort((a, b) => a.Live.CompareTo(b.Live));
        var pick = scored[0];
        var alternatives = scored.Skip(1).Take(2)
            .Select(x => new BusArrivalNotice.Alternative(x.Sub.RouteName, x.Sub.BoardStopName, x.Live))
            .ToList();

        return BusUi.Notification(group, pick.Sub, pick.Eta, pick.Live, alternatives, now,
                                  simulation: false);
    }

    // ─────────────────────────────────────────────────────
    //  到站時間總表
    // ─────────────────────────────────────────────────────

    /// <summary>一條訂閱目前的到站狀況。</summary>
    public sealed record RouteEtaRow(
        Subscription Subscription,
        BusEta? Eta,
        double? LiveSeconds,
        string StatusText);

    public sealed record EtaTableResult(
        IReadOnlyList<RouteEtaRow> Rows,
        bool Simulation,
        string? Error);

    /// <summary>
    /// 取得訂閱群組中**每一條訂閱**的到站時間，依剩餘時間排序。
    ///
    /// 有 TDX 金鑰時用真實 ETA；沒有時用可重現的模擬資料，
    /// 讓使用者至少能看到這個功能長什麼樣子（並明確標示是模擬資料）。
    /// </summary>
    public async Task<EtaTableResult> BuildEtaTableAsync(
        SubscriptionGroup group, DateTimeOffset now, CancellationToken ct = default)
    {
        var subs = _subs.GetSubscriptions(group);
        if (subs.Count == 0) return new EtaTableResult(Array.Empty<RouteEtaRow>(), false, null);

        // ── 沒有 TDX 金鑰：用模擬資料 ──
        if (_api is null)
        {
            var rows = subs
                .Select(s =>
                {
                    var eta = SyntheticEta(s, now);
                    var live = SubscriptionMatcher.LiveEstimateSeconds(eta, now, staleDataSeconds: 600);
                    return new RouteEtaRow(s, eta, live, DescribeStatus(eta, live));
                })
                .ToList();

            return new EtaTableResult(SortRows(rows), true, null);
        }

        // ── 有金鑰：一次呼叫涵蓋整個群組的上車站 ──
        var stops = subs.Select(s => s.BoardStopUid).Distinct(StringComparer.Ordinal).ToList();

        List<BusEta> etas;
        try
        {
            etas = await _api.GetEtasByStopsAsync(stops, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[eta-table] 查詢失敗：{ex.Message}");
            return new EtaTableResult(
                SortRows(subs.Select(s => new RouteEtaRow(s, null, null, "查詢失敗")).ToList()),
                false, ex.Message);
        }

        var byKey = new Dictionary<(string, int, string), BusEta>();
        foreach (var e in etas) byKey[(e.RouteUID, e.Direction, e.StopUID)] = e;

        var all = subs.Select(s =>
        {
            byKey.TryGetValue((s.RouteUid, s.Direction, s.BoardStopUid), out var eta);
            var live = eta is null ? null : SubscriptionMatcher.LiveEstimateSeconds(eta, now, staleDataSeconds: 180);
            return new RouteEtaRow(s, eta, live, DescribeStatus(eta, live));
        }).ToList();

        return new EtaTableResult(SortRows(all), false, null);
    }

    /// <summary>有剩餘時間的排前面（由近到遠），其餘依路線名排序。</summary>
    private static List<RouteEtaRow> SortRows(List<RouteEtaRow> rows)
        => rows
            .OrderBy(r => r.LiveSeconds.HasValue ? 0 : 1)
            .ThenBy(r => r.LiveSeconds ?? double.MaxValue)
            .ThenBy(r => r.Subscription.RouteName, TcBusBot.Core.Bus.NaturalComparer.Instance)
            .ThenBy(r => r.Subscription.Direction)
            .ToList();

    private static string DescribeStatus(BusEta? eta, double? live)
    {
        if (live is not null) return "";                              // 由 UI 算「約 N 分鐘」
        if (eta is null) return "目前查不到";

        var taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

        return eta.StopStatus switch
        {
            0 => "無預估時間",
            1 => eta.NextBusTime is { } t
                    ? $"尚未發車（{TimeZoneInfo.ConvertTime(t, taipei):HH:mm} 發車）"
                    : "尚未發車",
            2 => "交管不停靠",
            3 => "末班車已過",
            4 => "今日未營運",
            _ => $"其他狀態（{eta.StopStatus}）"
        };
    }

    /// <summary>用站牌 UID 當種子產生可重現的模擬 ETA（1.5 ~ 12 分鐘）。</summary>
    private static BusEta SyntheticEta(Subscription sub, DateTimeOffset now)
    {
        var seed = 17;
        foreach (var c in sub.BoardStopUid) seed = seed * 31 + c;
        foreach (var c in sub.RouteUid) seed = seed * 31 + c;
        var rnd = new Random(seed);

        var seconds = 90 + rnd.Next(0, 630);
        string[] prefixes = ["KKA", "EAL", "FAF", "TCB", "KKB"];
        var plate = $"{prefixes[rnd.Next(prefixes.Length)]}-{1000 + rnd.Next(0, 8999)}";

        return new BusEta
        {
            RouteUID = sub.RouteUid,
            RouteID = sub.RouteName,
            Direction = sub.Direction,
            StopUID = sub.BoardStopUid,
            StopSequence = sub.BoardSequence,
            PlateNumb = plate,
            EstimateTime = seconds,
            StopStatus = 0,
            SrcUpdateTime = now,
            UpdateTime = now
        };
    }
}
