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
            // 除了站名，也把「有哪幾條路線經過」講出來 —— 模型很常需要這個才能判斷
            // 「使用者講的是哪一個站」（同名站牌在不同路口時，經過的路線不一樣）
            var routes = g.Hits
                .SelectMany(h => _data.GetOccurrences(h.Entry.StopUid))
                .Select(o => _data.GetRouteName(o.RouteUid))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .Take(5)
                .ToList();

            var names = g.Hits.Select(h => h.Entry.DisplayName)
                              .Distinct(StringComparer.Ordinal)
                              .Take(4);

            return $"• {g.DisplayName}（{g.Hits.Count} 個站牌：{string.Join("、", names)}）" +
                   (routes.Count > 0 ? $"\n　　經過的路線：{string.Join("、", routes)}" : "");
        });

        return $"「{keyword}」找到 {shown.Count} 組站牌" +
               (hidden > 0 ? $"（另外濾掉 {hidden} 組模糊相符）" : "") + "：\n" + string.Join("\n", lines);
    }

    // ─────────────────────────────────────────────────────
    //  依路線號碼查（使用者常常只知道號碼）
    // ─────────────────────────────────────────────────────

    /// <summary>用路線號碼查路線（「300」「304」「藍1」…）。</summary>
    public string SearchRoutes(string number, int limit = 8)
    {
        number = (number ?? "").Trim();
        if (number.Length == 0) return "請給我路線號碼，例如 300、304、藍1。";

        var routes = _data.FindRoutesByNumber(number, limit);

        if (routes.Count == 0)
            return $"找不到號碼符合「{number}」的路線。（可以先用 search_stops 查站牌，" +
                   "再用 find_routes 看有哪些路線可以搭）";

        var lines = routes.Select(r =>
            $"• {r.Describe()}" +
            (r.SampleStops.Count > 0 ? $"\n　　經過：{string.Join(" → ", r.SampleStops)}" : ""));

        return $"號碼符合「{number}」的路線共 {routes.Count} 筆（同一條路線的去回程分開列）：\n" +
               string.Join("\n", lines);
    }

    // ─────────────────────────────────────────────────────
    //  查路線（不建立訂閱）
    // ─────────────────────────────────────────────────────

    public string FindRoutes(string origin, string destination)
    {
        var from = Resolve(origin);
        var to = Resolve(destination);

        if (from.Uids.Count == 0)
            return $"起點：{DescribeUnresolved(origin, from)}";

        if (to.Uids.Count == 0)
            return $"終點：{DescribeUnresolved(destination, to)}";

        var routes = _data.FindRoutes(from.Target, to.Target);

        if (routes.Count == 0)
        {
            // 沒有直達 → 幫忙找「轉一次」的走法（以前這裡就只回「沒有直達」，等於幫不上忙）
            var transfers = _data.FindTransferRoutes(from.Target, to.Target);
            var head = $"{from.Target.DisplayName} → {to.Target.DisplayName} **沒有直達路線**。";

            if (transfers.Count == 0)
                return head + "也找不到只轉一次就到的走法（可能要轉兩次以上，或其中一段方向不對）。";

            var lines = transfers.Select((t, i) => $"{i + 1}. {t.Describe()}（約 {t.TotalStops} 站）");

            return head + "\n以下是**轉一次**的走法（依總站數排序，僅供參考）：\n" + string.Join("\n", lines);
        }

        var direct = routes.Take(15).Select(r =>
            $"• {r.RouteName}（{(r.Direction == 0 ? "去程" : "返程")}）" +
            $"上車：{string.Join("、", r.BoardChoices.Select(b => $"{b.BoardStopName}（第 {b.BoardSequence} 站）"))}");

        var text = $"{from.Target.DisplayName} → {to.Target.DisplayName} 共 {routes.Count} 條直達路線：\n" +
                   string.Join("\n", direct);

        if (routes.Count == 1)
        {
            // 只有一條時，順便提一下轉乘的可能（使用者常常想比較哪個快）
            var transfers = _data.FindTransferRoutes(from.Target, to.Target, max: 1);

            if (transfers.Count > 0 && transfers[0].TotalStops < 40)
                text += $"\n（另外也可以轉車：{transfers[0].Describe()}）";
        }

        return text;
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
            return new SubscribeOutcome(false, $"起點：{DescribeUnresolved(origin, from)}",
                null, null, null, []);

        if (to.Uids.Count == 0)
            return new SubscribeOutcome(false, $"終點：{DescribeUnresolved(destination, to)}",
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

    private sealed record Resolved(
        LocationTarget Target,
        List<string> Uids,
        bool WeakMatch,
        bool Ambiguous,
        IReadOnlyList<string> Candidates);

    /// <summary>
    /// 把口語站名展開成候選 StopUID 集合。
    ///
    /// ★ **與面板（<c>/bus panel</c>）走完全同一份實作**（<see cref="StopPicks"/>）：
    ///   模糊搜尋 → 只留強相符的站區 → 取「精確命中」的預設勾選 → 用短鍵解出站牌。
    ///
    /// 為什麼一定要共用：以前這裡自己寫了一套（拿**搜尋命中**當候選），
    /// 而命中有上限（整體 25 筆、每組只留符合關鍵字的那些），
    /// 於是「臺中車站」38 個月台只會被放進 2~3 個 ——
    /// 停在其他月台的路線就整條找不到（面板按 `g:` 時是取整個站區，所以面板找得到）。
    /// 這就是「同名站牌找不到」的根因。
    /// </summary>
    private Resolved Resolve(string keyword)
    {
        keyword = (keyword ?? "").Trim();

        if (keyword.Length == 0)
            return new Resolved(EmptyTarget(), [], true, false, []);

        // 與面板相同：有強相符時只留強相符，不要被模糊相符的雜訊塞滿
        var (shown, _) = PreferStrong(_data.Search.SearchGrouped(keyword));

        if (shown.Count == 0)
            return new Resolved(EmptyTarget(), [], true, false, []);

        var (uids, ambiguous, candidates) = StopPicks.ResolveDefaults(shown, _data);

        if (ambiguous)
            return new Resolved(EmptyTarget(), [], true, true, candidates);

        return new Resolved(_data.TargetFromStops(uids), uids, false, false, []);
    }

    /// <summary>找不到站牌／對到多個站區時要講的話（讓模型去問使用者，而不是自己猜）。</summary>
    private string DescribeUnresolved(string keyword, Resolved resolved)
    {
        if (resolved.Ambiguous)
        {
            var list = resolved.Candidates.Count > 0
                ? "候選有：" + string.Join("、", resolved.Candidates)
                : "";

            return $"「{keyword}」對到好幾個不同的站區，我不確定是哪一個，所以先不動手。" +
                   $"請使用者說清楚（例如加上路口名或行政區），{list}";
        }

        return $"找不到符合「{keyword}」的站牌。請先問使用者正確的站名，或用 search_stops 查。" +
               $"這份資料集裡有的站名例如：{string.Join("、", _data.ExampleStopNames(6))}。";
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
