using System.IO.Compression;
using System.Net;
using TcBusBot.Core.Storage;
using TcBusBot.Core.Tdx;
using TcBusBot.Core.Configuration;
using TcBusBot.Core.DataSources;
using System.Text.Json;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Models;
using TcBusBot.Core.Realtime;
using TcBusBot.Core.Subscriptions;

namespace TcBusBot.Cli;

/// <summary>
/// 離線驗收測試（M2 的驗收條件）。
/// 不需要網路、不需要 TDX 金鑰、不需要 Discord，就能驗證整個核心邏輯。
/// </summary>
public static class SelfTest
{
    private static int _pass;
    private static int _fail;
    private static readonly List<string> Failures = new();

    public static int Run(string fixturesRoot)
    {
        _pass = 0; _fail = 0; Failures.Clear();

        Section("1. 真實 JSON 反序列化（TDX payload 相容性）");
        TestJsonDeserialization(fixturesRoot);

        Section("2. 站名正規化（搜尋用 / 分組用，兩套）");
        TestNormalizer();

        Section("3. 模糊站牌搜尋");
        var data = MiniFixtureSource.Load(fixturesRoot);
        TestFuzzySearch(data);

        Section("4. 建議群組（同名合併、不同名不該合併）");
        TestGrouping(data);

        Section("5. 候選集合路線匹配（本版核心）");
        var (origin, dest) = TestRouteMatching(data);

        Section("6. 多個候選站 → 多個訂閱");
        var subs = TestSubscriptionCreation(data, origin, dest);

        Section("7. 通知判定：挑最快到的那一班，且同一班車只通知一次");
        TestMatcher(subs);

        Section("8. ETA 時間遞減（TDX 不會自己遞減）");
        TestDecay();

        Section("9. 過期資料防護");
        TestStaleData();

        Section("10. .env 檔解析（含 PowerShell 風格）");
        TestDotEnv();

        Section("11. 設定來源優先序（命令列 > 環境變數 > .env）");
        TestSettingPrecedence();

        Section("11b. .env 與系統環境變數混用（含 ${VAR} 展開）");
        TestEnvInterop();

        Section("12. TDX 用戶端：gzip 解壓縮（曾有 0x1F 錯誤）");
        TestTdxGzip();

        Section("12. 簡體中文輸入（真正的簡繁折疊）");
        TestSimplifiedInput(data);

        Section("13. 訂閱組儲存（SQLite）");
        TestSavedGroupStore();

        Section("14. 合併訂閱組（多段行程）與復原");
        TestMergeAndUndo(data);

        Section("15. 縮寫搜尋（台中科大 → 國立臺中科技大學）");
        TestAbbreviationSearch();

        Section("16. 儲存後端：MongoDB → SQLite → 文字檔 → 記憶體");
        TestStorageBackends();

        Section("17. 輪詢成本：$select 只要求會用到的欄位");
        TestEtaSelect(Path.Combine(fixturesRoot, "real", "EstimatedTimeOfArrival.sample.json"));

        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        Console.WriteLine($"  通過 {_pass} 項，失敗 {_fail} 項");
        if (Failures.Count > 0)
        {
            Console.WriteLine("  失敗項目：");
            foreach (var f in Failures) Console.WriteLine($"    ✘ {f}");
        }
        Console.WriteLine(new string('─', 64));
        return _fail == 0 ? 0 : 1;
    }

    // ─────────────────────────────────────────────────────

    private static void TestJsonDeserialization(string root)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var stopJson = File.ReadAllText(Path.Combine(root, "real", "Stop.sample.json"));
        var stops = JsonSerializer.Deserialize<List<BusStop>>(stopJson, opts)!;
        Check("Stop.sample.json 解析出 1 筆", stops.Count == 1);
        Check("StopUID 正確", stops[0].StopUID == "TXG12251");
        Check("Zh_tw 站名正確", stops[0].ZhTwName == "臺中車站(A月台)");
        Check("座標正確", Math.Abs(stops[0].Lon - 120.686459) < 1e-6 && Math.Abs(stops[0].Lat - 24.137749) < 1e-6);

        var sorJson = File.ReadAllText(Path.Combine(root, "real", "StopOfRoute.sample.json"));
        var sors = JsonSerializer.Deserialize<List<BusStopOfRoute>>(sorJson, opts)!;
        Check("StopOfRoute 解析出 1 筆 (路線,方向)", sors.Count == 1);
        Check("Direction == 1", sors[0].Direction == 1);
        Check("站序筆數正確", sors[0].Stops.Count == 3);
        Check("站序已依 StopSequence", sors[0].Stops.Select(s => s.StopSequence).SequenceEqual(new[] { 1, 2, 24 }));
        Check("SubRouteUID 與 RouteUID 同值（台中實測）", sors[0].SubRouteUID == sors[0].RouteUID);

