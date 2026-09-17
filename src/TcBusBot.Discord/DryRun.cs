using System.Reflection;
using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Chat;
using TcBusBot.Core.Configuration;
using TcBusBot.Core.DataSources;
using TcBusBot.Core.Models;
using TcBusBot.Core.Realtime;
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

        // ── Step 12b：/bus end 結束追蹤（可以復原）──────────
        Console.WriteLine();
        Console.WriteLine("▶ Step 12b  `/bus end` 結束追蹤 → 一次取消全部訂閱（可以 ↩️ 復原）");

        var endedGroups = subs.GetGroupsByUser(session.UserId).ToList();
        var endedSubs = endedGroups.SelectMany(subs.GetSubscriptions).ToList();

        var endUndo = new UndoStack();
        var removed = subs.RemoveAllForUser(session.UserId);
        var removedSubs = removed.SelectMany(r => r.Subscriptions).ToList();

        endUndo.Push(new UndoEndedTracking(
            $"結束追蹤（{removed.Count} 組訂閱）", removed, session.CreatedGroupId));

        Print(BusUi.TrackingEnded(
                  removed.Count,
                  removedSubs.Count,
                  removedSubs.Select(s => s.BoardStopUid).Distinct(StringComparer.Ordinal).Count(),
                  removedSubs.Select(s => (s.RouteUid, s.Direction)).Distinct().Count()),
              BusUi.EndComponents(removed.Count));

        if (subs.GetGroupsByUser(session.UserId).Any() || removed.Count != endedGroups.Count)
            Problem($"/bus end 沒有把訂閱清乾淨：剩下 {subs.GetGroupsByUser(session.UserId).Count()} 組" +
                    $"（原本 {endedGroups.Count} 組，移除 {removed.Count} 組）");
        else
            Console.WriteLine($"  ✔ 已停止 {removed.Count} 個訂閱群組、{removedSubs.Count} 筆訂閱");

        Console.WriteLine();
        Console.WriteLine("▶ Step 12b-2  按「↩️ 復原」→ 訂閱整個回來（id 與通知去重狀態都保留）");

        var endUndoMessage = endUndo.Undo(subs, store: null!, session.UserId);
        Console.WriteLine($"  ↩️ 復原：{endUndoMessage}");

        var restoredGroups = subs.GetGroupsByUser(session.UserId).ToList();
        var restoredIds = restoredGroups.Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var expectedIds = endedGroups.Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (restoredGroups.Count != endedGroups.Count)
            Problem($"復原結束追蹤後群組數不符：{restoredGroups.Count} != {endedGroups.Count}");
        else if (!restoredIds.SequenceEqual(expectedIds))
            Problem("復原結束追蹤後群組 id 變了 —— 應該放回原本的物件，否則去重狀態會失效");
        else if (!restoredGroups.SelectMany(subs.GetSubscriptions).Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(endedSubs.Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal)))
            Problem("復原結束追蹤後訂閱內容不完整");
        else
            Console.WriteLine($"  ✔ 復原後 {restoredGroups.Count} 個群組、" +
                              $"{restoredGroups.SelectMany(subs.GetSubscriptions).Count()} 筆訂閱都回來了");

        Print(BusUi.SubscriptionList(restoredGroups, g => subs.GetSubscriptions(subs.GetGroup(g)!)),
              BusUi.SubscriptionListComponents(restoredGroups));

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

        // ── 面板與 LLM 共用同一份站牌解析 ────────────────────
        AuditStopPicks(data);

        // ── 模型「幫使用者按按鈕」（UI 動作）──────────────────
        AuditUiTools(data);

        // ── 偷聽模式的入口（「後面的訊息全被忽略」的那個 bug）────
        AuditEavesdropWiring();

        // ── DI 容器（與真正的 Bot 用同一份註冊程式碼）──────────
        using var client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            LogLevel = LogSeverity.Critical
        });

        var provider = AuditDependencyInjection(cfg, data, client);

        // ── 指令樹（斜線指令真的註冊得起來嗎）───────────────
        if (provider is not null) AuditCommandTree(cfg, data, provider);

        // ── LLM 設定（不連線，只驗設定本身合不合理）─────────
        AuditLlmConfig(cfg);

        // ── 環境變數清單（env-vars.csv 與程式是否一致）────────
        AuditEnvCsv();

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

    // ─────────────────────────────────────────────────────
    //  指令樹：斜線指令真的註冊得起來嗎
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 用 Discord.Net 自己的 <see cref="InteractionService"/> 把三個模組組起來，
    /// 確認**指令樹建得起來**，並印出使用者會在 Discord 看到的樣子。
    ///
    /// 為什麼需要這個：<c>AddModuleAsync</c> 失敗（指令名稱不合規則、選項名稱有大寫、
    /// 同一個名字註冊兩次、選項描述為空…）是**啟動後才會炸**的錯，
    /// 而且炸在 <c>Ready</c> 事件裡 —— 使用者只會看到「指令不見了」，console 也不一定看得到。
    /// 這裡完全不連線，只建樹，離線就先抓到。
    /// </summary>
    private static void AuditCommandTree(BotConfig cfg, TaichungBusDataService data, IServiceProvider services)
    {
        Console.WriteLine();
        Console.WriteLine("▶ 指令樹檢查  斜線指令 ↔ 模組註冊");

        var client = services.GetRequiredService<DiscordSocketClient>();
        var interactions = services.GetRequiredService<InteractionService>();

        try
        {
            interactions.AddModuleAsync<BusModule>(services).GetAwaiter().GetResult();
            interactions.AddModuleAsync<BusComponentModule>(services).GetAwaiter().GetResult();
            interactions.AddModuleAsync<SayModule>(services).GetAwaiter().GetResult();
            interactions.AddModuleAsync<ChatModule>(services).GetAwaiter().GetResult();
            interactions.AddModuleAsync<ResetModule>(services).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Problem($"指令樹建立失敗（Discord 會直接拒絕這份指令）：{ex.GetType().Name}: {ex.Message}");
            return;
        }

        var commands = interactions.SlashCommands
            .OrderBy(CommandPath, StringComparer.Ordinal)
            .ToList();

        foreach (var command in commands)
            PrintCommand(command);

        // ── /bus 這個群組本身：一定要是「群組」，不然子指令會變成
        //     /panel、/list 這種散落在最上層的指令（設定 [Group] 掉字就會這樣）──
        var busGroup = interactions.Modules.FirstOrDefault(m => m.SlashGroupName == "bus");
        if (busGroup is null || !busGroup.IsSlashGroup)
            Problem("/bus 沒有被註冊成指令群組（[Group(\"bus\")] 掉了嗎？）");
        else
            Console.WriteLine($"  ✔ /bus 是一個指令群組，底下 {busGroup.SlashCommands.Count} 個子指令");

        var subNames = commands
            .Where(c => CommandPath(c).StartsWith("bus ", StringComparison.Ordinal))
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);

        var expectedSubs = new[] { "panel", "list", "groups", "next", "status", "end" };
        var missingSubs = expectedSubs.Where(e => !subNames.Contains(e)).ToList();

        if (missingSubs.Count > 0)
            Problem($"/bus 缺少子指令：{string.Join("、", missingSubs)}");
        else
            Console.WriteLine($"  ✔ /bus 的 {expectedSubs.Length} 個子指令都在" +
                              $"（{string.Join("、", expectedSubs)}）");

        // ── /ai（AI 聊天）：status 與 forget 都要在 ──────────
        var aiGroup = interactions.Modules.FirstOrDefault(m => m.SlashGroupName == "ai");
        if (aiGroup is null || !aiGroup.IsSlashGroup)
        {
            Problem("/ai 沒有被註冊成指令群組（AI 聊天的查詢與清除記憶會按不到）");
        }
        else
        {
            var aiSubs = commands
                .Where(c => CommandPath(c).StartsWith("ai ", StringComparison.Ordinal))
                .Select(c => c.Name)
                .ToHashSet(StringComparer.Ordinal);

            var expectedAi = new[] { "status", "forget", "learned", "audit", "pset" };
            var missingAi = expectedAi.Where(e => !aiSubs.Contains(e)).ToList();

            if (missingAi.Count > 0)
                Problem($"/ai 缺少子指令：{string.Join("、", missingAi)}");
            else
                Console.WriteLine($"  ✔ /ai 的 {expectedAi.Length} 個子指令都在（{string.Join("、", expectedAi)}）");

            // ── /ai pset：管理員直接設定這個伺服器的提示詞 ──────
            var pset = commands.FirstOrDefault(c => CommandPath(c) == "ai pset");

            if (pset is null)
            {
                Problem("找不到 /ai pset —— 管理員沒辦法直接設定伺服器的提示詞");
            }
            else
            {
                var opts = pset.Parameters.Select(p => p.Name).ToList();
                Console.WriteLine($"  ℹ /ai pset 參數：{string.Join("、", opts)}");

                if (!opts.Contains("text") || !opts.Contains("mode") || !opts.Contains("clear"))
                    Problem("/ai pset 少了參數（需要 text／mode／clear）");
                else if (pset.Parameters.Any(p => p.IsRequired))
                    Problem("/ai pset 不該有必填參數 —— 不給參數時應該顯示目前的設定");
            }

            // ★ 授權一定要真的擋在指令裡（不是只寫在文件上）
            var psetAsync = typeof(ChatModule).GetMethod(nameof(ChatModule.PersonaSetAsync));
            var psetCalls = psetAsync is null ? [] : CollectCalls(psetAsync, resolveAll: true);

            if (psetAsync is null)
                Problem("找不到 ChatModule.PersonaSetAsync");
            else if (!psetCalls.Contains("LlmOptions.get_AdminUserIds"))
                Problem("/ai pset 沒有檢查 LLM_ADMIN_IDS —— 任何人都能改伺服器的提示詞");
            else if (!psetCalls.Contains("GuildPersonaStore.SetRules"))
                Problem("/ai pset 沒有走 GuildPersonaStore.SetRules（覆蓋模式的安全順序會失效）");
            else
                Console.WriteLine("  ✔ /ai pset 真的會擋人（檢查 LLM_ADMIN_IDS）而且走的是可測試的 SetRules");
        }

        // ── /rest（重設這個伺服器學到的規矩）────────────────
        if (commands.All(c => CommandPath(c) != "rest"))
            Problem("找不到 /rest 指令 —— 使用者沒辦法把「教壞的」提示詞重設");
        else
            Console.WriteLine("  ✔ /rest 已註冊（重設這個伺服器學到的規矩）");

        // ── /say 是這次新加的：一定要在，而且只能有一個必填的字串參數 ──
        var say = commands.FirstOrDefault(c => CommandPath(c) == "say");
        if (say is null)
        {
            Problem("找不到 /say 指令 —— 使用者根本打不出來");
        }
        else
        {
            var options = say.Parameters.ToList();

            if (options.Count != 1 || options[0].Name != "message")
                Problem($"/say 的參數應該只有一個 message，實際 " +
                        $"[{string.Join(", ", options.Select(o => o.Name))}]");
            else if (!options[0].IsRequired || options[0].DiscordOptionType != ApplicationCommandOptionType.String)
                Problem($"/say 的 message 應該要是必填字串，實際 {options[0].DiscordOptionType}／" +
                        $"{(options[0].IsRequired ? "必填" : "選填")}");
            else
                Console.WriteLine($"  ✔ /say 有一個必填的字串參數（最多 " +
                                  $"{options[0].MaxLength ?? 0} 字，剛好是 Discord 的訊息上限）");

            if (say.Description.Length is 0 or > 100)
                Problem($"/say 的說明長度不合法：{say.Description.Length} 字（必須 1~100）");
        }

        AuditSayHiddenFlow();

        // ── 名稱一律小寫、說明不超過 100 字（Discord 的硬限制）──
        foreach (var command in commands)
        {
            var path = CommandPath(command);

            if (command.Name != command.Name.ToLowerInvariant())
                Problem($"指令名稱必須是小寫：/{path}");

            if (command.Description.Length is 0 or > 100)
                Problem($"/{path} 的說明長度不合法（{command.Description.Length} 字，必須 1~100）");

            foreach (var option in command.Parameters)
            {
                if (option.Name != option.Name.ToLowerInvariant())
                    Problem($"/{path} 的參數名稱必須是小寫：{option.Name}");

                if (option.Description is { Length: > 100 })
                    Problem($"/{path} {option.Name} 的說明超過 100 字");
            }

            if (command.Parameters.Count > 25)
                Problem($"/{path} 的參數超過 25 個：{command.Parameters.Count}");
        }

        AuditSayAllowList();
    }

    /// <summary>
    /// `/say` 必須「不留使用指令的痕跡」。
    ///
    /// 為什麼要用這麼硬的方式驗：直接用 <c>RespondAsync</c> 回應的話，Discord 會在訊息上方掛一行
    /// 「@某某 使用了 /say」—— 那就等於把「這是有人叫 Bot 說的」寫在頻道上。
    /// 正確做法是 `DeferAsync(ephemeral)` → `Channel.SendMessageAsync`（普通訊息）→
    /// `DeleteOriginalResponseAsync`（把暫存回應刪掉）。
    ///
    /// 這件事**在畫面上驗不到**（DryRun 沒有真的互動可以按，要模擬得先架一整套 socket），
    /// 所以直接讀編譯出來的 IL，把 <c>SendSilentlyAsync</c> 實際呼叫到的其他方法列出來：
    /// 該有的要在、不該有的（<c>RespondAsync</c>／<c>FollowupAsync</c>）不能出現。
    /// 這樣連「未來的自己順手改回 RespondAsync」都會被擋下來。
    ///
    /// ⚠️ 一定要掃**非同步狀態機**的 <c>MoveNext</c>：async 方法自己的 IL 只有
    /// 「建狀態機 + Start」三行，內容全在編譯器產生的 <c>&lt;SendSilentlyAsync&gt;d__N</c> 裡。
    /// （第一次寫這個檢查時就是掃錯方法，什麼都沒掃到而誤判成「少了必要的呼叫」。）
    /// </summary>
    private static void AuditSayHiddenFlow()
    {
        var send = typeof(SayModule).GetMethod("SendSilentlyAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (send is null)
        {
            Problem("找不到 SayModule.SendSilentlyAsync —— 無法驗證 /say 不留痕跡");
            return;
        }

        // 呼叫端也一起驗：內容只能走 SendSilentlyAsync，不能有人繞過去
        var say = typeof(SayModule).GetMethod(nameof(SayModule.SayAsync));
        var sayCalls = say is null ? [] : CollectCalls(say);

        if (!sayCalls.Contains("SayModule.SendSilentlyAsync"))
            Problem("/say 沒有走 SendSilentlyAsync —— 內容可能被直接用 RespondAsync 送出去");

        var calls = CollectCalls(send);
        Console.WriteLine($"  ℹ /say 的送出流程會呼叫：{string.Join("、", calls)}");

        var forbidden = calls
            .Where(c => c is "InteractionModuleBase`1.RespondAsync"
                        or "InteractionModuleBase`1.FollowupAsync"
                        or "InteractionModuleBase`1.RespondWithFileAsync")
            .ToList();

        if (forbidden.Count > 0)
            Problem($"/say 用 {string.Join("、", forbidden)} 送內容 —— " +
                    "這會在頻道上留下「@某某 使用了 /say」的回應標頭");

        var required = new[]
        {
            "InteractionModuleBase`1.DeferAsync",
            "ISocketMessageChannel.SendMessageAsync",
            "InteractionModuleBase`1.DeleteOriginalResponseAsync"
        };
        var missing = required.Where(r => !calls.Contains(r)).ToList();

        if (missing.Count > 0)
            Problem($"/say 少了不留痕跡必要的呼叫：{string.Join("、", missing)}");
        else if (forbidden.Count == 0 && sayCalls.Contains("SayModule.SendSilentlyAsync"))
            Console.WriteLine("  ✔ /say 不留痕跡：只有自己看得到的 defer → 頻道普通訊息 → 刪掉暫存回應" +
                              "，全程沒有「使用了 /say」的回應標頭");
    }

    /// <summary>
    /// 把一個方法（若是 async 就自動找它的狀態機）實際呼叫到的其他方法列出來。
    ///
    /// 這是「線性掃描位元組」而不是完整反組譯，所以會用
    /// 「解析出來的方法真的屬於預期的型別」來過濾雜訊 ——
    /// 剛好掃到別人運算元裡的位元組時，解析結果不會是
    /// <c>InteractionModuleBase</c>／<c>ISocketMessageChannel</c>／本模組的方法，會直接被丟掉。
    /// </summary>
    private static List<string> CollectCalls(MethodInfo method, bool resolveAll = false)
    {
        var target = method;

        var stateMachine = method
            .GetCustomAttributes(inherit: false)
            .OfType<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()
            .FirstOrDefault();

        if (stateMachine is not null)
        {
            var moveNext = stateMachine.StateMachineType.GetMethod("MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (moveNext is not null) target = moveNext;
        }

        var il = target.GetMethodBody()?.GetILAsByteArray();
        if (il is null) return [];

        var calls = new List<string>();

        // 只認 call(0x28)／callvirt(0x6F)
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F)) continue;

            var token = BitConverter.ToInt32(il, i + 1);
            MethodBase? called;
            try { called = target.Module.ResolveMethod(token); }
            catch (Exception) { continue; }

            var owner = called?.DeclaringType?.Name ?? "";
            var interesting = resolveAll
                              || owner == nameof(SayModule)
                              || owner.StartsWith("InteractionModuleBase", StringComparison.Ordinal)
                              || owner is "ISocketMessageChannel" or "IMessageChannel";

            if (called is not null && interesting) calls.Add($"{owner}.{called.Name}");

            i += 4;
        }

        return calls.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// <c>/say</c> 的允許名單。
    ///
    /// 為什麼要驗這個：名單寫錯就是**洗頻破口** ——
    /// 少擋一個人等於任何人都能叫 Bot 去 @everyone（雖然 mention 已經被關掉了，
    /// 但還是能洗頻）。解析規則是「逗號／分號／空白都算分隔、壞掉的項目直接忽略」，
    /// 這剛好是最容易寫錯的地方。
    /// </summary>
    private static void AuditSayAllowList()
    {
        var previous = Environment.GetEnvironmentVariable(SayModule.AllowListVariable);

        try
        {
            Environment.SetEnvironmentVariable(SayModule.AllowListVariable, null);
            if (!SayModule.IsAllowed(1UL))
                Problem($"沒有設定 {SayModule.AllowListVariable} 時，所有人都應該可以用 /say");

            Environment.SetEnvironmentVariable(SayModule.AllowListVariable, "123, 456;789\t壞掉的字");
            var allowed = SayModule.AllowedUsers();

            if (!allowed.SequenceEqual(new[] { 123UL, 456UL, 789UL }))
                Problem($"允許名單解析錯誤（預期 123,456,789）：{string.Join(",", allowed)}");
            else if (SayModule.IsAllowed(999UL))
                Problem("允許名單沒有擋掉不在名單上的人");
            else if (!SayModule.IsAllowed(456UL))
                Problem("允許名單把名單上的人擋掉了");
            else
                Console.WriteLine("  ✔ /say 允許名單：未設定 = 所有人可用；設定後只放行名單上的人" +
                                  "（逗號／分號／空白都算分隔）");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SayModule.AllowListVariable, previous);
        }
    }

    /// <summary>
    /// 指令的完整路徑。
    ///
    /// ⚠️ <c>SlashCommandInfo.Name</c> **不含群組名稱** —— 群組模組底下的子指令，
    /// <c>Name</c> 只有 "panel"，是 <c>Module.SlashGroupName</c> 補上 "bus" 才成為 <c>/bus panel</c>。
    /// 直接印 Name 會讓人以為註冊成 <c>/panel</c>（我就被騙過一次）。
    /// </summary>
    private static string CommandPath(SlashCommandInfo command)
        => command.Module is { IsSlashGroup: true, SlashGroupName.Length: > 0 } module
           && !command.IgnoreGroupNames
            ? $"{module.SlashGroupName} {command.Name}"
            : command.Name;

    private static void PrintCommand(SlashCommandInfo command)
    {
        var path = CommandPath(command);
        Console.WriteLine($"  /{path}　{command.Description}");

        foreach (var option in command.Parameters)
        {
            var required = option.IsRequired ? "必填" : "選填";
            var limit = option.MaxLength is { } max ? $"／最多 {max} 字" : "";
            Console.WriteLine($"    └ {option.Name} [{option.DiscordOptionType}／{required}{limit}]" +
                              $"　{option.Description}");
        }
    }

    /// <summary>
    /// LLM 設定檢查（**完全離線**，不會打任何 API）。
    ///
    /// 這裡只驗「設定本身合不合理」——因為這些錯誤在啟動時看不出來，
    /// 要等到第一次有人 @ 它才會炸，而且會炸在使用者面前：
    ///   * base URL 不是 http(s) 或拼錯
    ///   * 模型名稱空白
    ///   * 每週上限小於一次對話的量（等於永遠不能用）
    ///   * 每週上限是 0（= 不限）時要明確講出來，因為那是「錢包沒有煞車」
    /// </summary>
    private static void AuditLlmConfig(BotConfig cfg)
    {
        Console.WriteLine();
        Console.WriteLine("▶ AI 聊天設定檢查（離線，不會呼叫 API）");

        var llm = cfg.Llm;

        if (!llm.IsConfigured)
        {
            Console.WriteLine($"  ℹ 未啟用：{llm.Describe()}");
            Console.WriteLine("     要啟用的話在 .env 放 LLM_API_KEY=<key>（可搭配 LLM_BASE_URL／LLM_MODEL）");
            return;
        }

        Console.WriteLine($"  模型：{llm.Model}");
        Console.WriteLine($"  端點：{llm.BaseUrl}");
        Console.WriteLine($"  金鑰：{llm.MaskedKey}");
        Console.WriteLine($"  每週上限：{(llm.WeeklyTokenLimit > 0 ? $"{llm.WeeklyTokenLimit:N0} tokens" : "不限（0）")}");
        Console.WriteLine($"  模型思考：{llm.ReasoningDescription}" +
                          (llm.Reasoning.Trim().ToLowerInvariant() is "auto"
                              ? "（⚠️ 思考會吃掉輸出額度又算錢：實測同一個問題 out 660 → 20 tokens）"
                              : ""));
        Console.WriteLine($"  時間切段：超過 {llm.SegmentGapMinutes} 分鐘算新的一段" +
                          $"（話題判斷：{(llm.TopicDetect ? "交給 LLM" : "關閉")}）");
        Console.WriteLine($"  上下文：最多 {llm.MaxContextTurns} 則／約 {llm.MaxContextTokens} tokens" +
                          $"（單次輸出上限 {llm.MaxOutputTokens}）");

        if (!Uri.TryCreate(llm.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            Problem($"LLM_BASE_URL 不是有效的 http(s) 網址：{llm.BaseUrl}");
        else if (!uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                 && !uri.AbsolutePath.Contains("/v1", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("  ℹ 提醒：OpenAI 相容端點通常是 …/v1，確認一下有沒有漏掉");

        if (string.IsNullOrWhiteSpace(llm.Model))
            Problem("LLM_MODEL 是空的");

        if (llm.WeeklyTokenLimit > 0 && llm.WeeklyTokenLimit < 1000)
            Problem($"LLM_WEEKLY_TOKENS={llm.WeeklyTokenLimit} 太小 —— " +
                    "一次對話（含話題判斷）大約就要 500~1500 tokens，等於一下子就沒得用了");

        if (llm.WeeklyTokenLimit <= 0)
            Console.WriteLine("  ⚠️  每週上限是 0 = **不限額度**：用量會一直累積，請自行注意帳單");

        if (llm.MaxContextTokens < llm.MaxOutputTokens)
            Console.WriteLine("  ℹ 提醒：上下文上限比單次輸出上限還小，長對話會被裁得很短");

        if (llm.AllowDm)
            Console.WriteLine("  ℹ 私訊也可以聊（LLM_ALLOW_DM=true）——任何人都能私訊消耗全域額度");
        else
            Console.WriteLine("  ℹ 只回伺服器頻道（私訊不回；要開請設 LLM_ALLOW_DM=true）");

        // 額度換算成人看得懂的話
        var perCall = llm.MaxContextTokens + llm.MaxOutputTokens;
        Console.WriteLine($"  ℹ 以最壞情況（每次都用滿上下文 {llm.MaxContextTokens} + 輸出 {llm.MaxOutputTokens}）估算，" +
                          $"每週上限大約可以回 {(llm.WeeklyTokenLimit <= 0 ? "無限" : $"{llm.WeeklyTokenLimit / Math.Max(1, perCall)}")} 則");

        if (!cfg.EnableMessageContentIntent)
            Problem("有設定 LLM 但 Message Content 意圖被關掉了（--no-message-intent）→ 收不到訊息內容，AI 不會回話");
        else
            Console.WriteLine("  ✔ 會要求 Message Content 意圖（開機時會先問 Discord 一次，沒被允許就不要求）");

        // ── 特權意圖的判斷（決定要不要向 Discord 要 Message Content）──
        //    這段位元運算錯了會導致「要了沒被允許的意圖 → 閘道 4014 → 連不上」，
        //    所以就算只是幾個 bit 也要測。
        void Expect(string label, bool ok)
        {
            if (ok) Console.WriteLine($"  ✔ {label}");
            else Problem(label);
        }

        Expect("flags bit18（未驗證 Bot）→ 判定為已開啟 Message Content",
            MessageContentIntentProbe.MessageContentEnabled(1L << 18));
        Expect("flags bit19（已驗證 Bot）→ 判定為已開啟",
            MessageContentIntentProbe.MessageContentEnabled(1L << 19));
        Expect("★ flags=0 → 判定為沒有開啟（這時就不該要求這個意圖）",
            !MessageContentIntentProbe.MessageContentEnabled(0));
        Expect("其他 bit 不會被誤判（例如 Presence 的 bit12）",
            !MessageContentIntentProbe.MessageContentEnabled(1L << 12));

        // Server Members（讀得到伺服器暱稱的那個意圖）——一樣是特權意圖，位元判斷錯了
        // 就會出現「要了沒被允許的意圖 → 4014 → 連不上」，或「默默讀不到暱稱」。
        Expect("Server Members：flags bit14（未驗證 Bot）→ 判定為已開啟",
            MessageContentIntentProbe.GuildMembersEnabled(1L << 14));
        Expect("Server Members：flags bit15（已驗證 Bot）→ 判定為已開啟",
            MessageContentIntentProbe.GuildMembersEnabled(1L << 15));
        Expect("★ Server Members：flags=0 → 判定為沒有開啟（這時就不該要它）",
            !MessageContentIntentProbe.GuildMembersEnabled(0));
        Expect("★ 兩個意圖的位元不會互相誤判（18/19 不等於成員意圖）",
            !MessageContentIntentProbe.GuildMembersEnabled(1L << 18)
            && !MessageContentIntentProbe.MessageContentEnabled(1L << 14));
        Expect("★ 實測值 2621440（本專案的 Bot：Message Content 開、Server Members 關）判得對",
            MessageContentIntentProbe.MessageContentEnabled(2621440)
            && !MessageContentIntentProbe.GuildMembersEnabled(2621440));

        using (var doc = System.Text.Json.JsonDocument.Parse("""{"flags":262144}"""))
            Expect("解析數字型 flags", MessageContentIntentProbe.ParseFlags(doc.RootElement) == 262144);

        using (var doc = System.Text.Json.JsonDocument.Parse("""{"flags":"262144"}"""))
            Expect("解析字串型 flags（Discord 有時會給字串）",
                MessageContentIntentProbe.ParseFlags(doc.RootElement) == 262144);

        using (var doc = System.Text.Json.JsonDocument.Parse("""{"id":"1"}"""))
            Expect("沒有 flags 欄位 → 0（保守判斷成沒開）",
                MessageContentIntentProbe.ParseFlags(doc.RootElement) == 0);

        // ── 名字（暱稱 vs @帳號）────────────────────────────
        //    這決定了「模型認不認得大家在講誰」，也決定判斷器準不準。
        Console.WriteLine($"  ℹ 模型看到的名字：{(llm.ShowNicknames ? "Discord 暱稱（伺服器顯示名稱）" : "@帳號（username）")}" +
                          (llm.ExposeUserIds
                              ? (llm.ShowNicknames ? "＋@帳號＋ID" : "＋ID")
                              : "（不帶 ID）"));

        // 純函式：暱稱優先、關掉時退回帳號、兩個都沒有時給「使用者<ID>」
        Expect("★ 開著時用暱稱（模型才聽得懂「小明剛剛說的」）",
            MentionFormatter.SpeakerName("小明", "wuxiaohan0922", 123UL, showNicknames: true) == "小明");
        Expect("★ 關掉時用 @帳號（同一個人在不同伺服器身分一致）",
            MentionFormatter.SpeakerName("小明", "wuxiaohan0922", 123UL, showNicknames: false) == "wuxiaohan0922");
        Expect("★ 沒有暱稱時退回帳號（DM 沒有暱稱）",
            MentionFormatter.SpeakerName(null, "wuxiaohan0922", 123UL, showNicknames: true) == "wuxiaohan0922");
        Expect("★ 兩個都沒有時不會給出空名字（模型至少看得到 ID）",
            MentionFormatter.SpeakerName("", null, 123UL, showNicknames: true) == "使用者123");
        Expect("主對話的標籤一定同時帶帳號與 ID（同名的人也分得出來）",
            MentionFormatter.Label("小明", "wuxiaohan0922", 123UL, includeId: true)
                == "小明(@wuxiaohan0922, 123)");

        if (!llm.ShowNicknames)
            Console.WriteLine("  ℹ 提示：關掉暱稱之後，模型在判斷「這句是不是在對我說話」時" +
                              "只看得到帳號，認人會變差（除非大家習慣用帳號互稱）");

        // ★ 光有設定不夠 —— 訊息進來的路徑真的要**用**它（跟偷聽那次一樣的教訓）：
        //   設定寫了、程式碼沒接，選項就只是個裝飾。
        var buildTurn = typeof(LlmChatService).GetMethod("BuildTurn",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var nameOf = typeof(LlmChatService).GetMethod("NameOf",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var buildCalls = buildTurn is null ? [] : CollectCalls(buildTurn, resolveAll: true);
        var nameCalls = nameOf is null ? [] : CollectCalls(nameOf, resolveAll: true);

        if (buildTurn is null || nameOf is null)
        {
            Problem("找不到 LlmChatService.BuildTurn／NameOf —— 無法驗證暱稱有沒有真的接上");
        }
        else if (!buildCalls.Contains("LlmChatService.NameOf"))
        {
            Problem("BuildTurn 沒有用 NameOf —— 訊息進來的名字不會跟著暱稱設定走");
        }
        else if (!nameCalls.Contains("MentionFormatter.SpeakerName"))
        {
            Problem("NameOf 沒有用 SpeakerName —— 模型看到的名字不會跟著 LLM_SHOW_NICKNAMES 走");
        }
        else if (!nameCalls.Contains("LlmOptions.get_ShowNicknames"))
        {
            Problem("NameOf 沒有讀 LLM_SHOW_NICKNAMES —— 這個選項不會生效");
        }

        var answerAsync = typeof(LlmChatService).GetMethod("AnswerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var answerCalls = answerAsync is null ? [] : CollectCalls(answerAsync, resolveAll: true);

        if (answerAsync is null)
            Problem("找不到 LlmChatService.AnswerAsync —— 無法驗證「自己在這裡叫什麼」有沒有傳進去");
        else if (!answerCalls.Contains("LlmChatService.SelfNameIn"))
            Problem("AnswerAsync 沒有傳「它在這個伺服器叫什麼」給 Core —— 改暱稱之後它不會認得那個名字");
        else
            Console.WriteLine("  ✔ 暱稱真的接得上：訊息 → SpeakerName（模型看到的名字）＋ SelfNameIn（它自己的名字）");

        // ── 偷聽設定本身合不合理 ─────────────────────────        //    這裡的錯誤都要等「真的有人在頻道上聊天」才會出現，
        //    所以離線就要先問一次。
        var ev = llm.Eavesdrop;
        var evMsgs = llm.EavesdropMaxMessages;
        var evSec = llm.EavesdropSeconds;

        if (ev && (evMsgs <= 0 || evSec <= 0))
        {
            Console.WriteLine($"  ℹ 偷聽已停用（上限設 0：判斷 {evMsgs} 則／{evSec} 秒）→ " +
                              "只回 @ 它或回覆它的訊息");
        }
        else if (ev)
        {
            Console.WriteLine($"  ℹ 偷聽：回完話後最多判斷 {evMsgs} 則，安靜 {evSec} 秒後回到「等 @」" +
                              (llm.EavesdropContext ? "；聽到的閒聊會留下來當上下文" : "；不保留閒聊"));

            if (evMsgs < 5)
                Console.WriteLine("  ℹ 提示：一群人在聊天時，中間被 @ 一次就會重新開窗。" +
                                  "判斷上限太小（例如 3）會讓它講兩句就退出 —— 使用者會覺得「後面的訊息被忽略」");

            if (evSec < 30)
                Problem($"偷聽的閒置時間只有 {evSec} 秒 —— 一群人聊天很容易超過，Bot 會中途退出");
        }

        // 判斷成本（每一則要問一次模型）→ 讓使用者看得到「最壞情況花多少」
        if (ev && evMsgs > 0)
            Console.WriteLine($"  ℹ 最壞情況：一輪偷聽會多花約 {evMsgs} 次短判斷" +
                              $"（每次約 250 tokens，合計約 {evMsgs * 250:N0} tokens）");

        // ── 對話記憶的容量（全部都是環境變數可調）────────────
        //    這幾個數字決定記憶體用量；改壞了不會有錯誤訊息，只會「東西莫名其妙不見」
        //    或「記憶體慢慢長大」，所以離線就把摘要與矛盾印出來。
        Console.WriteLine($"  ℹ 對話記憶：{llm.DescribeMemory()}");

        if (llm.MaxTurnsPerSegment < llm.MaxContextTurns)
            Console.WriteLine($"  ℹ 提示：一段只留 {llm.MaxTurnsPerSegment} 則，" +
                              $"但每次最多想送 {llm.MaxContextTurns} 則 —— 實際送出的上限是一段的量" +
                              "（LLM_MAX_TURNS_PER_SEGMENT 要 ≥ LLM_MAX_CONTEXT_TURNS 才有意義）");

        if (llm.MaxSegmentsPerChannel < 2)
            Console.WriteLine("  ℹ 提示：每頻道只留 1 段時，「回覆很舊的訊息」會拉不回那一段的上下文");

        if (llm.MaxChannels * (long)llm.MaxSegmentsPerChannel * llm.MaxTurnsPerSegment > 2_000_000)
            Problem($"對話記憶的最壞情況超過 200 萬則訊息（{llm.DescribeMemory()}）—— " +
                    "記憶體會爆掉，請調整 LLM_MAX_CHANNELS／LLM_MAX_SEGMENTS_PER_CHANNEL／LLM_MAX_TURNS_PER_SEGMENT");

        // ── 「學到的提示詞」的容量（也是環境變數可調）────────
        var limits = llm.BuildPersonaLimits();

        Console.WriteLine($"  ℹ 學到的規矩：{limits.Describe()}");

        if (limits.MaxOverlayLength < limits.MaxLineLength)
            Problem($"LLM_MAX_OVERLAY_CHARS（{limits.MaxOverlayLength}）比單一條的上限" +
                    $"（LLM_MAX_RULE_CHARS={limits.MaxLineLength}）還小 —— 一條都可能塞不進提示詞");

        if (limits.MaxLinesPerGuild * limits.MaxLineLength > limits.MaxOverlayLength * 4)
            Console.WriteLine($"  ℹ 提示：每伺服器最多 {limits.MaxLinesPerGuild} 條 × {limits.MaxLineLength} 字，" +
                              $"但接進提示詞只留 {limits.MaxOverlayLength} 字 —— " +
                              "後面的規則會被截掉（想全部生效就調高 LLM_MAX_OVERLAY_CHARS）");

        if (limits.MaxLinesPerGuild > 100)
            Console.WriteLine($"  ℹ 提示：LLM_MAX_GUILD_RULES={limits.MaxLinesPerGuild} 很大 —— " +
                              "公開伺服器上任何人都能叫它記東西，這個數字是濫用風險的上限");
    }

    /// <summary>
    /// `env-vars.csv` 與程式是否一致（**雙向**檢查）。
    ///
    /// 為什麼要做：環境變數清單是給人看的文件，而「加了新變數、忘了寫進文件」
    /// 一定會發生 —— 而且發現的時候通常是在別台機器上「怎麼設定沒生效」。
    /// 這裡兩邊都對一次：
    ///   * 程式問過的變數 → 文件一定要有
    ///   * 文件寫的變數 → 程式一定要用到（不然就是過期的說明）
    /// </summary>
    private static void AuditEnvCsv()
    {
        Console.WriteLine();
        Console.WriteLine("▶ 環境變數清單檢查  env-vars.csv ↔ 程式實際用到的鍵");

        var path = FindUpwards("env-vars.csv");

        if (path is null)
        {
            Console.WriteLine("  ℹ 找不到 env-vars.csv（在容器裡執行時是正常的，略過）");
            return;
        }

        string[] lines;

        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (IOException)
        {
            // ⚠️ 這真的發生過：Excel 開著這個 CSV 時會**獨佔鎖住**檔案，
            //    File.ReadAllLines 直接丟 IOException。
            //    讀不到一份「文件」不該讓整個驗證中斷，所以只提醒並略過。
            Console.WriteLine($"  ⚠️  {Path.GetFileName(path)} 現在被其他程式鎖住" +
                              "（Excel 開著？）→ 這次略過清單比對");
            return;
        }

        // 程式這次啟動問過的鍵（SettingResolver 會記錄），加上幾個「不是透過 Resolver 讀」的
        var used = new HashSet<string>(SettingResolver.SeenKeys, StringComparer.Ordinal)
        {
            "TCBUS_ENV",          // BotConfig 直接讀
            "TCBUS_ENV_FILE",
            SayModule.AllowListVariable   // SayModule 直接讀（每次呼叫都重讀，故意不快取）
        };

        // 這些是「平台用的」而不是程式讀的，寫在文件裡是給部署的人看的
        var external = new HashSet<string>(StringComparer.Ordinal) { "DOTNET_ENVIRONMENT" };

        var documented = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var text = line.Trim().TrimStart('\uFEFF');
            if (text.Length == 0) continue;

            var cells = SplitCsv(text);
            if (cells.Count == 0) continue;

            // 標頭列（欄位名）不是變數；CSV 被引號包住時 cells[0] 會是 "變數名稱"
            if (cells[0].Trim('"') == "變數名稱") continue;
            if (cells[0].Length == 0) continue;

            documented.Add(cells[0]);

            // 別名欄的寫法：A / B / C
            if (cells.Count > 1)
                foreach (var alias in cells[1].Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = alias.Trim();
                    if (name.Length > 0 && name != "（無）") aliases.Add(name);
                }
        }

        var missing = used.Where(k => !documented.Contains(k) && !aliases.Contains(k)).OrderBy(x => x).ToList();
        var stale = documented.Where(k => !used.Contains(k) && !external.Contains(k)).OrderBy(x => x).ToList();

        Console.WriteLine($"  文件：{documented.Count} 個變數｜程式用到：{used.Count} 個");

        if (missing.Count > 0)
            Problem($"env-vars.csv 少了程式會讀的變數：{string.Join("、", missing)}");
        else
            Console.WriteLine("  ✔ 程式用到的每個變數都寫在 env-vars.csv 裡");

        if (stale.Count > 0)
            Console.WriteLine($"  ℹ 文件裡有 {stale.Count} 個程式沒用到的變數（可能是別的平台用的，" +
                              $"或說明過期了）：{string.Join("、", stale)}");
    }

    /// <summary>從目前目錄往上找檔案（DryRun 常常在容器或別的目錄被執行）。</summary>
    private static string? FindUpwards(string fileName)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());

        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>夠用的 CSV 解析（支援雙引號包住、內部有逗號與 "" 逃脫）。</summary>
    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else quoted = false;
                }
                else current.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }

        cells.Add(current.ToString());
        return cells;
    }

    /// <summary>
    /// **面板（<see cref="BusUi"/>）與 Core 的 <see cref="StopPicks"/> 必須是同一份邏輯**。
    ///
    /// 為什麼要專門驗這個：使用者反映「AI 找公車會有同名站牌找不到的問題」，
    /// 根因就是兩邊各寫一套 —— 面板按 `g:{站區}` 短鍵時解出的是**整個站區**的站牌，
    /// 而 LLM 那條路當時拿的是**搜尋命中**（有 25 筆上限、每組只留符合關鍵字的那些），
    /// 於是「臺中車站」38 個月台只被放進 2~3 個，停在其他月台的路線整條找不到。
    ///
    /// 這裡驗三件事（都在離線、真實資料集上跑）：
    ///   1. 面板算出來的預設勾選 ＝ Core 算出來的預設勾選
    ///   2. 面板的 Select Menu 裡每一個選項值，都能被 Core 解析回非空的站牌
    ///      （順便確認值沒有被 Discord 的 100 字元上限截斷 —— 那會安靜地少掉候選站）
    ///   3. 解析出來的站牌數＝整個站區的站牌數（不是搜尋命中的數量）
    /// </summary>
    private static void AuditStopPicks(TaichungBusDataService data)
    {
        Console.WriteLine();
        Console.WriteLine("▶ 站牌解析檢查  面板（BusUi）↔ LLM 工具（StopPicks）");

        var keywords = new[] { "臺中車站", "台中車站", "靜宜大學", "干城站", "臺中科大" };
        var checkedCount = 0;

        foreach (var keyword in keywords)
        {
            var groups = data.Search.SearchGrouped(keyword);
            var strong = groups.Where(g => g.IsStrongMatch).ToList();
            if (strong.Count == 0) continue;

            checkedCount++;

            // 1) 預設勾選要完全一致
            var uiDefaults = BusUi.DefaultStopPicks(strong, data);
            var coreDefaults = StopPicks.Build(strong, data).Defaults.ToList();

            if (!uiDefaults.SequenceEqual(coreDefaults, StringComparer.Ordinal))
                Problem($"「{keyword}」面板與工具的預設勾選不一致：" +
                        $"面板 [{string.Join(",", uiDefaults)}]／工具 [{string.Join(",", coreDefaults)}]");

            // 2) 面板的每個選項值都要能被解析回站牌
            var components = BusUi.SearchComponents(true, strong, data, Array.Empty<string>());
            var optionValues = SelectOptionValues(components);

            if (optionValues.Count == 0)
            {
                Problem($"「{keyword}」面板沒有產生任何站牌選項");
                continue;
            }

            foreach (var value in optionValues)
            {
                var uids = BusUi.ResolveStopValues([value], data);

                if (uids.Count == 0)
                    Problem($"「{keyword}」選項 {value} 解析不出任何站牌（值被截斷或短鍵壞了？）");
            }

            // 3) 解析出來的站牌＝整個站區
            var uidsFromUi = BusUi.ResolveStopValues(uiDefaults, data);
            var area = data.GetGroupByShortKey(strong[0].GroupKey);

            if (area is not null && uiDefaults.Any(v => v.StartsWith("g:", StringComparison.Ordinal)))
            {
                if (!area.StopUids.All(uidsFromUi.Contains))
                    Problem($"「{keyword}」解出來的站牌沒有涵蓋整個站區" +
                            $"（{uidsFromUi.Count}／{area.StopUids.Count}）");
            }
        }

        if (checkedCount == 0)
            Console.WriteLine("  ℹ 這個資料集沒有任何強相符的站名可以比對（略過）");
        else
            Console.WriteLine($"  ✔ {checkedCount} 組關鍵字：面板與 LLM 工具算出同一組站牌" +
                              "（含同名不同月台，不會漏）");
    }

    /// <summary>把元件裡所有 Select Menu 的選項值抓出來。</summary>
    private static List<string> SelectOptionValues(MessageComponent components)
        => components.Components
            .OfType<ActionRowComponent>()
            .SelectMany(row => row.Components)
            .OfType<SelectMenuComponent>()
            .SelectMany(menu => menu.Options)
            .Select(o => o.Value)
            .ToList();

    /// <summary>
    /// **模型「幫使用者按按鈕」**（`ui` plugin）的離線驗證。
    ///
    /// 這一組工具動的是**同一個面板 session**（跟使用者自己按按鈕看到的是同一份），
    /// 所以最怕的是「工具說它設好了、其實 session 沒動」—— 那樣使用者之後按
    /// 「搜尋路線」會發現什麼都沒有。這裡就直接把整條路徑走一遍：
    /// 開面板 → 設起訖 → 找路線 → 訂閱 → 復原，
    /// 並且**每一步都驗 session 與元件真的建得出來**。
    /// </summary>
    private static void AuditUiTools(TaichungBusDataService data)
    {
        Console.WriteLine();
        Console.WriteLine("▶ 面板動作檢查  模型幫使用者按按鈕（ui plugin）");

        var subs = new SubscriptionService();
        var sessions = new BusSessionStore();

        using var savedGroups = new SavedGroupStore(":memory:");

        var context = new TcBusBot.Core.Chat.ChatToolContext(1UL, 2UL, 3UL, "小明");
        var log = new TcBusBot.Core.Chat.ToolCallLog();

        var tools = new UiTools(
            new BusActionService(data, subs), subs, sessions, savedGroups, context, log);

        var session = sessions.GetOrCreate(3UL, 2UL);

        // ① 開面板 → 要附上面板元件
        Console.WriteLine($"  {tools.OpenPanel().Split('\n')[0]}");

        if (tools.PendingUi?.Kind != UiRequest.Panel)
            Problem("open_panel 沒有要求附上面板元件");
        else
            BusUi.PanelComponents(session);   // 建得出來就代表真正的按鈕沒問題

        // ② 設定起訖 → session 真的要變
        var originMessage = tools.SetOrigin("臺中車站");
        var destMessage = tools.SetDestination("靜宜大學");

        Console.WriteLine($"  起點：{originMessage.Split('\n')[0]}");
        Console.WriteLine($"  目的地：{destMessage.Split('\n')[0]}");

        if (session.Origin is null || session.Destination is null)
            Problem("set_origin / set_destination 沒有真的寫進面板 session");
        else if (!session.Ready)
            Problem("設完起訖之後 session 還是沒 ready");
        else
            Console.WriteLine($"  ✔ session：{session.Origin.DisplayName} → {session.Destination.DisplayName}" +
                              $"（{session.Origin.CandidateStopUids.Count}／" +
                              $"{session.Destination.CandidateStopUids.Count} 個候選站牌）");

        // ③ 找路線 → 要附上路線元件
        var routesMessage = tools.SearchPanelRoutes();
        Console.WriteLine($"  {routesMessage.Split('\n')[0]}");

        if (session.LastRoutes.Count == 0)
            Problem("search_panel_routes 沒有把路線寫進 session");
        else if (tools.PendingUi?.Kind != UiRequest.Routes)
            Problem("search_panel_routes 沒有要求附上路線元件");
        else
            BusUi.RouteComponents(session.LastRoutes);

        // ④ 訂閱指定的路線（模擬使用者說「訂 300 就好」）
        var subscribeMessage = tools.SubscribePanelRoutes("300");
        Console.WriteLine($"  {subscribeMessage.Split('\n')[0]}");

        if (subs.GetGroupsByUser(3UL).Count() == 0)
            Problem("subscribe_panel_routes 沒有真的建立訂閱");
        else if (session.CreatedGroupId is null)
            Problem("訂閱之後 session 沒有指向那一組（面板狀態會跟實際不一致）");
        else if (tools.PendingUi?.Kind != UiRequest.Etas)
            Problem("訂閱之後應該附上到站時間元件");
        else
            Console.WriteLine($"  ✔ 已建立 {subs.SubscriptionCount} 筆訂閱，面板指向 {session.CreatedGroupId}");

        // ⑤ 復原（等於按「↩️ 復原」）→ 訂閱要被撤掉
        var groupId = session.CreatedGroupId!;
        session.Undo.Push(new UndoAppliedSubscriptions("剛剛的訂閱", [groupId], null));

        var undoMessage = tools.UndoLastAction();
        Console.WriteLine($"  {undoMessage.Split('\n')[0]}");

        if (!tools.UndoApplied)
            Problem("undo_last_action 沒有真的執行復原");
        else if (subs.GetGroup(groupId) is not null)
            Problem("復原之後訂閱群組應該要消失");
        else
            Console.WriteLine("  ✔ 復原成功：訂閱已撤銷");

        // ⑥ 站名找不到／模糊時不可以亂設
        var vagueSession = new BusSession { UserId = 9UL, ChannelId = 8UL };
        sessions.GetOrCreate(9UL, 8UL).Origin = null;

        var vagueTools = new UiTools(new BusActionService(data, subs), subs, sessions, savedGroups,
                                     new TcBusBot.Core.Chat.ChatToolContext(1UL, 8UL, 9UL, "小華"),
                                     new TcBusBot.Core.Chat.ToolCallLog());

        vagueTools.SetOrigin("路口");
        var vague = sessions.GetOrCreate(9UL, 8UL);

        if (vague.Origin is not null)
            Problem("模糊的站名（「路口」）被設成起點了 —— 應該拒絕並要使用者說清楚");
        else
            Console.WriteLine("  ✔ 模糊站名不會亂設（回報候選讓模型去問使用者）");

        Console.WriteLine("  ℹ 工具清單：" + tools.GetType().Name + "（5 個：開面板／設起訖／找路線／訂閱／復原）");

        // ── 「說做了但其實沒做」的誠實檢查 ────────────────
        //    實測真的發生過：使用者說「訂錯了幫我復原」，模型回「復原好了」卻沒呼叫工具。
        //    使用者不會知道設定根本沒動，所以回覆要自己加上警告。
        var claims = new[]
        {
            "復原好了，剛剛那筆訂閱已經取消",
            "已經幫你訂閱 300 了",
            "設定好了，起點是臺中車站",
            "記好了，以後都會簡短一點"
        };

        foreach (var claim in claims)
            if (!LlmChatService.ClaimsAnAction(claim))
                Problem($"「{claim}」看起來像聲稱做完了，但沒有被認出來（使用者會被誤導）");

        var neutral = new[]
        {
            "300 大約 3 分鐘到站",
            "我查不到即時到站時間，可以試試 /bus next",
            "你要從哪一站上車？"
        };

        foreach (var text in neutral)
            if (LlmChatService.ClaimsAnAction(text))
                Problem($"「{text}」只是普通回覆，卻被當成「聲稱做了動作」");

        Console.WriteLine("  ✔ 誠實檢查：聲稱做完了卻沒呼叫工具時會加警告（一般回覆不會誤判）");
    }

    /// <summary>
    /// **偷聽模式的入口檢查**：訊息要真的進得到偷聽那段程式。
    ///
    /// 為什麼需要這一關（真的出過包）：偷聽的功能、判斷器、窗口全都寫好了，
    /// 但入口那行 <c>if (!mentioned && !replyAddressed) return;</c> 會在**到達偷聽之前**就返回 ——
    /// 所以「回完話後繼續聽」從來沒有生效過，使用者看到的是
    /// 「@ 它講一句之後，其他人再講什麼它都當作沒看到」。
    /// 這種「功能寫好了但接不到」的 bug，元件限制與單元測試都抓不到，
    /// 只有把**入口的判斷**與**實際呼叫到的方法**一起驗才抓得到。
    /// </summary>
    private static void AuditEavesdropWiring()
    {
        Console.WriteLine("▶ 偷聽入口檢查  沒被 @ 的訊息要進得到偷聽（曾經整段接不到）");

        // ── 1) 入口判斷的真值表 ──────────────────────────
        var cases = new (bool Mentioned, bool Reply, bool Listening, bool Expected, string Why)[]
        {
            (true, false, false, true, "@ 它 → 要處理"),
            (false, true, false, true, "回覆它（或回覆記憶裡的訊息）→ 要處理"),
            (false, false, true, true, "★ 沒 @ 沒回覆，但正在偷聽 → 一定要處理（這就是以前漏掉的）"),
            (false, false, false, false, "什麼都沒有 → 不處理（維持原本「看到訊息就回」的禁令）")
        };

        foreach (var (mentioned, reply, listening, expected, why) in cases)
        {
            var actual = LlmChatService.ShouldHandle(mentioned, reply, listening);

            if (actual != expected)
                Problem($"入口判斷錯了（{why}）：ShouldHandle({mentioned}, {reply}, {listening}) = {actual}");
        }

        Console.WriteLine("  ✔ 入口判斷：@ 它／回覆它／**正在偷聽** 三種都會進來，其他一律不理");

        // ── 2) 實際的入口程式碼有沒有用到那個判斷 ──────────
        var handle = typeof(LlmChatService).GetMethod("HandleCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (handle is null)
        {
            Problem("找不到 LlmChatService.HandleCoreAsync —— 無法驗證偷聽入口");
            return;
        }

        var calls = CollectCalls(handle, resolveAll: true);

        // 真正交給 Core 的那一行在 AnswerAsync（HandleCoreAsync 只是入口與冷卻）
        var answer = typeof(LlmChatService).GetMethod("AnswerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (answer is not null) calls.AddRange(CollectCalls(answer, resolveAll: true));

        calls = calls.Distinct(StringComparer.Ordinal).ToList();

        var interesting = calls
            .Where(c => c.Contains("Chat") || c.Contains("Listen") || c.Contains("ShouldHandle"))
            .Distinct()
            .ToList();

        Console.WriteLine($"  ℹ 訊息入口會呼叫：{string.Join("、", interesting)}");

        if (!calls.Contains("LlmChatService.ShouldHandle"))
            Problem("訊息入口沒有用 ShouldHandle —— 沒被 @ 的訊息可能在到達偷聽之前就被丟掉了");

        if (!calls.Contains("ConversationStore.PeekListening"))
            Problem("訊息入口沒有查偷聽窗口 —— 偷聽永遠不會生效");

        if (!calls.Contains("ChatOrchestrator.AskAsync"))
            Problem("訊息入口沒有把訊息交給 AskAsync —— 所有規則（偷聽判斷、上下文、額度）都在那裡");

        // 舊版的 bug：看到「@ 了別人」就**停止偷聽**（多人頻道裡 @ 別人太常見）。
        // 現在那條路徑只是「不插話」，退出與否交給 Core 的判斷。
        if (calls.Contains("ConversationStore.StopListening"))
            Problem("訊息入口直接停止了偷聽 —— 「@ 了別人」等情況應該留在頻道裡繼續聽（交給 Core 判斷）");

        Console.WriteLine("  ✔ 入口真的會：查偷聽窗口 → 交給 ShouldHandle → 再交給 AskAsync（不會自己決定退出）");

        // ── 3) Core 的偷聽路徑本身有沒有「留在頻道裡」────────────────
        var ask = typeof(ChatOrchestrator).GetMethod(nameof(ChatOrchestrator.AskAsync));

        if (ask is null)
        {
            Problem("找不到 ChatOrchestrator.AskAsync —— 無法驗證偷聽的核心規則");
            return;
        }

        var coreCalls = CollectCalls(ask, resolveAll: true);

        // 「@ 了別人」這種不花錢的訊息：只延後到期，不扣判斷次數
        if (!coreCalls.Contains("ConversationStore.TouchListen"))
            Problem("AskAsync 沒有在「@ 了別人」時 TouchListen —— 那種訊息會把判斷額度吃掉");

        // 每一則判斷過／聽到的訊息都要留下來當上下文（先經過 orchestrator 自己的 RecordAmbient）
        if (!coreCalls.Contains("ChatOrchestrator.RecordAmbient"))
            Problem("AskAsync 沒有把偷聽到的訊息記進上下文 —— 「閒聊帶不進去」的 bug 會回來");

        if (!coreCalls.Contains("ConversationStore.ConsumeListen"))
            Problem("AskAsync 沒有消耗偷聽額度 —— 窗口永遠不會結束");

        // 再往下一層：ChatOrchestrator.RecordAmbient 真的要寫進對話記憶（而且設定關掉時不寫）
        var remember = typeof(ChatOrchestrator).GetMethod(nameof(ChatOrchestrator.RecordAmbient));
        var rememberCalls = remember is null ? [] : CollectCalls(remember, resolveAll: true);

        if (!rememberCalls.Contains("ConversationStore.RecordAmbient"))
            Problem("ChatOrchestrator.RecordAmbient 沒有真的寫進對話記憶");
        else if (!rememberCalls.Contains("LlmOptions.get_EavesdropContext"))
            Problem("ChatOrchestrator.RecordAmbient 沒有看 LLM_EAVESDROP_CONTEXT 設定 —— 關不掉會很花 token");

        Console.WriteLine("  ✔ 核心真的會：TouchListen（@ 別人）／ConsumeListen（判斷）／RecordAmbient（留下上下文）");
    }

    /// <summary>
    /// **DI 容器檢查**：用與真正的 Bot 完全相同的註冊程式碼（<see cref="BotServices.Create"/>）
    /// 組出容器，並一個一個解析。
    ///
    /// 為什麼這是目前最有價值的一段檢查：
    ///   * <c>ValidateOnBuild = true</c> 會在建容器時就驗證「每個服務的建構子參數都解析得到」——
    ///     **之前那個「沒設定 LLM_API_KEY → ILlmClient 不存在 → 整個 Bot 起不來」的 bug，
    ///     就是在這一關會被擋下來的**（當時沒有 DI 容器，所以只能靠 DryRun 手動組一遍）
    ///   * 再逐一把註冊表上的每個服務真的解析一次（有些錯只在「解析的當下」才會出現，
    ///     例如工廠裡丟例外）
    ///   * 順便驗兩個「同一條後端鏈」的保證：<c>SavedGroupStore</c> 與 <c>ILlmStateStore</c>
    ///     必須是同一個實例，否則每週用量會寫到另一條連線上
    ///
    /// 回傳 null 代表容器建不起來（後面的檢查就跳過）。
    /// </summary>
    private static IServiceProvider? AuditDependencyInjection(
        BotConfig cfg, TaichungBusDataService data, DiscordSocketClient client)
    {
        Console.WriteLine();
        Console.WriteLine("▶ DI 容器檢查  服務註冊 ↔ 建構子需求");

        // ★ 與 Program.cs 同一份註冊程式碼。儲存強制用記憶體：
        //    離線驗證不該去開（也不該改到）真正的 tcbus.db / MongoDB。
        var collection = BotServices.Create(cfg, data, api: null, client, "（dry-run）",
                                            msg => Console.WriteLine($"  ｜{msg}"),
                                            storage: StorageSettings.Memory);

        ServiceProvider provider;

        try
        {
            provider = collection.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        }
        catch (Exception ex)
        {
            Problem($"DI 容器建不起來（有服務的建構子參數找不到）：{ex.GetType().Name}: {ex.Message}");
            return null;
        }

        // 逐一解析：ValidateOnBuild 只驗「建得出來」，這裡確認真的拿得到東西
        var failures = new List<string>();

        foreach (var descriptor in collection)
        {
            try
            {
                if (provider.GetService(descriptor.ServiceType) is null)
                    failures.Add($"{descriptor.ServiceType.Name}（解析結果是 null）");
            }
            catch (Exception ex)
            {
                failures.Add($"{descriptor.ServiceType.Name}（{ex.GetType().Name}: {ex.Message}）");
            }
        }

        var singletons = collection.Count(d => d.Lifetime == ServiceLifetime.Singleton);
        var transients = collection.Count(d => d.Lifetime == ServiceLifetime.Transient);

        Console.WriteLine($"  註冊 {collection.Count} 個服務（{singletons} singleton／{transients} transient）");

        foreach (var group in collection.GroupBy(d => d.Lifetime).OrderBy(g => g.Key.ToString()))
        {
            var names = group.Select(d => d.ServiceType.Name).OrderBy(x => x, StringComparer.Ordinal);
            Console.WriteLine($"  {group.Key}：{string.Join("、", names)}");
        }

        if (failures.Count > 0)
            foreach (var f in failures) Problem($"服務解析失敗：{f}");
        else
            Console.WriteLine("  ✔ 每個註冊的服務都解析得到");

        // ── 儲存方案：兩個入口要指向同一個實例 ───────────────
        var store = provider.GetRequiredService<SavedGroupStore>();
        var state = provider.GetRequiredService<TcBusBot.Core.Chat.ILlmStateStore>();

        if (!ReferenceEquals(store, state))
            Problem("SavedGroupStore 與 ILlmStateStore 不是同一個實例（每週用量會寫到別的地方）");
        else
            Console.WriteLine($"  ✔ 儲存後端：{store.Describe()}（訂閱組與 LLM 用量共用同一個實例）");

        // ── Discord 的模組一定要註冊成 transient ────────────
        var moduleTypes = new[]
        {
            typeof(BusModule), typeof(BusComponentModule), typeof(SayModule),
            typeof(ChatModule), typeof(ResetModule)
        };

        var badLifetime = collection
            .Where(d => moduleTypes.Contains(d.ServiceType))
            .Where(d => d.Lifetime != ServiceLifetime.Transient)
            .Select(d => $"{d.ServiceType.Name}（{d.Lifetime}）")
            .ToList();

        if (badLifetime.Count > 0)
            Problem($"指令模組必須是 transient（共用實例會讓並行的互動互相蓋掉 Context）：" +
                    string.Join("、", badLifetime));
        else
            Console.WriteLine($"  ✔ {moduleTypes.Length} 個指令模組都註冊成 transient（每次互動都是新實例）");

        return provider;
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
