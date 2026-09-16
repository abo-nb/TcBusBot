namespace TcBusBot.Core.Bus;

/// <summary>
/// 「用一句話就完成公車操作」的邏輯層。
///
/// 為什麼要獨立這一層（而不是全部塞進 SK 的 plugin 方法）：
/// LLM 的 tool call 只是**輸入通道**，真正危險的是後面的決策 ——
/// 「台中火車站」要對到哪幾個 StopUID、「臺中科大」要展開成哪些候選站。
/// 這正是整個專案最容易出錯的地方（同名站牌、去回程、站序），
/// 所以它必須能在**沒有 LLM、沒有網路**的情況下離線測試。
///
/// Discord 那邊的 `BusTools`（Semantic Kernel 的 plugin）只是把字串參數
/// 轉呼叫這一層，然後把結果講給模型聽。
/// </summary>
public sealed class BusActionService
{
    private readonly TaichungBusDataService _data;
    private readonly Subscriptions.SubscriptionService _subs;

    public BusActionService(TaichungBusDataService data, Subscriptions.SubscriptionService subs)
    {
        _data = data;
        _subs = subs;
    }

    /// <summary>預設最多帶幾個候選站進匹配（太多會讓「同站區不同站名」全部命中而失去精準度）。</summary>
    public const int MaxGroupsPerKeyword = 3;

    // ─────────────────────────────────────────────────────
    //  站牌搜尋（給模型看的文字）
    // ─────────────────────────────────────────────────────