        var etaJson = File.ReadAllText(Path.Combine(root, "real", "EstimatedTimeOfArrival.sample.json"));
        var etas = JsonSerializer.Deserialize<List<BusEta>>(etaJson, opts)!;
        Check("ETA 解析出 3 筆", etas.Count == 3);
        Check("StopStatus=1 時 EstimateTime 為 null", etas[0].StopStatus == 1 && etas[0].EstimateTime is null);
        Check("StopStatus=0 時 EstimateTime 有值", etas[1].StopStatus == 0 && etas[1].EstimateTime == 960);
        Check("Estimates[] 多班車資料可解析", etas[1].Estimates.Count == 1 && etas[1].Estimates[0].EstimateTime == 960);
        Check("IsLastBus 以 bool 表示也能解析（TDX 實際回 bool，標準寫 0/1）",
              etas[1].Estimates[0].IsLastBus == false);
        Check("StopStatus=3（末班車已過）且無 NextBusTime",
              etas[2].StopStatus == 3 && etas[2].NextBusTime is null);
        Check("PlateNumb 為空字串 → 判定為無車輛", !etas[0].HasVehicle && etas[1].HasVehicle);
    }

    private static void TestNormalizer()
    {
        // ★ 比對用的鍵一律是「簡體」（見 ChineseText 的說明），顯示用的站名才是繁體
        Check("ForSearch 保留括號內容（打 A月台 要能找到）",
            StopNameNormalizer.ForSearch("臺中車站(A月台)") == "台中车站a月台",
            StopNameNormalizer.ForSearch("臺中車站(A月台)"));

        Check("ForGroup 去掉括號內容（把月台併成同一站區）",
            StopNameNormalizer.ForGroup("臺中車站(臺灣大道)") == "台中车站",
            StopNameNormalizer.ForGroup("臺中車站(臺灣大道)"));

        Check("臺 → 台", StopNameNormalizer.ForSearch("臺中車站") == "台中车站");
        Check("口語「火車站」→「車站」", StopNameNormalizer.ForSearch("台中火車站") == "台中车站");
        Check("空白被移除", StopNameNormalizer.ForSearch("臺中 車站") == "台中车站");
        Check("全形轉半形", StopNameNormalizer.ForSearch("Ａ月台") == "a月台");
        Check("英文轉小寫並去空白", StopNameNormalizer.ForSearch("Taichung Station") == "taichungstation");
        Check("無括號的站名分組時保持獨立",
            StopNameNormalizer.ForGroup("靜宜大學靜園餐廳") == "静宜大学静园餐厅");
        Check("ForSearch 與 ForGroup 確實不同",
            StopNameNormalizer.ForSearch("臺中車站(A月台)") != StopNameNormalizer.ForGroup("臺中車站(A月台)"));

        // ★ 最重要的一條：繁體輸入與簡體輸入**經過正規化之後必須一模一樣**
        Check("繁體與簡體輸入得到同一個搜尋鍵",
            StopNameNormalizer.ForSearch("臺中車站") == StopNameNormalizer.ForSearch("台中车站"));
        Check("簡體與繁體輸入得到同一個分組鍵",
            StopNameNormalizer.ForGroup("靜宜大學") == StopNameNormalizer.ForGroup("静宜大学"));
    }

    private static void TestFuzzySearch(TaichungBusDataService data)
    {
        Check($"搜尋索引已建立（{data.Search.EntryCount} 筆）", data.Search.EntryCount > 50);

        void Found(string query, string expectedUid, string note)
        {
            var hits = data.Search.Search(query, 25);
            var rank = -1;
            for (var i = 0; i < hits.Count; i++)
                if (hits[i].Entry.StopUid == expectedUid) { rank = i; break; }

            var ok = rank >= 0;
            Check($"[{note}] 搜「{query}」找到 {data.GetDisplayName(expectedUid)}",
                ok, ok ? $"排名 {rank + 1}/{hits.Count}" : "找不到");
        }

        Found("臺中車站", "TXG12251", "標準輸入");
        Found("台中車站", "TXG12251", "臺→台（最常見的變體）");
        Found("台中車站", "TXG11020", "臺→台，另一個月台也要找到");
        Found("台中火車站", "TXG12251", "口語「火車站」");
        Found("臺中 車站", "TXG12251", "含空白");
        Found("Ａ月台", "TXG12251", "全形字母");
        Found("Taichung Station", "TXG12251", "英文站名");
        Found("中車站", "TXG12251", "口語＋省略");
        Found("靜宜", "TXG13567", "站名前綴");

        // 簡體輸入（原本列為 v1 的已知限制，現在是完整支援）
        var simplified = data.Search.Search("台中车站", 25);
        Check("簡體「台中车站」找得到（不再需要打繁體）", simplified.Count > 0,
            $"命中 {simplified.Count} 筆");

        // 群組化結果：確認每個群組都有代表，不會被單一熱門群組吃光名額
        var grouped = data.Search.SearchGrouped("車站");
        Check("群組化搜尋有結果", grouped.Count > 0, $"共 {grouped.Count} 組");
        Console.WriteLine("  ℹ 搜「車站」的群組：" +
            string.Join(" / ", grouped.Take(6).Select(g => $"{g.DisplayName}({g.Hits.Count})")));
    }

    private static void TestGrouping(TaichungBusDataService data)
    {
        var gA = data.GetGroupKeyForStop("TXG12251");   // 臺中車站(A月台)
        var gB = data.GetGroupKeyForStop("TXG11020");   // 臺中車站(臺灣大道)
        var gC = data.GetGroupKeyForStop("TXG12769");   // 干城站
        var gD = data.GetGroupKeyForStop("TXG13567");   // 靜宜大學(專用道) 去程
        var gE = data.GetGroupKeyForStop("TXG21478");   // 靜宜大學(專用道) 回程
        var gF = data.GetGroupKeyForStop("TXG19438");   // 靜宜大學(專用道) 304 用

        Check("同站區不同站名會合併：臺中車站(A月台) 與 臺中車站(臺灣大道)", gA == gB, $"{gA} vs {gB}");
        Check("★ 不同站不會被合併：干城站 與 臺中車站（相距約 340m）", gC != gA, $"{gC} vs {gA}");
        Check("同名站牌跨方向/跨路線合併：靜宜大學(專用道) 三個 UID 同組", gD == gE && gE == gF,
            $"{gD} / {gE} / {gF}");

        var group = data.GetGroupByShortKey(gA);
        Check("群組可依短鍵取回", group is not null && group.StopUids.Count >= 2,
            group is null ? "null" : $"{group.DisplayName}: {string.Join(",", group.StopUids)}");
        Check("群組顯示名為人類可讀的站區名", group?.DisplayName == "臺中車站", group?.DisplayName);
    }

    private static (LocationTarget Origin, LocationTarget Dest) TestRouteMatching(TaichungBusDataService data)
    {
        // 起點：使用者勾了「臺中車站」整組 + 干城站
        var origin = data.TargetFromStops(new[] { "TXG12251", "TXG11020", "TXG12769" });
        // 目的地：使用者勾了「靜宜大學」整組（涵蓋去回程不同月台與別條路線用的 UID）
        var dest = data.TargetFromStops(new[] { "TXG13567", "TXG21478", "TXG19438" });

        Console.WriteLine($"  ℹ 起點候選：{origin}");
        Console.WriteLine($"  ℹ 目的地候選：{dest}");

        var routes = data.FindRoutes(origin, dest);
        foreach (var r in routes) Console.WriteLine($"  ℹ {r.Describe()}");

        Check("找到 2 條可用路線（300 與 304）", routes.Count == 2, $"實際 {routes.Count} 條");

        var r300 = routes.FirstOrDefault(r => r.RouteUid == "TXG300");
        var r304 = routes.FirstOrDefault(r => r.RouteUid == "TXG304");

        Check("★ 300 路只有「返程(1)」（去程是反方向，不可列）",
            r300 is not null && r300.Direction == 1,
            r300 is null ? "找不到 300" : $"Direction={r300.Direction}");
        Check("★ 304 路存在（它的上車站叫「臺中車站(臺灣大道)」）",
            r304 is not null && r304.Direction == 1,
            r304 is null ? "找不到 304" : $"Direction={r304.Direction}");

        Check("300 路的下車站是靜宜大學(專用道) 且站序正確",
            r300 is not null && r300.Earliest.BoardSequence == 1 && r300.Earliest.AlightSequence == 24,
            r300 is null ? "" : $"{r300.Earliest.BoardSequence} → {r300.Earliest.AlightSequence}");

        Check("★ 304 路有 2 個候選上車站（干城站 seq9 與 臺中車站(臺灣大道) seq10）",
            r304 is not null && r304.BoardChoices.Count == 2,
            r304 is null ? "" : string.Join(", ", r304.BoardChoices.Select(b => $"{b.BoardStopName}@{b.BoardSequence}")));

        Check("★ 「干城站」比「臺中車站(臺灣大道)」更早到（seq 9 < 10）",
            r304 is not null && r304.BoardChoices[0].BoardStopName == "干城站"
                             && r304.BoardChoices[0].BoardSequence == 9
                             && r304.BoardChoices[1].BoardSequence == 10,
            r304 is null ? "" : string.Join(" → ", r304.BoardChoices.Select(b => $"{b.BoardStopName}({b.BoardSequence})")));

        return (origin, dest);
    }

    private static SubscriptionService TestSubscriptionCreation(
        TaichungBusDataService data, LocationTarget origin, LocationTarget dest)
    {
        var routes = data.FindRoutes(origin, dest);
        var subs = new SubscriptionService();

        var group = subs.CreateGroup(userId: 12345UL, origin, dest, routes, notifyBeforeMinutes: 10);

        Console.WriteLine($"  ℹ {group}");
        foreach (var s in subs.GetSubscriptions(group))
            Console.WriteLine($"    └ {s.RouteName}({s.Direction}) 上車 {s.BoardStopName}@{s.BoardSequence} → 下車 {s.AlightStopName}@{s.AlightSequence}");

        Check("★ 3 個候選上車站 → 建立 3 個訂閱（2 條路線展開）",
            group.SubscriptionIds.Count == 3, $"實際 {group.SubscriptionIds.Count} 個");
        Check("群組層級只有 1 個（一次「從 A 到 B」的意圖）", subs.GroupCount == 1);

        var boardStops = subs.GetAllEnabledBoardStopUids();
        Check("★ 輪詢只認「不重複的上車站」（3 個），與訂閱數無關",
            boardStops.Count == 3, $"實際 {boardStops.Count} 個：{string.Join(",", boardStops)}");

        return subs;
    }

    private static void TestMatcher(SubscriptionService subs)
    {
        var cache = new RealtimeBusCache { StaleDataSeconds = 180 };
        var matcher = new SubscriptionMatcher(subs, cache, staleDataSeconds: 180);
        var groupId = subs.GetGroups().First().Id;

        var t0 = DateTimeOffset.UtcNow;

        // 注意：SrcUpdateTime 必須等於「當下評估時間」，否則會被過期資料防護擋掉
        BusEta Eta(string route, int dir, string stop, string plate, int seconds, DateTimeOffset at)
            => new()
            {
                RouteUID = route,
                Direction = dir,
                StopUID = stop,
                PlateNumb = plate,
                EstimateTime = seconds,
                StopStatus = 0,
                SrcUpdateTime = at,
                UpdateTime = at
            };

        // ── T0：三班車都在 10 分鐘的通知視窗內 ─────────────────
        //   300  @ 臺中車站(A月台)   8 分鐘
        //   304  @ 干城站            5 分鐘 ← 最快
        //   304  @ 臺中車站(臺灣大道) 7 分鐘 ← 同一台車 BBB-222 的另一個候選站
        cache.ReplaceAll(new[]
        {
            Eta("TXG300", 1, "TXG12251", "AAA-111", 480, t0),
            Eta("TXG304", 1, "TXG12769", "BBB-222", 300, t0),
            Eta("TXG304", 1, "TXG11020", "BBB-222", 420, t0)
        }, t0);

        var n1 = matcher.EvaluateAll(t0);
        Check("T0：產生 1 則通知（每群組每週期最多一則）", n1.Count == 1, $"實際 {n1.Count} 則");

        if (n1.Count == 1)
        {
            var n = n1[0];
            Console.WriteLine($"  ℹ 通知：{n.Subscription.RouteName} @ {n.Subscription.BoardStopName} — {n.DescribeTime()}");
            if (n.Alternatives.Count > 0)
                Console.WriteLine($"  ℹ 其他選擇：{string.Join("、", n.Alternatives.Select(a => $"{a.RouteName}@{a.StopName} {Math.Round(a.LiveSeconds / 60.0)}分"))}");

            Check("★ 通知的是「最快到」的那一班（304 @ 干城站，5 分鐘）",
                n.Subscription.RouteName == "304" && n.Subscription.BoardStopName == "干城站",
                $"{n.Subscription.RouteName} @ {n.Subscription.BoardStopName}");
            Check("通知秒數約 300 秒", Math.Abs(n.LiveSeconds - 300) < 2, n.LiveSeconds.ToString("F0"));
            Check("附帶顯示其他選擇（300 約 8 分鐘）",
                n.Alternatives.Any(a => a.RouteName == "300"), $"{n.Alternatives.Count} 個");
        }

        // ── T0+30s：同一批資料 ────────────────────────────────
        var t1 = t0.AddSeconds(30);
        cache.ReplaceAll(new[]
        {
            Eta("TXG300", 1, "TXG12251", "AAA-111", 450, t1),
            Eta("TXG304", 1, "TXG12769", "BBB-222", 270, t1),
            Eta("TXG304", 1, "TXG11020", "BBB-222", 390, t1)
        }, t1);

        var n2 = matcher.EvaluateAll(t1);
        Check("★ 已通知過的同一班車不會重複通知", n2.Count == 0, $"實際 {n2.Count} 則");
        Check("★ 正在等最快那班車時，不會改通知別班車（避免洗版）", n2.Count == 0);
        Check("   → 但其他選擇仍會顯示在 T0 的通知裡", n1.Count == 1 && n1[0].Alternatives.Count > 0);

        // ── T0+60s：BBB-222 只出現在第二個候選站（跨站牌去重）──
        var t2 = t0.AddSeconds(60);
        cache.ReplaceAll(new[]
        {
            Eta("TXG304", 1, "TXG11020", "BBB-222", 240, t2),
            Eta("TXG300", 1, "TXG12251", "AAA-111", 180, t2)
        }, t2);

        var n3 = matcher.EvaluateAll(t2);
        Check("★ 同一台車經過第二個候選站時不重複通知", n3.Count == 0,
            n3.Count == 0 ? "無通知" : string.Join(", ", n3.Select(n => $"{n.Subscription.RouteName}@{n.Subscription.BoardStopName}")));

        // ── T0+120s：BBB-222 過站消失，只剩 300 ────────────────
        var t3 = t0.AddSeconds(120);
        cache.ReplaceAll(new[] { Eta("TXG300", 1, "TXG12251", "AAA-111", 120, t3) }, t3);

        var n4 = matcher.EvaluateAll(t3);
        Check("★ 等待的班車過站後，改通知下一班（300）",
            n4.Count == 1 && n4[0].Subscription.RouteName == "300",
            n4.Count == 0 ? "無通知" : $"{n4[0].Subscription.RouteName} @ {n4[0].Subscription.BoardStopName}");

        // ── 每個車牌超過 90 分鐘視為新班次 ──────────────────────
        var t4 = t0.AddMinutes(91);
        cache.ReplaceAll(new[] { Eta("TXG304", 1, "TXG12769", "BBB-222", 180, t4) }, t4);
        var n5 = matcher.EvaluateAll(t4);
        Check("★ 超過 90 分鐘後同一車牌視為新班次（該車跑下一趟，可再次通知）",
            n5.Count == 1, $"實際 {n5.Count} 則");

        // ── 另一種模式：每班車各通知一次 ────────────────────────
        if (!subs.SetMode(groupId, GroupNotifyMode.EveryBusOnce))
            Check("可切換通知模式", false, "找不到群組");
        else
        {
            var t5 = t0.AddMinutes(120);
            cache.ReplaceAll(new[]
            {
                Eta("TXG300", 1, "TXG12251", "DDD-444", 300, t5),
                Eta("TXG304", 1, "TXG12769", "EEE-555", 360, t5)
            }, t5);

            var a = matcher.EvaluateAll(t5);
            var b = matcher.EvaluateAll(t5);
            Check("EveryBusOnce 模式：兩班車會在相鄰兩週期各被通知一次（最快要先）",
                a.Count == 1 && a[0].Subscription.RouteName == "300" && b.Count == 1,
                $"第一輪 {a.Count} 則（{(a.Count > 0 ? a[0].Subscription.RouteName : "-")}）、第二輪 {b.Count} 則");
        }
    }

    private static void TestDecay()
    {
        var now = DateTimeOffset.UtcNow;
        var eta = new BusEta
        {
            RouteUID = "TXG300",
            Direction = 1,
            StopUID = "TXG12251",
            PlateNumb = "AAA-111",
            EstimateTime = 600,
            StopStatus = 0,
            SrcUpdateTime = now.AddSeconds(-60),
            UpdateTime = now.AddSeconds(-60)
        };

        var live = SubscriptionMatcher.LiveEstimateSeconds(eta, now, staleDataSeconds: 180);
        Check("★ ETA 會依 SrcUpdateTime 自行遞減（600 秒、資料 60 秒前 → 約 540 秒）",
            live is not null && Math.Abs(live.Value - 540) < 2, live?.ToString("F0") ?? "null");

        var noEstimate = new BusEta
        {
            RouteUID = "TXG300", Direction = 1, StopUID = "TXG12251",
            StopStatus = 1, SrcUpdateTime = now, UpdateTime = now
        };
        Check("EstimateTime 為 null 時不產生預估值（避免誤報）",
            SubscriptionMatcher.LiveEstimateSeconds(noEstimate, now, 180) is null);

        // 官方明文：部分縣市 StopStatus=1 且 EstimateTime>0 代表「多久後開始發車」，屬正常情形
        var notDeparted = new BusEta
        {
            RouteUID = "TXG300", Direction = 1, StopUID = "TXG12251",
            StopStatus = 1, EstimateTime = 300, SrcUpdateTime = now, UpdateTime = now
        };
        Check("StopStatus=1 但 EstimateTime>0 仍視為有效預估（官方明文）",
            SubscriptionMatcher.LiveEstimateSeconds(notDeparted, now, 180) is not null);
    }

    private static void TestStaleData()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = new BusEta
        {
            RouteUID = "TXG300", Direction = 1, StopUID = "TXG12251",
            PlateNumb = "AAA-111", EstimateTime = 120, StopStatus = 0,
            SrcUpdateTime = now.AddSeconds(-300),
            UpdateTime = now.AddSeconds(-300)
        };

        Check("SrcUpdateTime 落後 300 秒（門檻 180 秒）→ 不產生預估值",
            SubscriptionMatcher.LiveEstimateSeconds(stale, now, staleDataSeconds: 180) is null);

        Check("門檻放寬到 600 秒時，同一筆資料就有效",
            SubscriptionMatcher.LiveEstimateSeconds(stale, now, staleDataSeconds: 600) is not null);
    }

    // ─────────────────────────────────────────────────────

    private static void TestSimplifiedInput(TaichungBusDataService data)
    {
        Console.WriteLine($"  ℹ 簡繁轉換來源：{(ChineseText.UsesWindowsApi ? "Windows LCMapStringEx（完整對照表）" : "內建字表（覆蓋率較低）")}");

        // ── 1) 方向無關：繁體與簡體必須折疊成同一個鍵 ──────────
        //     這是整個設計的核心：「繁 → 簡」是多對一，一定折疊得起來；
        //     「簡 → 繁」是一對多，Windows 的表會挑錯（頭髮→頭發），所以不往那個方向做。
        var pairs = new (string Traditional, string Simplified)[]
        {
            ("臺灣", "台湾"), ("車站", "车站"), ("靜宜大學", "静宜大学"),
            ("國立臺中科技大學", "国立台中科技大学"), ("圖書館", "图书馆"),
            ("乾淨", "干净"), ("頭髮", "头发"), ("裡面", "里面"), ("一隻", "一只"),
            ("週末", "周末"), ("麵線", "面线"), ("什麼", "什么"), ("為什麼", "为什么"),
            ("於", "于"), ("甯", "宁"), ("藉", "借"), ("菸", "烟"), ("摺", "折"),
            ("慾", "欲"), ("塚", "冢"), ("蹟", "迹"), ("鹹酥雞", "咸酥鸡"),
            ("轉運站", "转运站"), ("消防隊", "消防队"), ("戶政事務所", "户政事务所"),
        };

        var mismatched = new List<string>();
        foreach (var (traditional, simplified) in pairs)
        {
            if (ChineseText.ToSimplified(traditional) != ChineseText.ToSimplified(simplified))
                mismatched.Add($"{traditional}/{simplified} → {ChineseText.ToSimplified(traditional)} vs {ChineseText.ToSimplified(simplified)}");
        }

        Check($"{pairs.Length} 組繁簡詞都折疊成同一個搜尋鍵", mismatched.Count == 0,
            mismatched.Count == 0 ? "" : string.Join("；", mismatched.Take(4)));

        // ── 2) 之前會壞掉的具體案例（迴歸測試）──────────────
        Check("「乾淨」轉成標準簡體「干净」", ChineseText.ToSimplified("乾淨") == "干净",
            ChineseText.ToSimplified("乾淨"));
        Check("「什麼」轉成標準簡體「什么」", ChineseText.ToSimplified("什麼") == "什么",
            ChineseText.ToSimplified("什麼"));
        Check("已經繁體且與簡體同形者不變", ChineseText.ToSimplified("中正路") == "中正路");
        Check("英文不受影響", ChineseText.ToSimplified("Taichung Station 123") == "Taichung Station 123");
        Check("空字串安全", ChineseText.ToSimplified("") == "" && ChineseText.ToSimplified(null) == "");

        void Found(string query, string expectedUid, string note)
        {
            var hits = data.Search.Search(query, 25);
            var rank = -1;
            for (var i = 0; i < hits.Count; i++)
                if (hits[i].Entry.StopUid == expectedUid) { rank = i; break; }

            Check($"[{note}] 搜「{query}」找到 {data.GetDisplayName(expectedUid)}",
                rank >= 0, rank >= 0 ? $"排名 {rank + 1}/{hits.Count}" : "找不到");
        }

        Found("台中车站", "TXG12251", "簡體 + 臺→台");
        Found("静宜大学", "TXG13567", "簡體");
        Found("台湾大道", "TXG19264", "簡體的「湾」");
        Found("荣总", "TXG696", "簡體的「荣总」");
        Found("火车站", "TXG12251", "簡體 + 口語「火車站」");
        Found("干城站", "TXG12769", "簡繁同形");
        Found("台中站", "TXG12251", "縮寫：臺中[車]站");
    }

    /// <summary>
    /// 縮寫搜尋：使用者打「台中科大」要能找到「國立臺中科技大學」。
    ///
    /// 用合成索引測（不依賴資料集），因為要驗的是**評分行為**本身：
    /// 哪些比對算「強相符」（會預設勾選、不會被強相符過濾器藏起來）。
    /// </summary>
    private static void TestAbbreviationSearch()
    {
        static StopSearchEntry Make(string uid, string name, int routes) => new(
            StopUid: uid,
            SearchKey: StopNameNormalizer.ForSearch(name),
            SearchKeyEn: "",
            DisplayName: name,
            GroupKey: uid,
            RouteCount: routes,
            Lon: 0, Lat: 0);

        var search = new StopSearchService(new[]
        {
            Make("TXG13460", "國立臺中科技大學", 13),      // 主要目標
            Make("TXG10382", "臺中科大民生校區", 8),        // 同校的另一個校區，名字就以前綴開頭
            Make("TXG19264", "臺灣大道北勢路口", 5),        // 負向控制：不該被子序列誤命中
            Make("TXG13567", "靜宜大學", 17),
        });

        void Expect(string query, string expectedUid, bool precise, string note)
        {
            var hits = search.Search(query, 25);
            var hit = hits.FirstOrDefault(h => h.Entry.StopUid == expectedUid);
            Check($"[{note}] 搜「{query}」找到 {expectedUid}（預設勾選={precise}）",
                hit is not null && hit.Precise == precise,
                hit is null ? "找不到（0 筆）" : $"分數 {hit.Score}、{hit.Kind}");
        }

        // 核心案例：中間只跳一個字的縮寫
        Expect("台中科大", "TXG13460", true, "縮寫：臺中[技]科大");
        Expect("台中科大", "TXG10382", true, "前綴相符的校區名也要在");

        // 子字串相符：會顯示（Strong）但不預設勾選（Not Precise）——
        // 使用者打了名字的「中間一段」，清單裡看得到、自己勾就好
        Expect("中科技大", "TXG13460", false, "子字串（顯示但不預設勾）");
        Expect("台中科技大学", "TXG13460", false, "簡體、子字串（顯示但不預設勾）");

        // ★ 「能顯示」與「預設勾選」是兩件事：
        //   子字串相符很相關（要顯示），但不夠精確（不該全部預設勾起來）。
        var sub = search.Search("大学", 25);
        var subHit = sub.FirstOrDefault(h => h.Entry.StopUid == "TXG13567");
        Check("子字串相符會顯示但不預設勾選",
            subHit is { Strong: true, Precise: false, Kind: StopMatchKind.Substring },
            subHit is null ? "找不到" : $"{subHit.Kind}、Strong={subHit.Strong}、Precise={subHit.Precise}");

        // 負向控制：完全無關的路口不該被縮寫比對拉進來
        var abbrevHits = search.Search("台中科大", 25);
        Check("縮寫不會命中無關的路口（臺灣大道北勢路口）",
            abbrevHits.All(h => h.Entry.StopUid != "TXG19264"),
            abbrevHits.Count == 0 ? "0 筆" : string.Join("、", abbrevHits.Select(h => h.Entry.DisplayName)));

        // 簡體輸入同一個縮寫
        Expect("国立台中科技大学", "TXG13460", true, "簡體全名（完全相符）");

        // 負向控制：2 字元的子序列不啟用（否則會滿滿都是雜訊）
        var noise = search.Search("台北", 25);
        Check("2 字元不會用子序列亂命中（「台北」不該找到北勢路口）",
            noise.All(h => h.Entry.StopUid != "TXG19264"),
            noise.Count == 0 ? "0 筆（正確）" : string.Join("、", noise.Select(h => h.Entry.DisplayName)));

        // 子序列細節：內部跳幾個字決定它是縮寫還是雜訊
        var tight = StopSearchService.ScoreDetail(StopNameNormalizer.ForSearch("國立臺中科技大學"), StopNameNormalizer.ForSearch("台中科大"));
        var loose = StopSearchService.ScoreDetail(StopNameNormalizer.ForSearch("臺灣大道北勢路口"), StopNameNormalizer.ForSearch("台北路"));
        Check("緊湊子序列判定為縮寫（可預設勾選）",
            tight.Kind == StopMatchKind.Abbreviation && tight.Kind.IsPrecise(), $"{tight.Kind}、分數 {tight.Score}");
        Check("鬆散子序列判定為雜訊（不顯示）",
            loose.Kind == StopMatchKind.Loose && !loose.Kind.IsRelevant(), $"{loose.Kind}、分數 {loose.Score}");
    }

    /// <summary>
    /// 輪詢的點數是「呼叫次數 / 1500 ＋ 回傳資料量(MB) / 150」，
    /// 所以 $select 少要一個欄位就是省錢。這裡用真實 payload 量給你看，
    /// 並且擋住「不小心又要求用不到的欄位」。
    /// </summary>
    private static void TestEtaSelect(string fixturePath)
    {
        // 1) 每個要求的欄位都必須真的有人讀（改動 EtaSelect 時最容易犯的錯）
        Check("$select 含 8 個必要欄位", TdxApiClient.EtasByStopsFields.Length == 8,
            string.Join(",", TdxApiClient.EtasByStopsFields));

        foreach (var needed in new[]
                 {
                     "RouteUID", "Direction", "StopUID",       // 配對與快取鍵
                     "EstimateTime", "StopStatus", "NextBusTime",  // 判斷與顯示
                     "PlateNumb", "SrcUpdateTime",             // 去重與新鮮度
                 })
        {
            Check($"$select 有 {needed}", TdxApiClient.EtasByStopsFields.Contains(needed));
        }

        // 2) 不該要求已知用不到的欄位（那些欄位加回來只會讓資料量變大）
        foreach (var notNeeded in new[] { "StopName", "SubRouteName", "Estimates", "UpdateTime" })
            Check($"$select 不要 {notNeeded}（用不到，只會增加資料量）",
                !TdxApiClient.EtasByStopsFields.Contains(notNeeded));

        // 3) 實測省了多少：把 fixture 的每一筆只挑我們要的欄位重新序列化
        try
        {
            var raw = File.ReadAllText(fixturePath);
            var records = JsonSerializer.Deserialize<List<BusEta>>(
                raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            var wanted = TdxApiClient.EtasByStopsFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var props = typeof(BusEta).GetProperties().Where(p => wanted.Contains(p.Name)).ToArray();

            var projected = records.Average(e => (double)JsonSerializer
                .Serialize(props.ToDictionary(p => p.Name, p => p.GetValue(e))).Length);
            var fullBytes = (double)raw.Length / records.Count;
            var saved = 1 - projected / fullBytes;

            Check($"實測 $select 讓每筆少 {saved * 100:N0}% 位元組（{fullBytes:N0} → {projected:N0} bytes）",
                saved > 0.4, $"省下 {saved * 100:N0}%");
        }
        catch (Exception ex)
        {
            Check("量測 $select 省下的資料量", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // 4) 一次呼叫涵蓋多個站：這是「呼叫次數與使用者數無關」的關鍵
        var url = TdxApiClient.BuildEtasByStopsUrl("Taichung", new[] { "TXG1", "TXG2", "TXG3" });
        Check("一個請求可以同時過濾多個上車站",
            url.Contains("StopUID%20eq%20%27TXG1%27", StringComparison.Ordinal)
            && url.Contains("or%20StopUID%20eq%20%27TXG3%27", StringComparison.Ordinal));

        var fullBatch = TdxApiClient.BuildEtasByStopsUrl(
            "Taichung", Enumerable.Range(1, 40).Select(i => $"TXG{i:D5}"));
        Check($"滿批 40 站的 URL 還很短（{fullBatch.Length} 字元，不會撞到 URL 長度上限）",
            fullBatch.Length < 2000, $"{fullBatch.Length} 字元");
    }

    private static void TestTdxGzip()
    {
        // ── 1) handler 設定必須開啟自動解壓縮（這是實際踩過的 bug 的迴歸測試）──
        var handler = TdxApiClient.CreateHandler();
        Check("CreateHandler() 啟用 gzip 自動解壓縮",
            handler.AutomaticDecompression.HasFlag(DecompressionMethods.GZip),
            handler.AutomaticDecompression.ToString());
        Check("CreateHandler() 也支援 brotli（TDX 支援 br）",
            handler.AutomaticDecompression.HasFlag(DecompressionMethods.Brotli),
            handler.AutomaticDecompression.ToString());

        // ── 2) 端到端：用假的 socket 連線餵一段 gzip 過的 JSON ──
        //     完全離線、不開任何真實 socket（ConnectCallback 直接回傳預先寫好的 HTTP 回應）
        var json = File.ReadAllText(
            Path.Combine(MiniFixtureSource.ResolveRoot(), "real", "Stop.sample.json"));

        var opts = new TdxOptions { BaseUrl = "http://tdx.local" };   // http 才不會觸發 TLS
        var apiHandler = TdxApiClient.CreateHandler();
        apiHandler.ConnectCallback = (_, _) =>
        {
            var gz = GzipBytes(System.Text.Encoding.UTF8.GetBytes(json));
            var head = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Encoding: gzip\r\n" +
                $"Content-Length: {gz.Length}\r\n" +
                "Connection: close\r\n\r\n");

            var response = new byte[head.Length + gz.Length];
            Buffer.BlockCopy(head, 0, response, 0, head.Length);
            Buffer.BlockCopy(gz, 0, response, head.Length, gz.Length);
            return ValueTask.FromResult<Stream>(new CannedStream(response));
        };

        using var client = new HttpClient(apiHandler);
        var api = new TdxApiClient(client, opts);

        const string name = "★ gzip 壓縮的 TDX 回應可以正確解析（未設定 AutomaticDecompression 時會出現 0x1F 錯誤）";
        try
        {
            var stops = api.GetStopsAsync(default).GetAwaiter().GetResult();
            Check(name, stops.Count == 1 && stops[0].StopUID == "TXG12251", $"解析出 {stops.Count} 筆");
        }
        catch (Exception ex)
        {
            Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ── 3) 負向對照：證明這個測試真的抓得到 bug ──
        //     故意不開自動解壓縮 → 必須失敗。若這裡「沒有失敗」，
        //     代表上面的測試其實沒測到東西（例如少了 Content-Encoding 標頭）。
        var badHandler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None };
        badHandler.ConnectCallback = (_, _) =>
        {
            var gz = GzipBytes(System.Text.Encoding.UTF8.GetBytes(json));
            var head = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Encoding: gzip\r\n" +
                $"Content-Length: {gz.Length}\r\n" +
                "Connection: close\r\n\r\n");

            var response = new byte[head.Length + gz.Length];
            Buffer.BlockCopy(head, 0, response, 0, head.Length);
            Buffer.BlockCopy(gz, 0, response, head.Length, gz.Length);
            return ValueTask.FromResult<Stream>(new CannedStream(response));
        };

        using var badClient = new HttpClient(badHandler);
        var badApi = new TdxApiClient(badClient, new TdxOptions { BaseUrl = "http://tdx.local" });

        var failedAsExpected = false;
        try { badApi.GetStopsAsync(default).GetAwaiter().GetResult(); }
        catch (TdxException) { failedAsExpected = true; }

        Check("★ 負向對照：未開啟自動解壓縮時確實會失敗（證明上面的測試有效）", failedAsExpected);
    }

    /// <summary>
    /// 假的連線串流：寫入（HTTP 請求）一律丟棄，讀取則供應預先準備好的回應位元組。
    ///
    /// 不能直接用 MemoryStream —— SocketsHttpHandler 會先把請求寫進同一個串流，
    /// 結果把假回應的開頭覆蓋掉，解析就會出現
    /// 「Received an invalid status line: ': close'」這種詭異錯誤。
    /// </summary>
    private sealed class CannedStream(byte[] response) : Stream
    {
        private readonly MemoryStream _read = new(response);

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { /* 丟棄請求內容 */ }
    }

    private static byte[] GzipBytes(byte[] data)
    {
        using var outMs = new MemoryStream();
        using (var gz = new GZipStream(outMs, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(data);
        return outMs.ToArray();
    }

    private static void TestSavedGroupStore()
    {
        Check($"SQLite 可用（Windows 內建 winsqlite3，版本 {SqliteDatabase.Version ?? "無"}）",
            SqliteDatabase.IsAvailable);

        if (!SqliteDatabase.IsAvailable) return;

        var dbPath = Path.Combine(Path.GetTempPath(), $"tcbus-test-{Guid.NewGuid():N}.db");
        const ulong user = 123456789012345678;

        var payload = new SavedGroupPayload(
            [
                new SavedLeg(
                    new SavedTarget("臺中車站", new[] { "TXG12251", "TXG11020" }),
                    new SavedTarget("靜宜大學", new[] { "TXG13567" }),
                    new[] { new SavedRoute("TXG300", 1, "300"), new SavedRoute("TXG304", 1, "304") })
            ],
            10);

        try
        {
            long groupId;

            using (var store = new SavedGroupStore(dbPath))
            {
                Check("資料庫路徑可讀", store.IsPersistent, store.Describe());
                Check("SQLite 版本字串", store.Describe().Contains("SQLite"), store.Describe());

                var (created, _) = store.Save(user, "上班通勤", payload);
                Check("新增訂閱組", created == SaveGroupResult.Created, created.ToString());

                // 中文 + 引號 + emoji 都要能安全寫入（參數化查詢）
                var (quoted, _) = store.Save(user, "測試 '引號\" 與 emoji 🚌", payload);
                Check("特殊字元名稱可寫入", quoted == SaveGroupResult.Created, quoted.ToString());

                var list = store.ListByUser(user);
                Check("清單有 2 筆", list.Count == 2, $"實際 {list.Count}");
                Check("名稱正確", list.Any(g => g.Name == "上班通勤"));
                Check("路線數正確", list.All(g => g.RouteCount == 2));

                var g = list.First(x => x.Name == "上班通勤");
                groupId = g.Id;

                var (updated, _) = store.Save(user, "上班通勤", payload with { NotifyMinutes = 15 });
                Check("同名再存會覆蓋成 Updated", updated == SaveGroupResult.Updated, updated.ToString());
                Check("覆蓋後只有 1 筆同名", store.ListByUser(user).Count(x => x.Name == "上班通勤") == 1);
                Check("覆蓋後內容有更新", store.Get(groupId, user)!.NotifyMinutes == 15);

                var stored = store.GetPayload(groupId, user);
                Check("payload 可還原", stored is not null && stored.RouteCount == 2);
                Check("中文與陣列 round-trip",
                    stored!.Origin.DisplayName == "臺中車站"
                    && stored.Origin.StopUids.Count == 2
                    && stored.Legs[0].Routes[0].RouteUid == "TXG300");

                Check("改名成功", store.Rename(groupId, user, "回家路線"));
                Check("改名後清單反映", store.ListByUser(user).Any(x => x.Name == "回家路線"));

                store.Touch(groupId, user);
                Check("使用次數累加", store.Get(groupId, user)!.UseCount == 1);
                Check("常用的排前面", store.ListByUser(user)[0].Id == groupId);

                Check("別的使用者看不到別人的組", store.ListByUser(999UL).Count == 0);
                Check("別人不能讀取", store.Get(groupId, 999UL) is null);
                Check("別人不能刪除", !store.Delete(groupId, 999UL));
                Check("別人不能改名", !store.Rename(groupId, 999UL, "偷改"));

                var (emptyName, _) = store.Save(user, "   ", payload);
                Check("空名稱被擋", emptyName == SaveGroupResult.NameEmpty);

                var (longName, _) = store.Save(user, new string('字', 100), payload);
                Check("過長名稱被擋", longName == SaveGroupResult.NameTooLong);

                var (noRoutes, _) = store.Save(user, "空的",
                    payload with
                    {
                        Legs = [payload.Legs[0] with { Routes = Array.Empty<SavedRoute>() }]
                    });
                Check("沒有路線被擋", noRoutes == SaveGroupResult.RouteCountMismatch);

                // 數量上限
                var overflow = SaveGroupResult.Created;
                for (var i = 0; i < SavedGroupStore.MaxGroupsPerUser + 3; i++)
                {
                    var (r, _) = store.Save(user, $"群組{i}", payload);
                    overflow = r;
                }
                Check($"超過 {SavedGroupStore.MaxGroupsPerUser} 組會被擋",
                    overflow == SaveGroupResult.TooManyGroups, overflow.ToString());
            }

            // ★ 最關鍵：重開資料庫後資料必須還在（這才是「持久化」的意義）
            using (var reopened = new SavedGroupStore(dbPath))
            {
                var list = reopened.ListByUser(user);
                Check("★ 重開資料庫後訂閱組還在（真正的持久化）",
                    list.Any(g => g.Name == "回家路線"), $"共 {list.Count} 組");

                var kept = list.First(g => g.Name == "回家路線");
                var stored = reopened.GetPayload(kept.Id, user);
                Check("★ 重開後 payload 仍可還原", stored is not null && stored.RouteCount == 2);
                Check("★ 重開後中文站名仍正確", stored!.Destination.DisplayName == "靜宜大學");
            }

            // 刪除
            using (var store = new SavedGroupStore(dbPath))
            {
                Check("刪除成功", store.Delete(groupId, user));
                Check("刪除後查不到", store.Get(groupId, user) is null);
            }

            Check("資料庫檔案真的存在", File.Exists(dbPath), $"{new FileInfo(dbPath).Length} bytes");
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }
    }

    /// <summary>
    /// 多段行程的訂閱組、合併、以及復原。
    /// 這幾個都是「一次影響很多東西」的操作，壞掉的代價很高，所以測得細一點。
    /// </summary>
    private static void TestMergeAndUndo(TaichungBusDataService data)
    {
        const ulong user = 42UL;

        // ── 1) 合併多個訂閱組 → 多段行程 ────────────────────
        var legA = SavedGroupPayloadFactory.FromSubscriptions(
            new SavedTarget("臺中車站", new[] { "TXG12251" }),
            new SavedTarget("靜宜大學", new[] { "TXG13567" }),
            new[] { ("TXG300", 1, "300"), ("TXG304", 1, "304") },
            notifyMinutes: 10);

        var legB = SavedGroupPayloadFactory.FromSubscriptions(
            new SavedTarget("靜宜大學", new[] { "TXG13567" }),
            new SavedTarget("臺中車站", new[] { "TXG12251" }),
            new[] { ("TXG300", 0, "300") },
            notifyMinutes: 3);

        var mergedPayload = SavedGroupPayloadFactory.Merge([legA, legB]);

        Check("合併兩個不同的行程 → 2 段", mergedPayload.LegCount == 2, $"{mergedPayload.LegCount} 段");
        Check("合併後路線數是聯集", mergedPayload.RouteCount == 3, $"{mergedPayload.RouteCount} 條");
        // 10 分 vs 3 分 → 取 10：提醒得早只多一則訊息，提醒得太晚會錯過公車
        Check("合併後的提前時間取最大的（提醒最早，寧可早不要晚）", mergedPayload.NotifyMinutes == 10,
            $"{mergedPayload.NotifyMinutes} 分");

        // 同一段（起訖候選站相同）要合併成一段，而不是兩段重複
        var sameLegAgain = SavedGroupPayloadFactory.Merge([legA, legA]);
        Check("起訖相同的段落會併成一段", sameLegAgain.LegCount == 1, $"{sameLegAgain.LegCount} 段");
        Check("重複的路線不會重複列", sameLegAgain.RouteCount == 2, $"{sameLegAgain.RouteCount} 條");

        // ── 2) 合併後存得進去、讀得回來，而且舊格式還能讀 ────
        var dbPath = Path.Combine(Path.GetTempPath(), $"tcbus-merge-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SavedGroupStore(dbPath);

            var (saved, _) = store.Save(user, "通勤全部", mergedPayload);
            Check("多段行程的訂閱組存得進去", saved == SaveGroupResult.Created, saved.ToString());

            var reloaded = store.GetPayload(store.GetByName(user, "通勤全部")!.Id, user)!;
            Check("★ 多段行程 round-trip 後仍是 2 段", reloaded.LegCount == 2, $"{reloaded.LegCount} 段");
            Check("★ round-trip 後每一段的路線都還在",
                reloaded.Legs[0].Routes.Count == 2 && reloaded.Legs[1].Routes.Count == 1);
            Check("round-trip 後中文站名正確",
                reloaded.Legs[0].Origin.DisplayName == "臺中車站"
                && reloaded.Legs[1].Origin.DisplayName == "靜宜大學");

            // 舊格式（改版前存的單段 payload）必須讀得進來 —— 使用者存好的組不能消失
            var legacyJson = """
                {"origin":{"name":"臺中車站","uids":["TXG12251"]},
                 "dest":{"name":"靜宜大學","uids":["TXG13567"]},
                 "routes":[{"u":"TXG300","d":1,"n":"300"}],"notify":10}
                """;
            var legacy = System.Text.Json.JsonSerializer.Deserialize<SavedGroupPayload>(
                legacyJson, SavedGroupStore.PayloadJson);

            Check("★ 舊格式（單段）payload 可以升級成 1 段行程",
                legacy is { LegCount: 1, RouteCount: 1 }
                && legacy.Origin.DisplayName == "臺中車站"
                && legacy.Legs[0].Routes[0].RouteUid == "TXG300",
                legacy is null ? "null" : $"{legacy.LegCount} 段");

            // 段數上限
            var tooMany = new SavedGroupPayload(
                Enumerable.Range(0, SavedGroupStore.MaxLegsPerGroup + 1)
                    .Select(_ => mergedPayload.Legs[0]).ToList(), 10);
            var (tooManyResult, _) = store.Save(user, "太多段", tooMany);
            Check($"超過 {SavedGroupStore.MaxLegsPerGroup} 段會被擋",
                tooManyResult == SaveGroupResult.TooManyLegs, tooManyResult.ToString());

            // ── 3) 復原：合併 ────────────────────────────────
            var mergedRow = store.GetByName(user, "通勤全部")!;
            var undo = new UndoStack();
            undo.Push(new UndoMergedSavedGroup("合併成「通勤全部」", mergedRow.Id, "通勤全部", null));

            Check("復原堆疊有 1 筆", undo.Count == 1);
            var message = undo.Undo(Subs(), store, user);
            Check("復原合併 → 新組被刪掉", store.Get(mergedRow.Id, user) is null, message);
            Check("復原後堆疊空了", undo.Count == 0);

            // 復原「覆蓋同名」的合併 → 舊內容要回來
            var original = SavedGroupPayloadFactory.FromSubscriptions(
                new SavedTarget("原起點", new[] { "TXG1" }), new SavedTarget("原終點", new[] { "TXG2" }),
                new[] { ("TXG999", 0, "999") }, 7);
            store.Save(user, "會被覆蓋", original);

            var beforeOverwrite = store.GetByName(user, "會被覆蓋")!;
            var overwrittenSnapshot = store.GetPayload(beforeOverwrite.Id, user);
            store.Save(user, "會被覆蓋", mergedPayload);                 // 同名覆蓋

            var afterOverwrite = store.GetPayload(store.GetByName(user, "會被覆蓋")!.Id, user)!;
            Check("同名合併確實覆蓋了內容", afterOverwrite.LegCount == 2);

            new UndoMergedSavedGroup("合併成「會被覆蓋」", beforeOverwrite.Id, "會被覆蓋", overwrittenSnapshot)
                .Undo(Subs(), store, user);

            var restored = store.GetPayload(store.GetByName(user, "會被覆蓋")!.Id, user)!;
            Check("★ 復原覆蓋式合併 → 舊內容回來了",
                restored.LegCount == 1 && restored.RouteCount == 1 && restored.NotifyMinutes == 7,
                $"{restored.LegCount} 段、{restored.RouteCount} 條、{restored.NotifyMinutes} 分");

            // ── 4) 復原：批次刪除 ────────────────────────────
            var keepId = store.GetByName(user, "會被覆蓋")!.Id;
            var snapshot = new UndoDeletedSavedGroups.Snapshot("會被覆蓋", restored);
            Check("刪除成功", store.Delete(keepId, user));

            var undoDelete = new UndoStack();
            undoDelete.Push(new UndoDeletedSavedGroups("刪除 1 個訂閱組", new[] { snapshot }));

            var deleteMsg = undoDelete.Undo(Subs(), store, user);
            var back = store.GetByName(user, "會被覆蓋");
            Check("★ 復原刪除 → 訂閱組回來了", back is not null, deleteMsg);
            Check("復原刪除後內容正確",
                back is not null && store.GetPayload(back.Id, user)!.RouteCount == 1);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }

        // ── 5) 合併成一個通知流：多段行程放在同一個群組 ──────
        var subs = Subs();
        var origin = data.TargetFromStops(new[] { "TXG12251" });
        var dest = data.TargetFromStops(new[] { "TXG13567" });

        var forward = data.FindRoutes(origin, dest).Take(3).ToList();
        var backward = data.FindRoutes(dest, origin).Take(2).ToList();

        if (forward.Count > 0)
        {
            var single = subs.CreateGroup(user, origin, dest, forward, 10);
            Check("單段群組：1 段行程", single.LegCount == 1);
            Check("單段群組的訂閱都指向第 0 段",
                subs.GetSubscriptions(single).All(s => s.LegIndex == 0));

            if (backward.Count > 0)
            {
                var merged = subs.CreateMultiLegGroup(user,
                [
                    new LegPlan(origin, dest, forward),
                    new LegPlan(dest, origin, backward),
                ], notifyBeforeMinutes: 10);

                Check("★ 合併成一個通知流：2 段行程、1 個群組", merged.LegCount == 2);
                Check("★ 兩段的訂閱都在同一個群組裡（才能跨段挑最快）",
                    subs.GetSubscriptions(merged).Select(s => s.LegIndex).Distinct().OrderBy(x => x)
                        .SequenceEqual(new[] { 0, 1 }));
                Check("多段群組的顯示名稱包含「段行程」",
                    merged.DescribeRoute().Contains("段行程", StringComparison.Ordinal), merged.DescribeRoute());
                Check("每一段都拿得到自己的起訖",
                    merged.LegOf(subs.GetSubscriptions(merged).First(s => s.LegIndex == 1)).Origin.DisplayName
                        == dest.DisplayName);

                // 復原批次套用 → 群組整個消失，裡面的訂閱也要一起消失
                var before = subs.SubscriptionCount;
                var undoApply = new UndoStack();
                undoApply.Push(new UndoAppliedSubscriptions("套用 1 組", new[] { merged.Id, single.Id }, null));
                undoApply.Undo(subs, store: null!, user);

                Check("★ 復原批次套用 → 訂閱群組消失", subs.GetGroup(merged.Id) is null && subs.GetGroup(single.Id) is null);
                Check("復原批次套用 → 底下的訂閱也一起清掉",
                    subs.SubscriptionCount < before, $"{before} → {subs.SubscriptionCount}");
            }
        }
    }

    /// <summary>
    /// 儲存後端的選擇與後備鏈：MongoDB → SQLite → 文字檔 → 記憶體。
    ///
    /// 這裡測「沒有伺服器也能測」的部分：
    ///   * document ↔ 物件的轉換（純函式）
    ///   * 連線字串遮罩（不能把帳密印出來）
    ///   * MongoDB 連不上時**要退回本機儲存並留下警告**（不硬撐、也不默默變記憶體）
    ///   * 文字檔後備真的能存能讀，而且是看得懂的文字
    /// </summary>
    private static void TestStorageBackends()
    {
        // ── 1) MongoDB 的 document 轉換（不需要伺服器）──
        var payload = SavedGroupPayloadFactory.FromSubscriptions(
            new SavedTarget("臺中車站", new[] { "TXG12251", "TXG11020" }),
            new SavedTarget("靜宜大學", new[] { "TXG13567" }),
            new[] { ("TXG300", 1, "300"), ("TXG304", 1, "304") },
            notifyMinutes: 10);

        var doc = MongoSavedGroupMapping.ToDocument(
            userId: 12345UL, name: "上班通勤", payload,
            seq: 7, id: default, createdAt: DateTime.UtcNow, lastUsedAt: null, useCount: 3);

        Check("Mongo document：使用者存成字串", doc.User == "12345");
        Check("Mongo document：反正規化欄位取第一段起點／最後一段終點",
            doc.OriginName == "臺中車站" && doc.DestinationName == "靜宜大學",
            $"{doc.OriginName} → {doc.DestinationName}");
        Check("Mongo document：路線數與提前時間", doc.RouteCount == 2 && doc.NotifyMinutes == 10);
        Check("Mongo document：seq 就是對外的 long id", doc.Seq == 7);

        var summary = MongoSavedGroupMapping.ToSummary(doc);
        Check("Mongo document → 摘要：id 用 seq", summary.Id == 7 && summary.UserId == 12345UL);
        Check("Mongo document → 摘要：名稱與路線數", summary.Name == "上班通勤" && summary.RouteCount == 2);
        Check("Mongo document → 摘要：使用次數", summary.UseCount == 3);

        var restored = MongoSavedGroupMapping.ToPayload(doc);
        Check("★ Mongo payload 可以完整還原（含候選站牌）",
            restored is { LegCount: 1, RouteCount: 2 }
            && restored.Origin.StopUids.Count == 2
            && restored.Destination.DisplayName == "靜宜大學",
            restored is null ? "null" : $"{restored.LegCount} 段、{restored.RouteCount} 條");

        // 舊格式（單段）也要能經由 Mongo 這條路讀進來
        var legacyDoc = new SavedGroupDocument
        {
            User = "1",
            Seq = 1,
            Name = "舊的",
            PayloadJson = """
                {"origin":{"name":"A","uids":["X"]},"dest":{"name":"B","uids":["Y"]},
                 "routes":[{"u":"R","d":0,"n":"R"}],"notify":5}
                """
        };
        Check("★ Mongo 上的舊格式 payload 也能升級",
            MongoSavedGroupMapping.ToPayload(legacyDoc) is { LegCount: 1, RouteCount: 1 });

        // ── 2) 連線字串遮罩（帳密絕對不能出現在日誌／畫面上）──
        var masked = MongoSavedGroupRepository.Mask(
            "mongodb+srv://alice:s3cret@cluster0.abc.mongodb.net/?retryWrites=true");

        Check("連線字串遮罩：不洩漏帳號密碼",
            masked.Contains("***@cluster0.abc.mongodb.net", StringComparison.Ordinal)
            && !masked.Contains("s3cret", StringComparison.Ordinal), masked);
        Check("連線字串遮罩：本機無帳密時原樣顯示",
            MongoSavedGroupRepository.Mask("mongodb://localhost:27017") == "mongodb://localhost:27017");

        // ── 3) MongoDB 連不上 → 退回本機儲存，而且要有警告 ──
        var dbPath = Path.Combine(Path.GetTempPath(), $"tcbus-backend-{Guid.NewGuid():N}.db");
        var warnings = new List<string>();
        var previousSink = BotLog.Sink;
        BotLog.Sink = warnings.Add;

        try
        {
            // 127.0.0.1:1 一定連不上；連線逾時設 3 秒，所以不會卡太久
            using var store = new SavedGroupStore(dbPath, "mongodb://127.0.0.1:1/tcbus", "tcbus");

            Check("★ MongoDB 連不上時仍可用（退回本機儲存）", store.IsPersistent);
            Check("退回本機時會留下清楚的警告",
                warnings.Any(w => w.Contains("MongoDB 連不上", StringComparison.Ordinal)),
                warnings.FirstOrDefault() ?? "沒有任何警告");
            Check("退回後 Describe() 說得出實際用的後端",
                store.Describe().Contains("SQLite", StringComparison.Ordinal), store.Describe());

            var (saved, _) = store.Save(1UL, "離線也能存", payload);
            Check("退回本機後仍可正常儲存", saved == SaveGroupResult.Created, saved.ToString());
        }
        finally
        {
            BotLog.Sink = previousSink;
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }

        // ── 4) 文字檔後備：真的存得進去、讀得回來、看得懂 ──
        var textDir = Path.Combine(Path.GetTempPath(), $"tcbus-text-{Guid.NewGuid():N}");
        Directory.CreateDirectory(textDir);
        var textDb = Path.Combine(textDir, "tcbus.db");

        try
        {
            using (var store = new SavedGroupStore(textDb, SavedGroupStore.StorageMode.Text))
            {
                Check("文字檔模式：是持久化的", store.IsPersistent);
                Check("文字檔模式：Describe() 說明得清楚",
                    store.Describe().Contains("文字檔模式", StringComparison.Ordinal), store.Describe());

                var (created, _) = store.Save(42UL, "上班通勤", payload);
                Check("文字檔模式：可以新增", created == SaveGroupResult.Created, created.ToString());

                var (updated, _) = store.Save(42UL, "上班通勤", payload with { NotifyMinutes = 15 });
                Check("文字檔模式：同名會覆蓋", updated == SaveGroupResult.Updated, updated.ToString());

                store.Save(42UL, "回家路線", payload);
                store.Touch(store.ListByUser(42UL).First(g => g.Name == "回家路線").Id, 42UL);

                Check("文字檔模式：清單有 2 筆", store.CountByUser(42UL) == 2);
                Check("文字檔模式：常用的排前面",
                    store.ListByUser(42UL)[0].Name == "回家路線", store.ListByUser(42UL)[0].Name);
                Check("文字檔模式：別的使用者看不到", store.CountByUser(99UL) == 0);
                Check("文字檔模式：不能刪別人的組",
                    !store.Delete(store.ListByUser(42UL)[0].Id, 99UL));
            }

            var textFile = Path.Combine(textDir, SavedGroupStore.TextFileName);
            Check("★ 文字檔真的產生了", File.Exists(textFile), textFile);

            var lines = File.ReadAllLines(textFile);
            Check("★ 文字檔是看得懂的一行一筆（含註解與中文）",
                lines.Length >= 3 && lines[0].StartsWith('#')
                && lines.Any(l => l.Contains("上班通勤", StringComparison.Ordinal)),
                $"{lines.Length} 行");

            using (var reopened = new SavedGroupStore(textDb, SavedGroupStore.StorageMode.Text))
            {
                var kept = reopened.ListByUser(42UL).First(g => g.Name == "上班通勤");

                Check("★ 重開後文字檔的訂閱組還在", reopened.CountByUser(42UL) == 2);
                Check("★ 重開後內容正確（含中文與路線）",
                    kept.RouteCount == 2
                    && reopened.GetPayload(kept.Id, 42UL) is { LegCount: 1 } p2
                    && p2.Origin.DisplayName == "臺中車站");

                var id = reopened.ListByUser(42UL).First(g => g.Name == "回家路線").Id;
                Check("文字檔模式：可以刪除", reopened.Delete(id, 42UL));
                Check("文字檔模式：刪除後查不到", reopened.CountByUser(42UL) == 1);
            }

            // 壞掉的行不該讓整個檔案讀不出來
            File.AppendAllText(textFile,
                "{ 這行壞掉了\n{\"id\":999,\"user\":\"77\",\"name\":\"好的\",\"payload\":{}}\n");

            using (var tolerant = new SavedGroupStore(textDb, SavedGroupStore.StorageMode.Text))
            {
                Check("★ 文字檔有一行壞掉時，其他資料仍讀得出來",
                    tolerant.CountByUser(42UL) == 1 && tolerant.CountByUser(77UL) == 1,
                    $"42 → {tolerant.CountByUser(42UL)} 筆、77 → {tolerant.CountByUser(77UL)} 筆");
            }
        }
        finally
        {
            try { Directory.Delete(textDir, recursive: true); } catch { }
        }
    }

    private static SubscriptionService Subs() => new();

    private static void TestDotEnv()
    {
        static void Parses(string line, string? expectKey, string? expectValue, string note)
        {
            var (k, v) = DotEnv.ParseLine(line);
            var ok = k == expectKey && v == expectValue;
            Check($"[{note}] {line}", ok, ok ? "" : $"得到 ({k ?? "null"}, {v ?? "null"})");
        }

        Parses("DISCORD_TOKEN=abc123", "DISCORD_TOKEN", "abc123", "標準 dotenv");
        Parses("$env:DISCORD_TOKEN=abc123", "DISCORD_TOKEN", "abc123", "PowerShell 風格");
        Parses("  $env:TDX_CLIENT_ID =  xyz  ", "TDX_CLIENT_ID", "xyz", "PowerShell + 空白");
        Parses("export FOO=bar", "FOO", "bar", "shell export");
        Parses("FOO=\"a=b\"", "FOO", "a=b", "值含等號 + 雙引號");
        Parses("FOO=' spaced '", "FOO", " spaced ", "單引號保留空白");
        Parses("KEY=", "KEY", "", "空值");
        Parses("# 這是註解", null, null, "註解");
        Parses("", null, null, "空行");
        Parses("NOEQUALS", null, null, "沒有等號");
        Parses("\uFEFFDISCORD_TOKEN=bom", "DISCORD_TOKEN", "bom", "UTF-8 BOM");

        var tmp = Path.Combine(Path.GetTempPath(), $"tcbus-env-{Guid.NewGuid():N}.env");
        try
        {
            File.WriteAllLines(tmp,
            [
                "# 測試用",
                "",
                "$env:DISCORD_TOKEN=token-value",
                "tdx_client_id=id-value"
            ]);

            var map = DotEnv.LoadFromFile(tmp);
            Check("檔案解析出 2 個變數", map.Count == 2, $"實際 {map.Count}");
            Check("PowerShell 風格的鍵正確", map.GetValueOrDefault("DISCORD_TOKEN") == "token-value");
            Check("鍵名大小寫不敏感", map.GetValueOrDefault("TDX_CLIENT_ID") == "id-value");

            // ── 指定 .env 路徑的各種寫法（--env / TCBUS_ENV 都走這裡）──
            Check("指定檔名（不叫 .env 也可以）", DotEnv.FindFile(tmp) == Path.GetFullPath(tmp));
            Check("路徑帶引號也能用（PowerShell 常直接貼 'C:\\...\\x.env'）",
                DotEnv.FindFile($"'{tmp}'") == Path.GetFullPath(tmp));

            var dir = Path.GetDirectoryName(tmp)!;
            var dotEnvInDir = Path.Combine(dir, ".env");
            var hadDotEnv = File.Exists(dotEnvInDir);
            if (!hadDotEnv) File.WriteAllText(dotEnvInDir, "DISCORD_TOKEN=from-dir");

            try
            {
                Check("指定資料夾 → 讀資料夾裡的 .env",
                    DotEnv.FindFile(dir) == Path.GetFullPath(dotEnvInDir), dir);
                Check("資料夾結尾帶斜線也可以",
                    DotEnv.FindFile(dir + Path.DirectorySeparatorChar) == Path.GetFullPath(dotEnvInDir));
            }
            finally
            {
                if (!hadDotEnv) { try { File.Delete(dotEnvInDir); } catch { } }
            }

            Check("路徑不存在時回 null（上層才知道要警告使用者）",
                DotEnv.FindFile(Path.Combine(dir, "no-such-file.env")) is null);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// .env 與系統環境變數的互動：三種來源可混用，而且 .env 可以引用環境變數。
    ///
    /// 這一段對應使用者的實際需求：「金鑰放系統環境變數，其他設定放 .env」。
    /// </summary>
    private static void TestEnvInterop()
    {
        const string envKey = "TCBUS_TEST_OS_KEY";
        const string refKey = "TCBUS_TEST_REF";

        var previousOs = Environment.GetEnvironmentVariable(envKey);
        var previousRef = Environment.GetEnvironmentVariable(refKey);

        var file = Path.Combine(Path.GetTempPath(), $"tcbus-env-{Guid.NewGuid():N}.env");

        try
        {
            // 系統環境變數（模擬使用者已經在 shell／系統設定裡設好）
            Environment.SetEnvironmentVariable(envKey, "FROM-OS-ENV");

            File.WriteAllLines(file,
            [
                "# 三種來源混用",
                $"TCBUS_MONGO=${{{envKey}}}",              // 引用「系統環境變數」
                "BASE_URL=https://tdx.transportdata.tw",
                "MY_URL=${BASE_URL}/api",                 // 引用「同檔案的其他鍵」
                "UNKNOWN=${TCBUS_TEST_NOT_SET_XYZ}",      // 沒設定的變數：保留原樣
                "PLAIN=no-expansion-here"
            ]);

            var map = DotEnv.LoadFromFile(file);

            Check("★ .env 可以引用系統環境變數（${VAR}）",
                map.GetValueOrDefault("TCBUS_MONGO") == "FROM-OS-ENV",
                map.GetValueOrDefault("TCBUS_MONGO") ?? "(null)");

            Check("★ .env 可以引用同一個檔案裡的其他鍵",
                map.GetValueOrDefault("MY_URL") == "https://tdx.transportdata.tw/api",
                map.GetValueOrDefault("MY_URL") ?? "(null)");

            Check("沒設定的 ${VAR} 保留原樣（才看得出是哪個沒設）",
                map.GetValueOrDefault("UNKNOWN") == "${TCBUS_TEST_NOT_SET_XYZ}",
                map.GetValueOrDefault("UNKNOWN") ?? "(null)");

            Check("沒有 ${} 的值原樣保留",
                map.GetValueOrDefault("PLAIN") == "no-expansion-here");

            // ── ApplyToEnvironment：讓程式內任何地方都讀得到 ──
            var newKey = "TCBUS_TEST_EXPORTED";
            var existingKey = "TCBUS_TEST_EXISTING";

            Environment.SetEnvironmentVariable(newKey, null);
            Environment.SetEnvironmentVariable(existingKey, "OS-WINS");

            var applied = DotEnv.ApplyToEnvironment(map);
            Check("ApplyToEnvironment 會回報寫入幾個鍵", applied > 0, $"{applied} 個");

            Check("★ .env 的值可以用 Environment.GetEnvironmentVariable 讀到",
                Environment.GetEnvironmentVariable("TCBUS_MONGO") == "FROM-OS-ENV",
                Environment.GetEnvironmentVariable("TCBUS_MONGO") ?? "(null)");

            // 已存在的系統環境變數不能被 .env 蓋掉（否則會破壞「環境變數 > .env」的優先序）
            var map2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TCBUS_TEST_EXISTING"] = "FROM-DOTENV"
            };
            DotEnv.ApplyToEnvironment(map2);
            Check("★ 既有的系統環境變數不會被 .env 覆蓋",
                Environment.GetEnvironmentVariable(existingKey) == "OS-WINS",
                Environment.GetEnvironmentVariable(existingKey) ?? "(null)");

            DotEnv.ApplyToEnvironment(map2, overwrite: true);
            Check("overwrite 時才會覆蓋",
                Environment.GetEnvironmentVariable(existingKey) == "FROM-DOTENV",
                Environment.GetEnvironmentVariable(existingKey) ?? "(null)");

            Environment.SetEnvironmentVariable(existingKey, null);
            Environment.SetEnvironmentVariable(newKey, null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, previousOs);
            Environment.SetEnvironmentVariable(refKey, previousRef);
            Environment.SetEnvironmentVariable("TCBUS_MONGO", null);
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>
    /// 設定來源的優先序：**命令列 &gt; 系統環境變數 &gt; .env**。
    ///
    /// 最關鍵的一項是「**沒有 .env 檔時，環境變數仍然有效**」——
    /// 找不到檔案時 `LoadFromFile(null)` 回傳空的字典，解析流程照樣會走到環境變數。
    /// </summary>
    private static void TestSettingPrecedence()
    {
        const string key = "TCBUS_TEST_PRECEDENCE";
        var previous = Environment.GetEnvironmentVariable(key);

        // 模擬「三個地方都有值」
        var fileWithKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = "FROM-DOTENV"
        };

        // 模擬「完全沒有 .env 檔」（LoadFromFile 對 null 的回傳值）
        var noFile = DotEnv.LoadFromFile(null);

        try
        {
            // ── 沒有任何來源 ──
            Environment.SetEnvironmentVariable(key, null);
            var none = SettingResolver.Resolve(Array.Empty<string>(), "--test", noFile, key);
            Check("★ 沒有 .env、沒有環境變數 → 未設定",
                none.Value is null && none.Source == "（未設定）", none.Source);

            // ── 只有環境變數（.env 不存在）★ 這項就是使用者要確認的 ──
            Environment.SetEnvironmentVariable(key, "FROM-OS-ENV");
            var onlyEnv = SettingResolver.Resolve(Array.Empty<string>(), "--test", noFile, key);
            Check("★ 沒有 .env 檔時，讀得到系統環境變數",
                onlyEnv.Value == "FROM-OS-ENV", $"{onlyEnv.Value}（{onlyEnv.Source}）");
            Check("★ 沒有 .env 檔時，來源標示為環境變數",
                onlyEnv.Source == $"環境變數 {key}", onlyEnv.Source);

            // ── 環境變數存在的空字串／空白不算設定 ──
            Environment.SetEnvironmentVariable(key, "   ");
            var blankEnv = SettingResolver.Resolve(Array.Empty<string>(), "--test", fileWithKey, key);
            Check("環境變數只有空白時視為未設定，會落到 .env",
                blankEnv.Value == "FROM-DOTENV", $"{blankEnv.Value}（{blankEnv.Source}）");

            // ── 環境變數 vs .env：環境變數贏 ──
            Environment.SetEnvironmentVariable(key, "FROM-OS-ENV");
            var envWins = SettingResolver.Resolve(Array.Empty<string>(), "--test", fileWithKey, key);
            Check("★ 環境變數優先於 .env", envWins.Value == "FROM-OS-ENV", envWins.Source);

            // ── 命令列贏過全部 ──
            var cliWins = SettingResolver.Resolve(["--test", "FROM-CLI"], "--test", fileWithKey, key);
            Check("★ 命令列優先於環境變數與 .env", cliWins.Value == "FROM-CLI", cliWins.Source);

            var cliEq = SettingResolver.Resolve(["--test=FROM-CLI-EQ"], "--test", fileWithKey, key);
            Check("--opt=值 也吃", cliEq.Value == "FROM-CLI-EQ", cliEq.Source);

            var cliColon = SettingResolver.Resolve(["--test:\"FROM-CLI-COLON\""], "--test", fileWithKey, key);
            Check("--opt:\"值\" 也吃（PowerShell 常見）", cliColon.Value == "FROM-CLI-COLON", cliColon.Source);

            // ── 別名：第一個有值的為準 ──
            Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable("TCBUS_TEST_ALIAS2", "FROM-ALIAS2");
            var alias = SettingResolver.Resolve(Array.Empty<string>(), "--test", noFile,
                                                "TCBUS_TEST_ALIAS1", "TCBUS_TEST_ALIAS2");
            Check("別名清單：跳過沒設的、取第一個有值的",
                alias.Value == "FROM-ALIAS2", $"{alias.Value}（{alias.Source}）");
            Environment.SetEnvironmentVariable("TCBUS_TEST_ALIAS2", null);

            // ── 值有前後空白會被修掉（複製貼上常帶空白）──
            Environment.SetEnvironmentVariable(key, "  PADDED  ");
            var padded = SettingResolver.Resolve(Array.Empty<string>(), "--test", noFile, key);
            Check("值的前後空白會被去除", padded.Value == "PADDED", $"「{padded.Value}」");
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} " + new string('─', Math.Max(0, 60 - title.Length)));
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine($"  ✔ {name}" + (detail is null ? "" : $"  [{detail}]")); }
        else
        {
            _fail++;
            Failures.Add(name);
            Console.WriteLine($"  ✘ {name}" + (detail is null ? "" : $"  [{detail}]"));
        }
    }
}
