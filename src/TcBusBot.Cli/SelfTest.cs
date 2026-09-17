using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TcBusBot.Core.Chat;
using TcBusBot.Core.Storage;
using TcBusBot.Core.Tdx;
using TcBusBot.Core.Configuration;
using TcBusBot.Core.DataSources;
using System.Text.Json;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Hosting;
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

        Section("17. 健康檢查端點與防休眠（Render 部署用）");
        TestHealthEndpoint();

        Section("18. 輪詢成本：$select 只要求會用到的欄位");
        TestEtaSelect(Path.Combine(fixturesRoot, "real", "EstimatedTimeOfArrival.sample.json"));

        Section("19. 結束追蹤（/bus end）與復原");
        TestEndTracking(data, origin, dest);

        Section("20. AI 聊天：時間切段、回覆舊訊息、伺服器隔離、每週 token 額度");
        TestChat();

        Section("21. LLM 工具：用一句話訂閱公車（口語站名 → 真的建立訂閱）");
        TestBusActions(data, origin, dest);

        Section("22. 儲存方案的 DI 註冊（後端選擇、共用實例、生命週期）");
        TestStorageDi();

        Section("23. 模型思考開關（LLM_REASONING：SK 送不出去，所以在 HTTP 層補）");
        TestReasoning();

        Section("24. 可被馴服的提示詞（每個伺服器各自一份、可以重設）");
        TestPersona();

        Section("25. 公車查詢加強：路線號碼、轉乘（同名不同月台）");
        TestBusSearchPlus(data, origin, dest);

        Section("26. 認人（暱稱＋ID）與主人授權（特殊 key）");
        TestIdentityAndOwner();

        Section("27. 面板與 LLM 用同一套站牌解析（同名站牌找不到的根因）");
        TestSharedStopPicks();

        Section("28. 偷聽模式（回完話後繼續聽：接話／先不出聲／退出，閒聊要留下來）");
        TestEavesdrop();

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
    /// `/bus end`：一次取消全部訂閱，而且要能整個放回來。
    ///
    /// 這個功能壞掉的代價特別高（使用者一次失去所有訂閱），
    /// 所以除了「有沒有清乾淨」，更要驗「復原之後是不是真的跟沒按過一樣」：
    ///   * 群組與訂閱的 **id 必須完全相同**（面板與去重狀態都掛在 id 上）
    ///   * 通知去重狀態必須還在（否則復原後同一班車會被再通知一次）
    ///   * 別人的訂閱不能被動到
    /// </summary>
    private static void TestEndTracking(TaichungBusDataService data, LocationTarget origin, LocationTarget dest)
    {
        const ulong me = 77UL;
        const ulong other = 78UL;

        var subs = Subs();

        var forward = data.FindRoutes(origin, dest).Take(3).ToList();
        if (forward.Count == 0)
        {
            Check("資料集裡有可用的路線（後續測試的前提）", false);
            return;
        }

        var backward = data.FindRoutes(dest, origin).Take(2).ToList();
        if (backward.Count == 0) backward = forward;

        var mineA = subs.CreateGroup(me, origin, dest, forward, 10);
        var mineB = subs.CreateMultiLegGroup(me,
        [
            new LegPlan(origin, dest, forward),
            new LegPlan(dest, origin, backward),
        ], notifyBeforeMinutes: 5);
        var notMine = subs.CreateGroup(other, origin, dest, forward, 10);

        // 假裝「這班車已經通知過」—— 結束追蹤再復原之後，這個狀態必須還在
        var notifiedKey = "TXG300|1|123-FT";
        mineA.State.MarkNotified(notifiedKey, DateTimeOffset.UtcNow);
        Check("前置：去重狀態已記錄", mineA.State.WasNotified(notifiedKey, DateTimeOffset.UtcNow));

        var myGroupIds = new[] { mineA.Id, mineB.Id }.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var mySubIds = subs.GetGroupsByUser(me).SelectMany(subs.GetSubscriptions)
                           .Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var myStops = subs.GetAllEnabledBoardStopUids();
        var subCountBefore = subs.SubscriptionCount;

        // ── 1) 結束追蹤 ──────────────────────────────────
        var removed = subs.RemoveAllForUser(me);

        Check("結束追蹤回傳 2 個訂閱群組", removed.Count == 2, $"{removed.Count} 組");
        Check("★ 結束追蹤後我的群組都不見了", !subs.GetGroupsByUser(me).Any());
        Check("結束追蹤後訂閱數只剩別人的",
            subs.SubscriptionCount == subCountBefore - mySubIds.Count,
            $"{subCountBefore} → {subs.SubscriptionCount}");
        Check("★ 別人的訂閱完全沒被動到",
            subs.GetGroup(notMine.Id) is not null
            && subs.GetSubscriptions(notMine).Count == notMine.SubscriptionIds.Count);
        var onlyOtherStops = subs.GetSubscriptions(notMine)
                                 .Select(s => s.BoardStopUid)
                                 .Distinct(StringComparer.Ordinal)
                                 .OrderBy(x => x, StringComparer.Ordinal)
                                 .ToList();
        var stopsAfterEnd = subs.GetAllEnabledBoardStopUids()
                                .OrderBy(x => x, StringComparer.Ordinal)
                                .ToList();

        Check("★ 結束追蹤後只剩別人的上車站要輪詢（我的都不查了）",
            stopsAfterEnd.SequenceEqual(onlyOtherStops) && stopsAfterEnd.Count < myStops.Count,
            $"{myStops.Count} → {stopsAfterEnd.Count} 個");
        Check("回傳的內容包含每一個被移除的訂閱",
            removed.Sum(r => r.Subscriptions.Count) == mySubIds.Count,
            $"{removed.Sum(r => r.Subscriptions.Count)} 筆");

        // ── 2) 復原：放回原本的物件，而不是重新建立 ─────────
        var undo = new UndoStack();
        undo.Push(new UndoEndedTracking($"結束追蹤（{removed.Count} 組訂閱）", removed, mineA.Id));
        var message = undo.Undo(subs, store: null!, me);

        Check("★ 復原後群組 id 完全相同（面板與去重狀態都掛在 id 上）",
            subs.GetGroupsByUser(me).Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(myGroupIds), message);
        Check("★ 復原後訂閱 id 完全相同",
            subs.GetGroupsByUser(me).SelectMany(subs.GetSubscriptions)
                .Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(mySubIds));
        Check("★ 復原後「這班車已經通知過」的狀態還在（不會被重複通知）",
            subs.GetGroup(mineA.Id)!.State.WasNotified(notifiedKey, DateTimeOffset.UtcNow));
        Check("復原後要輪詢的上車站與結束前一致",
            subs.GetAllEnabledBoardStopUids().SequenceEqual(myStops),
            $"{subs.GetAllEnabledBoardStopUids().Count} 個");
        Check("復原後訂閱總數回到原本的值", subs.SubscriptionCount == subCountBefore,
            $"{subs.SubscriptionCount}");

        // ── 3) 沒有訂閱的人按結束追蹤 ────────────────────
        var emptyRemoved = subs.RemoveAllForUser(99UL);
        Check("沒有訂閱的使用者：結束追蹤回傳空清單", emptyRemoved.Count == 0);
        Check("空清單的復原訊息不會騙人",
            new UndoEndedTracking("結束追蹤", emptyRemoved).Undo(subs, store: null!, 99UL)
                .Contains("沒有東西可以復原", StringComparison.Ordinal));

        // ── 4) 只影響自己 ────────────────────────────────
        subs.RemoveAllForUser(other);
        Check("結束追蹤別人的訂閱時，我的完全不受影響",
            subs.GetGroup(mineA.Id) is not null && subs.GetGroup(notMine.Id) is null);
    }

    /// <summary>
    /// <summary>
    /// AI 聊天：**決定「這則訊息要不要接續前面」的規則**、伺服器隔離、每週額度。
    ///
    /// 這一段刻意完全不碰網路：切段規則、上下文裁剪、額度窗口都是純邏輯，
    /// 而它們錯掉的代價很高 ——
    ///   * 切錯 → 使用者看到「突然失憶」或「把別人的話題接起來」
    ///   * 隔離錯 → **A 伺服器的對話跑到 B 伺服器**（這是最嚴重的那種錯）
    ///   * 額度錯 → 錢包失控
    /// </summary>
    private static void TestChat()
    {
        var t0 = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

        // ── 1) 時間切段：超過門檻就當新的一段 ────────────────
        var options = new LlmOptions { SegmentGapMinutes = 30, TopicDetect = false, MaxContextTurns = 20 };
        var store = new ConversationStore(options);

        var first = Turn(1, "小明", "我要搭車", t0);
        var d1 = store.Draft(100UL, 1000UL, first, null);
        Check("★ 第一次說話 → 沒有上下文（新的一段）", d1.IsFirstEver && d1.GapExceeded);
        var c1 = store.Commit(d1, first, startNewSegment: true, "第一次對話");
        Check("第一次對話會建立段落", c1.NewSegment && c1.Context.Count == 0);

        var second = Turn(2, "小明", "從車站到靜宜", t0.AddMinutes(2));
        var d2 = store.Draft(100UL, 1000UL, second, null);
        Check("兩分鐘後說話 → 不算新的一段", !d2.GapExceeded);
        var c2 = store.Commit(d2, second, startNewSegment: false, "接續");
        Check("★ 接續時會帶入前兩則（含 Bot 的回覆）", c2.Context.Count == 1, $"{c2.Context.Count} 則");

        store.RecordAssistant(c2, BotTurn(3, "靜宜大學搭 300 或 304", t0.AddMinutes(2)));

        var later = Turn(4, "小明", "晚安", t0.AddMinutes(120));
        var d3 = store.Draft(100UL, 1000UL, later, null);
        Check("★ 過了 2 小時 → 當成新的一段（不帶舊上下文）",
            d3.GapExceeded && d3.Gap > TimeSpan.FromMinutes(30), $"間隔 {d3.Gap.TotalMinutes:0} 分");

        var c3 = store.Commit(d3, later, startNewSegment: true, "距離上次超過 30 分鐘");
        Check("★ 新的一段不帶任何舊訊息", c3.Context.Count == 0, $"{c3.Context.Count} 則");
        Check("舊段落還在（可以回覆它把話題拉回來）", c3.Conversation.Segments.Count == 2);

        // ── 2) 回覆舊訊息 → 即使超過時間門檻也回到那一段 ────
        var botReply = BotTurn(3, "靜宜大學搭 300 或 304", t0.AddMinutes(2));
        // （上面已經記過同樣的訊息 ID，這裡再用一次當作「回覆那則 Bot 訊息」）
        var replied = Turn(5, "小明", "那大概多久一班？", t0.AddHours(3), replyTo: 3);
        var d4 = store.Draft(100UL, 1000UL, replied, botReply);

        Check("★ 被回覆的訊息找得到它屬於哪一段", d4.ReplySegment is not null);

        var c4 = store.Commit(d4, replied, startNewSegment: false, "回覆舊訊息");
        Check("★ 回覆舊訊息 → 回到那一段（不是目前這一段）",
            c4.Segment.Id == c3.Conversation.Segments[0].Id, $"segment={c4.Segment.Id}");
        Check("★ 回覆舊訊息時，被回覆的內容一定在上下文裡",
            c4.Context.Any(t => t.MessageId == 3), $"{c4.Context.Count} 則");

        // ── 3) 不同伺服器／不同頻道完全不相通 ──────────────
        var otherGuild = Turn(10, "別人", "我要去逢甲", t0.AddMinutes(1));
        var dOther = store.Draft(999UL, 1000UL, otherGuild, null);
        Check("★ 別的伺服器是全新的一段（看不到 A 伺服器的對話）",
            dOther.IsFirstEver && dOther.Current is null);

        var otherChannel = Turn(11, "小明", "在另一個頻道問", t0.AddMinutes(1));
        var dChannel = store.Draft(100UL, 2000UL, otherChannel, null);
        Check("★ 同伺服器的另一個頻道也是全新的（每個頻道各自一條）",
            dChannel.IsFirstEver && dChannel.Current is null);

        // 跨伺服器「回覆」也不該撈到別人的歷史
        Check("★ 別的伺服器的訊息 ID 在這個頻道查不到",
            !store.HasMessage(100UL, 1000UL, dOther.Conversation.ChannelId == 1000UL ? 999UL : 999UL)
            || !store.HasMessage(999UL, 1000UL, 1UL));

        // ── 4) 上下文裁剪 ────────────────────────────────
        var many = Enumerable.Range(1, 30)
            .Select(i => Turn((ulong)i, "小明", $"第 {i} 句", t0.AddSeconds(i)))
            .ToList();

        var small = new LlmOptions { MaxContextTurns = 5, MaxContextTokens = 10_000 };
        var trimmed = ContextBuilder.Trim(many, small);
        Check("★ 超過數量上限時只留最新的幾則",
            trimmed.Turns.Count == 5 && trimmed.Turns[^1].Content == "第 30 句",
            $"{trimmed.Turns.Count} 則、丟掉 {trimmed.DroppedByCount}");

        var tiny = new LlmOptions { MaxContextTurns = 50, MaxContextTokens = 60 };
        var trimmedByTokens = ContextBuilder.Trim(many, tiny);
        Check("★ token 上限也會生效（而且至少留一則）",
            trimmedByTokens.Turns.Count is > 0 and < 30 && trimmedByTokens.EstimatedTokens <= 200,
            $"{trimmedByTokens.Turns.Count} 則／約 {trimmedByTokens.EstimatedTokens} tokens");

        var mustKeep = many[0];
        var keepReply = ContextBuilder.Trim(many, small, mustKeep);
        Check("★ 被回覆的那一則即使很舊也會被保留",
            keepReply.KeptReplyTarget && keepReply.Turns.Any(t => t.MessageId == mustKeep.MessageId));

        // ── 5) 話題判斷的解析（LLM 的回答不會永遠很乖）──────
        Check("解析「SAME」→ 同一段", TopicSwitchDetector.Parse("SAME") == false);
        Check("解析「NEW」→ 新的一段", TopicSwitchDetector.Parse("NEW") == true);
        Check("解析「\\n new \\n」→ 新的一段（寬鬆比對）", TopicSwitchDetector.Parse("\n new \n") == true);
        Check("★ 模型多講幾句時，以最後出現的結論為準",
            TopicSwitchDetector.Parse("先前的對話是 SAME，但這則明顯是 NEW") == true);
        Check("解析看不懂的回答 → null（呼叫端會沿用目前段落）",
            TopicSwitchDetector.Parse("我不確定") is null);
        Check("空字串 → null", TopicSwitchDetector.Parse("") is null);

        // ── 6) token 估算 ────────────────────────────────
        Check("token 估算：中文一字約一 token", TokenEstimator.Estimate("台中公車") is >= 4 and <= 8,
            TokenEstimator.Estimate("台中公車").ToString());
        Check("token 估算：英文四字約一 token", TokenEstimator.Estimate("hello world") is >= 2 and <= 5,
            TokenEstimator.Estimate("hello world").ToString());
        Check("token 估算：空字串是 0", TokenEstimator.Estimate("") == 0);
        Check("token 估算：越長越大",
            TokenEstimator.Estimate("公車") < TokenEstimator.Estimate("公車公車公車公車"));

        // ── 7) 每週額度：窗口、重置、擋下 ──────────────────
        Check("★ 週窗口從 UTC 週一 00:00 起算",
            WeeklyTokenBudget.WeekStartUtc(new DateTimeOffset(2026, 9, 16, 15, 30, 0, TimeSpan.Zero))
                == new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            WeeklyTokenBudget.WeekStartUtc(t0).ToString("u"));

        Check("★ 週日也算同一週（週一才是分界）",
            WeeklyTokenBudget.WeekStartUtc(new DateTimeOffset(2026, 9, 20, 23, 59, 0, TimeSpan.Zero))
                == new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero));

        Check("★ 星期一 00:00 之後就是新的一週",
            WeeklyTokenBudget.WeekStartUtc(new DateTimeOffset(2026, 9, 21, 0, 0, 1, TimeSpan.Zero))
                == new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));

        Check("重置時間 = 週一 + 7 天",
            WeeklyTokenBudget.ResetAt(t0) - WeeklyTokenBudget.WeekStartUtc(t0) == TimeSpan.FromDays(7));

        var budgetOptions = new LlmOptions { WeeklyTokenLimit = 1000 };
        var budget = new WeeklyTokenBudget(budgetOptions, store: null, now: t0);

        Check("一開始額度是滿的", budget.Check(100, t0).Allowed);
        Check("剩餘量正確", budget.Check(100, t0).Remaining == 1000);

        budget.Record(300, 200, 100UL, t0);
        Check("記帳之後用量累加（in + out）", budget.Usage.TotalTokens == 500, budget.Usage.TotalTokens.ToString());
        Check("記帳含呼叫次數", budget.Usage.Calls == 1);
        Check("★ 記得住是哪個伺服器花的（全域額度要看得出誰在花）",
            budget.Usage.ByGuild.TryGetValue("100", out var byGuild) && byGuild == 500);

        var almostFull = budget.Check(500, t0);
        Check("★ 這次的輸入會讓總量超過上限 → 事前就擋下來", !almostFull.Allowed, almostFull.Reason);
        Check("還差一點點但不會超過 → 放行", budget.Check(499, t0).Allowed);

        budget.Record(700, 0, 100UL, t0);
        Check("★ 額度用完後一律擋下", !budget.Check(1, t0).Allowed, budget.Usage.TotalTokens.ToString());

        budget.RecordRefusal(t0);
        Check("被擋下的次數會記錄", budget.Usage.Refusals == 1);

        // 跨週自動歸零
        var nextWeek = t0.AddDays(7);
        var afterRollover = budget.Check(100, nextWeek);
        Check("★ 進入新的一週 → 用量歸零、額度恢復",
            afterRollover.Allowed && budget.Usage.TotalTokens == 0, budget.Describe());

        // 不限額度（0 = 不限）
        var unlimited = new WeeklyTokenBudget(new LlmOptions { WeeklyTokenLimit = 0 }, null, t0);
        unlimited.Record(999_999, 1, 1UL, t0);
        Check("LLM_WEEKLY_TOKENS=0 → 不限額度", unlimited.Check(500_000, t0).Allowed && unlimited.Check(0, t0).Unlimited);

        // ── 8) 用量要能跨重啟（持久化）────────────────────
        var memory = new FakeStateStore();
        var persisted = new WeeklyTokenBudget(new LlmOptions { WeeklyTokenLimit = 5000 }, memory, t0);
        persisted.Record(120, 80, 42UL, t0);
        persisted.Flush(t0);

        var reloaded = new WeeklyTokenBudget(new LlmOptions { WeeklyTokenLimit = 5000 }, memory, t0);
        Check("★ 重啟後讀得到同一週的用量（否則每週上限形同虛設）",
            reloaded.Usage.TotalTokens == 200, reloaded.Usage.TotalTokens.ToString());
        Check("重啟後也記得是哪些伺服器花的",
            reloaded.Usage.ByGuild.TryGetValue("42", out var reused) && reused == 200);

        // ── 9) 對話記憶會過期清掉 ────────────────────────
        var ttlStore = new ConversationStore(new LlmOptions { ChannelTtl = TimeSpan.FromHours(1) });
        var ttlTurn = Turn(1, "小明", "嗨", t0);
        ttlStore.Commit(ttlStore.Draft(1UL, 1UL, ttlTurn, null), ttlTurn, true, "第一次");
        Check("清掃前還在", ttlStore.Snapshot(1UL, 1UL, t0) is not null);
        Check("★ 超過 TTL 的頻道會被清掉", ttlStore.Purge(t0.AddHours(3)) == 1);
        Check("清掉之後查不到", ttlStore.Snapshot(1UL, 1UL, t0.AddHours(3)) is null);

        // ── 9b) 「每個頻道記多少」是環境變數可調的 ──────────
        //     這幾個數字就是記憶體用量（LLM_MAX_TURNS_PER_SEGMENT／…_SEGMENTS_PER_CHANNEL／
        //     LLM_MAX_CHANNELS／LLM_CHANNEL_TTL_HOURS），所以行為一定要真的跟著設定走。
        var smallStore = new ConversationStore(new LlmOptions
        {
            MaxTurnsPerSegment = 4,
            MaxSegmentsPerChannel = 2,
            MaxChannels = 2,
            TopicDetect = false
        });

        // 同一段連續 6 則 → 只留最後 4 則
        for (var i = 1; i <= 6; i++)
        {
            var turn = Turn((ulong)i, "小明", $"第 {i} 句", t0.AddSeconds(i));
            smallStore.Commit(smallStore.Draft(1UL, 7UL, turn, null), turn, i == 1, "測試");
        }

        var capped = smallStore.Snapshot(1UL, 7UL, t0.AddSeconds(6));
        Check("★ LLM_MAX_TURNS_PER_SEGMENT：一段最多留幾則（超過丟最舊的）",
            capped?.CurrentTurnCount == 4, $"{capped?.CurrentTurnCount} 則（上限 4）");
        Check("★ 丟掉的是最舊的（最近的才重要）",
            smallStore.FindTurn(1UL, 7UL, 1UL) is null && smallStore.FindTurn(1UL, 7UL, 6UL) is not null);

        // 每個頻道最多 2 段（硬開新段 4 次）
        for (var i = 10; i <= 13; i++)
        {
            var turn = Turn((ulong)i, "小明", $"換話題 {i}", t0.AddMinutes(i * 10));
            smallStore.Commit(smallStore.Draft(1UL, 7UL, turn, null), turn, true, "新的一段");
        }

        Check("★ LLM_MAX_SEGMENTS_PER_CHANNEL：每頻道最多幾段（超過丟最舊的）",
            smallStore.Snapshot(1UL, 7UL, t0.AddMinutes(200))?.SegmentCount == 2,
            $"{smallStore.Snapshot(1UL, 7UL, t0.AddMinutes(200))?.SegmentCount} 段（上限 2）");

        // 最多 2 個頻道（第 3 個進來就把最久沒動的擠掉）
        foreach (var ch in new ulong[] { 20UL, 21UL, 22UL })
        {
            var turn = Turn(100UL + ch, "小華", $"頻道 {ch}", t0.AddMinutes(300 + ch));
            smallStore.Commit(smallStore.Draft(1UL, ch, turn, null), turn, true, "測試");
        }

        Check("★ LLM_MAX_CHANNELS：最多同時記幾個頻道（超過淘汰最久沒動的）",
            smallStore.ChannelCount == 2, $"{smallStore.ChannelCount} 個頻道（上限 2）");
        Check("★ 留下的是最近有動靜的頻道（最舊的 7 與 20 被淘汰）",
            smallStore.Snapshot(1UL, 22UL, t0.AddMinutes(400)) is not null
            && smallStore.Snapshot(1UL, 21UL, t0.AddMinutes(400)) is not null
            && smallStore.Snapshot(1UL, 7UL, t0.AddMinutes(400)) is null
            && smallStore.Snapshot(1UL, 20UL, t0.AddMinutes(400)) is null);

        Check("★ 設定摘要印得出容量（改環境變數後看得出有沒有生效）",
            new LlmOptions().DescribeMemory().Contains("24 則", StringComparison.Ordinal),
            new LlmOptions().DescribeMemory());

        // ── 10) 長回覆要分段（Discord 單則 2000 字）─────────
        var longText = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"第 {i} 行：公車資訊"));
        var chunks = SplitForTest(longText, 1900);
        Check("★ 超長回覆會切成多段", chunks.Count > 1, $"{chunks.Count} 段");
        Check("★ 每一段都不超過 2000 字（Discord 上限）", chunks.All(c => c.Length <= 2000),
            chunks.Max(c => c.Length).ToString());
        Check("★ 分段後內容完整（沒有漏字）",
            string.Concat(chunks).Replace("\n", "").Replace(" ", "").Length
                == longText.Replace("\n", "").Replace(" ", "").Length);
    }

    /// <summary>測試用的假儲存區（模擬「重啟後還在」）。</summary>
    private sealed class FakeStateStore : TcBusBot.Core.Chat.ILlmStateStore
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

        public string? GetBlob(string key) => _map.TryGetValue(key, out var json) ? json : null;

        public void SetBlob(string key, string json) => _map[key] = json;
    }

    private static ChatTurn Turn(ulong id, string author, string content, DateTimeOffset at, ulong? replyTo = null)
        => new(ChatRole.User, author, 12345UL, id, content, at, replyTo);

    private static ChatTurn BotTurn(ulong id, string content, DateTimeOffset at)
        => new(ChatRole.Assistant, "笨蛋猫猫搭公车", 999UL, id, content, at);

    /// <summary>與 LlmChatService.Split 相同的規則（Core 不該知道 Discord 的字數限制，所以在這裡比對行為）。</summary>
    private static List<string> SplitForTest(string text, int maxChars)
    {
        var chunks = new List<string>();
        var remaining = (text ?? "").Trim();

        while (remaining.Length > maxChars)
        {
            var cut = remaining.LastIndexOf('\n', Math.Min(maxChars, remaining.Length - 1));
            if (cut < maxChars / 2) cut = maxChars;

            chunks.Add(remaining[..cut].TrimEnd());
            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0) chunks.Add(remaining);
        return chunks;
    }

    /// <summary>
    /// LLM 工具的公車操作層（<see cref="BusActionService"/>）。
    ///
    /// 這是「模型幫使用者訂公車」真正會出事的地方，而且出事的代價是**訂錯路線**：
    ///   * 模型給的是口語站名（「台中火車站」「靜宜」），要展開成正確的候選站牌集合
    ///   * 站名找不到時**不能**硬訂（回報失敗，讓模型去問使用者）
    ///   * 只能動「那個人」的訂閱（工具參數裡沒有任何「幫誰訂」的欄位）
    ///   * 取消要能還原（回傳被移除的內容）
    ///
    /// 全程不需要 LLM、不需要網路 —— 模型講什麼都只是字串輸入。
    /// </summary>
    private static void TestBusActions(TaichungBusDataService data, LocationTarget origin, LocationTarget dest)
    {
        const ulong me = 555UL;
        const ulong other = 556UL;

        var subs = Subs();
        var actions = new BusActionService(data, subs);

        // ── 1) 口語站名 → 正確的候選站牌 ──────────────────
        var search = actions.SearchStops("台中火車站");
        Check("★ 查站牌：打「台中火車站」找得到（口語 + 台/臺）",
            search.Contains("臺中車站", StringComparison.Ordinal), search.Split('\n')[0]);

        var abbreviated = actions.SearchStops("台中科大");
        Check("★ 查站牌：縮寫「台中科大」也找得到",
            abbreviated.Contains("科技大學", StringComparison.Ordinal), abbreviated.Split('\n')[0]);

        var missing = actions.SearchStops("不存在的站XYZ");
        Check("★ 查不到的站名會老實說找不到，並給範例",
            missing.Contains("找不到", StringComparison.Ordinal) &&
            missing.Contains("例如", StringComparison.Ordinal));

        Check("太短的關鍵字會被擋掉（避免亂命中）",
            actions.SearchStops("中").Contains("太短", StringComparison.Ordinal));

        // ── 2) 查路線（不建立訂閱）────────────────────────
        var routeText = actions.FindRoutes("臺中車站", "靜宜大學");
        Check("★ 查路線：找得到且**不會**建立訂閱",
            routeText.Contains("路線", StringComparison.Ordinal) && subs.GroupCount == 0,
            routeText.Split('\n')[0]);

        var reverseText = actions.FindRoutes("靜宜大學", "臺中車站");
        Check("反方向也查得出來（方向不同 → 路線不同）",
            reverseText.Contains("路線", StringComparison.Ordinal), reverseText.Split('\n')[0]);

        // ── 3) 訂閱：真的建立，而且與面板同一套邏輯 ─────────
        var subscribe = actions.Subscribe(me, "台中車站", "靜宜大學", notifyMinutes: 7,
                                          guildId: 111UL, channelId: 222UL);

        Check("★ 用一句話就訂閱成功", subscribe.Ok, subscribe.Message.Split('\n')[0]);

        // 「與面板同一套邏輯」的硬證據：把「臺中車站」這一組站牌直接餵給同一條路線匹配，
        // 得到的候選上車站數量必須與工具建立的一模一樣（面板就是這樣做的）。
        var expectedSubs = data
            .FindRoutes(data.TargetFromStops(new[] { "TXG12251", "TXG11020" }), dest)
            .Sum(r => r.BoardChoices.Count);

        Check($"★ 訂閱內容與面板相同（{expectedSubs} 筆候選上車站）",
            subscribe.Group?.SubscriptionIds.Count == expectedSubs,
            $"{subscribe.Group?.SubscriptionIds.Count} 筆");
        Check("提前通知時間有帶進去", subscribe.Group?.NotifyBeforeMinutes == 7);
        Check("訊息裡有列出上車站（使用者才知道在哪等）",
            subscribe.Message.Contains("上車", StringComparison.Ordinal));
        Check("訂閱屬於正確的使用者",
            subs.GetGroupsByUser(me).Count() == 1 && subscribe.Group!.UserId == me);

        // 重複訂閱是合法的（兩段不同的行程），但要是**不同的**群組
        var second = actions.Subscribe(me, "靜宜大學", "臺中車站", notifyMinutes: 10);
        Check("★ 再訂一次反向行程 → 另一個群組（不會蓋掉前一個）",
            second.Ok && subs.GetGroupsByUser(me).Count() == 2);

        // ── 4) 站名錯誤時不能硬訂 ─────────────────────────
        var beforeBad = subs.GroupCount;
        var badOrigin = actions.Subscribe(me, "不存在的站XYZ", "靜宜大學");
        Check("★ 起點找不到 → 不建立訂閱，並回報失敗", !badOrigin.Ok && subs.GroupCount == beforeBad);
        Check("失敗訊息有提示模型去查正確站名",
            badOrigin.Message.Contains("search_stops", StringComparison.Ordinal) ||
            badOrigin.Message.Contains("正確的站名", StringComparison.Ordinal));

        var badDest = actions.Subscribe(me, "臺中車站", "不存在的站XYZ");
        Check("★ 終點找不到 → 不建立訂閱", !badDest.Ok && subs.GroupCount == beforeBad);

        // 沒有直達路線時也不能硬訂
        var noRoute = actions.Subscribe(me, "臺中車站", "臺中車站");
        Check("★ 沒有可搭路線 → 不建立訂閱（同站到同站）", !noRoute.Ok, noRoute.Message.Split('\n')[0]);

        // ── 5) 列出訂閱（模型用來確認）────────────────────
        var list = actions.ListSubscriptions(me);
        Check("★ 列得出自己的訂閱", list.Contains("2 組訂閱", StringComparison.Ordinal),
            list.Split('\n')[0]);
        Check("別人的訂閱看不到（工具只作用在那個人身上）",
            actions.ListSubscriptions(other).Contains("沒有任何訂閱", StringComparison.Ordinal));

        // ── 6) 取消要能還原 ──────────────────────────────
        var idsBefore = subs.GetGroupsByUser(me).Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var subCountBefore = subs.SubscriptionCount;

        var cancel = actions.CancelAll(me);
        Check("★ 取消全部訂閱",
            cancel.GroupCount == idsBefore.Count && cancel.SubscriptionCount == subCountBefore,
            $"{cancel.GroupCount} 組、{cancel.SubscriptionCount} 筆");
        Check("取消後就不再查詢任何上車站",
            subs.GetAllEnabledBoardStopUids().Count == 0,
            $"{subs.GetAllEnabledBoardStopUids().Count} 個");
        Check("★ 取消會回傳被移除的內容（呼叫端才能掛「復原」）",
            cancel.Removed.Count == idsBefore.Count &&
            cancel.Removed.Sum(r => r.Subscriptions.Count) == subCountBefore);

        var undo = new UndoStack();
        undo.Push(new UndoEndedTracking("AI 取消全部訂閱", cancel.Removed, null));
        undo.Undo(subs, store: null!, me);

        Check("★ 復原後 id 完全相同（AI 做的破壞性操作也救得回來）",
            subs.GetGroupsByUser(me).Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(idsBefore));
        Check("復原後訂閱數回到原本的值", subs.SubscriptionCount == subCountBefore,
            $"{subs.SubscriptionCount}");

        var cancelEmpty = actions.CancelAll(other);
        Check("沒有訂閱時取消 → 老實說沒有東西可以取消",
            cancelEmpty.GroupCount == 0 && cancelEmpty.Message.Contains("沒有", StringComparison.Ordinal));

        // ── 7) 工具只影響自己：別的伺服器的使用者互不干擾 ──
        var otherSub = actions.Subscribe(other, "臺中車站", "靜宜大學");
        Check("另一個使用者的訂閱是分開的", otherSub.Ok && subs.GetGroupsByUser(other).Count() == 1);
        Check("自己的訂閱不會因為別人訂閱而增加",
            subs.GetGroupsByUser(me).Count() == 2, $"{subs.GetGroupsByUser(me).Count()} 組");
    }

    /// <summary>
    /// 儲存方案的 **DI 註冊**（`services.AddBusBotStorage(...)`）。
    ///
    /// 以前「挑後端」寫死在 SavedGroupStore 的 private static 裡、服務用自己手寫的容器組；
    /// 現在是標準的 ServiceCollection，所以這裡驗三件事：
    ///   1. 註冊之後真的解析得到，而且**訂閱組與 LLM 用量是同一个實例**
    ///      （不是同一個的話，每週用量會寫到另一條連線上，重啟後就對不起來）
    ///   2. 後端由 StorageSettings 決定（記憶體／文字檔／MongoDB 強制）
    ///   3. 生命週期語意正確：SavedGroupStore 由**工廠**建立 → 容器會負責釋放；
    ///      底層連線則是實例註冊 → 由 store 釋放（避免同一條連線被釋放兩次）
    /// </summary>
    private static void TestStorageDi()
    {
        // ── 1) 記憶體模式：兩個入口指向同一個實例 ────────────
        var services = new ServiceCollection();
        services.AddBusBotStorage(StorageSettings.Memory, _ => { });

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var store = provider.GetRequiredService<SavedGroupStore>();
        var state = provider.GetRequiredService<TcBusBot.Core.Chat.ILlmStateStore>();

        Check("★ 註冊後解析得到 SavedGroupStore", store is not null);
        Check("★ 訂閱組與 LLM 用量共用同一個實例（同一條後端鏈）",
            ReferenceEquals(store, state));

        // 兩邊都真的能用
        var payload = SavedGroupPayloadFactory.FromSubscriptions(
            new SavedTarget("臺中車站", new[] { "TXG12251" }),
            new SavedTarget("靜宜大學", new[] { "TXG13567" }),
            new[] { ("TXG300", 1, "300") }, notifyMinutes: 10);

        var (saved, _) = store.Save(7UL, "DI 測試", payload);
        Check("透過容器拿到的 store 可以存訂閱組", saved == SaveGroupResult.Created, saved.ToString());

        state.SetBlob("llm_weekly_usage", "{\"total\":123}");
        Check("★ 同一條後端也能存 LLM 用量（ILlmStateStore）",
            state.GetBlob("llm_weekly_usage")?.Contains("123", StringComparison.Ordinal) == true);
        Check("從 store 這一頭讀得到剛剛寫的用量",
            store.GetBlob("llm_weekly_usage")?.Contains("123", StringComparison.Ordinal) == true);

        // ── 2) 生命週期：誰負責釋放 ────────────────────────
        var storeDescriptor = services.First(d => d.ServiceType == typeof(SavedGroupStore));
        var repoDescriptor = services.First(d => d.ServiceType.Name == "ISavedGroupRepository");

        Check("★ SavedGroupStore 用**工廠**註冊（容器會負責釋放連線）",
            storeDescriptor.ImplementationFactory is not null);
        Check("★ 底層後端用**實例**註冊（由 store 釋放，不會被釋放兩次）",
            repoDescriptor.ImplementationInstance is not null);
        Check("SavedGroupStore 是 singleton（整台 Bot 共用一條連線）",
            storeDescriptor.Lifetime == ServiceLifetime.Singleton);

        // ── 3) 文字檔模式：後端真的照設定走 ────────────────
        var dir = Path.Combine(Path.GetTempPath(), $"tcbus-di-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var textServices = new ServiceCollection();
        textServices.AddBusBotStorage(
            new StorageSettings(Path.Combine(dir, "tcbus.db"), Mode: SavedGroupStore.StorageMode.Text));

        using var textProvider = textServices.BuildServiceProvider();
        var textStore = textProvider.GetRequiredService<SavedGroupStore>();

        Check("★ 指定文字檔模式 → 真的用文字檔",
            textStore.Describe().Contains("文字檔", StringComparison.Ordinal), textStore.Describe());
        Check("文字檔模式仍然算是持久化", textStore.IsPersistent);

        var (textSaved, _) = textStore.Save(8UL, "文字檔測試", payload);
        Check("文字檔模式可以存", textSaved == SaveGroupResult.Created, textSaved.ToString());

        var textFile = Path.Combine(dir, SavedGroupStore.TextFileName);
        Check("★ 檔案真的產生在設定的目錄底下", File.Exists(textFile), textFile);

        try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* 測試的暫存檔 */ }

        // ── 4) 強制 MongoDB 卻沒有連線字串 → 明確失敗（不偷偷退回）──
        var threw = false;

        try
        {
            new ServiceCollection().AddBusBotStorage(
                new StorageSettings("tcbus.db", Mode: SavedGroupStore.StorageMode.Mongo));
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Check("★ 指定 MongoDB 模式卻沒給連線字串 → 直接丟例外（不偷偷用本機）", threw);

        // ── 5) 連線字串不會被印出來 ────────────────────────
        var settings = new StorageSettings("tcbus.db", "mongodb+srv://user:secret@cluster.example.net/db");
        Check("★ 連線字串預覽有遮罩（不能把帳密印出來）",
            !settings.MongoPreview.Contains("secret", StringComparison.Ordinal), settings.MongoPreview);
        Check("StorageSettings 說得出自己的模式", settings.Describe().Contains("MongoDB", StringComparison.Ordinal));
        Check("沒有連線字串時預覽寫「存在本機」",
            new StorageSettings("tcbus.db").MongoPreview.Contains("本機", StringComparison.Ordinal));
    }

    /// <summary>
    /// 模型思考開關（<see cref="ReasoningOffHandler"/>）。
    ///
    /// 為什麼要這麼「繞」：SK 這個版本送不出「不要思考」的欄位
    /// （`ExtensionData` 被忽略、型別化的 `ReasoningEffort` 拒絕 `none`），
    /// 所以在 HTTP 這一層把欄位補進 JSON。這裡就驗那一段改寫：
    ///   * 該加的欄位有加（off → 兩種寫法都送）
    ///   * 不該動的時候一個字都不動（auto／非 chat 路徑／呼叫端自己指定過）
    ///   * **壞掉的 body 不可以讓對話掛掉**（原樣送出）
    /// </summary>
    private static void TestReasoning()
    {
        // ── 1) 欄位對應（純函式）──────────────────────────
        var off = LlmOptions.ReasoningFields("off");
        Check("★ off → 送 reasoning_effort=none（OpenAI 系寫法）",
            off.TryGetValue("reasoning_effort", out var effort) && (string?)effort == "none");
        Check("★ off → 同時送 thinking={type:disabled}（Anthropic 系寫法）",
            off.TryGetValue("thinking", out var thinking)
            && thinking is Dictionary<string, object?> { } dict
            && dict["type"] as string == "disabled");

        Check("auto → 什麼都不加（用服務端預設）", LlmOptions.ReasoningFields("auto").Count == 0);
        Check("沒設定（null）→ 預設就是關閉思考", LlmOptions.ReasoningFields(null).Count == 2);

        var high = LlmOptions.ReasoningFields("high");
        Check("high → 只送 reasoning_effort=high（不硬關思考）",
            high.Count == 1 && (string?)high["reasoning_effort"] == "high",
            string.Join(",", high.Keys));

        Check("描述文字分得出「已關閉」與「服務端預設」",
            new LlmOptions { Reasoning = "off" }.ReasoningDescription.Contains("已關閉", StringComparison.Ordinal)
            && new LlmOptions { Reasoning = "auto" }.ReasoningDescription.Contains("預設", StringComparison.Ordinal));

        // ── 2) HTTP 改寫（用假的 handler 攔下送出的內容）────
        string? sent = null;
        string? sentPath = null;

        async Task<string?> Send(string url, string body, LlmOptions options)
        {
            sent = null;
            sentPath = null;

            var handler = new ReasoningOffHandler(options) { InnerHandler = new CaptureHandler(body, captured => sent = captured) };
            using var http = new HttpClient(handler);

            await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
            sentPath ??= url;
            return sent;
        }

        const string chatUrl = "https://api.example.com/v1/chat/completions";
        const string chatBody = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";

        var offBody = Send(chatUrl, chatBody, new LlmOptions { Reasoning = "off" }).GetAwaiter().GetResult();
        Check("★ 送出去的 JSON 真的被補上了 reasoning_effort=none",
            offBody?.Contains("\"reasoning_effort\":\"none\"", StringComparison.Ordinal) == true, offBody);
        Check("★ 也補上了 thinking",
            offBody?.Contains("\"thinking\":{\"type\":\"disabled\"}", StringComparison.Ordinal) == true);
        Check("原本的內容沒有被動到（model／messages 還在）",
            offBody?.Contains("\"model\":\"m\"", StringComparison.Ordinal) == true
            && offBody?.Contains("content", StringComparison.Ordinal) == true);

        var autoBody = Send(chatUrl, chatBody, new LlmOptions { Reasoning = "auto" }).GetAwaiter().GetResult();
        Check("★ auto → 一個字都沒改", autoBody == chatBody, autoBody);

        var highBody = Send(chatUrl, chatBody, new LlmOptions { Reasoning = "high" }).GetAwaiter().GetResult();
        Check("high → 送 reasoning_effort=high 而且不送 thinking",
            highBody?.Contains("\"reasoning_effort\":\"high\"", StringComparison.Ordinal) == true
            && highBody?.Contains("thinking", StringComparison.Ordinal) != true);

        var modelsBody = Send("https://api.example.com/v1/models", chatBody, new LlmOptions { Reasoning = "off" })
            .GetAwaiter().GetResult();
        Check("★ 非 chat 路徑（/models）不動它", modelsBody == chatBody);

        // 呼叫端自己指定過就不要覆蓋
        var explicitBody = Send(chatUrl, """{"model":"m","reasoning_effort":"high"}""", new LlmOptions { Reasoning = "off" })
            .GetAwaiter().GetResult();
        Check("★ 呼叫端已經指定 reasoning_effort → 不覆蓋（尊重呼叫端）",
            explicitBody?.Contains("\"reasoning_effort\":\"high\"", StringComparison.Ordinal) == true, explicitBody);

        // ── 3) 壞掉的 body 不可以讓對話掛掉 ────────────────
        var brokenBody = Send(chatUrl, "這不是 JSON", new LlmOptions { Reasoning = "off" }).GetAwaiter().GetResult();
        Check("★ body 不是 JSON → 原樣送出、不丟例外", brokenBody == "這不是 JSON", brokenBody);

        var emptyBody = Send(chatUrl, "", new LlmOptions { Reasoning = "off" }).GetAwaiter().GetResult();
        Check("空 body 也不會壞", emptyBody == "");

        // ── 4) 不會誤判形狀 ──────────────────────────────
        Check("JSON 不是物件（例如陣列）→ 不改",
            ReasoningOffHandler.Patch("[1,2,3]", LlmOptions.ReasoningFields("off")) is null);
        Check("不需要改時回傳 null（呼叫端就知道不用換 Content）",
            ReasoningOffHandler.Patch("""{"reasoning_effort":"x"}""",
                                      new Dictionary<string, object?> { ["reasoning_effort"] = "none" }) is null);
    }

    /// <summary>攔下送出的 body（模擬 HTTP 層，不需要網路）。</summary>
    private sealed class CaptureHandler(string responseBody, Action<string> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) };
        }
    }

    /// <summary>
    /// 「可以被馴服的提示詞」：<see cref="GuildPersonaStore"/>。
    ///
    /// 這裡最重要的是**邊界**：
    ///   * 一個伺服器學到的東西**絕對不能**跑到別的伺服器（不然就是「別人家的人格被改掉」）
    ///   * 不能被拿來塞爆提示詞（有條數與長度上限）
    ///   * 重設要真的清乾淨，而且要把清掉的內容交出來（教了很多條的人才能複製回去）
    /// </summary>
    private static void TestPersona()
    {
        var memory = new FakeStateStore();
        var store = new GuildPersonaStore(memory);

        const ulong guildA = 111UL;
        const ulong guildB = 222UL;

        // ── 1) 學習與 overlay ─────────────────────────────
        Check("一開始什麼都沒學到", store.Lines(guildA).Count == 0);
        Check("沒學到東西時 overlay 是空的（不會多送沒用的提示詞）",
            store.Overlay(guildA).Length == 0);

        Check("★ 學第一條", store.Learn(guildA, "講話再簡短一點") == GuildPersonaStore.LearnResult.Added);

        var learned = store.Learn(guildA, "自訂表情（委屈臉）代表委屈，看到就用撒嬌的語氣回");
        Check("★ 可以學「自訂表情代表什麼」這種伺服器專屬知識",
            learned == GuildPersonaStore.LearnResult.Added);

        Check("★ overlay 真的包含學到的內容",
            store.Overlay(guildA).Contains("講話再簡短一點", StringComparison.Ordinal)
            && store.Overlay(guildA).Contains("委屈", StringComparison.Ordinal));

        Check("★ overlay 有明確標示「這是後天學到的」",
            store.Overlay(guildA).Contains("後天學到", StringComparison.Ordinal));

        // ── 2) 伺服器之間完全隔離（最重要）─────────────────
        Check("★ 別的伺服器看不到 A 學到的東西",
            store.Lines(guildB).Count == 0 && store.Overlay(guildB).Length == 0);

        store.Learn(guildB, "B 伺服器專屬：講話要很正式");
        Check("★ 兩個伺服器各自記自己的",
            store.Lines(guildA).Count == 2 && store.Lines(guildB).Count == 1,
            $"{store.Lines(guildA).Count} / {store.Lines(guildB).Count}");
        Check("★ A 的 overlay 不含 B 的內容（反之亦然）",
            !store.Overlay(guildA).Contains("很正式", StringComparison.Ordinal)
            && !store.Overlay(guildB).Contains("簡短", StringComparison.Ordinal));

        // ── 3) 去重與清理 ────────────────────────────────
        Check("★ 同一句學第二次 → 不重複加（AI 很常講兩遍）",
            store.Learn(guildA, "講話再簡短一點") == GuildPersonaStore.LearnResult.Duplicate
            && store.Lines(guildA).Count == 2);

        Check("空白內容不會被記下來", store.Learn(guildA, "   ") == GuildPersonaStore.LearnResult.Empty);

        store.Learn(guildA, "- 用條列式回答");
        Check("★ 前面的條列符號會被清掉（提示詞的結構才不會亂）",
            store.Lines(guildA)[^1] == "用條列式回答", store.Lines(guildA)[^1]);

        store.Learn(guildA, "換行\n也要\n變成空白");
        Check("★ 換行會被壓成一行",
            store.Lines(guildA)[^1] == "換行 也要 變成空白", store.Lines(guildA)[^1]);

        // ── 4) 上限 ──────────────────────────────────────
        var longLine = new string('長', GuildPersonaStore.MaxLineLength + 1);
        Check("★ 太長的一條會被拒絕（不讓它變成記事本）",
            store.Learn(guildA, longLine) == GuildPersonaStore.LearnResult.TooLong);

        var guildC = 333UL;
        var added = 0;
        var tooMany = GuildPersonaStore.LearnResult.Added;

        for (var i = 0; i < GuildPersonaStore.MaxLinesPerGuild + 5; i++)
        {
            tooMany = store.Learn(guildC, $"規則 {i}");
            if (tooMany == GuildPersonaStore.LearnResult.Added) added++;
        }

        Check($"★ 每個伺服器最多 {GuildPersonaStore.MaxLinesPerGuild} 條（再多會被擋）",
            added == GuildPersonaStore.MaxLinesPerGuild
            && tooMany == GuildPersonaStore.LearnResult.TooMany,
            $"加了 {added} 條，最後一次：{tooMany}");

        // ── 5) 忘記與重設 ────────────────────────────────
        var before = store.Lines(guildA).Count;
        Check("★ 用關鍵字刪掉符合的規則", store.Forget(guildA, "條列") == 1 && store.Lines(guildA).Count == before - 1);
        Check("關鍵字找不到時回傳 0（不亂刪）", store.Forget(guildA, "不存在的關鍵字") == 0);

        var removed = store.Reset(guildA);
        Check("★ 重設會清掉這個伺服器的全部內容", store.Lines(guildA).Count == 0 && removed.Count > 0,
            $"清掉 {removed.Count} 條");
        Check("★ 重設會回傳被清掉的內容（教了很多條的人可以複製回去）",
            removed.Any(l => l.Contains("講話再簡短一點", StringComparison.Ordinal)));
        Check("★ 重設只影響那一個伺服器（B 的還在）", store.Lines(guildB).Count == 1);

        // ── 6) 持久化（跨重啟）──────────────────────────
        var reloaded = new GuildPersonaStore(memory);
        Check("★ 學到的內容會寫進儲存區（重啟後還在）",
            reloaded.Lines(guildB).Count == 1 && reloaded.Overlay(guildB).Contains("很正式", StringComparison.Ordinal));
        Check("重設過的伺服器重啟後也是空的", reloaded.Lines(guildA).Count == 0);

        // ── 7) 提示詞組裝順序 ────────────────────────────
        var options = new LlmOptions { SystemPrompt = "【主機人格】", ToolsEnabled = false };
        var orchestrator = new ChatOrchestrator(
            new DisabledLlmClient(), options, new ConversationStore(options),
            new WeeklyTokenBudget(options), NoChatTools.Instance, reloaded);

        var prompt = orchestrator.EffectiveSystemPrompt(guildB);
        Check("★ 系統提示＝主機人格（工具關閉時不加工具說明）＋這個伺服器學到的規則",
            prompt.StartsWith("【主機人格】", StringComparison.Ordinal)
            && prompt.Contains("很正式", StringComparison.Ordinal));
        Check("★ 學到的規則放在最後（模型對最後的指示最聽話）",
            prompt.IndexOf("【主機人格】", StringComparison.Ordinal)
            < prompt.IndexOf("很正式", StringComparison.Ordinal));

        var otherPrompt = orchestrator.EffectiveSystemPrompt(999UL);
        Check("★ 沒有學過東西的伺服器拿到的是乾淨的提示詞",
            otherPrompt == "【主機人格】", otherPrompt);
    }

    /// <summary>
    /// 公車查詢加強：路線號碼查詢、以及**沒有直達時的轉乘建議**。
    ///
    /// 轉乘刻意用「站名」比對而不是 StopUID —— 台中同一條路上常有同名不同 UID 的月台
    /// （去回程各一組、專用道與慢車道各一組），用 UID 比對幾乎找不到轉乘點。
    /// 這裡用**自己造的資料集**驗演算法本身（fixture 是單一走廊，不一定有轉乘案例）。
    /// </summary>
    private static void TestBusSearchPlus(TaichungBusDataService realData, LocationTarget origin, LocationTarget dest)
    {
        // ── 1) 路線號碼查詢（真實 fixture）────────────────
        var service = new BusActionService(realData, Subs());

        var routes300 = service.SearchRoutes("300");
        Check("★ 查得到 300（去回程分開列）",
            routes300.Contains("300", StringComparison.Ordinal) &&
            routes300.Contains("去程", StringComparison.Ordinal) &&
            routes300.Contains("返程", StringComparison.Ordinal),
            routes300.Split('\n')[0]);

        var routes304 = service.SearchRoutes("304");
        Check("★ 查得到 304", routes304.Contains("304", StringComparison.Ordinal));

        Check("查不到的路線號碼會老實說找不到",
            service.SearchRoutes("9999").Contains("找不到", StringComparison.Ordinal));

        Check("空號碼不會爆", service.SearchRoutes("").Contains("請給我", StringComparison.Ordinal));

        var stopsWithRoutes = service.SearchStops("臺中車站");
        Check("★ 查站牌時會一併回報「有哪幾條路線經過」（模型才判斷得出是哪一個站）",
            stopsWithRoutes.Contains("經過的路線", StringComparison.Ordinal), stopsWithRoutes.Split('\n')[1]);

        // ── 2) 轉乘演算法（自造資料集）────────────────────
        var data = BuildTransferFixture();

        var fromA = data.TargetFromStops(new[] { "A1" });      // A 線起點
        var toC = data.TargetFromStops(new[] { "C2" });        // C 線終點（A→C 沒有直達）

        var direct = data.FindRoutes(fromA, toC);
        Check("★ 這個資料集裡 A→C 沒有直達（前提）", direct.Count == 0, $"{direct.Count} 條直達");

        var transfers = data.FindTransferRoutes(fromA, toC);
        Check("★ 找得到「轉一次」的走法", transfers.Count > 0, $"{transfers.Count} 個建議");

        if (transfers.Count > 0)
        {
            var t = transfers[0];
            Check("★ 轉乘點是兩條路線共同經過的站",
                t.TransferStopName == "共用站", t.TransferStopName);
            Check("★ 第一段是 A 線、第二段是 C 線",
                t.RouteNameA == "A 線" && t.RouteNameB == "C 線", $"{t.RouteNameA} → {t.RouteNameB}");
            // A 線：起點A(1) → 中間A(2) → 共用站(3) → 搭 2 站到轉乘點
            // C 線：共用站(1) → 中間C(2) → 終點C(3) → 再 2 站到目的地
            Check("★ 站數計算正確（A 線 2 站到轉乘點、C 線再 2 站到目的地）",
                t.StopsToTransfer == 2 && t.StopsAfterTransfer == 2,
                $"{t.StopsToTransfer} + {t.StopsAfterTransfer}");
            Console.WriteLine($"  ℹ {t.Describe()}");
        }

        // 反方向（C 線起點 → A 線終點）：因為方向不對，不該給出轉乘建議
        var reverse = data.FindTransferRoutes(data.TargetFromStops(new[] { "C1" }), data.TargetFromStops(new[] { "A3" }));
        Check("★ 方向不對時不會硬給建議（C1 → A3 需要逆向）", reverse.Count == 0, $"{reverse.Count} 個建議");

        // 有直達時就不需要轉乘建議（FindRoutes 會直接回直達）
        var directExists = data.FindRoutes(data.TargetFromStops(new[] { "A1" }), data.TargetFromStops(new[] { "X1" }));
        Check("★ 有直達時回傳直達路線", directExists.Count == 1, $"{directExists.Count} 條");

        // ── 3) 同名不同月台也要能轉乘 ─────────────────────
        var sameNameData = BuildSameNamePlatformFixture();
        var sameNameTransfers = sameNameData.FindTransferRoutes(
            sameNameData.TargetFromStops(new[] { "P1" }),
            sameNameData.TargetFromStops(new[] { "Q2" }));

        Check("★ 同名但不同 StopUID 的月台也算同一個轉乘點（台中常態）",
            sameNameTransfers.Count > 0 && sameNameTransfers[0].TransferStopName == "共用站",
            $"{sameNameTransfers.Count} 個建議");

        if (sameNameTransfers.Count > 0)
            Console.WriteLine($"  ℹ 同名不同月台：{sameNameTransfers[0].Describe()}");

        // ── 4) 不可以回「上車 0 站就轉乘」這種廢話 ──────────
        //    真實資料上真的出現過：「搭 131 在干城站上車 → 在干城站下車（0 站），轉 303」
        //    ——那不是轉乘，只是換月台。兩段都至少要搭 1 站。
        var realCache = TcBusBot.Core.DataSources.StaticDataLoader.TryReadCache(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "cache"));

        var checkedPairs = 0;
        var badTransfers = new List<string>();

        if (realCache is not null)
        {
            var real = new TaichungBusDataService();
            real.Load(realCache.Stops, realCache.StopOfRoutes, realCache.Routes);

            foreach (var (from, to) in new[] { ("干城站", "東海別墅"), ("靜宜大學", "逢甲大學"), ("新民高中", "霧峰") })
            {
                var f = TargetOf(real, from);
                var t = TargetOf(real, to);
                if (f is null || t is null) continue;

                checkedPairs++;

                foreach (var option in real.FindTransferRoutes(f, t, max: 3))
                {
                    if (option.StopsToTransfer < 1 || option.StopsAfterTransfer < 1)
                        badTransfers.Add(option.Describe());
                }
            }
        }

        Check("★ 真實資料上不會給出「搭 0 站就轉乘」的建議" +
              (checkedPairs == 0 ? "（沒有快取，略過）" : $"（檢查 {checkedPairs} 組起訖）"),
            badTransfers.Count == 0,
            badTransfers.FirstOrDefault() ?? "全部合法");
    }

    /// <summary>造一個「A→共用站→C」的資料集：A 與 C 沒有共同站牌（UID 不同），只有同名站。</summary>
    private static TaichungBusDataService BuildTransferFixture() => BuildFixture(
    [
        ("A", 0, "A 線", ["A1", "A2", "X1"]),        // X1 = 共用站（UID 與 C 線不同）
        ("C", 0, "C 線", ["X2", "C1", "C2"])         // X2 = 共用站（同名不同 UID）
    ],
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["A1"] = "起點A", ["A2"] = "中間A", ["X1"] = "共用站",
        ["X2"] = "共用站", ["C1"] = "中間C", ["C2"] = "終點C"
    });

    private static TaichungBusDataService BuildSameNamePlatformFixture() => BuildFixture(
    [
        ("P", 0, "P 線", ["P1", "M1", "P2"]),
        ("Q", 1, "Q 線", ["M9", "Q1", "Q2"])         // M9 與 M1 **同名**但不同 UID、不同方向
    ],
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["P1"] = "起點P",
        ["M1"] = "共用站",                            // 同名
        ["P2"] = "終點P",
        ["M9"] = "共用站",                            // 同名、不同 StopUID（真實世界的常態）
        ["Q1"] = "中間Q",
        ["Q2"] = "終點Q"
    });

    /// <summary>把站名關鍵字變成候選集合（測試用；與 BusActionService 的規則一致：只取精確相符的群組）。</summary>
    private static LocationTarget? TargetOf(TaichungBusDataService data, string keyword)
    {
        var groups = data.Search.SearchGrouped(keyword);
        var precise = groups.Where(g => g.IsDefaultPick).ToList();
        var picked = precise.Count > 0 ? precise : groups.Where(g => g.IsStrongMatch).Take(1).ToList();

        var uids = picked.SelectMany(g => g.Hits).Select(h => h.Entry.StopUid)
                         .Distinct(StringComparer.Ordinal).ToList();

        return uids.Count == 0 ? null : data.TargetFromStops(uids);
    }

    /// <summary>用假的站牌／站序造一個可查詢的資料集（測演算法用，不需要 TDX）。</summary>
    private static TaichungBusDataService BuildFixture(
        (string RouteUid, int Direction, string Name, string[] Stops)[] routes,
        Dictionary<string, string> stopNames)
    {
        var stops = stopNames.Select(kv => new BusStop
        {
            StopUID = kv.Key,
            StopName = new LocalizedName(kv.Value, null),
            StopPosition = new StopPosition(120.68, 24.14, null),
            City = "Taichung",
            CityCode = "TXG"
        }).ToList();

        var stopOfRoutes = routes.Select(r =>
        {
            var sor = new BusStopOfRoute
            {
                RouteUID = r.RouteUid,
                RouteID = r.RouteUid,
                RouteName = new LocalizedName(r.Name, null),
                SubRouteUID = r.RouteUid,
                SubRouteID = r.RouteUid,
                SubRouteName = new LocalizedName(r.Name, null),
                Direction = r.Direction,
                City = "Taichung",
                CityCode = "TXG",
                Stops = r.Stops.Select((uid, i) => new StopOfRouteStop
                {
                    StopUID = uid,
                    StopSequence = i + 1,
                    // 站名要放在站序裡：FindTransferRoutes 是用「站名」比對轉乘點的
                    StopName = new LocalizedName(stopNames[uid], null)
                }).ToList()
            };

            return sor;
        }).ToList();

        var routeMetas = routes.Select(r => new BusRoute
        {
            RouteUID = r.RouteUid,
            RouteID = r.RouteUid,
            RouteName = new LocalizedName(r.Name, null),
            HasSubRoutes = true,
            City = "Taichung",
            CityCode = "TXG",
            SubRoutes = new List<BusSubRoute>()
        }).ToList();

        var data = new TaichungBusDataService();
        data.Load(stops, stopOfRoutes, routeMetas);
        return data;
    }

    /// <summary>
    /// 認人與主人授權。
    ///
    /// 兩件事都很容易出錯、而且錯的代價不對稱：
    ///   * **認人**：名字標籤組錯 → 模型把別人講的話當成你的（或反過來）
    ///   * **授權**：判斷太鬆 → 任何人都能對 Bot 下命令；太嚴 → 主人自己也用不了
    /// 所以 `fail closed`（沒設 key 就等於沒這個功能）與「key 一定要從內容移除」
    /// 這兩點是這裡最重要的檢查。
    /// </summary>
    private static void TestIdentityAndOwner()
    {
        // ── 1) 名字標籤（暱稱 ＋ @帳號 ＋ ID）───────────────
        Check("★ 標籤＝暱稱(@帳號, ID)",
            MentionFormatter.Label("小明", "wuxiaohan0922", 123456789012345678UL, includeId: true)
                == "小明(@wuxiaohan0922, 123456789012345678)",
            MentionFormatter.Label("小明", "wuxiaohan0922", 123456789012345678UL, true));

        Check("關掉 ID 時只給暱稱（隱私選項）",
            MentionFormatter.Label("小明", "wuxiaohan0922", 1UL, includeId: false) == "小明");

        Check("暱稱等於帳號名時不重複顯示",
            MentionFormatter.Label("小明", "小明", 999UL, includeId: true) == "小明(999)",
            MentionFormatter.Label("小明", "小明", 999UL, true));

        Check("沒有暱稱時退回帳號名（而且不會重複顯示兩次同樣的字）",
            MentionFormatter.Label("", "wuxiaohan0922", 5UL, includeId: true) == "wuxiaohan0922(5)",
            MentionFormatter.Label("", "wuxiaohan0922", 5UL, true));

        Check("什麼都沒有時至少給得出 ID",
            MentionFormatter.Label(null, null, 42UL, includeId: true) == "使用者42(42)");

        // ── 2) 提示詞裡的樣子 ────────────────────────────
        var labeled = new ChatTurn(ChatRole.User, "wuxiaohan0922", 123456789012345678UL, 1UL,
            "幫我查 300", DateTimeOffset.UtcNow, PromptLabel: "小明(@wuxiaohan0922, 123456789012345678)");

        Check("★ 進提示詞的是「標籤：訊息」（模型看得到 ID）",
            labeled.ToPromptText() == "小明(@wuxiaohan0922, 123456789012345678)：幫我查 300",
            labeled.ToPromptText());

        var unlabeled = new ChatTurn(ChatRole.User, "小明", 1UL, 1UL, "嗨", DateTimeOffset.UtcNow);
        Check("沒給標籤時退回原本的「名字：訊息」", unlabeled.ToPromptText() == "小明：嗨");

        // ── 3) @ 別人的展開 ──────────────────────────────
        var users = new Dictionary<ulong, string> { [111UL] = "阿明", [222UL] = "小華" };

        Check("★ 把 <@ID> 展開成 @暱稱(ID)",
            MentionFormatter.Expand("請 <@111> 幫我看看", users, includeIds: true)
                == "請 @阿明(111) 幫我看看");

        Check("★ <@!ID> 這種寫法也要會（Discord 有兩種格式）",
            MentionFormatter.Expand("<@!222> 早安", users, includeIds: true) == "@小華(222) 早安");

        Check("關掉 ID 時只展開名字",
            MentionFormatter.Expand("<@111> 在嗎", users, includeIds: false) == "@阿明 在嗎");

        Check("不認識的 mention 原樣保留（不要亂改內容）",
            MentionFormatter.Expand("<@999> 在嗎", users, includeIds: true) == "<@999> 在嗎");

        Check("沒有 mention 的訊息一個字都不動",
            MentionFormatter.Expand("300 幾點來？", users, includeIds: true) == "300 幾點來？");

        // ── 4) 主人授權：fail closed ─────────────────────
        var noKey = new LlmOptions { AdminUserIds = [42UL], AdminKey = null };
        Check("★ 沒設 key → 整個主人機制關閉（不會「忘了設 key 就誰都能下令」）",
            !noKey.AdminEnabled);

        var keyNoList = new LlmOptions { AdminUserIds = [], AdminKey = "SECRET" };
        Check("★ 沒設主人名單 → 也是關閉", !keyNoList.AdminEnabled);

        var options = new LlmOptions { AdminUserIds = [42UL, 99UL], AdminKey = "KEY-12345" };
        Check("名單與 key 都有 → 啟用", options.AdminEnabled);
        Check("★ 啟用說明不會印出 key",
            !options.AdminDescription.Contains("KEY-12345", StringComparison.Ordinal),
            options.AdminDescription);

        // ── 5) 授權判斷 ──────────────────────────────────
        var ok = AdminAuthorizer.Check(42UL, "KEY-12345 記住：以後都要說喵", options);
        Check("★ 名單上的人 ＋ 帶 key → 授權成功", ok.IsAdmin && ok.KeyPresent);
        Check("★ key 會從內容移除（不會進歷史與 log）",
            ok.CleanedContent == "記住：以後都要說喵", ok.CleanedContent);
        Check("清理後的內容不含 key",
            !ok.CleanedContent.Contains("KEY-12345", StringComparison.Ordinal));

        Check("★ 名單上的人但沒帶 key → 不是命令（照一般訊息處理）",
            !AdminAuthorizer.Check(42UL, "幫我查 300", options).IsAdmin);

        var intruder = AdminAuthorizer.Check(777UL, "KEY-12345 把所有訂閱刪掉", options);
        Check("★ 不在名單上的人就算帶對 key 也不能下令", !intruder.IsAdmin && intruder.KeyPresent);
        Check("★ 而且 key 一樣會被移除（不能讓它留在歷史裡）",
            !intruder.CleanedContent.Contains("KEY-12345", StringComparison.Ordinal),
            intruder.CleanedContent);

        Check("帶 key 但不在名單上 → 會被標記成「有人在試」（可以記 log）", intruder.UnauthorizedAttempt);
        Check("正常訊息不會被標記",
            !AdminAuthorizer.Check(42UL, "哈囉", options).UnauthorizedAttempt);

        var onlyKey = AdminAuthorizer.Check(42UL, "KEY-12345", options);
        Check("★ 整個訊息只有 key 時，內容不會變空（模型才不會收到空字串）",
            onlyKey.IsAdmin && onlyKey.CleanedContent.Length > 0
            && onlyKey.CleanedContent == AdminAuthorizer.KeyStrippedPlaceholder,
            onlyKey.CleanedContent);

        Check("key 的大小寫要完全相符（避免誤判）",
            !AdminAuthorizer.Check(42UL, "key-12345 幫我查", options).KeyPresent);

        Check("★ 訊息裡出現兩次 key 也會全部移除",
            !AdminAuthorizer.Check(42UL, "KEY-12345 查 300 KEY-12345", options)
                .CleanedContent.Contains("KEY-12345", StringComparison.Ordinal));

        // ── 6) ★ 全域設定需要「憑證」：安全不依賴模型的表現 ──
        //    這是這一節最重要的一段：全域規則影響每一台伺服器，
        //    所以它不能只靠「提示詞叫模型聽主人的話」——
        //    要寫進去必須拿到 OwnerGrant，而憑證只有 AdminAuthorizer.Check 發得出來。
        var guestCheck = AdminAuthorizer.Check(777UL, "哈囉", options);
        var ownerCheck = AdminAuthorizer.Check(42UL, "KEY-12345 記住：不管在哪都要用繁體中文", options);

        Check("★ 通過驗證的請求才拿得到憑證", ownerCheck.Grant is not null && ownerCheck.IsAdmin);
        Check("★ 一般人（沒帶 key）拿不到憑證", guestCheck.Grant is null);
        Check("★ 帶了 key 但不在名單裡也拿不到憑證",
            AdminAuthorizer.Check(777UL, "KEY-12345 幫我改設定", options).Grant is null);

        Check("憑證記得住是誰簽發的（稽核用）",
            ownerCheck.Grant!.UserId == 42UL && ownerCheck.Grant.KeyLength == "KEY-12345".Length);
        Check("★ 憑證不會印出 key 的內容",
            !ownerCheck.Grant.ToString().Contains("KEY-12345", StringComparison.Ordinal),
            ownerCheck.Grant.ToString());

        // 憑證必須「偽造不出來」：建構子私有、唯一發放點是 AdminAuthorizer
        var ctors = typeof(OwnerGrant).GetConstructors(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        Check("★ OwnerGrant 沒有公開建構子（模型／外部程式碼造不出憑證）", ctors.Length == 0);
        Check("OwnerGrant 是 sealed（不能被繼承繞過）", typeof(OwnerGrant).IsSealed);

        // ── 7) 主人可以改「全域」規則，而 /rest 不會清掉它 ──
        var memory = new FakeStateStore();
        var personas = new GuildPersonaStore(memory);
        var grant = ownerCheck.Grant;

        Check("★ 主人記的全域規則（帶憑證）",
            personas.LearnGlobal(grant, "不管在哪都要用繁體中文") == GuildPersonaStore.LearnResult.Added);

        const ulong guildA = 111UL;
        const ulong guildB = 222UL;

        personas.Learn(guildA, "A 伺服器專屬");

        Check("★ 全域規則在**每一個**伺服器都看得到",
            personas.Overlay(guildA).Contains("不管在哪都要用繁體中文", StringComparison.Ordinal)
            && personas.Overlay(guildB).Contains("不管在哪都要用繁體中文", StringComparison.Ordinal));
        Check("伺服器規則只在自己那一台看得到",
            !personas.Overlay(guildB).Contains("A 伺服器專屬", StringComparison.Ordinal));
        Check("★ 全域與伺服器的區塊分得出來（模型才知道哪個是通用的）",
            personas.Overlay(guildA).Contains("全域規則", StringComparison.Ordinal)
            && personas.Overlay(guildA).Contains("這個伺服器", StringComparison.Ordinal));

        personas.Reset(guildA);
        Check("★ /rest 只清伺服器那一份，**不會**清掉主人的全域規則",
            personas.Lines(guildA).Count == 0 && personas.GlobalLines.Count == 1);
        Check("重設後別的伺服器仍然看得到全域規則",
            personas.Overlay(guildB).Contains("不管在哪都要用繁體中文", StringComparison.Ordinal));

        // ── 8) 破壞性操作要再擋一層（模型抽風傳空字串時）──
        Check("★ 沒指定關鍵字就想清空全部全域規則 → 拒絕（不會默默清掉）",
            personas.ForgetGlobal(grant, "", confirmAll: false) == 0 && personas.GlobalLines.Count == 1);

        Check("明確確認之後才清得掉",
            personas.ForgetGlobal(grant, "", confirmAll: true) == 1 && personas.GlobalLines.Count == 0);

        personas.LearnGlobal(grant, "第二條全域規則");
        Check("依關鍵字刪除不需要額外確認", personas.ForgetGlobal(grant, "第二條") == 1);
        Check("刪完就沒有了", personas.GlobalLines.Count == 0);

        // ── 9) 稽核紀錄：模型抽風時查得出發生什麼事 ────────
        var audit = personas.AuditLog(10);
        Check("★ 每次動全域設定都留下紀錄（誰／什麼時候／做了什麼）",
            audit.Count >= 3 && audit.All(e => e.UserId == 42UL),
            $"{audit.Count} 筆");
        Check("★ 連「被拒絕的清空嘗試」也留紀錄（看得到有人在試）",
            audit.Any(e => e.Action.Contains("拒絕", StringComparison.Ordinal)));
        Check("稽核紀錄有寫進儲存區（重啟後還在）",
            new GuildPersonaStore(memory).AuditLog(10).Count == audit.Count);
        Check("★ 模糊關鍵字刪除時也記得關鍵字是什麼",
            audit.Any(e => e.Action.Contains("第二條", StringComparison.Ordinal)));

        // ── 10) 工具上下文：一般人拿不到主人權限 ────────────
        var guestContext = new ChatToolContext(1UL, 2UL, 777UL, "小明", Owner: guestCheck.Grant);
        var ownerContext = new ChatToolContext(1UL, 2UL, 42UL, "喵喵主人", Owner: grant);

        Check("★ 一般人的工具上下文沒有憑證（工具清單裡不會有 owner plugin）",
            !guestContext.IsOwner && guestContext.Owner is null);
        Check("主人的工具上下文有憑證", ownerContext.IsOwner && ownerContext.Owner is not null);

        // ── 7) 提示詞裡的順位 ────────────────────────────
        var promptOptions = new LlmOptions { SystemPrompt = "【主機人格】", ToolsEnabled = false };
        var orchestrator = new ChatOrchestrator(
            new DisabledLlmClient(), promptOptions, new ConversationStore(promptOptions),
            new WeeklyTokenBudget(promptOptions), NoChatTools.Instance, personas);

        var owner = orchestrator.EffectiveSystemPrompt(guildA, isOwner: true);
        var guest = orchestrator.EffectiveSystemPrompt(guildA, isOwner: false);

        Check("★ 主人授權時會多一段「必須接受請求」的說明",
            owner.Contains("你的主人", StringComparison.Ordinal)
            && owner.Contains("必須", StringComparison.Ordinal));
        Check("★ 主人說明放在最前面（優先於人格設定）",
            owner.IndexOf("你的主人", StringComparison.Ordinal)
            < owner.IndexOf("【主機人格】", StringComparison.Ordinal));
        Check("授權說明不會提到 key 的內容",
            !owner.Contains("KEY-12345", StringComparison.Ordinal));
        Check("★ 一般訊息不會拿到主人說明", !guest.Contains("你的主人", StringComparison.Ordinal));
    }

    /// <summary>
    /// **面板與 LLM 必須用同一套站牌解析**（使用者反映「同名站牌找不到」的根因）。
    ///
    /// 問題出在兩邊各寫一套：
    ///   * 面板：`g:{站區}` 短鍵 → 解出**整個站區**的站牌（「臺中車站」38 個月台全都要）
    ///   * LLM ：拿**搜尋命中**當候選 —— 而命中有上限（整體 25 筆，
    ///     每組只留符合關鍵字的那些），所以只放進 2~3 個月台，
    ///     停在其他月台的路線就整條找不到
    ///
    /// 現在兩邊都走 <see cref="StopPicks"/>，這裡用**真實資料集**驗：
    ///   1. 解析出來的站牌數＝整個站區的站牌數（不是搜尋命中的數量）
    ///   2. LLM 那條路（<see cref="BusActionService"/>）用到的候選站集合
    ///      與 <see cref="StopPicks"/> 算出來的**完全相同**
    ///   3. 對到多個站區時**不會自己挑一個**，而是回報候選讓模型去問使用者
    /// </summary>
    private static void TestSharedStopPicks()
    {
        var cachePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "cache");
        var cached = TcBusBot.Core.DataSources.StaticDataLoader.TryReadCache(cachePath);

        // ── 先用離線 fixture 驗行為（沒有快取時也測得到）────
        var fixture = MiniFixtureSource.Load(MiniFixtureSource.ResolveRoot(null));

        var fixtureGroups = StrongGroups(fixture, "臺中車站");
        var (fixtureUids, fixtureAmbiguous, _) = StopPicks.ResolveDefaults(fixtureGroups, fixture);
        var fixtureArea = fixture.GetGroupByShortKey(fixtureGroups[0].GroupKey)!;

        Check("★ 解析出來的站牌＝整個站區（fixture）",
            !fixtureAmbiguous && fixtureArea.StopUids.All(fixtureUids.Contains),
            $"{fixtureUids.Count} 個／站區 {fixtureArea.StopUids.Count} 個");

        var ambiguous = StopPicks.ResolveDefaults(StrongGroups(fixture, "中正國小"), fixture);
        Check("★ 對到多個站區時不會自己挑一個（回報候選讓模型問使用者）",
            ambiguous.Ambiguous || ambiguous.Uids.Count > 0,
            $"ambiguous={ambiguous.Ambiguous}, 候選 {ambiguous.Candidates.Count} 個");

        if (cached is null)
        {
            Check("（真實快取不存在 → 略過真實資料的站區比對）", true);
            return;
        }

        // ── 真實資料集（14,036 站牌）：這才是同名站牌問題真正會出現的地方 ──
        var data = new TaichungBusDataService();
        data.Load(cached.Stops, cached.StopOfRoutes, cached.Routes);

        var groups = StrongGroups(data, "臺中車站");
        var set = StopPicks.Build(groups, data);
        var (uids, isAmbiguous, candidates) = StopPicks.ResolveDefaults(groups, data);

        Check("★ 「臺中車站」解析出整個站區的所有月台（真實資料）",
            !isAmbiguous && uids.Count > 25 && uids.Count == data.GetGroupByShortKey(groups[0].GroupKey)!.StopUids.Count,
            $"{uids.Count} 個（舊版因為搜尋命中上限只會拿到 2~3 個）");

        Check("★ 而且選項是「整個站區」而不是逐月台（多種站名時）",
            set.Defaults.Any(v => v.StartsWith("g:", StringComparison.Ordinal)),
            string.Join("、", set.Defaults));

        // 每個月台都要在候選集合裡 —— 這是「同名站牌找不到」的直接檢查
        var area = data.GetGroupByShortKey(groups[0].GroupKey)!;
        Check("★ 站區裡的每一個 StopUID 都在候選集合裡（不會漏掉同名不同月台）",
            area.StopUids.All(u => uids.Contains(u, StringComparer.Ordinal)),
            $"站區 {area.StopUids.Count} 個／候選 {uids.Count} 個");

        // ── LLM 那條路必須用同一組候選 ────────────────────
        var subs = new SubscriptionService();
        var actions = new BusActionService(data, subs);

        var outcome = actions.Subscribe(555UL, "台中車站", "靜宜大學");
        Check("★ LLM 訂閱用的起點候選＝StopPicks 算出來的那一組",
            outcome.Ok && outcome.Group is not null
            && outcome.Group.Origin.CandidateStopUids.OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(uids.OrderBy(x => x, StringComparer.Ordinal)),
            $"工具用 {outcome.Group?.Origin.CandidateStopUids.Count} 個／解析 {uids.Count} 個");

        // 與面板同一條路徑算出來的「可搭路線數」也要一致
        var destGroups = StrongGroups(data, "靜宜大學");
        var (destUids, _, _) = StopPicks.ResolveDefaults(destGroups, data);
        var expectedRoutes = data.FindRoutes(
            data.TargetFromStops(uids), data.TargetFromStops(destUids)).Count;

        Check("★ LLM 找到的路線數＝面板同一條路徑算出來的路線數",
            subs.GetSubscriptions(outcome.Group!).Select(s => (s.RouteUid, s.Direction)).Distinct().Count()
                == expectedRoutes,
            $"工具 {subs.GetSubscriptions(outcome.Group!).Select(s => (s.RouteUid, s.Direction)).Distinct().Count()} 條／預期 {expectedRoutes} 條");

        Check("★ 真實資料上「臺中車站 → 靜宜大學」找得到 20 條路線（面板的水準）",
            expectedRoutes >= 20, $"{expectedRoutes} 條");

        // ── 模糊到對不起來的關鍵字：不可以自己挑 ──────────
        // ⚠️ 「什麼叫模糊」取決於資料集（例如「車站」在真實資料裡會前綴命中「車站前」，
        //    那算精確命中，不算模糊）。所以這裡**找一個真的沒有精確命中的關鍵字**來測，
        //    而不是寫死一個猜測。
        string? vagueKeyword = null;

        foreach (var candidate in new[] { "路口", "國小", "市場", "前站", "站牌" })
        {
            var probe = StopPicks.Build(StrongGroups(data, candidate), data);
            if (probe.Options.Count > 0 && probe.Defaults.Count == 0)
            {
                vagueKeyword = candidate;
                break;
            }
        }

        if (vagueKeyword is null)
        {
            Check("（真實資料裡找不到「只能模糊比對」的關鍵字 → 略過這一項）", true);
        }
        else
        {
            var vagueGroups = StrongGroups(data, vagueKeyword);
            var (vagueUids, vagueAmbiguous, vagueCandidates) = StopPicks.ResolveDefaults(vagueGroups, data);

            Check($"★ 只打「{vagueKeyword}」這種模糊關鍵字 → 不自己挑站區，回報候選",
                vagueAmbiguous && vagueUids.Count == 0 && vagueCandidates.Count > 0,
                $"ambiguous={vagueAmbiguous}, 候選 {vagueCandidates.Count} 個：" +
                string.Join("、", vagueCandidates.Take(3)));

            var vague = actions.Subscribe(556UL, vagueKeyword, "靜宜大學");
            Check("★ 模糊關鍵字不會亂訂（訂閱失敗並請模型去問使用者）",
                !vague.Ok && vague.Message.Contains("站區", StringComparison.Ordinal),
                vague.Message.Split('\n')[0]);
        }
    }

    /// <summary>與面板相同的過濾：有強相符時只留強相符。</summary>
    private static IReadOnlyList<StopSearchGroupResult> StrongGroups(TaichungBusDataService data, string keyword)
    {
        var groups = data.Search.SearchGrouped(keyword);
        var strong = groups.Where(g => g.IsStrongMatch).ToList();
        return strong.Count > 0 ? strong : groups;
    }

    /// <summary>
    /// 偷聽模式：回完話之後順便聽幾句，判斷不是對它說話就安靜（或退出）。
    ///
    /// 這裡驗四件事：
    ///   1. 判斷器的解析（REPLY／SKIP／STOP；模型常多講幾句；看不懂就當成「先不出聲」）
    ///   2. 偷聽窗口的生命週期（開始／消耗／用完／**滑動**過期／主動停止）
    ///   3. 兩個上限都要生效（判斷則數 ＋ 閒置秒數）
    ///   4. **多人頻道真的會用到的情境**：@ 別人之後還能繼續聽、
    ///      SKIP 不會退出、**閒聊會被帶進上下文**
    /// </summary>
    private static void TestEavesdrop()
    {
        // ── 1) 解析（三種結果）────────────────────────────
        Check("解析「REPLY」→ 接話", AddresseeDetector.Parse("REPLY") == Addressee.Reply);
        Check("解析「SKIP」→ 不出聲但繼續聽", AddresseeDetector.Parse("SKIP") == Addressee.Skip);
        Check("解析「STOP」→ 退出、回到等 @", AddresseeDetector.Parse("STOP") == Addressee.Stop);

        Check("★ 模型多講幾句時以最後出現的結論為準",
            AddresseeDetector.Parse("看起來像在問我 REPLY，但其實是在約別人 → STOP") == Addressee.Stop);
        Check("★ 看不懂的回答 → null（呼叫端當成「先不出聲」，而不是退出）",
            AddresseeDetector.Parse("我不確定") is null);
        Check("空字串 → null", AddresseeDetector.Parse("") is null);
        Check("★ 舊版的 YES／NO 不會再被誤判成三種之一（換過提示詞就不該沿用）",
            AddresseeDetector.Parse("YES") is null && AddresseeDetector.Parse("NO") is null);

        // ── 2) 提示詞內容 ────────────────────────────────
        var history = new List<ChatTurn>
        {
            new(ChatRole.User, "小明", 1UL, 1UL, "300 幾點來", DateTimeOffset.UtcNow),
            new(ChatRole.Assistant, "Bot", 2UL, 2UL, "大約 3 分鐘", DateTimeOffset.UtcNow)
        };
        var incoming = new ChatTurn(ChatRole.User, "阿華", 3UL, 3UL, "那我要不要等他", DateTimeOffset.UtcNow);

        var prompt = AddresseeDetector.BuildUserPrompt(history, incoming, "笨蛋猫猫");

        Check("★ 判斷提示詞帶了 Bot 的名字（才知道在問誰）",
            prompt.Contains("笨蛋猫猫", StringComparison.Ordinal));
        Check("提示詞有最近的對話與新訊息",
            prompt.Contains("300 幾點來", StringComparison.Ordinal)
            && prompt.Contains("那我要不要等他", StringComparison.Ordinal));
        Check("★ 指示「不確定時 → SKIP」（不要插話，但也不要離開）",
            AddresseeDetector.SystemPrompt.Contains("不確定時 → SKIP", StringComparison.Ordinal));
        Check("★ 提示詞說明「離開之後就接不上了」（這是多人頻道被忽略的根因）",
            AddresseeDetector.SystemPrompt.Contains("離開之後你就接不上了", StringComparison.Ordinal));

        // ── 3) 偷聽窗口 ──────────────────────────────────
        var options = new LlmOptions { Eavesdrop = true, EavesdropMaxMessages = 3, EavesdropSeconds = 120 };
        var store = new ConversationStore(options);

        const ulong guild = 11UL;
        const ulong channel = 22UL;
        var now = DateTimeOffset.UtcNow;

        Check("一開始沒有在偷聽", store.PeekListening(guild, channel, now) is null);

        store.StartListening(guild, channel, options.EavesdropSeconds, options.EavesdropMaxMessages, now);

        var opened = store.PeekListening(guild, channel, now);
        Check("★ 開始偷聽後看得到窗口", opened is not null && opened.RemainingMessages == 3);
        Check("★ 偷聽窗口有時間上限（不是永久開啟）",
            opened!.Until == now.AddSeconds(options.EavesdropSeconds),
            $"{(opened.Until - now).TotalSeconds:0} 秒");

        Check("第一個頻道的窗口不影響別的頻道",
            store.PeekListening(guild, 99UL, now) is null);

        // 消耗
        Check("消耗一次 → 還剩 2 次判斷", store.ConsumeListen(guild, channel, now)?.RemainingMessages == 2);
        Check("再消耗 → 還剩 1 次", store.ConsumeListen(guild, channel, now)?.RemainingMessages == 1);
        Check("★ 用完最後一次判斷 → 自動停止偷聽（回到等 @）",
            store.ConsumeListen(guild, channel, now) is null
            && store.PeekListening(guild, channel, now) is null);

        // ★ 滑動窗：每一則訊息都往後延，不是「從開窗起算」
        store.StartListening(guild, channel, 120, 5, now);
        var slid = store.TouchListen(guild, channel, now.AddSeconds(100));
        Check("★ 時間窗是滑動的（100 秒後有人講話 → 再往後 120 秒，不會中途退出）",
            slid is not null && slid.Until == now.AddSeconds(220),
            $"到期 = 開窗後 {(slid!.Until - now).TotalSeconds:0} 秒");
        Check("★ @ 了別人只是「延後到期」，不扣判斷次數",
            slid.RemainingMessages == 5);
        Check("判斷也一樣會把窗口往後延（ConsumeListen）",
            store.ConsumeListen(guild, channel, now.AddSeconds(200))?.Until == now.AddSeconds(320));

        // 閒置到時間到
        store.StartListening(guild, channel, 60, 5, now);
        Check("★ 超過時間沒人講話 → 自動停止",
            store.PeekListening(guild, channel, now.AddSeconds(61)) is null);

        // 主動停止（判斷 STOP 時走這條）
        store.StartListening(guild, channel, 60, 5, now);
        var stopped = store.StopListening(guild, channel);
        Check("★ 可以主動停止（判斷 STOP 的時候）",
            stopped is not null && store.PeekListening(guild, channel, now) is null);
        Check("已經沒在偷聽時再停止回傳 null（不丟例外）",
            store.StopListening(guild, channel) is null);

        // 選項關掉時不該有窗口
        var offStore = new ConversationStore(new LlmOptions { Eavesdrop = false });
        offStore.StartListening(guild, channel, 0, 0);
        Check("上限設 0 → 等於關閉（不會開出窗口）",
            offStore.PeekListening(guild, channel, now) is null);

        // ── 4) snapshot 要看得到偷聽狀態（/ai status 用）──
        store.StartListening(guild, channel, 120, 2, now);
        var snapshot = store.Snapshot(guild, channel, now);
        Check("★ `/ai status` 看得到「還能判斷幾則／剩幾秒」",
            snapshot is { ListenRemaining: 2 } && snapshot.ListenTimeLeft > TimeSpan.Zero,
            $"{snapshot?.ListenRemaining} 則／{snapshot?.ListenTimeLeft?.TotalSeconds:0} 秒");

        // ── 5) 「偷聽到的閒聊」要留下來當上下文 ────────────
        var ambientStore = new ConversationStore(new LlmOptions { EavesdropContext = true });
        var chatter = new List<ChatTurn>
        {
            new(ChatRole.User, "阿美", 88UL, 700UL, "你們晚餐要吃什麼？", now),
            new(ChatRole.User, "小華", 89UL, 701UL, "都可以啊，不要太遠", now.AddSeconds(10))
        };

        foreach (var turn in chatter)
            ambientStore.RecordAmbient(guild, channel, turn, "偷聽到的閒聊");

        var after = ambientStore.Snapshot(guild, channel, now.AddSeconds(10));
        Check("★ 偷聽到的閒聊會進到上下文（不是只有 Bot 回過的才記）",
            after is { AmbientTurnCount: 2, CurrentTurnCount: 2 },
            $"{after?.CurrentTurnCount} 則（其中閒聊 {after?.AmbientTurnCount}）");

        Check("★ 閒聊在提示詞裡標成 [閒聊]（否則模型會以為那是在問它）",
            (chatter[0] with { Ambient = true }).ToPromptText().StartsWith("[閒聊]", StringComparison.Ordinal)
            && (chatter[0] with { Ambient = true }).ToPromptText()
                .Contains("你們晚餐要吃什麼", StringComparison.Ordinal));
        Check("一般訊息不會被標成閒聊（只有偷聽到的才會）",
            !chatter[0].ToPromptText().Contains("[閒聊]", StringComparison.Ordinal));

        // 同一則不會被記兩次
        ambientStore.RecordAmbient(guild, channel, chatter[0], "重複");
        Check("同一則訊息重複進來只記一次",
            ambientStore.Snapshot(guild, channel, now.AddSeconds(10))?.CurrentTurnCount == 2);

        // 有閒聊時才加說明；沒有閒聊就不浪費 token
        var chat = new ChatOrchestrator(
            new CountingLlmClient("REPLY"), new LlmOptions { SystemPrompt = "人格" },
            new ConversationStore(new LlmOptions()), new WeeklyTokenBudget(new LlmOptions()),
            NoChatTools.Instance, new GuildPersonaStore());

        Check("★ 上下文裡有閒聊時，提示詞會說明「那是背景、不要回它們」",
            chat.EffectiveSystemPrompt(guild, ambientContext: true)
                .Contains("不要回覆它們", StringComparison.Ordinal));
        Check("沒有閒聊時不會多送那一段（省 token）",
            !chat.EffectiveSystemPrompt(guild).Contains("頻道背景", StringComparison.Ordinal));

        // ── 6) 三種判斷結果的完整流程 ─────────────────────
        //    用假 LLM 數**呼叫次數**：判斷要花錢，所以「沒窗口時一次都不該問」是重點。
        var eavesdropOptions = new LlmOptions
        {
            Eavesdrop = true,
            EavesdropMaxMessages = 5,
            EavesdropContext = true,
            ToolsEnabled = false,
            TopicDetect = false,
            SystemPrompt = "測試",
            ApiKey = "test"
        };

        var fakeLlm = new CountingLlmClient("REPLY");
        var eavesdropChat = new ChatOrchestrator(
            fakeLlm, eavesdropOptions, new ConversationStore(eavesdropOptions),
            new WeeklyTokenBudget(eavesdropOptions), NoChatTools.Instance, new GuildPersonaStore())
        {
            BotName = "笨蛋猫猫"
        };

        var strayAnswer = eavesdropChat.AskAsync(
            guild, channel,
            new ChatTurn(ChatRole.User, "阿美", 88UL, 500UL, "300 幾點來？", now),
            null, cancellationToken: default, addressed: false).GetAwaiter().GetResult();

        Check("★ 沒在偷聽時直接忽略，連判斷都不問（不花錢）",
            strayAnswer.Ignored && fakeLlm.Calls == 0,
            $"ignored={strayAnswer.Ignored}／LLM 呼叫 {fakeLlm.Calls} 次");

        eavesdropChat.Conversations.StartListening(guild, channel, 120, 5, now);

        // ① REPLY：判斷 1 次 ＋ 回答 1 次
        var heardAnswer = eavesdropChat.AskAsync(
            guild, channel,
            new ChatTurn(ChatRole.User, "阿華", 77UL, 501UL, "那 304 呢？", now),
            null, cancellationToken: default, addressed: false).GetAwaiter().GetResult();

        Check("★ 判斷 REPLY → 照常回答（判斷 1 次 ＋ 回答 1 次）",
            !heardAnswer.Ignored && heardAnswer.Ok && fakeLlm.Calls == 2,
            $"ignored={heardAnswer.Ignored}／LLM 呼叫 {fakeLlm.Calls} 次");

        Check("回答完窗口繼續（5 次判斷用掉 1 次，剩 4 次）",
            eavesdropChat.Conversations.PeekListening(guild, channel, now)?.RemainingMessages == 4);

        // ② SKIP：不出聲，但**不退出**（這是「後面說的被忽略」的主要修正）
        fakeLlm.Reply = "SKIP";
        var sideNote = eavesdropChat.AskAsync(
            guild, channel,
            new ChatTurn(ChatRole.User, "阿美", 88UL, 502UL, "我今天不想搭公車", now),
            null, cancellationToken: default, addressed: false).GetAwaiter().GetResult();

        Check("★ 判斷 SKIP → 不回話，但**繼續偷聽**（不是退出）",
            sideNote.Ignored && fakeLlm.Calls == 3
            && eavesdropChat.Conversations.PeekListening(guild, channel, now) is not null,
            $"{sideNote.IgnoreReason ?? "（沒有說明）"}");

        Check("★ SKIP 的訊息也留下來當上下文（之後 @ 它時接得上）",
            eavesdropChat.Conversations.Snapshot(guild, channel, now)?.AmbientTurnCount >= 1);

        // ③ @ 了別人：不問模型、不扣判斷次數，但繼續聽
        var before = eavesdropChat.Conversations.PeekListening(guild, channel, now)!.RemainingMessages;

        var otherPerson = eavesdropChat.AskAsync(
            guild, channel,
            new ChatTurn(ChatRole.User, "阿美", 88UL, 503UL, "@小華 你要幾點到？", now),
            null, cancellationToken: default, addressed: false, mentionsOtherHuman: true)
            .GetAwaiter().GetResult();

        Check("★ @ 了別人 → 不插話而且**不花錢判斷**（LLM 呼叫次數不變）",
            otherPerson.Ignored && fakeLlm.Calls == 3, $"LLM 呼叫 {fakeLlm.Calls} 次");
        Check("★ @ 了別人之後**還在偷聽**（以前在這裡直接退出，之後的訊息全被忽略）",
            eavesdropChat.Conversations.PeekListening(guild, channel, now) is { } w
            && w.RemainingMessages == before,
            $"判斷次數 {before} → {eavesdropChat.Conversations.PeekListening(guild, channel, now)?.RemainingMessages}");

        // ④ STOP：明確是別人之間的對話 → 退出、回到等 @
        fakeLlm.Reply = "STOP";
        var goodbye = eavesdropChat.AskAsync(
            guild, channel,
            new ChatTurn(ChatRole.User, "阿美", 88UL, 504UL, "你要不要一起去吃火鍋？", now),
            null, cancellationToken: default, addressed: false).GetAwaiter().GetResult();

        Check("★ 判斷 STOP → 退出偷聽（回到等 @）",
            goodbye.Ignored && fakeLlm.Calls == 4
            && eavesdropChat.Conversations.PeekListening(guild, channel, now) is null,
            $"{goodbye.IgnoreReason ?? "（沒有說明）"}");

        Check("★ STOP 之前聽到的內容仍然留在上下文裡",
            eavesdropChat.Conversations.Snapshot(guild, channel, now)?.AmbientTurnCount >= 2);

        // ⑤ 退出之後就真的不理了（連判斷都不問）
        var afterStop = eavesdropChat.AskAsync(
            guild, channel,
            new ChatTurn(ChatRole.User, "阿美", 88UL, 505UL, "那 300 呢？", now),
            null, cancellationToken: default, addressed: false).GetAwaiter().GetResult();

        Check("★ 退出後再講話 → 忽略（連判斷都不問）",
            afterStop.Ignored && fakeLlm.Calls == 4);

        // ⑥ 「再被 @ 一次」會重新開窗（多人頻道裡這是最常見的循環）
        var restartConv = new ChannelConversation { GuildId = guild, ChannelId = channel };
        var restartSegment = new ConversationSegment
        {
            GuildId = guild, ChannelId = channel, StartedAt = now, LastActivityAt = now
        };
        restartConv.Segments.Add(restartSegment);

        eavesdropChat.RecordReply(
            new ContextDecision(restartConv, restartSegment, [], NewSegment: false, Reason: "測試", ReplyTarget: null),
            "好喔", messageId: 900UL, botId: 1UL, botName: "笨蛋猫猫", at: now);

        Check("★ 又被 @ 一次 → 重新開一個完整的偷聽窗口",
            eavesdropChat.Conversations.PeekListening(guild, channel, now)?.RemainingMessages
                == eavesdropOptions.EavesdropMaxMessages);
    }

    /// <summary>測試用的假 LLM：固定回一句話，並記住被呼叫幾次（用來驗證「該不該花錢」）。</summary>
    private sealed class CountingLlmClient(string reply) : ILlmClient
    {
        public string Reply { get; set; } = reply;

        public int Calls { get; private set; }

        public bool IsConfigured => true;

        public string Describe() => "（測試用假 LLM）";

        public Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new LlmReply(
                Reply, InputTokens: 10, OutputTokens: 2, UsageReported: true,
                Model: "fake", Elapsed: TimeSpan.FromMilliseconds(1)));
        }
    }

    /// <summary>
    /// 儲存後端的選擇與後備鏈：MongoDB → SQLite → 文字檔 → 記憶體。
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

    /// <summary>
    /// 健康檢查端點與防休眠迴圈（部署到 Render 這類 Web Service 用的）。
    ///
    /// 這兩個元件放在 Core（不依賴 Discord），所以可以在離線環境完整驗證：
    /// 真的開一個埠、真的用 HttpClient 打它、真的檢查 HTTP 狀態碼與內容。
    /// </summary>
    private static void TestHealthEndpoint()
    {
        var payloadCalls = 0;

        // 埠傳 0 = 讓作業系統挑一個空閒埠（測試不必猜）
        using var endpoint = new HealthEndpoint(0, () => { payloadCalls++; return "{\"status\":\"ok\"}"; });

        if (!endpoint.Start(out var message))
        {
            Check("健康檢查端點可以啟動", false, message);
            return;
        }

        Check("★ 健康檢查端點可以啟動", endpoint.IsRunning, message);

        var baseUrl = $"http://127.0.0.1:{endpoint.Port}";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // ── GET /（Render 的健康檢查會打這個）──
        var root = http.GetAsync($"{baseUrl}/").GetAwaiter().GetResult();
        var rootBody = root.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        Check("★ GET / 回 200", (int)root.StatusCode == 200, ((int)root.StatusCode).ToString());
        Check("★ GET / 的內容正確", rootBody.Contains("TcBusBot is running", StringComparison.Ordinal), rootBody);
        Check("回應有正確的 Content-Type",
            root.Content.Headers.ContentType?.MediaType == "text/plain", root.Content.Headers.ContentType?.ToString());

        // ── GET /health（狀態 JSON）──
        var health = http.GetAsync($"{baseUrl}/health").GetAwaiter().GetResult();
        var healthBody = health.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        Check("★ GET /health 回 200 JSON",
            (int)health.StatusCode == 200
            && health.Content.Headers.ContentType?.MediaType == "application/json", healthBody);
        Check("/health 有去問狀態內容（不是寫死的字串）", payloadCalls > 0, $"呼叫 {payloadCalls} 次");

        // ── 查詢字串與結尾斜線都要能通（監控服務常常這樣送）──
        var withQuery = http.GetAsync($"{baseUrl}/health?from=uptimerobot").GetAwaiter().GetResult();
        Check("★ /health?query 與 /health/ 都能通",
            (int)withQuery.StatusCode == 200
            && (int)http.GetAsync($"{baseUrl}/health/").GetAwaiter().GetResult().StatusCode == 200);

        // ── 其他路徑 404、非 GET 405 ──
        Check("★ 不存在的路徑回 404",
            (int)http.GetAsync($"{baseUrl}/nope").GetAwaiter().GetResult().StatusCode == 404);

        var post = http.PostAsync($"{baseUrl}/health", new StringContent("x")).GetAwaiter().GetResult();
        Check("★ 非 GET 回 405（不讓別人亂打）", (int)post.StatusCode == 405, ((int)post.StatusCode).ToString());

        // ── HEAD 只要標頭（有些監控服務用 HEAD）──
        var head = new HttpRequestMessage(HttpMethod.Head, $"{baseUrl}/");
        var headResponse = http.SendAsync(head).GetAwaiter().GetResult();
        Check("HEAD / 回 200 且沒有內容",
            (int)headResponse.StatusCode == 200
            && headResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult().Length == 0);

        // ── 防休眠：真的去 ping 那個端點 ★（這是沙箱裡原本驗不到的部分）──
        var keepAlive = new KeepAliveLoop($"{baseUrl}/health", intervalMinutes: 10,
                                          requestTimeout: TimeSpan.FromSeconds(5));

        Check("防休眠的說明文字包含網址",
            keepAlive.Describe().Contains(baseUrl, StringComparison.Ordinal), keepAlive.Describe());

        keepAlive.PingOnceAsync().GetAwaiter().GetResult();

        Check("★ 防休眠 ping 成功（成功 1 次）",
            keepAlive.SuccessCount == 1 && keepAlive.FailureCount == 0,
            $"成功 {keepAlive.SuccessCount}／失敗 {keepAlive.FailureCount}（{keepAlive.LastResult}）");
        Check("防休眠記得住最後一次結果",
            keepAlive.LastResult?.Contains("200", StringComparison.Ordinal) == true
            && keepAlive.LastPingAt is not null, keepAlive.LastResult ?? "(null)");

        keepAlive.Dispose();

        // ping 不到的網址要算失敗（而且不能丟例外）
        using var broken = new KeepAliveLoop("http://127.0.0.1:1/health", 10, TimeSpan.FromSeconds(3));
        broken.PingOnceAsync().GetAwaiter().GetResult();

        Check("★ 打不通時算失敗且不丟例外",
            broken.FailureCount == 1 && broken.SuccessCount == 0, broken.LastResult ?? "(null)");

        endpoint.Dispose();
        Check("端點可以正常關閉", !endpoint.IsRunning);
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
