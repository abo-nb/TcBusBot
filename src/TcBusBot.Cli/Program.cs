using TcBusBot.Core.DataSources;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Subscriptions;

namespace TcBusBot.Cli;

/// <summary>
/// 離線開發用的主控台工具。
///
/// 這個專案刻意「零外部套件」，可以在沒有網路、沒有 TDX 金鑰、沒有 Discord Token
/// 的環境下完整驗證核心邏輯 —— 也就是先確定「站牌搜尋」與「路線匹配」是對的，
/// 再往上接 Discord 與即時資料。
///
///   tcbus selftest                      離線驗收測試（M2 的驗收條件）
///   tcbus search 台中車站                模糊站牌搜尋（含建議群組）
///   tcbus route 台中車站 靜宜大學         候選集合路線匹配 + 訂閱展開
///   tcbus help
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var fixtures = TakeOption(ref args, "--fixtures");
        var dataMode = TakeOption(ref args, "--data") ?? "auto";
        var cacheDir = TakeOption(ref args, "--cache") ?? "cache";
        var command = args[0];
        var rest = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "selftest" => SelfTest.Run(MiniFixtureSource.ResolveRoot(fixtures)),
                "search" => RunSearch(LoadData(fixtures, dataMode, cacheDir), rest),
                "route" => RunRoute(LoadData(fixtures, dataMode, cacheDir), rest),
                "diag" => RunDiag(LoadData(fixtures, dataMode, cacheDir), rest),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"錯誤：{ex.Message}");
            return 1;
        }
    }

    // ─────────────────────────────────────────────────────

    private static int RunSearch(TaichungBusDataService data, string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("用法：tcbus search <站名關鍵字>"); return 1; }

        var query = string.Join(' ', args);
        var groups = data.Search.SearchGrouped(query);

        Console.WriteLine($"搜尋「{query}」→ {groups.Count} 組");
        Console.WriteLine();

        if (groups.Count == 0)
        {
            Console.WriteLine("  找不到。試試更短的關鍵字（例：靜宜）。");
            var near = data.Search.Search(query, 3);
            if (near.Count > 0)
            {
                Console.WriteLine("  最接近的站牌：");
                foreach (var h in near) Console.WriteLine($"    {h.Entry.DisplayName}  (分數 {h.Score})");
            }
            return 0;
        }

        foreach (var g in groups)
        {
            var mark = g.IsDefaultPick ? "☑" : g.IsStrongMatch ? "▪" : "☐";
            Console.WriteLine($"{mark} {g.DisplayName}   （{g.Hits.Count} 個站牌，最高分 {g.BestScore}，" +
                              $"最強比對 {g.Hits.Max(h => h.Kind)}）");
            foreach (var h in g.Hits)
            {
                var rc = data.GetRouteCount(h.Entry.StopUid);
                Console.WriteLine($"      {h.Entry.StopUid}  {h.Entry.DisplayName}   " +
                                  $"[經過 {rc} 條路線，分數 {h.Score}，{h.Kind}]");
            }
        }
        Console.WriteLine();
        Console.WriteLine("（☑ = 精確命中（完全／前綴／縮寫）→ UI 預設勾選；▪ = 子字串相符，會顯示但不預設勾；☐ = 模糊相符）");
        return 0;
    }

    private static int RunRoute(TaichungBusDataService data, string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("用法：tcbus route <起點關鍵字> <目的地關鍵字>"); return 1; }

        var origin = ResolveTarget(data, args[0], "起點");
        var dest = ResolveTarget(data, args[1], "目的地");
        if (origin is null || dest is null) return 1;

        Console.WriteLine($"起點　：{origin}");
        Console.WriteLine($"目的地：{dest}");
        Console.WriteLine();

        var routes = data.FindRoutes(origin, dest);
        if (routes.Count == 0)
        {
            Console.WriteLine("找不到可搭路線。（可能是方向不對，或候選站牌沒有交集）");
            return 0;
        }

        Console.WriteLine($"找到 {routes.Count} 條可用路線：");
        foreach (var r in routes)
        {
            Console.WriteLine($"  {r.RouteName}（{r.DirectionLabel}）  {r.Headsign}");
            foreach (var b in r.BoardChoices)
                Console.WriteLine($"      上車 {b.BoardStopName} @第 {b.BoardSequence} 站 → 下車 {b.AlightStopName} @第 {b.AlightSequence} 站（{b.StopsBetween} 站）");
        }

        // 示範「多個候選站 → 多個訂閱」
        var subs = new SubscriptionService();
        var group = subs.CreateGroup(userId: 1UL, origin, dest, routes, notifyBeforeMinutes: 10);

        Console.WriteLine();
        Console.WriteLine($"若使用者訂閱全部路線 → 建立 {group.SubscriptionIds.Count} 個訂閱（{routes.Count} 條路線展開）：");
        foreach (var s in subs.GetSubscriptions(group))
            Console.WriteLine($"  • {s.RouteName}（{DirLabel(s.Direction)}）{s.BoardStopName} → {s.AlightStopName}");

        var pollStops = subs.GetAllEnabledBoardStopUids();
        Console.WriteLine();
        Console.WriteLine($"輪詢只需查這 {pollStops.Count} 個上車站（去重）：{string.Join(", ", pollStops)}");
        Console.WriteLine("→ 一次 API 呼叫就能涵蓋這個群組的所有訂閱。");
        return 0;
    }

    /// <summary>
    /// 載入資料：預設優先使用本機快取（Bot 抓過的全量 TDX 資料），
    /// 沒有的話才退回內建最小資料集。這樣離線也能對真實資料做診斷。
    /// </summary>
    private static TaichungBusDataService LoadData(string? fixtures, string dataMode, string cacheDir)
    {
        if (dataMode is "auto" or "cache")
        {
            var cached = StaticDataLoader.TryReadCache(cacheDir);
            if (cached is not null)
            {
                var svc = new TaichungBusDataService();
                svc.Load(cached.Stops, cached.StopOfRoutes, cached.Routes);
                var ts = StaticDataLoader.CacheTimestamp(cacheDir);
                Console.WriteLine($"[資料] 本機快取 {cacheDir}（{svc.StopCount} 個站牌、{svc.TripCount} 筆路線站序，抓取於 {ts:yyyy-MM-dd HH:mm}）");
                return svc;
            }

            if (dataMode == "cache")
                throw new FileNotFoundException($"找不到快取資料：{cacheDir}（請先用 Bot 抓一次，或改用 --data fixture）");
        }

        var root = MiniFixtureSource.ResolveRoot(fixtures);
        Console.WriteLine($"[資料] 內建最小資料集（{root}）");
        return MiniFixtureSource.Load(root);
    }

    /// <summary>
    /// 診斷「為什麼某條路線沒有被找出來」。
    /// 跟 Bot 用完全一樣的匹配邏輯，但會記錄每個被排除的原因。
    /// </summary>
    private static int RunDiag(TaichungBusDataService data, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：tcbus diag <起點關鍵字> <目的地關鍵字>");
            return 1;
        }

        var origin = ResolveTarget(data, args[0], "起點");
        var dest = ResolveTarget(data, args[1], "目的地");
        if (origin is null || dest is null) return 1;

        Console.WriteLine();
        Console.WriteLine($"起點候選（{origin.CandidateStopUids.Count} 個站牌）：");
        foreach (var uid in origin.CandidateStopUids)
            Console.WriteLine($"    {uid}  {data.GetDisplayName(uid)}  （{data.GetOccurrences(uid).Count} 條路線方向）");

        Console.WriteLine($"目的地候選（{dest.CandidateStopUids.Count} 個站牌）：");
        foreach (var uid in dest.CandidateStopUids)
            Console.WriteLine($"    {uid}  {data.GetDisplayName(uid)}  （{data.GetOccurrences(uid).Count} 條路線方向）");

        Console.WriteLine();
        var d = data.Diagnose(origin, dest);

        Console.WriteLine("── 匹配結果 " + new string('─', 50));
        Console.WriteLine($"  經過起點候選的路線方向　　：{d.TripsServingOrigin}");
        Console.WriteLine($"  其中也經過目的地候選的　　：{d.TripsServingBoth}");
        Console.WriteLine($"  其中方向正確（上車在前）　：{d.TripsCorrectDirection}");
        Console.WriteLine($"  ✅ 可用路線　　　　　　　：{d.Matched.Count} 條");
        Console.WriteLine();

        if (d.Matched.Count > 0)
        {
            Console.WriteLine("  可用路線：");
            foreach (var r in d.Matched)
            {
                var b = r.Earliest;
                Console.WriteLine($"    {r.RouteName}（{r.DirectionLabel}）　{b.BoardStopName}@{b.BoardSequence} → {b.AlightStopName}@{b.AlightSequence}");
            }
            Console.WriteLine();
        }

        if (d.Rejected.Count > 0)
        {
            Console.WriteLine("  被排除的路線（前 40 筆）：");
            foreach (var r in d.Rejected)
                Console.WriteLine($"    {r.RouteName}（dir={r.Direction}）　{r.Reason}");
        }

        return 0;
    }

    private static LocationTarget? ResolveTarget(TaichungBusDataService data, string keyword, string label)
    {
        var groups = data.Search.SearchGrouped(keyword);
        if (groups.Count == 0)
        {
            Console.Error.WriteLine($"{label}：找不到「{keyword}」");
            return null;
        }

        // 取最相符的那一組當候選集合（真實 UI 會讓使用者勾選、可再增減）
        var g = groups[0];
        var area = data.GetGroupByShortKey(g.GroupKey);
        var uids = area?.StopUids ?? g.Hits.Select(h => h.Entry.StopUid).ToArray();

        Console.WriteLine($"[{label}] 「{keyword}」→ 建議群組「{g.DisplayName}」{uids.Count} 個站牌" +
                          (groups.Count > 1 ? $"（另有 {groups.Count - 1} 組相符）" : ""));

        return data.TargetFromStops(uids);
    }

    private static string DirLabel(int direction)
        => direction switch { 0 => "去程", 1 => "返程", 2 => "迴圈", _ => "未知" };

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知指令：{command}");
        PrintHelp();
        return 1;
    }

    private static string? TakeOption(ref string[] args, string name)
    {
        var list = args.ToList();
        var i = list.IndexOf(name);
        if (i < 0 || i + 1 >= list.Count) return null;

        var value = list[i + 1];
        list.RemoveRange(i, 2);
        args = list.ToArray();
        return value;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            tcbus - 台中公車 Discord Bot 的離線開發工具（零外部套件）

            用法：
              tcbus selftest                    離線驗收測試（不需網路 / TDX 金鑰 / Discord）
              tcbus search <站名關鍵字>           模糊站牌搜尋，顯示建議群組
              tcbus route <起點> <目的地>         候選集合路線匹配 + 訂閱展開
              tcbus help

            選項：
              --fixtures <路徑>                  指定 tests/fixtures 的位置

            範例：
              tcbus selftest
              tcbus search 台中車站
              tcbus search 靜宜
              tcbus route 台中車站 靜宜大學
            """);
    }
}