    /// <summary>搜尋站牌，回傳「模型看得懂」的文字清單。</summary>
    public string SearchStops(string keyword, int limit = 8)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length < 2) return "關鍵字太短（至少 2 個字）。";

        var (shown, hidden) = PreferStrong(_data.Search.SearchGrouped(keyword));

        if (shown.Count == 0)
            return $"找不到符合「{keyword}」的站牌。" +
                   $"這份資料集裡有的站名例如：{string.Join("、", _data.ExampleStopNames(6))}";

        var lines = shown.Take(Math.Max(1, limit)).Select(g =>
        {
            var names = g.Hits.Select(h => h.Entry.DisplayName).Distinct(StringComparer.Ordinal).Take(4);
            return $"• {g.DisplayName}（{g.Hits.Count} 個站牌：{string.Join("、", names)}）";
        });

        return $"「{keyword}」找到 {shown.Count} 組站牌" +
               (hidden > 0 ? $"（另外濾掉 {hidden} 組模糊相符）" : "") + "：\n" + string.Join("\n", lines);
    }

    // ─────────────────────────────────────────────────────
    //  查路線（不建立訂閱）
    // ─────────────────────────────────────────────────────

    public string FindRoutes(string origin, string destination)
    {
        var from = Resolve(origin);
        var to = Resolve(destination);

        if (from.Uids.Count == 0) return $"找不到起點「{origin}」的站牌。";
        if (to.Uids.Count == 0) return $"找不到終點「{destination}」的站牌。";

        var routes = _data.FindRoutes(from.Target, to.Target);

        if (routes.Count == 0)
            return $"{from.Target.DisplayName} → {to.Target.DisplayName} 目前沒有直接抵達的路線" +
                   "（可能方向不對、或需要轉乘）。";

        var lines = routes.Take(15).Select(r =>
            $"• {r.RouteName}（{(r.Direction == 0 ? "去程" : "返程")}）" +
            $"上車：{string.Join("、", r.BoardChoices.Select(b => $"{b.BoardStopName}（第 {b.BoardSequence} 站）"))}");

        return $"{from.Target.DisplayName} → {to.Target.DisplayName} 共 {routes.Count} 條路線：\n" +
               string.Join("\n", lines);
    }

    // ─────────────────────────────────────────────────────
    //  訂閱（會真的建立）
    // ─────────────────────────────────────────────────────

    /// <summary>訂閱結果（文字給模型、結構給 Bot 記錄與除錯）。</summary>
    public sealed record SubscribeOutcome(
        bool Ok,
        string Message,
        Subscriptions.SubscriptionGroup? Group,
        string? OriginName,
        string? DestinationName,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// 用「站名關鍵字」訂閱：把使用者的口語站名展開成候選站集合，再比對站序找出可搭的路線。
    ///
    /// ★ 這個方法做的事**與面板完全相同**（模糊搜尋 → 強相符候選 → 站序匹配），
    ///   所以「用嘴巴講」與「用面板點」得到的結果一模一樣。
    /// </summary>
    public SubscribeOutcome Subscribe(
        ulong userId,
        string origin,
        string destination,
        int notifyMinutes = 10,
        ulong? guildId = null,
        ulong? channelId = null)
    {
        var from = Resolve(origin);
        var to = Resolve(destination);

        if (from.Uids.Count == 0)
            return new SubscribeOutcome(false,
                $"找不到起點「{origin}」。請先問使用者正確的站名，或用 search_stops 查。" +
                $"這份資料集裡有的站名例如：{string.Join("、", _data.ExampleStopNames(4))}。",
                null, null, null, []);

        if (to.Uids.Count == 0)
            return new SubscribeOutcome(false,
                $"找不到終點「{destination}」。請先問使用者正確的站名，或用 search_stops 查。",
                null, null, null, []);

        var routes = _data.FindRoutes(from.Target, to.Target);

        if (routes.Count == 0)
            return new SubscribeOutcome(false,
                $"「{from.Target.DisplayName}」到「{to.Target.DisplayName}」找不到可以直接搭的路線，" +
                "所以沒有建立訂閱。請告訴使用者這個結果（可以建議他換方向或轉乘）。",
                null, null, null, []);

        // 夾在合理範圍內：太短會錯過公車，太長只是多一則訊息
        notifyMinutes = Math.Clamp(notifyMinutes <= 0 ? 10 : notifyMinutes, 1, 60);

        var group = _subs.CreateGroup(
            userId: userId,
            origin: from.Target,
            destination: to.Target,
            options: routes,
            notifyBeforeMinutes: notifyMinutes,
            guildId: guildId,
            channelId: channelId);

        var warnings = new List<string>();
        if (from.Uids.Count > 0 && from.WeakMatch)
            warnings.Add($"起點「{origin}」是模糊相符（{from.Target.DisplayName}）");
        if (to.WeakMatch)
            warnings.Add($"終點「{destination}」是模糊相符（{to.Target.DisplayName}）");

        var lines = _subs.GetSubscriptions(group).Take(10).Select(s =>
            $"• {s.RouteName}（{(s.Direction == 0 ? "去程" : "返程")}）" +
            $"在 {s.BoardStopName} 上車 → 在 {s.AlightStopName} 下車");

        var message =
            $"✅ 已建立訂閱（{group.SubscriptionIds.Count} 筆、{routes.Count} 條路線，提前 {notifyMinutes} 分鐘通知）：\n" +
            string.Join("\n", lines) +
            (group.SubscriptionIds.Count > 10 ? $"\n…還有 {group.SubscriptionIds.Count - 10} 筆" : "");

        if (warnings.Count > 0)
            message += "\n⚠️ " + string.Join("；", warnings) + "（如果不對請取消再重訂）";

        return new SubscribeOutcome(true, message, group,
            from.Target.DisplayName, to.Target.DisplayName, warnings);
    }

    // ─────────────────────────────────────────────────────
    //  查詢／取消自己的訂閱
    // ─────────────────────────────────────────────────────

    public string ListSubscriptions(ulong userId)
    {
        var groups = _subs.GetGroupsByUser(userId).ToList();

        if (groups.Count == 0) return "使用者目前沒有任何訂閱。";

        var blocks = groups.Select((g, index) =>
        {
            var subs = _subs.GetSubscriptions(g);
            var lines = subs.Take(6).Select(s =>
                $"　　• {s.RouteName}（{(s.Direction == 0 ? "去程" : "返程")}）{s.BoardStopName} → {s.AlightStopName}");
            var more = subs.Count > 6 ? $"\n　　…還有 {subs.Count - 6} 筆" : "";

            return $"{index + 1}. {g.DescribeRoute()}（{subs.Count} 筆，提前 {g.NotifyBeforeMinutes} 分鐘）\n" +
                   string.Join("\n", lines) + more;
        });

        return $"使用者目前有 {groups.Count} 組訂閱：\n" + string.Join("\n", blocks);
    }

    /// <summary>取消全部訂閱（可以復原：回傳被移除的內容讓呼叫端推進 Undo 堆疊）。</summary>
    public sealed record CancelOutcome(
        int GroupCount,
        int SubscriptionCount,
        int BoardStopCount,
        string Message,
        IReadOnlyList<(Subscriptions.SubscriptionGroup Group, IReadOnlyList<Subscriptions.Subscription> Subscriptions)> Removed);

    public CancelOutcome CancelAll(ulong userId)
    {
        var removed = _subs.RemoveAllForUser(userId);

        if (removed.Count == 0)
            return new CancelOutcome(0, 0, 0, "使用者目前沒有任何訂閱，所以沒有東西可以取消。", removed);

        var subscriptions = removed.SelectMany(r => r.Subscriptions).ToList();
        var stops = subscriptions.Select(s => s.BoardStopUid).Distinct(StringComparer.Ordinal).Count();

        return new CancelOutcome(
            removed.Count,
            subscriptions.Count,
            stops,
            $"✅ 已取消全部訂閱：{removed.Count} 組、{subscriptions.Count} 筆、" +
            $"不再查詢 {stops} 個上車站。",
            removed);
    }

    // ─────────────────────────────────────────────────────
    //  內部：站名關鍵字 → 候選站牌集合
    // ─────────────────────────────────────────────────────

    private sealed record Resolved(LocationTarget Target, List<string> Uids, bool WeakMatch);

    /// <summary>
    /// 把口語站名展開成候選 StopUID 集合。
    ///
    /// 規則與面板的「預設勾選」一致（見 BusUi.BuildStopOptions）：
    ///   1. 只留強相符（完全／前綴／子字串／縮寫）的群組
    ///   2. 從中挑「夠精確」的（不含單純子字串）當候選；一個都沒有才退回最相符的那一組
    ///   3. 最多取 <see cref="MaxGroupsPerKeyword"/> 組，避免把整個城市都當成候選
    ///
    /// 「同一個站區裡的不同站名」全部都會進候選（例如「臺中車站」8 個月台），
    /// 這樣「300 在 A 月台、304 在臺灣大道」這種情況才找得出來。
    /// </summary>
    private Resolved Resolve(string keyword)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0) return new Resolved(EmptyTarget(), [], true);

        var (shown, _) = PreferStrong(_data.Search.SearchGrouped(keyword));
        if (shown.Count == 0) return new Resolved(EmptyTarget(), [], true);

        var precise = shown.Where(g => g.IsDefaultPick).ToList();
        var weak = precise.Count == 0;

        var picked = (weak ? shown.Take(1) : precise.Take(MaxGroupsPerKeyword)).ToList();

        var uids = picked
            .SelectMany(g => g.Hits)
            .Where(h => h.Strong)
            .Select(h => h.Entry.StopUid)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (uids.Count == 0)
            return new Resolved(EmptyTarget(), [], true);

        return new Resolved(_data.TargetFromStops(uids), uids, weak);
    }

    private static LocationTarget EmptyTarget() => new() { DisplayName = "", CandidateStopUids = [] };

    /// <summary>與面板相同的過濾：有強相符時只留強相符，不要被模糊相符的雜訊塞滿。</summary>
    private static (List<StopSearchGroupResult> Shown, int HiddenFuzzy) PreferStrong(
        IReadOnlyList<StopSearchGroupResult> groups)
    {
        var strong = groups.Where(g => g.IsStrongMatch).ToList();
        return strong.Count > 0 ? (strong, groups.Count - strong.Count) : (groups.ToList(), 0);
    }
}
