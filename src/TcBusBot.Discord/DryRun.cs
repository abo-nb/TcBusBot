using System.Reflection;
using System.Text;
using Discord;
using Discord.Interactions;
using TcBusBot.Core.Bus;
using TcBusBot.Core.DataSources;
using TcBusBot.Core.Models;
using TcBusBot.Core.Storage;
using TcBusBot.Core.Subscriptions;
using TcBusBot.Core.Tdx;
using TcBusBot.Discord.Modules;

namespace TcBusBot.Discord;

/// <summary>
/// 離線 UI 驗證：把整個 Discord 互動流程走一遍，印出「會送出什麼」，
/// 並且檢查每一個元件是否符合 Discord 的限制。
///
/// 為什麼要做這個：沒有 Bot Token 就沒辦法在 Discord 上點，
/// 但絕大多數「上線才發現」的問題其實是元件超限（選項超過 25 個、
/// custom_id 超過 100 字元、按鈕超過 5 個…）。這些都能在本地先驗證掉。
/// </summary>
public static class DryRun
{
    private static int _problems;

    /// <summary>這輪驗證中，所有「實際被送出去」的 custom_id。</summary>
    private static readonly HashSet<string> SeenIds = new(StringComparer.Ordinal);

    private static string[]? _handlers;

    public static int Run(TaichungBusDataService data, BotConfig cfg)
    {
        _problems = 0;
        SeenIds.Clear();
        _handlers = null;
        var subs = new SubscriptionService();
        var runtime = new BotRuntime(subs, null) { DataSourceDescription = "（dry-run）" };
        var session = new BusSession { UserId = 1UL, ChannelId = 2UL };

        Console.WriteLine("══════════════════════════════════════════════════════════════");
        Console.WriteLine("  Discord UI 離線驗證（不會連線、不需要 Token）");
        Console.WriteLine("══════════════════════════════════════════════════════════════");

        // ── Step 1：/bus panel ─────────────────────────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 1  `/bus panel`");
        var panel = BusUi.Panel(session, 0);
        var panelComp = BusUi.PanelComponents(session);
        Print(panel, panelComp);

        // ── Step 2-4：搜尋起點 → 勾選 → 確認 ────────────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 2  起點：在 Modal 輸入「台中車站」（故意用「台」）");
        var originKeyword = "台中車站";
        var originGroups = data.Search.SearchGrouped(originKeyword).ToList();
        session.LastSearch = originGroups;
        session.LastKeyword = originKeyword;

        Console.WriteLine();
        Console.WriteLine("▶ Step 3  搜尋結果（可多選）");
        var searchEmbed = BusUi.SearchResult(originKeyword, originGroups, data);
        var searchComp = BusUi.SearchComponents(true, originGroups, data, Array.Empty<string>());
        Print(searchEmbed, searchComp);

        var originPicks = originGroups.Where(g => g.IsDefaultPick).Select(g => $"g:{g.GroupKey}").ToList();
        var originUids = Resolve(data, originPicks);
        session.Origin = data.TargetFromStops(originUids);
        Console.WriteLine($"  （預設勾選：{string.Join(", ", originPicks)} → {originUids.Count} 個站牌）");

        // ── Step 5：搜尋目的地 ─────────────────────────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 4  目的地：輸入「靜宜大學」");
        var destGroups = data.Search.SearchGrouped("靜宜大學").ToList();
        session.LastSearch = destGroups;
        var destPicks = destGroups.Where(g => g.IsDefaultPick).Select(g => $"g:{g.GroupKey}").ToList();
        session.Destination = data.TargetFromStops(Resolve(data, destPicks));
        Console.WriteLine($"  → {session.Destination}");

        // ── Step 6：面板（兩者都已設定）────────────────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 5  面板已就緒");
        var readyPanel = BusUi.Panel(session, 0);
        var readyComp = BusUi.PanelComponents(session);
        Print(readyPanel, readyComp);

        // ── Step 7：搜尋路線 ───────────────────────────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 6  `搜尋路線`");
        var routes = data.FindRoutes(session.Origin!, session.Destination!);
        session.LastRoutes = routes.ToList();
        var routeEmbed = BusUi.RouteList(session.Origin!, session.Destination!, routes);
        var routeComp = BusUi.RouteComponents(routes);
        Print(routeEmbed, routeComp);

        if (routes.Count == 0)
        {
            Console.WriteLine("  ❌ 找不到路線，無法繼續驗證。");
            return 1;
        }

        // ── Step 8：勾選路線（僅選取，尚未訂閱）────────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 7  在選單裡勾選路線（此時**還沒有**訂閱）");
        var allRouteValues = routes
            .Select(r => $"r:{r.RouteUid}|{r.Direction}")
            .ToList();
        var pickedEmbed = BusUi.RoutePicked(session.Origin!, session.Destination!, routes, routes);
        var pickedComp = BusUi.RouteComponents(routes, allRouteValues);
        Print(pickedEmbed, pickedComp);

        // ── Step 9：按下「訂閱」才真的建立 ────────────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 8  按下「✅ 訂閱這 N 條路線」→ 建立訂閱");
        var group = subs.CreateGroup(
            userId: session.UserId,
            origin: session.Origin!,
            destination: session.Destination!,
            options: routes,
            notifyBeforeMinutes: cfg.DefaultNotifyMinutes,
            guildId: 3UL,
            channelId: session.ChannelId);
        session.CreatedGroupId = group.Id;

        var subsOfGroup = subs.GetSubscriptions(group);
        Console.WriteLine($"  建立 {subsOfGroup.Count} 個訂閱：");
        foreach (var s in subsOfGroup)
            Console.WriteLine($"    • {s.RouteName}（{BusUi.DirLabel(s.Direction)}）{s.BoardStopName}@{s.BoardSequence} → {s.AlightStopName}@{s.AlightSequence}");

        var pollStops = subs.GetAllEnabledBoardStopUids();
        Console.WriteLine($"  輪詢只需查 {pollStops.Count} 個不重複的上車站：{string.Join(", ", pollStops)}");
        PrintPollPlan(data, pollStops, cfg);

        Console.WriteLine();
        Console.WriteLine("▶ Step 9  選擇提前通知時間");
        var notifyEmbed = BusUi.NotifyChooser(group, subsOfGroup);
        var notifyComp = BusUi.NotifyComponents();
        Print(notifyEmbed, notifyComp);

        // ── Step 9：完成 ───────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 10  按下「10 分鐘」→ 訂閱完成（同時顯示所有訂閱的到站時間）");
        group.NotifyBeforeMinutes = 10;

        var now2 = DateTimeOffset.UtcNow;
        var etaResult = runtime.BuildEtaTableAsync(group, now2).GetAwaiter().GetResult();
        Console.WriteLine($"  （ETA 來源：{(etaResult.Simulation ? "模擬資料（沒有 TDX 金鑰）" : "TDX 真實資料")}，" +
                          $"{etaResult.Rows.Count} 條訂閱）");

        Print(new[]
        {
            BusUi.FinalCard(group, subsOfGroup),
            BusUi.EtaTable(group, etaResult.Rows, now2, etaResult.Simulation, etaResult.Error)
        }, BusUi.EtaComponents());

        // ── Step 10：通知長相 ──────────────────────────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 11  按「模擬一則通知」→ 你會收到的通知");
        var noticeEmbed = runtime.PreviewWithSyntheticData(group, DateTimeOffset.UtcNow);
        var noticeComp = new ComponentBuilder()
            .WithButton("發送到我的私訊", Cid.SendDm, ButtonStyle.Primary)
            .WithButton("返回面板", "bus:back", ButtonStyle.Secondary)
            .Build();
        Print(noticeEmbed, noticeComp);

        // ── Step 11：訂閱清單 ──────────────────────────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 12  按「查看我的訂閱」");
        var groups = subs.GetGroupsByUser(session.UserId).ToList();
        var listEmbed = BusUi.SubscriptionList(groups, g => subs.GetSubscriptions(subs.GetGroup(g)!));
        var listComp = BusUi.SubscriptionListComponents(groups);
        Print(listEmbed, listComp);

        // ── Step 13：訂閱組（存到 SQLite）──────────────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 13  💾 存成訂閱組 → 下次一鍵套用／一次套用多個／合併");

        using (var store = new SavedGroupStore(":memory:"))
        {
            var payload = SavedGroupPayloadFactory.FromSubscriptions(
                new SavedTarget(group.Origin.DisplayName, group.Origin.CandidateStopUids.ToList()),
                new SavedTarget(group.Destination.DisplayName, group.Destination.CandidateStopUids.ToList()),
                subsOfGroup.Select(s => (s.RouteUid, s.Direction, s.RouteName)),
                group.NotifyBeforeMinutes);

            var (saveResult, _) = store.Save(session.UserId, "上班通勤", payload);
            var (saveResult2, _) = store.Save(session.UserId, "回家路線", payload with { NotifyMinutes = 15 });

            // 第二個組故意是「反過來」的行程，這樣合併後看得出是兩段不同的行程
            var reverseOrigin = session.Destination!;
            var reverseDest = session.Origin!;
            var reverseOptions = data.FindRoutes(reverseOrigin, reverseDest);
            if (reverseOptions.Count > 0)
            {
                var reversePayload = SavedGroupPayloadFactory.FromSubscriptions(
                    new SavedTarget(reverseOrigin.DisplayName, reverseOrigin.CandidateStopUids.ToList()),
                    new SavedTarget(reverseDest.DisplayName, reverseDest.CandidateStopUids.ToList()),
                    reverseOptions.Take(5).Select(r => (r.RouteUid, r.Direction, r.RouteName)),
                    notifyMinutes: 5);

                var (r3, _) = store.Save(session.UserId, "回程（靜宜→臺中）", reversePayload);
                Console.WriteLine($"  第三個組（反向行程）：{r3}，{reverseOptions.Count} 條可選路線");
            }

            Console.WriteLine($"  儲存結果：{saveResult} / {saveResult2}　（{store.Describe()}）");

            var savedGroups = store.ListByUser(session.UserId);
            Func<long, SavedGroupPayload?> payloadOf = id => store.GetPayload(id, session.UserId);
            var selectedIds = new List<long> { savedGroups[0].Id };

            Print(BusUi.SavedGroupsList(savedGroups, selectedIds,
                                        SavedGroupStore.MaxGroupsPerUser, payloadOf),
                  BusUi.SavedGroupsComponents(savedGroups, selectedIds));

            // ── 一次選多個 → 兩個套用模式 ──────────────────
            var multiSelected = savedGroups.Select(g => g.Id).ToList();
            Console.WriteLine();
            Console.WriteLine($"▶ Step 13b  一次勾選 {multiSelected.Count} 個訂閱組");
            Print(BusUi.SavedGroupsList(savedGroups, multiSelected,
                                        SavedGroupStore.MaxGroupsPerUser, payloadOf),
                  BusUi.SavedGroupsComponents(savedGroups, multiSelected));

            // 模式一：各自獨立（每一組各自成為一個訂閱）
            // 模式二：合併成一個通知流（所有行程合成一個訂閱群組）
            var plans = new List<LegPlan>();
            var planWarnings = new List<string>();

            foreach (var saved in savedGroups)
            {
                var loaded = store.GetPayload(saved.Id, session.UserId)!;
                foreach (var leg in loaded.Legs)
                {
                    var o = new LocationTarget
                    {
                        DisplayName = leg.Origin.DisplayName,
                        CandidateStopUids = leg.Origin.StopUids.ToList()
                    };
                    var d = new LocationTarget
                    {
                        DisplayName = leg.Destination.DisplayName,
                        CandidateStopUids = leg.Destination.StopUids.ToList()
                    };

                    var wanted = leg.Routes.Select(r => (r.RouteUid, r.Direction)).ToHashSet();
                    var matched = data.FindRoutes(o, d)
                                      .Where(r => wanted.Contains((r.RouteUid, r.Direction)))
                                      .ToList();

                    if (matched.Count == 0)
                    {
                        planWarnings.Add($"「{saved.Name}」{o.DisplayName} → {d.DisplayName} 找不到路線");
                        continue;
                    }

                    plans.Add(new LegPlan(o, d, matched));
                }
            }

            var independent = plans
                .Select(p => subs.CreateGroup(session.UserId, p.Origin, p.Destination, p.Options, 10))
                .ToList();

            var mergedStream = subs.CreateMultiLegGroup(session.UserId, plans, notifyBeforeMinutes: 10);

            Console.WriteLine($"  各自獨立：{independent.Count} 個訂閱群組、" +
                              $"{independent.Sum(g => g.SubscriptionIds.Count)} 個訂閱");
            Console.WriteLine($"  合併成一個通知流：1 個訂閱群組、{mergedStream.LegCount} 段行程、" +
                              $"{mergedStream.SubscriptionIds.Count} 個訂閱");

            if (mergedStream.LegCount != plans.Count)
                Problem($"合併後的段數不符：計畫 {plans.Count} 段、群組 {mergedStream.LegCount} 段");

            // 合併後「挑最快」是跨行程的 —— 這是合併成一個通知流的意義
            var legsInGroup = subs.GetSubscriptions(mergedStream).Select(s => s.LegIndex).Distinct().OrderBy(x => x).ToList();
            if (legsInGroup.Count != mergedStream.LegCount)
                Problem($"合併後的訂閱沒有涵蓋所有行程段：{string.Join(",", legsInGroup)}");

            Print(BusUi.GroupsApplied(savedGroups, [mergedStream],
                                      subs.GetSubscriptions(mergedStream),
                                      mergedStream: true, planWarnings),
                  BusUi.EtaComponents());

            // ★ 合併成一個通知流之後，通知上的「起訖」必須是**那一條訂閱所屬的那一段**，
            //   而不是群組的第一段 —— 否則使用者會看到完全錯誤的行程。
            Console.WriteLine();
            Console.WriteLine("▶ Step 13b-2  合併後的模擬通知（起訖要是該訂閱所屬的那一段）");

            var mergedNotice = runtime.PreviewWithSyntheticData(mergedStream, DateTimeOffset.UtcNow);
            Print(mergedNotice, new ComponentBuilder()
                .WithButton("發送到我的私訊", Cid.SendDm, ButtonStyle.Primary)
                .WithButton("返回面板", "bus:back", ButtonStyle.Secondary)
                .Build());

            EmbedField? noticeLeg = mergedNotice.Fields
                .Cast<EmbedField?>()
                .FirstOrDefault(f => f!.Value.Name == "起訖");
            var noticeLegField = noticeLeg?.Value;
            var finalSubs = subs.GetSubscriptions(mergedStream);
            var expectedLegs = finalSubs
                .Select(s => $"{mergedStream.LegOf(s).Origin.DisplayName} → " +
                             $"{mergedStream.LegOf(s).Destination.DisplayName}")
                .Distinct()
                .ToList();

            if (noticeLegField is null)
                Problem("模擬通知沒有「起訖」欄位");
            else if (!expectedLegs.Contains(noticeLegField))
                Problem($"模擬通知的「起訖」({noticeLegField}) 不屬於任何一段行程：" +
                        string.Join("；", expectedLegs));
            else
                Console.WriteLine($"  ✔ 模擬通知的起訖「{noticeLegField}」是正確的那一段" +
                                  $"（群組共 {expectedLegs.Count} 種起訖）");

            // ── 合併成「新訂閱組」（多段行程）＋ 復原 ────────
            Console.WriteLine();
            Console.WriteLine("▶ Step 13c  🔗 合併成新組（多段行程）→ 可以 ↩️ 復原");

            var mergedPayload = SavedGroupPayloadFactory.Merge(
                savedGroups.Select(g => store.GetPayload(g.Id, session.UserId)!), notifyMinutes: 10);

            var (mergeResult, mergeMsg) = store.Save(session.UserId, "通勤全部", mergedPayload);
            Console.WriteLine($"  合併結果：{mergeResult}（{mergeMsg ?? "OK"}）→ " +
                              $"{mergedPayload.LegCount} 段行程、{mergedPayload.RouteCount} 條路線");

            if (mergedPayload.LegCount < 2)
                Problem($"合併多個組應該至少 2 段行程，得到 {mergedPayload.LegCount} 段");

            if (mergedPayload.RouteCount > SavedGroupStore.MaxRoutesPerGroup)
                Problem($"合併後路線數 {mergedPayload.RouteCount} 超過上限 {SavedGroupStore.MaxRoutesPerGroup}（應該要被擋下來）");

            var afterMerge = store.ListByUser(session.UserId);
            var mergedRow = store.GetByName(session.UserId, "通勤全部")!;

            Print(BusUi.SavedGroupsList(afterMerge, new List<long> { mergedRow.Id },
                                        SavedGroupStore.MaxGroupsPerUser, payloadOf),
                  BusUi.SavedGroupsComponents(afterMerge, new List<long> { mergedRow.Id },
                                              new UndoMergedSavedGroup("合併成「通勤全部」", mergedRow.Id, "通勤全部", null)));

            // 復原：合併出來的新組要被刪掉
            var undo = new UndoStack();
            undo.Push(new UndoMergedSavedGroup("合併成「通勤全部」", mergedRow.Id, "通勤全部", null));
            var undoMessage = undo.Undo(subs, store, session.UserId);
            Console.WriteLine($"  ↩️ 復原：{undoMessage}");

            if (store.Get(mergedRow.Id, session.UserId) is not null)
                Problem("復原合併後，合併出來的新組應該要消失");
            else
                Console.WriteLine("  ✔ 復原後「通勤全部」已移除");

            // 復原：批次套用的訂閱要被取消
            var undo2 = new UndoStack();
            undo2.Push(new UndoAppliedSubscriptions("套用 3 個訂閱組",
                independent.Select(g => g.Id).ToList(), null));
            var before = subs.GroupCount;
            var undoMessage2 = undo2.Undo(subs, store, session.UserId);
            Console.WriteLine($"  ↩️ 復原：{undoMessage2}（群組數 {before} → {subs.GroupCount}）");

            if (subs.GetGroup(independent[0].Id) is not null)
                Problem("復原批次套用後，剛才建立的訂閱群組應該要消失");

            // 舊格式（單段 payload）必須還能讀 —— 使用者已經存好的組不能因為改版消失
            var legacyJson = """
                {"origin":{"name":"臺中車站","uids":["TXG12251"]},
                 "dest":{"name":"靜宜大學","uids":["TXG13567"]},
                 "routes":[{"u":"TXG300","d":1,"n":"300"}],"notify":10}
                """;
            var legacy = System.Text.Json.JsonSerializer.Deserialize<SavedGroupPayload>(
                legacyJson, SavedGroupStore.PayloadJson);

            if (legacy is null || legacy.LegCount != 1 || legacy.RouteCount != 1)
                Problem("舊格式的訂閱組（單段 origin/dest/routes）應該要能升級成 1 段行程");
            else
                Console.WriteLine($"  ✔ 舊格式相容：單段 payload → {legacy.LegCount} 段行程、" +
                                  $"{legacy.Origin.DisplayName} → {legacy.Destination.DisplayName}");

            // 單一訂閱組的套用結果（最常見的路徑）
            Print(BusUi.GroupApplied(savedGroups[0], independent[0],
                                     subs.GetSubscriptions(independent[0]), null),
                  BusUi.EtaComponents());
        }

        // ── 邊界情況 ───────────────────────────────────────
        // 這一段是為了「曾經真的壞掉」的情境而加的：
        // 使用者輸入資料集裡沒有的站名 → 搜尋結果 0 筆 →
        // 空的 Select Menu 在 Build() 時丟例外 → Discord 顯示「無法提交」。
        Boundary(data);

        // ── 元件與處理函式的雙向接線檢查 ────────────────────
        AuditWiring();

        // ── 總結 ───────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════════════════════");
        if (_problems == 0)
            Console.WriteLine("  ✅ 所有元件都符合 Discord 限制，流程可以完整走完");
        else
            Console.WriteLine($"  ❌ 發現 {_problems} 個元件違規（見上方 ⚠️）");
        Console.WriteLine("══════════════════════════════════════════════════════════════");

        return _problems == 0 ? 0 : 1;
    }

    /// <summary>邊界情況：搜尋不到、資料集很小、極端輸入。</summary>
    private static void Boundary(TaichungBusDataService data)
    {
        Console.WriteLine();
        Console.WriteLine("▶ 邊界情況 A  搜尋資料集裡沒有的站名（例如「不存在站」）");

        // 刻意混合三種情況：
        //   「不存在站XYZ」→ 完全沒有結果（曾經讓互動直接失敗）
        //   「北勢」        → 只命中 1 個站牌（驗證單站牌群組不會產生重複選項）
        //   「!!!」         → 只有標點，正規化後為空
        foreach (var keyword in new[] { "不存在站XYZ", "北勢", "!!!" })
        {
            var groups = data.Search.SearchGrouped(keyword).ToList();

            try
            {
                var embed = BusUi.SearchResult(keyword, groups, data);
                // ★ 這一行以前會丟 ArgumentException（Select Menu 至少要 1 個選項）
                var comp = BusUi.SearchComponents(false, groups, data, Array.Empty<string>());
                var originComp = BusUi.SearchComponents(true, groups, data, Array.Empty<string>());

                Console.WriteLine($"  「{keyword}」→ 命中 {groups.Count} 組" +
                                  $"（元件：{comp.Components.Count} 列、{originComp.Components.Count} 列）");
                Print(embed, comp);

                // 起點的「重新輸入」按鈕只印一次會漏掉（接線檢查會誤判它按不到），
                // 所以只驗證、不重複印。
                Validate(embed, originComp);
            }
            catch (Exception ex)
            {
                Problem($"搜尋「{keyword}」時元件建構失敗：{ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("▶ 邊界情況 B  太短的關鍵字（< 2 字元會被擋掉）");
        var shortResult = data.Search.SearchGrouped("中").ToList();
        Console.WriteLine($"  「中」→ 命中 {shortResult.Count} 組" +
                          $"（{(_problems == 0 ? "正確擋掉，未產生空選單" : "有問題")}）");

        try
        {
            var comp = BusUi.SearchComponents(true, shortResult, data, Array.Empty<string>());
            Console.WriteLine($"  元件可正常建構（{comp.Components.Count} 列）");
        }
        catch (Exception ex)
        {
            Problem($"太短關鍵字時元件建構失敗：{ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("▶ 邊界情況 C  建議站名清單（給搜尋不到的使用者參考）");
        Console.WriteLine("  " + string.Join("　", data.ExampleStopNames(8)));        Console.WriteLine();
        Console.WriteLine("▶ 邊界情況 D  同名站牌合併（去回程不該出現兩次）");

        foreach (var kw in new[] { "中正國小", "靜宜大學", "臺中車站" })
        {
            // ★ 走與 Bot 完全相同的路徑（含「只顯示強相符」的過濾）
            var allGroups = data.Search.SearchGrouped(kw).ToList();
            var (mergeGroups, hiddenFuzzy) = BusUi.PreferStrongMatches(allGroups);

            if (mergeGroups.Count == 0)
            {
                Problem($"預期找得到「{kw}」");
                continue;
            }

            var g0 = mergeGroups[0];
            var distinctNames = g0.Hits.Select(h => h.Entry.DisplayName)
                                       .Distinct(StringComparer.Ordinal).Count();

            Console.WriteLine($"  「{kw}」→ 顯示 {mergeGroups.Count} 組（過濾掉 {hiddenFuzzy} 組模糊相符）、" +
                              $"第一組 {g0.Hits.Count} 個站牌 / {distinctNames} 種站名");

            var comp = BusUi.SearchComponents(true, mergeGroups, data, Array.Empty<string>());
            var options = SelectOptions(comp);

            foreach (var o in options.Take(10))
                Console.WriteLine($"      {o.Label}  →  {o.Value}");

            if (options.Count > 10) Console.WriteLine($"      … 另有 {options.Count - 10} 個選項");

            // 過濾後不該再出現完全無關的站（例如搜「臺中車站」不該出現「沙鹿車站」）
            var unrelated = options.Where(o => !o.Label.Contains(g0.DisplayName.TrimEnd('(') , StringComparison.Ordinal)
                                               && !g0.DisplayName.Contains(o.Label, StringComparison.Ordinal))
                                   .ToList();
            if (hiddenFuzzy > 0 && unrelated.Count > 0 && mergeGroups.Count == 1)
                Console.WriteLine($"      ⚠️ 有 {unrelated.Count} 個選項與主要群組無關");

            var dupes = options.GroupBy(o => o.Label)
                               .Where(x => x.Count() > 1)
                               .Select(x => x.Key)
                               .ToList();

            if (dupes.Count > 0)
                Problem($"「{kw}」的選項標籤重複：{string.Join(", ", dupes)}");

            if (options.Count > 25)
                Problem($"「{kw}」選項超過 25 個（{options.Count}）");

            // ★ 迴歸測試：每一個選項的值都必須能「原值還原」成站牌。
            //    曾經因為把 StopUID 串進 value、超過 Discord 的 100 字元被截斷，
            //    導致候選集合少掉大半（國立臺中科技大學有 44 個同名站牌）。
            foreach (var o in options)
            {
                var resolved = BusUi.ResolveStopValues(new[] { o.Value }, data);
                if (resolved.Count == 0)
                    Problem($"選項「{o.Label}」的值無法還原成任何站牌：{o.Value}");

                if (o.Value.Length >= 95)
                    Problem($"選項「{o.Label}」的值長度 {o.Value.Length} 已接近 100 上限，可能被截斷");
            }
        }

        // ── 邊界情況 F：到站時間總表的「各種狀態」──
        //     用真實 TDX ETA payload（tests/fixtures/real）驗證每一種狀態文字，
        //     以及「有 ETA 的排前面、沒有的排後面」的排序。
        Console.WriteLine();
        Console.WriteLine("▶ 邊界情況 F  到站時間總表的各種狀態（用真實 ETA payload）");

        try
        {
            var realPath = Path.Combine(MiniFixtureSource.ResolveRoot(), "real",
                                        "EstimatedTimeOfArrival.sample.json");
            var realEtas = System.Text.Json.JsonSerializer.Deserialize<List<BusEta>>(
                File.ReadAllText(realPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            var now = DateTimeOffset.UtcNow;
            // 把 fixture 的 SrcUpdateTime 換成「剛剛」，否則會被過期防護擋掉
            var fresh = realEtas.Select(e => new BusEta
            {
                RouteUID = e.RouteUID, RouteID = e.RouteID, Direction = e.Direction,
                StopUID = e.StopUID, StopSequence = e.StopSequence,
                PlateNumb = e.PlateNumb, EstimateTime = e.EstimateTime, StopStatus = e.StopStatus,
                NextBusTime = e.NextBusTime, SrcUpdateTime = now, UpdateTime = now
            }).ToList();

            var fakeSubs = fresh.Select((e, i) => new Subscription
            {
                Id = $"t{i}", GroupId = "t", RouteUid = e.RouteUID, RouteName = e.RouteName?.ZhTw ?? e.RouteID,
                Direction = e.Direction, Headsign = "",
                BoardStopUid = e.StopUID, BoardStopName = e.StopName?.ZhTw ?? e.StopUID, BoardSequence = e.StopSequence,
                AlightStopUid = "x", AlightStopName = "目的地", AlightSequence = e.StopSequence + 10
            }).ToList();

            // 再加一條「完全查不到」的
            fakeSubs.Add(new Subscription
            {
                Id = "t9", GroupId = "t", RouteUid = "TXG999", RouteName = "999", Direction = 0, Headsign = "",
                BoardStopUid = "TXG000", BoardStopName = "某站牌", BoardSequence = 1,
                AlightStopUid = "x", AlightStopName = "目的地", AlightSequence = 10
            });

            var rows = new List<BotRuntime.RouteEtaRow>();
            foreach (var s in fakeSubs)
            {
                var eta = fresh.FirstOrDefault(e => e.RouteUID == s.RouteUid && e.Direction == s.Direction);
                var live = eta is null ? null : SubscriptionMatcher.LiveEstimateSeconds(eta, now, 180);
                rows.Add(new BotRuntime.RouteEtaRow(s, eta, live, DescribeForTest(eta, live)));
            }

            // 依「有 ETA 優先、時間由近到遠」排序（與 BotRuntime.SortRows 相同規則）
            rows = rows
                .OrderBy(r => r.LiveSeconds.HasValue ? 0 : 1)
                .ThenBy(r => r.LiveSeconds ?? double.MaxValue)
                .ThenBy(r => r.Subscription.RouteName, NaturalComparer.Instance)
                .ToList();

            var group = new SubscriptionGroup
            {
                Id = "t", UserId = 1, Origin = data.TargetFromStops(new[] { "TXG12251" }),
                Destination = data.TargetFromStops(new[] { "TXG13567" }), NotifyBeforeMinutes = 10
            };

            Print(BusUi.EtaTable(group, rows, now, simulation: false), new ComponentBuilder().Build(), validate: false);

            var withEta = rows.Count(r => r.LiveSeconds.HasValue);
            Console.WriteLine($"  ℹ {withEta} 條有預估時間、{rows.Count - withEta} 條沒有（應排在最後）");

            if (rows.Count > 1 && rows[0].LiveSeconds is null)
                Problem("有預估時間的應該排在前面");
        }
        catch (Exception ex)
        {
            Problem($"真實 ETA payload 渲染失敗：{ex.GetType().Name}: {ex.Message}");
        }

        // ── 大群組迴歸測試：很多同名站牌的站區必須完整還原 ──
        Console.WriteLine();
        Console.WriteLine("▶ 邊界情況 E  大量同名站牌的完整還原");
        foreach (var kw in new[] { "國立臺中科技大學", "靜宜大學", "臺中車站", "中友百貨" })
        {
            var gs = data.Search.SearchGrouped(kw).ToList();
            if (gs.Count == 0) continue;

            var g0 = gs[0];
            var expected = data.GetGroupByShortKey(g0.GroupKey)?.StopUids.Count ?? 0;
            if (expected == 0) continue;

            var opts = SelectOptions(BusUi.SearchComponents(true, new[] { g0 }, data, Array.Empty<string>()));
            var resolved = BusUi.ResolveStopValues(opts.Select(o => o.Value), data);

            if (resolved.Count < expected)
                Problem($"「{kw}」群組有 {expected} 個同名站牌，但選項只能還原 {resolved.Count} 個（值可能被截斷）");
            else
                Console.WriteLine($"  ✔ 「{kw}」{expected} 個同名站牌 → {opts.Count} 個選項，全部可還原");
        }

        // ── 邊界情況 G：縮寫與簡體輸入（用真實資料集）──
        //     使用者實測回報：打「台中科大」找不到「國立臺中科技大學」。
        //     原因有兩個，這裡各自驗一次：
        //       1) 縮寫（中間漏字）以前只算弱相符 → 被「只顯示強相符」的過濾器藏起來
        //       2) 簡→繁用 Windows 的表會挑錯字（乾淨→乾净），所以改成統一折疊成簡體
        Console.WriteLine();
        Console.WriteLine("▶ 邊界情況 G  縮寫與簡體輸入（打「台中科大」要找到「國立臺中科技大學」）");

        const string target = "國立臺中科技大學";
        var hasTarget = data.Search.Search(target, 25).Any(h => h.Entry.DisplayName == target);

        if (!hasTarget)
        {
            Console.WriteLine($"  （這份資料集沒有「{target}」，略過）");
        }
        else
        {
            foreach (var (query, note) in new[]
                     {
                         ("台中科大", "縮寫（中間漏「技」）"),
                         ("中科技大", "連續子字串"),
                         ("台中科技大学", "簡體、省略「國立」"),
                         ("国立台中科技大学", "簡體全名"),
                     })
            {
                var groups = data.Search.SearchGrouped(query).ToList();
                var (shown, _) = BusUi.PreferStrongMatches(groups);
                var hit = shown.FirstOrDefault(g => g.DisplayName == target);

                if (hit is null)
                {
                    var reason = groups.Any(g => g.DisplayName == target)
                        ? "找到了，但被判為雜訊而被過濾掉"
                        : "完全找不到";
                    Problem($"[{note}] 搜「{query}」→ {reason}");
                    continue;
                }

                var strongest = hit.Hits.Max(h => h.Kind);
                var mark = hit.IsDefaultPick ? "☑" : "▪";
                Console.WriteLine($"  {mark} [{note}] 搜「{query}」→ {hit.DisplayName}" +
                                  $"（{hit.Hits.Count} 個站牌，{strongest}，最高分 {hit.BestScore}，" +
                                  $"預設勾選={hit.IsDefaultPick}）");

                // 使用者要的是「打了就選得到」—— 縮寫必須是**預設勾選**，不能只是顯示出來
                if (note.StartsWith("縮寫") && !hit.IsDefaultPick)
                    Problem($"[{note}] 搜「{query}」只顯示卻沒預設勾選（{strongest}）");
            }
        }
    }

    // ─────────────────────────────────────────────────────
    //  輪詢計畫與點數估算
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 印出**實際會送出的輪詢請求**與點數估算。
    ///
    /// 為什麼要做這個：點數是這個專案唯一會花錢的地方，
    /// 而「一次呼叫涵蓋幾個站」「回傳幾筆」這種事不該只能靠猜。
    /// 這裡用真實靜態資料算出預估筆數、用真實 payload 算出每筆大小。
    /// </summary>
    private static void PrintPollPlan(
        TaichungBusDataService data, IReadOnlyCollection<string> pollStops, BotConfig cfg)
    {
        Console.WriteLine();
        Console.WriteLine("▶ Step 8b  輪詢計畫（唯一會花點數的地方）");

        if (pollStops.Count == 0)
        {
            Console.WriteLine("  （沒有訂閱 → 完全不呼叫 API）");
            return;
        }

        const int batchSize = 40;
        var batches = (pollStops.Count + batchSize - 1) / batchSize;
        var expectedRecords = pollStops.Sum(data.GetRouteCount);

        var recordBytes = MeasureEtaRecordBytes();
        var bytesPerCycle = expectedRecords * recordBytes;
        var interval = cfg.PollIntervalSeconds;

        var callsPerDay = 86_400.0 / interval * batches;
        var mbPerDay = 86_400.0 / interval * bytesPerCycle / 1024 / 1024;
        var pointsPerMonth = (callsPerDay * 30 / 1500.0) + (mbPerDay * 30 / 150.0);

        Console.WriteLine($"  {pollStops.Count} 個不重複的上車站 → 每週期 {batches} 次呼叫（每批 {batchSize} 站）");
        Console.WriteLine($"  預估回傳 {expectedRecords} 筆 ETA（每筆約 {recordBytes} bytes，已用 $select 精簡）");
        Console.WriteLine($"  間隔 {interval} 秒 → {callsPerDay:N0} 次/日、約 {mbPerDay:N2} MB/日（未壓縮）");
        Console.WriteLine($"  點數估算：呼叫 {callsPerDay * 30 / 1500.0:N1} ＋ 資料量 {mbPerDay * 30 / 150.0:N1}" +
                          $" = 約 {pointsPerMonth:N0} 點/月");
        Console.WriteLine("  （官方公式：呼叫次數 / 1,500 ＋ 回傳資料量(MB) / 150）");
        Console.WriteLine($"  ★ 這裡只有 {pollStops.Count} 個上車站；" +
                          "不論幾個使用者關注同一個站，呼叫次數都不變（100 人共用 1 次呼叫）");
        Console.WriteLine();
        Console.WriteLine("  實際請求（第一批，可以自己拿去驗證）：");

        var requestPath = TdxApiClient.BuildEtasByStopsUrl("Taichung", pollStops.Take(batchSize));
        var fullUrl = "https://tdx.transportdata.tw" + requestPath;
        Console.WriteLine($"    {fullUrl[..Math.Min(fullUrl.Length, 300)]}" +
                          (fullUrl.Length > 300 ? "…" : ""));

        // 滿批的 URL 長度：這是「一批最多幾個站」的真正限制（不是 API 的規則，是 URL 長度）
        var fullBatchUrl = TdxApiClient.BuildEtasByStopsUrl(
            "Taichung", Enumerable.Range(1, batchSize).Select(i => $"TXG{i:D5}"));
        Console.WriteLine($"    （這批 URL {fullUrl.Length} 字元；滿批 {batchSize} 站約 {fullBatchUrl.Length} 字元，" +
                          "還離 URL 長度上限很遠）");
    }

    /// <summary>
    /// 用真實 TDX payload 量「$select 之後一筆 ETA 大約幾個 bytes」。
    ///
    /// 做法：把 fixture 的每一筆，只挑出 <see cref="TdxApiClient.EtasByStopsFields"/>
    /// 列的欄位重新序列化一次，跟完整 payload 的平均大小比較。
    /// 這是估計值（TDX 的序列化細節可能略有不同），但足以判斷量級。
    /// </summary>
    private static int MeasureEtaRecordBytes()
    {
        const int fallback = 300;

        try
        {
            var path = Path.Combine(MiniFixtureSource.ResolveRoot(), "real",
                                    "EstimatedTimeOfArrival.sample.json");
            var raw = File.ReadAllText(path);
            var records = System.Text.Json.JsonSerializer.Deserialize<List<BusEta>>(
                raw, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (records is null or { Count: 0 }) return fallback;

            var wanted = TdxApiClient.EtasByStopsFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var props = typeof(BusEta).GetProperties()
                                      .Where(p => wanted.Contains(p.Name))
                                      .ToArray();

            var projected = records.Average(e =>
            {
                var dict = props.ToDictionary(p => p.Name, p => p.GetValue(e));
                return (double)System.Text.Json.JsonSerializer.Serialize(dict).Length;
            });

            var fullBytes = (double)raw.Length / records.Count;
            var estimated = (int)Math.Round(projected);

            Console.WriteLine($"  （實測：完整一筆約 {fullBytes:N0} bytes、" +
                              $"$select 後約 {estimated} bytes → 少 {(1 - projected / fullBytes) * 100:N0}%）");
            return estimated;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>把元件裡的 Select Menu 選項抽出來（驗證標籤與值用）。</summary>
    private static List<(string Label, string Value, bool Default)> SelectOptions(MessageComponent components)
    {
        var list = new List<(string, string, bool)>();

        foreach (var row in components.Components.OfType<ActionRowComponent>())
            foreach (var comp in row.Components)
                if (comp is SelectMenuComponent menu)
                    foreach (var o in menu.Options)
                        list.Add((o.Label, o.Value, o.IsDefault == true));

        return list;
    }

    private static List<string> Resolve(TaichungBusDataService data, IEnumerable<string> values)
    {
        var uids = new List<string>();
        foreach (var v in values)
        {
            if (v.StartsWith("g:", StringComparison.Ordinal))
                uids.AddRange(data.GetGroupByShortKey(v[2..])?.StopUids ?? Array.Empty<string>());
            else if (v.StartsWith("s:", StringComparison.Ordinal))
                uids.Add(v[2..]);
        }
        return uids.Distinct(StringComparer.Ordinal).ToList();
    }

    // ─────────────────────────────────────────────────────
    //  印出 + 驗證
    // ─────────────────────────────────────────────────────

    private static void Print(Embed[] embeds, MessageComponent components)
    {
        foreach (var e in embeds) Validate(e, components);

        var first = true;
        foreach (var e in embeds)
        {
            Print(e, first ? components : new ComponentBuilder().Build(), validate: false);
            first = false;
        }
        Console.WriteLine();
    }

    private static void Print(Embed embed, MessageComponent components, bool validate = true)    {
        Console.WriteLine("  ┌── Embed ─────────────────────────────────────────────");
        if (embed.Title is { Length: > 0 }) Console.WriteLine($"  │ {embed.Title}");
        if (embed.Description is { Length: > 0 })
            foreach (var line in embed.Description.Split('\n'))
                Console.WriteLine($"  │ {line}");

        foreach (var f in embed.Fields)
        {
            Console.WriteLine($"  │ 【{f.Name}】");
            foreach (var line in f.Value.Split('\n'))
                Console.WriteLine($"  │   {line}");
        }

        if (embed.Footer is { } footer && footer.Text is { Length: > 0 })
            Console.WriteLine($"  │ — {footer.Text}");
        Console.WriteLine("  └──────────────────────────────────────────────────────");

        foreach (var row in components.Components.OfType<ActionRowComponent>())
        {
            var parts = new List<string>();
            foreach (var comp in row.Components)
            {
                switch (comp)
                {
                    case ButtonComponent b:
                        parts.Add($"[{b.Label}]");
                        break;
                    case SelectMenuComponent s:
                        parts.Add($"[▼ {s.Placeholder} ({s.Options.Count} 選項)]");
                        foreach (var o in s.Options.Take(8))
                            parts.Add($"\n        {(o.IsDefault == true ? "☑" : "☐")} {o.Label}  →  {o.Value}");
                        if (s.Options.Count > 8) parts.Add($"\n        … 另有 {s.Options.Count - 8} 個選項");
                        break;
                }
            }
            Console.WriteLine("  " + string.Join("  ", parts));
        }

        if (validate) Validate(embed, components);
    }

    private static void Validate(Embed embed, MessageComponent components)
    {
        if (embed.Title is { Length: > 256 }) Problem($"embed title 超過 256（{embed.Title.Length}）");
        if (embed.Description is { Length: > 4096 }) Problem($"embed description 超過 4096（{embed.Description.Length}）");
        if (embed.Fields.Length > 25) Problem($"embed 欄位超過 25（{embed.Fields.Length}）");
        if (embed.Footer?.Text is { Length: > 2048 }) Problem("embed footer 超過 2048");

        foreach (var f in embed.Fields)
        {
            if (f.Name.Length > 256) Problem($"欄位名稱超過 256：{f.Name}");
            if (f.Value.Length > 1024) Problem($"欄位內容超過 1024：{f.Name}");
        }

        var total = (embed.Title?.Length ?? 0) + (embed.Description?.Length ?? 0)
                    + embed.Fields.Sum(f => f.Name.Length + f.Value.Length)
                    + (embed.Footer?.Text.Length ?? 0);
        if (total > 6000) Problem($"embed 總字數超過 6000（{total}）");

        var rows = components.Components.OfType<ActionRowComponent>().ToList();
        if (rows.Count > 5) Problem($"ActionRow 超過 5 列（{rows.Count}）");

        foreach (var row in rows.OfType<ActionRowComponent>())
        {
            var list = row.Components;
            var hasSelect = list.Any(c => c is SelectMenuComponent);

            if (hasSelect && list.Count > 1) Problem("Select 必須單獨一列（不能與按鈕併排）");
            if (!hasSelect && list.Count > 5) Problem($"一列按鈕超過 5 個（{list.Count}）");

            foreach (var comp in list)
            {
                switch (comp)
                {
                    case ButtonComponent b:
                        if (string.IsNullOrEmpty(b.CustomId)) Problem("按鈕缺少 custom_id");
                        else if (b.CustomId.Length > 100) Problem($"custom_id 超過 100：{b.CustomId}");
                        if (b.Label.Length > 80) Problem($"按鈕文字超過 80：{b.Label}");
                        RecordId(b.CustomId);
                        break;

                    case SelectMenuComponent s:
                        if (s.CustomId.Length > 100) Problem($"select custom_id 超過 100：{s.CustomId}");
                        if (s.Options.Count > 25) Problem($"選項超過 25 個（{s.Options.Count}）");
                        if (s.Options.Count == 0) Problem("select 沒有任何選項");
                        if (s.MaxValues > s.Options.Count) Problem($"max_values（{s.MaxValues}）大於選項數（{s.Options.Count}）");
                        foreach (var o in s.Options)
                        {
                            if (string.IsNullOrEmpty(o.Label)) Problem("選項缺少 label");
                            if (o.Label.Length > 100) Problem($"選項 label 超過 100：{o.Label}");
                            if (o.Value.Length > 100) Problem($"選項 value 超過 100：{o.Value}");
                            if (o.Description is { Length: > 100 }) Problem($"選項 description 超過 100：{o.Description}");
                        }
                        RecordId(s.CustomId);
                        break;
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────
    //  接線檢查：按鈕 ↔ 處理函式
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 為什麼要做這個：曾經發生過「處理函式寫好了、按鈕也定義好了，
    /// 但實際送出的那一組元件沒有把它加進去」—— 使用者端看到的是
    /// 「這個功能根本不存在」。純看元件限制的檢查抓不到這種錯，
    /// 因為壞掉的不是限制，而是**接線**。
    ///
    /// 這裡做雙向檢查：
    ///   1. 畫面上出現的每個 custom_id，都要有處理函式（否則按了沒反應）
    ///   2. 每個處理函式，都要在某一輪驗證中真的出現過（否則使用者按不到）
    /// </summary>
    private static void AuditWiring()
    {
        Console.WriteLine();
        Console.WriteLine("▶ 接線檢查  按鈕 ↔ 處理函式");

        var handlers = Handlers();
        var orphans = handlers
            .Where(h => !SeenIds.Any(seen => Matches(h, seen)))
            .Where(h => !ReachableOnlyFromSlashCommand(h))
            .ToList();

        Console.WriteLine($"  畫面上出現 {SeenIds.Count} 種 custom_id；模組註冊 {handlers.Length} 個處理函式");

        if (orphans.Count > 0)
        {
            foreach (var o in orphans)
                Problem($"處理函式 `{o}` 沒有出現在任何畫面上 —— 使用者按不到這個功能");
        }
        else
        {
            Console.WriteLine("  ✔ 每個處理函式都有對應的按鈕／選單");
        }

        var dead = SeenIds.Where(id => !handlers.Any(h => Matches(h, id))).ToList();
        if (dead.Count > 0)
        {
            foreach (var d in dead)
                Problem($"按鈕 `{d}` 找不到處理函式 —— 按了不會有反應");
        }
        else
        {
            Console.WriteLine("  ✔ 每個按鈕／選單都有對應的處理函式");
        }
    }

    /// <summary>
    /// 這些 custom_id 不是由「訊息上的元件」觸發，所以不會出現在畫面上：
    /// Modal 是 <c>RespondWithModalAsync&lt;T&gt;(id)</c> 直接開的，
    /// 而 <c>bus:panel</c> 是「沒有任何訂閱時」的清單按鈕（本驗證一定有訂閱）。
    /// </summary>
    private static bool ReachableOnlyFromSlashCommand(string id)
        => id is Cid.ModalOrigin or Cid.ModalDest or Cid.SaveGroupModal
           or Cid.GroupRenameModal or Cid.GroupMergeModal
           || Matches(Cid.Panel, id);

    private static string[] Handlers()
        => _handlers ??= typeof(BusComponentModule)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes(inherit: true))
            .Select(a => a switch
            {
                ComponentInteractionAttribute c => c.CustomId,
                ModalInteractionAttribute m => m.CustomId,
                _ => null
            })
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void RecordId(string customId)
    {
        if (!string.IsNullOrEmpty(customId)) SeenIds.Add(customId);
    }

    /// <summary>與 Discord.Net 相同的 wildcard 規則：<c>*</c> 對應「一個」冒號分隔的段落。</summary>
    private static bool Matches(string pattern, string id)
    {
        if (string.Equals(pattern, id, StringComparison.Ordinal)) return true;

        var p = pattern.Split(':');
        var v = id.Split(':');
        if (p.Length != v.Length) return false;

        for (var i = 0; i < p.Length; i++)
            if (p[i] != "*" && !string.Equals(p[i], v[i], StringComparison.Ordinal))
                return false;

        return true;
    }

    /// <summary>與 BotRuntime.DescribeStatus 相同邏輯（DryRun 無法直接呼叫 private 方法）。</summary>
    private static string DescribeForTest(BusEta? eta, double? live)
    {
        if (live is not null) return "";
        if (eta is null) return "目前查不到";
        return eta.StopStatus switch
        {
            0 => "無預估時間",
            1 => eta.NextBusTime is { } t
                    ? $"尚未發車（{TimeZoneInfo.ConvertTime(t, TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei")):HH:mm} 發車）"
                    : "尚未發車",
            2 => "交管不停靠",
            3 => "末班車已過",
            4 => "今日未營運",
            _ => $"其他狀態（{eta.StopStatus}）"
        };
    }

    private static void Problem(string message)
    {
        _problems++;
        Console.WriteLine($"  ⚠️  {message}");
    }
}
