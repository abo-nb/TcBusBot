using Discord;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Models;
using TcBusBot.Core.Storage;
using TcBusBot.Core.Subscriptions;

namespace TcBusBot.Discord;

/// <summary>所有 custom_id 集中管理，避免散落各處打錯字。</summary>
public static class Cid
{
    public const string Panel = "bus:panel";
    public const string SetOrigin = "bus:set:o";
    public const string SetDest = "bus:set:d";
    public const string ModalOrigin = "bus:modal:o";
    public const string ModalDest = "bus:modal:d";
    public const string PickOrigin = "bus:pick:o";
    public const string PickDest = "bus:pick:d";
    public const string SelectAllOrigin = "bus:all:o";
    public const string SelectAllDest = "bus:all:d";
    public const string ConfirmOrigin = "bus:ok:o";
    public const string ConfirmDest = "bus:ok:d";
    public const string RetryOrigin = "bus:retry:o";
    public const string RetryDest = "bus:retry:d";
    public const string Find = "bus:find";
    public const string Routes = "bus:routes";
    public const string Subscribe = "bus:subscribe";
    public const string NotifyPrefix = "bus:notify:";
    public const string Reset = "bus:reset";
    public const string ToList = "bus:tolist";
    public const string Simulate = "bus:simulate";
    public const string ShowEtas = "bus:etas";

    // 訂閱組
    public const string OpenGroups = "bus:groups";
    public const string SaveGroup = "bus:savegroup";
    public const string SaveGroupModal = "bus:savegroupmodal";
    public const string GroupSelect = "bus:groupsel";
    public const string GroupUse = "bus:groupuse";
    public const string GroupUseMerged = "bus:groupusemerge";
    public const string GroupMerge = "bus:groupmerge";
    public const string GroupMergeModal = "bus:groupmergemodal";
    public const string GroupDelete = "bus:groupdel";
    public const string GroupRename = "bus:groupren";
    public const string GroupRenameModal = "bus:grouprenmodal";
    public const string Undo = "bus:undo";
    public const string SendDm = "bus:dm";
    public const string RemoveGroupPrefix = "bus:rm:";
}

/// <summary>
/// 把所有 Discord 訊息（Embed / 元件）的組裝集中在這裡。
/// 全部是純函式 —— 不碰網路、不碰狀態，方便日後單獨測試。
/// </summary>
public static class BusUi
{
    private static readonly TimeZoneInfo Taipei =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private static readonly Color Info = new(0x2B, 0x6C, 0xB0);
    private static readonly Color Success = new(0x2D, 0x9C, 0x4F);
    private static readonly Color Alert = new(0xE6, 0x7E, 0x22);
    private static readonly Color Muted = new(0x5A, 0x5A, 0x5A);

    // ─────────────────────────────────────────────────────
    //  主面板
    // ─────────────────────────────────────────────────────

    public static Embed Panel(BusSession s, int subscriptionCount)
    {
        var b = new EmbedBuilder()
            .WithTitle($"🚌 {BotStatus.CityDisplay}公車訂閱")
            .WithColor(s.Ready ? Success : Info)
            .AddField("起點", s.Origin?.DisplayName ?? "尚未設定", inline: false)
            .AddField("目的地", s.Destination?.DisplayName ?? "尚未設定", inline: false);

        if (s.Ready)
        {
            b.AddField("下一步", "按「搜尋路線」找出可以搭的路線。", inline: false);
        }
        else
        {
            b.AddField("怎麼用",
                $"1. 按「設定起點」→ 輸入站名關鍵字（例如 `火車站`，服務城市：{BotStatus.CityDisplay}）\n" +
                "2. 從搜尋結果勾選一個或多個站牌\n" +
                "3. 同樣設定目的地\n" +
                "4. 按「搜尋路線」選要訂閱的路線",
                inline: false);
        }

        b.WithFooter($"目前有 {subscriptionCount} 個訂閱提示：你打「台」也會找到「臺」");
        return b.Build();
    }

    public static MessageComponent PanelComponents(BusSession s)
        => new ComponentBuilder()
            .WithButton("設定起點", Cid.SetOrigin, ButtonStyle.Primary)
            .WithButton("設定目的地", Cid.SetDest, ButtonStyle.Primary)
            .WithButton("搜尋路線", Cid.Find, ButtonStyle.Success, disabled: !s.Ready)
            .AddRow(new ActionRowBuilder()
                .WithButton("📋 我的訂閱", Cid.ToList, ButtonStyle.Secondary)
                .WithButton("📂 訂閱組", Cid.OpenGroups, ButtonStyle.Secondary)
                .WithButton("清除", Cid.Reset, ButtonStyle.Secondary))
            .Build();

    // ─────────────────────────────────────────────────────
    //  站牌搜尋結果
    // ─────────────────────────────────────────────────────

    public static Embed SearchResult(
        string keyword,
        IReadOnlyList<StopSearchGroupResult> groups,
        BusDataService data,
        int hiddenFuzzyCount = 0)
    {
        var b = new EmbedBuilder().WithColor(Info);
        var totalStops = groups.Sum(g => g.Hits.Count);

        if (groups.Count == 0)
        {
            var examples = data.ExampleStopNames(6);
            b.WithColor(Alert)
             .WithTitle($"🔍 找不到「{keyword}」")
             .WithDescription(
                 "試試站名的不同寫法（`台中`／`臺中`、`火车`／`火車`、`火車站`／`車站`；兩岸用字都通），" +
                 "或只打一部分（例：`靜宜`）。");

            // 只有在小資料集時才提示「資料集很小」——用完整 TDX 資料時這句話會誤導
            if (data.StopCount < 500)
            {
                b.AddField("⚠️ 目前是內建最小資料集",
                    $"只有 **{data.StopCount} 個站牌**（臺中車站～靜宜大學走廊），" +
                    "所以查不到其他站牌是正常的。\n" +
                    $"設定 TDX 金鑰即可取得全{BotStatus.CityDisplay}約 5,000+ 個站牌（含跨區路線約 14,000 筆）。",
                    inline: false);
            }
            else
            {
                b.AddField("這份資料集裡有的站名（可以複製去試）",
                    string.Join("　", examples.Select(e => $"`{e}`")), inline: false);
            }

            return b.Build();
        }

        b.WithTitle($"🔍 「{keyword}」→ {groups.Count} 組、{totalStops} 個站牌")
         .WithDescription("勾選你要的**站名**（可多選），然後按「確認」。\n" +
                          "☑ 是系統建議預設會勾選的項目。**同名站牌已合併**（去回程／不同月台算同一個）。");

        foreach (var g in groups.Take(6))
        {
            // 同名站牌合併成一列（用資料服務推導，與選項的短鍵解析共用同一份邏輯）
            var byName = data.GetGroupNameBreakdown(g.GroupKey);
            if (byName.Count == 0) continue;

            var lines = byName.Select(x =>
            {
                var mark = g.IsStrongMatch ? "•" : "◦";
                var count = x.StopUids.Count > 1 ? $"（同站 {x.StopUids.Count} 個站牌）" : "";
                return $"{mark} {x.Name}{count} · 經過 {x.RouteCount} 條路線";
            });

            b.AddField($"{g.DisplayName}　{byName.Count} 種站名", string.Join("\n", lines), inline: false);
        }

        var notes = new List<string>();
        if (groups.Count > 6) notes.Add($"只顯示前 6 組（共 {groups.Count} 組）");
        if (hiddenFuzzyCount > 0) notes.Add($"另有 {hiddenFuzzyCount} 組模糊相符的結果未顯示");

        if (notes.Count > 0)
            b.WithFooter(string.Join("；", notes) + "。請輸入更精確的關鍵字。");

        return b.Build();
    }

    /// <summary>
    /// 如果有「完全相符／前綴相符」的結果，就只顯示那些。
    ///
    /// 為什麼需要：使用者打「臺中車站」時，子字串／子序列比對會撈出
    /// 「沙鹿車站」「潭子車站」「豐原車站」之類不相關的站，
    /// 25 個選項被雜訊塞滿反而找不到真正要的那個。
    /// </summary>
    public static (List<StopSearchGroupResult> Shown, int HiddenFuzzy) PreferStrongMatches(
        IReadOnlyList<StopSearchGroupResult> groups)
    {
        var strong = groups.Where(g => g.IsStrongMatch).ToList();

        return strong.Count > 0
            ? (strong, groups.Count - strong.Count)
            : (groups.ToList(), 0);
    }

    /// <summary>同名的站牌會合併成一項，因此數量以「種站名」計。</summary>
    private static string DescribeGroupCounts(
        IReadOnlyList<(string Name, IReadOnlyList<string> StopUids, int RouteCount)> byName)
        => $"{byName.Count} 種站名 / {byName.Sum(x => x.StopUids.Count)} 個站牌";

    public static MessageComponent SearchComponents(
        bool isOrigin,
        IReadOnlyList<StopSearchGroupResult> groups,
        BusDataService data,
        IReadOnlyCollection<string> selected)
    {
        var options = BuildStopOptions(groups, data, selected);

        var builder = new ComponentBuilder();

        // ⚠️ Discord 的 Select Menu 至少要有 1 個選項。
        //    硬塞空清單會在 Build() 時丟例外，整個互動就失敗（實際踩過：
        //    使用者輸入資料集裡沒有的站名 → 0 個選項 → bus:modal:d 執行失敗）。
        if (options.Options.Count > 0)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId(isOrigin ? Cid.PickOrigin : Cid.PickDest)
                .WithPlaceholder("選擇站牌或群組（可多選）")
                .WithMinValues(0)
                .WithMaxValues(Math.Min(25, options.Options.Count))
                .WithOptions(options.Options);

            builder.WithSelectMenu(menu)
                   .WithButton("全選", isOrigin ? Cid.SelectAllOrigin : Cid.SelectAllDest,
                               ButtonStyle.Secondary)
                   .WithButton("確認", isOrigin ? Cid.ConfirmOrigin : Cid.ConfirmDest,
                               ButtonStyle.Success);
        }
        else
        {
            // 找不到東西時給一條出路，而不是讓使用者卡在死路
            builder.WithButton("重新輸入", isOrigin ? Cid.RetryOrigin : Cid.RetryDest,
                               ButtonStyle.Primary);
        }

        return builder.Build();
    }

    /// <summary>
    /// 把選項的值解析成 StopUID 清單 —— 與 <see cref="BuildStopOptions"/> 對稱。
    ///
    /// ⚠️ 實作在 <see cref="StopPicks.Resolve"/>：**LLM 工具走的是同一份**。
    /// 之前兩邊各寫一次，結果 LLM 那版拿「搜尋命中」當候選（有 25 筆上限），
    /// 同名站牌就找不到 —— 面板與 AI 對同一句話必須給出同一組站牌。
    /// </summary>
    public static List<string> ResolveStopValues(
        IEnumerable<string> values, BusDataService data)
        => StopPicks.Resolve(values, data);

    /// <summary>使用者沒有手動勾選時，預設要勾的項目（強相符的群組／站牌）。</summary>
    public static List<string> DefaultStopPicks(
        IReadOnlyList<StopSearchGroupResult> groups, BusDataService data)
        => StopPicks.Build(groups, data).Defaults.ToList();

    /// <summary>「全選」要帶入的值 —— 必須與實際存在的選項完全一致。</summary>
    public static List<string> AllStopValues(
        IReadOnlyList<StopSearchGroupResult> groups, BusDataService data)
        => StopPicks.Build(groups, data).AllValues.ToList();

    /// <summary>
    /// 把搜尋結果變成選項。
    ///
    /// ⚠️ 規則全部在 <see cref="StopPicks.Build"/>（與 LLM 工具共用同一份），
    /// 這裡只負責把純資料轉成 Discord 的 Select Menu，並套用 Discord 的長度限制。
    /// </summary>
    private static StopOptions BuildStopOptions(
        IReadOnlyList<StopSearchGroupResult> groups,
        BusDataService data,
        IReadOnlyCollection<string> selected)
    {
        var set = StopPicks.Build(groups, data, selected);
        var list = new List<SelectMenuOptionBuilder>();

        foreach (var option in set.Options)
        {
            // 值超長一定是設計錯誤（Discord 上限 100），大聲說出來而不是默默截斷
            if (option.Value.Length > 100)
                Console.WriteLine($"[ui] ⚠️ 選項 value 超過 100 字元（{option.Value.Length}）：" +
                                  $"{option.Value[..40]}… 請改用短鍵，不要塞 StopUID。");

            var builder = new SelectMenuOptionBuilder()
                .WithLabel(Truncate(option.Label, 100))
                .WithValue(Truncate(option.Value, 100))
                .WithDescription(Truncate(option.Description, 100));

            if (option.IsDefault) builder.WithDefault(true);

            list.Add(builder);
        }

        return new StopOptions(list, set.Defaults.ToList(), set.AllValues.ToList());
    }


    /// <summary>一次「挑站牌」所需的選項、預設勾選、全部值。</summary>
    private sealed record StopOptions(
        List<SelectMenuOptionBuilder> Options,
        List<string> StrongDefaults,
        List<string> AllValues);

    public static Embed PickedSummary(bool isOrigin, IReadOnlyList<string> stopUids,
        IReadOnlyList<string> groupNames, BusDataService data)
    {
        var title = isOrigin ? "起點" : "目的地";
        if (stopUids.Count == 0)
            return new EmbedBuilder()
                .WithColor(Alert)
                .WithTitle($"⚠️ {title}沒有選到任何站牌")
                .WithDescription("請至少勾選一個站牌或一個群組。")
                .Build();

        // 同名站牌只列一次，避免出現重複的「靜宜大學(專用道)、靜宜大學(專用道)」
        var byName = stopUids
            .GroupBy(data.GetDisplayName, StringComparer.Ordinal)
            .Select(grp => grp.Count() > 1 ? $"{grp.Key}（{grp.Count()} 個站牌）" : grp.Key)
            .ToList();

        var lines = byName.Take(20).Select(n => $"• {n}");
        var more = byName.Count > 20 ? $"\n… 還有 {byName.Count - 20} 種站名" : "";

        return new EmbedBuilder()
            .WithColor(Success)
            .WithTitle($"✅ {title}已設定：{byName.Count} 種站名、{stopUids.Count} 個候選站牌")
            .WithDescription(string.Join("\n", lines) + more)
            .WithFooter("按「確認」繼續；或再選一次可以重新勾選。")
            .Build();
    }

    // ─────────────────────────────────────────────────────
    //  路線清單
    // ─────────────────────────────────────────────────────

    public static Embed RouteList(
        LocationTarget origin, LocationTarget destination,
        IReadOnlyList<RouteOption> routes)
    {
        var b = new EmbedBuilder().WithColor(Info)
            .WithTitle($"🚏 找到 {routes.Count} 條可用路線")
            .WithFooter($"{origin.CandidateStopUids.Count} 個起點候選 × {destination.CandidateStopUids.Count} 個目的地候選");

        if (routes.Count == 0)
        {
            b.WithColor(Alert)
             .WithDescription("找不到可以搭的路線。\n" +
                              "可能是起訖方向不對（例如去程／返程），或兩組候選站牌沒有交集。\n" +
                              "試試加入更多候選站牌（例如加上附近的站牌）。");
            return b.Build();
        }

        var lines = routes.Take(20).Select((r, i) =>
        {
            var boards = string.Join(" 或 ", r.BoardChoices.Select(x => x.BoardStopName).Distinct());
            return $"`{i + 1}.` **{r.RouteName}**（{r.DirectionLabel}）\n" +
                   $"　　{boards} 上車 · {r.StopsBetween} 站\n" +
                   $"　　往 {r.Earliest.AlightStopName}";
        });

        b.WithDescription(string.Join("\n", lines) +
                          (routes.Count > 20 ? $"\n… 還有 {routes.Count - 20} 條" : ""));

        return b.Build();
    }

    /// <summary>
    /// 路線清單的元件。
    ///
    /// ⚠️ 兩個重點：
    ///   1. `routes` 為空時**不能**產生 Select Menu（Discord 要求至少 1 個選項，
    ///      空清單會在 Build() 丟例外）。
    ///   2. 勾選路線只是「選取」，**要按「訂閱」才會真的建立訂閱** ——
    ///      使用者需要一個明確的動作，不能只是選了選單就默默生效。
    /// </summary>
    public static MessageComponent RouteComponents(
        IReadOnlyList<RouteOption> routes,
        IReadOnlyCollection<string>? selected = null)
    {
        var picked = new HashSet<string>(selected ?? Array.Empty<string>(), StringComparer.Ordinal);
        var builder = new ComponentBuilder();

        if (routes.Count > 0)
        {
            var options = routes.Take(25).Select(r =>
            {
                var value = $"r:{r.RouteUid}|{r.Direction}";
                var boards = string.Join("/", r.BoardChoices.Select(x => x.BoardStopName).Distinct());

                var opt = new SelectMenuOptionBuilder()
                    .WithLabel(Truncate($"{r.RouteName}（{r.DirectionLabel}）· {r.StopsBetween} 站", 100))
                    .WithValue(Truncate(value, 100))
                    .WithDescription(Truncate($"{boards} 上車 → {r.Earliest.AlightStopName}", 100));

                if (picked.Contains(value)) opt.WithDefault(true);

                return opt;
            }).ToList();

            builder.WithSelectMenu(new SelectMenuBuilder()
                .WithCustomId(Cid.Routes)
                .WithPlaceholder("選擇要訂閱的路線（可多選）")
                .WithMinValues(1)
                .WithMaxValues(Math.Min(25, options.Count))
                .WithOptions(options));
        }

        if (picked.Count > 0)
            builder.WithButton($"✅ 訂閱這 {picked.Count} 條路線", Cid.Subscribe, ButtonStyle.Success);

        builder.WithButton("重新設定起點", Cid.SetOrigin, ButtonStyle.Secondary);
        builder.WithButton("重新設定目的地", Cid.SetDest, ButtonStyle.Secondary);
        builder.WithButton("重新搜尋路線", Cid.Find, ButtonStyle.Secondary);

        return builder.Build();
    }

    /// <summary>勾選路線後的中間狀態：明確告訴使用者「還沒訂閱，請按下面的按鈕」。</summary>
    public static Embed RoutePicked(
        LocationTarget origin, LocationTarget destination,
        IReadOnlyList<RouteOption> allRoutes, IReadOnlyList<RouteOption> picked)
    {
        var lines = picked.Take(15).Select(r =>
        {
            var boards = string.Join(" 或 ", r.BoardChoices.Select(x => x.BoardStopName).Distinct());
            return $"• **{r.RouteName}**（{r.DirectionLabel}）{boards} 上車 · {r.StopsBetween} 站";
        });

        return new EmbedBuilder()
            .WithColor(Info)
            .WithTitle($"☑ 已選擇 {picked.Count} 條路線（尚未訂閱）")
            .WithDescription(string.Join("\n", lines))
            .AddField("下一步", "按下面的「✅ 訂閱這 N 條路線」才會真的建立訂閱。", inline: false)
            .WithFooter($"{origin.DisplayName} → {destination.DisplayName}　共找到 {allRoutes.Count} 條")
            .Build();
    }

    // ─────────────────────────────────────────────────────
    //  通知時間與完成
    // ─────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────
    //  到站時間總表
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 把所有訂閱的到站時間列出來，**依剩餘時間由近到遠排序**。
    /// 沒有預估時間的（尚未發車／末班車已過／查不到）排在後面。
    /// </summary>
    public static Embed EtaTable(
        SubscriptionGroup group,
        IReadOnlyList<BotRuntime.RouteEtaRow> rows,
        DateTimeOffset now,
        bool simulation,
        string? error = null)
    {
        var b = new EmbedBuilder().WithColor(rows.Any(r => r.LiveSeconds.HasValue) ? Info : Muted)
            .WithTitle("⏱ 到站時間（依剩餘時間排序）");

        if (rows.Count == 0)
        {
            b.WithDescription("這個訂閱沒有任何路線。");
            return b.Build();
        }

        var lines = rows.Select(r =>
        {
            var time = r.LiveSeconds is { } live
                ? (live <= 30
                    ? "**即將進站**"
                    : $"**約 {Math.Round(live / 60.0)} 分鐘**")
                : r.StatusText;

            var arrive = r.LiveSeconds is { } l
                ? "（" + TimeZoneInfo.ConvertTime(now.AddSeconds(l), Taipei).ToString("HH:mm") + "）"
                : "";

            return $"**{r.Subscription.RouteName}**（{DirLabel(r.Subscription.Direction)}）" +
                   $"　{r.Subscription.BoardStopName}　{time}{arrive}";
        });

        b.WithDescription(string.Join("\n", lines))
         .WithFooter($"{group.DescribeRoute()}　" +
                     $"共 {rows.Count} 條訂閱　" +
                     $"更新於 {TimeZoneInfo.ConvertTime(now, Taipei):HH:mm:ss}");

        if (simulation)
            b.AddField("⚠️ 模擬資料",
                "沒有 TDX 金鑰，這些是模擬的到站時間。設定 `TDX_CLIENT_ID` / `TDX_CLIENT_SECRET` 就會顯示真實資料。",
                inline: false);

        if (error is not null)
            b.AddField("⚠️ 查詢失敗", Truncate(error, 1000), inline: false);

        return b.Build();
    }

    /// <summary>
    /// 訂閱完成／看到站時間之後的按鈕列。
    ///
    /// ⚠️ 這裡是使用者看到「訂閱完成」時唯一的一組按鈕 ——
    /// 「💾 存成訂閱組」必須在這裡，否則使用者根本按不到（曾經就是這樣壞掉的）。
    /// </summary>
    public static MessageComponent EtaComponents()
        => new ComponentBuilder()
            .WithButton("🔄 重新整理", Cid.ShowEtas, ButtonStyle.Primary)
            .WithButton("模擬一則通知", Cid.Simulate, ButtonStyle.Secondary)
            .WithButton("返回面板", "bus:back", ButtonStyle.Secondary)
            .AddRow(new ActionRowBuilder()
                .WithButton("💾 存成訂閱組", Cid.SaveGroup, ButtonStyle.Success)
                .WithButton("📂 我的訂閱組", Cid.OpenGroups, ButtonStyle.Secondary))
            .Build();

    // ─────────────────────────────────────────────────────
    //  訂閱組
    // ─────────────────────────────────────────────────────

    public static Embed SavedGroupsList(
        IReadOnlyList<SavedGroup> groups,
        IReadOnlyList<long> selectedIds,
        int maxGroups,
        Func<long, SavedGroupPayload?>? payloadOf = null)
    {
        var b = new EmbedBuilder().WithColor(Info).WithTitle("📂 我的訂閱組");

        if (groups.Count == 0)
        {
            b.WithDescription(
                "你還沒有任何訂閱組。\n\n" +
                "完成一次訂閱後，按「💾 存成訂閱組」就能把目前的起訖點與路線存起來。\n" +
                "下次直接套用，不用重新搜尋站牌與路線 —— 而且**重開 Bot 之後還在**。");
            return b.Build();
        }

        var lines = groups.Select(g =>
        {
            var mark = selectedIds.Contains(g.Id) ? "▶ " : "• ";
            var used = g.UseCount > 0 ? $"　（用過 {g.UseCount} 次）" : "";

            // 合併過的組會有多段行程 —— 這裡逐段列出來，才看得出它到底包含什麼
            var payload = payloadOf?.Invoke(g.Id);
            string route;
            if (payload is { LegCount: > 1 })
            {
                var legLines = payload.Legs.Select((l, i) =>
                    $"　　　{i + 1}. {l.Origin.DisplayName} → {l.Destination.DisplayName}" +
                    $"（{l.Routes.Count} 條）");
                route = $"　　**{payload.LegCount} 段行程**\n" + string.Join("\n", legLines);
            }
            else
            {
                route = $"　　{g.OriginName} → {g.DestinationName}";
            }

            return $"{mark}**{g.Name}**{used}\n{route}\n" +
                   $"　　{g.RouteCount} 條路線 · 提前 {g.NotifyMinutes} 分鐘";
        });

        var selectedNote = selectedIds.Count > 0
            ? $"已選 {selectedIds.Count} 組。"
            : "可以**一次選多組**（多選）。";

        b.WithDescription(string.Join("\n\n", lines))
         .WithFooter($"共 {groups.Count}/{maxGroups} 組（依使用次數排序）｜{selectedNote}");

        return b.Build();
    }

    /// <summary>
    /// 訂閱組清單的按鈕與多選選單。
    ///
    /// 三個「套用」的差別（這是這個功能最容易混淆的地方，所以按鈕文字要說清楚）：
    ///   [▶ 各自獨立] 每一組各自成為一個訂閱 → 各通知各的
    ///   [🧩 合併通知] 勾選的組合成**一個**訂閱 → 通知時從所有路線裡挑最快的一班，只通知一次
    ///   [🔗 合併成新組] 把勾選的組合併成**一個新的訂閱組**存起來，下次一鍵套用
    /// </summary>
    public static MessageComponent SavedGroupsComponents(
        IReadOnlyList<SavedGroup> groups,
        IReadOnlyList<long> selectedIds,
        UndoEntry? undo = null)
    {
        var builder = new ComponentBuilder();
        var count = selectedIds.Count;

        if (groups.Count > 0)
        {
            var options = groups.Take(25).Select(g => new SelectMenuOptionBuilder()
                .WithLabel(Truncate(g.Name, 100))
                .WithValue(Truncate($"sg:{g.Id}", 100))
                .WithDescription(Truncate($"{g.OriginName} → {g.DestinationName}｜{g.RouteCount} 條", 100))
                .WithDefault(selectedIds.Contains(g.Id))).ToList();

            builder.WithSelectMenu(new SelectMenuBuilder()
                .WithCustomId(Cid.GroupSelect)
                .WithPlaceholder(count > 0 ? $"已選 {count} 組（可多選）" : "選擇一或多個訂閱組")
                .WithMinValues(1)
                .WithMaxValues(Math.Min(25, groups.Count))
                .WithOptions(options));

            builder.WithButton(count > 1 ? $"▶ 各自獨立訂閱 {count} 組" : "▶ 套用",
                Cid.GroupUse, ButtonStyle.Success, disabled: count == 0);
            builder.WithButton(count > 1 ? $"🧩 合併成一個通知流（{count} 組）" : "🧩 合併成一個通知流",
                Cid.GroupUseMerged, ButtonStyle.Primary, disabled: count == 0);
            builder.WithButton("🔗 合併成新組", Cid.GroupMerge, ButtonStyle.Secondary, disabled: count < 2);
            builder.WithButton("✏️ 改名", Cid.GroupRename, ButtonStyle.Secondary, disabled: count != 1);
            builder.WithButton("🗑 刪除", Cid.GroupDelete, ButtonStyle.Danger, disabled: count == 0);
        }

        if (undo is not null)
        {
            builder.WithButton(Truncate($"↩️ 復原：{undo.Description}", 80),
                Cid.Undo, ButtonStyle.Secondary);
        }

        builder.WithButton("返回面板", "bus:back", ButtonStyle.Secondary);
        return builder.Build();
    }

    /// <summary>套用訂閱組後的結果卡片（可以一次套用多組，所以吃的是清單）。</summary>
    public static Embed GroupsApplied(
        IReadOnlyList<SavedGroup> applied,
        IReadOnlyList<SubscriptionGroup> groups,
        IReadOnlyList<Subscription> subs,
        bool mergedStream,
        IReadOnlyList<string> warnings)
    {
        var nameList = string.Join("、", applied.Select(a => a.Name));
        var title = applied.Count == 1
            ? $"▶ 已套用訂閱組「{nameList}」"
            : $"▶ 已套用 {applied.Count} 個訂閱組";

        var b = new EmbedBuilder()
            .WithColor(Success)
            .WithTitle(Truncate(title, 256));

        if (groups.Count == 1)
        {
            var g = groups[0];
            b.WithDescription($"**{g.DescribeRoute()}**");
        }
        else
        {
            var lines = groups.Select(g => $"• {g.DescribeRoute()}（{g.SubscriptionIds.Count} 個訂閱）");
            b.WithDescription(string.Join("\n", lines));
        }

        var routeNames = subs.Select(s => s.RouteName).Distinct().Take(20).ToList();
        var notifyMinutes = groups.Count > 0 ? $"{groups[0].NotifyBeforeMinutes} 分鐘" : "—";
        var channel = groups.Count == 0 || groups[0].ChannelId is null ? "私訊（DM）" : "本頻道";

        // ⚠️ Discord 不接受空的欄位值 —— 訂閱被取消後這裡可能是空的
        //    （乾跑的 Step 13c 就是這樣炸出來的：整則訊息建不出來）
        b.AddField("來源", nameList.Length == 0 ? "—" : nameList, inline: false)
         .AddField("路線", routeNames.Count == 0 ? "（目前沒有可搭的路線）" : string.Join("、", routeNames),
                   inline: false)
         .AddField("訂閱數", $"{subs.Count} 個", inline: true)
         .AddField("提前通知", notifyMinutes, inline: true)
         .AddField("通知方式", channel, inline: true)
         .AddField("通知流", mergedStream
             ? $"🧩 合併成一個（{Math.Max(1, groups.Count)} 段行程共用）→ 只會通知最快的那一班"
             : $"▶ 各自獨立（{groups.Count} 個訂閱）", inline: false);

        foreach (var w in warnings.Take(3))
            b.AddField("⚠️ 注意", Truncate(w, 1024), inline: false);

        b.WithFooter("這是新的訂閱，會用目前的路線資料重新建立。按「↩️ 復原」可以取消。");
        return b.Build();
    }

    /// <summary>合併訂閱組的 Modal 說明（名稱 + 提前時間）。</summary>
    public static Embed GroupMergePrompt(IReadOnlyList<SavedGroup> selected, int defaultMinutes)
    {
        var lines = selected.Select(g => $"• **{g.Name}**　{g.OriginName} → {g.DestinationName}（{g.RouteCount} 條）");
        var totalRoutes = selected.Sum(g => g.RouteCount);
        var totalLegs = selected.Count;

        return new EmbedBuilder()
            .WithColor(Info)
            .WithTitle($"🔗 合併 {selected.Count} 個訂閱組")
            .WithDescription(string.Join("\n", lines) +
                             $"\n\n合併後會有 **{totalLegs} 段行程、共 {totalRoutes} 條路線**" +
                             "，套用時一次全部訂閱。")
            .AddField("提前通知", $"預設 {defaultMinutes} 分鐘（取原本設定中最大的，提醒最早）", inline: false)
            .WithFooter("同名會覆蓋原本的訂閱組；合併後可以按「↩️ 復原」還原。")
            .Build();
    }

    /// <summary>套用訂閱組後的結果卡片（單組，向後相容）。</summary>
    public static Embed GroupApplied(
        SavedGroup saved, SubscriptionGroup group, IReadOnlyList<Subscription> subs, string? warning)
        => GroupsApplied([saved], [group], subs, mergedStream: group.LegCount == 1,
                         warnings: warning is null ? [] : [warning]);

    public static Embed NotifyChooser(SubscriptionGroup group, IReadOnlyList<Subscription> subs)
    {
        var lines = subs.Take(10).Select(s =>
            $"• **{s.RouteName}**（{DirLabel(s.Direction)}）{s.BoardStopName} → {s.AlightStopName}");
        var more = subs.Count > 10 ? $"\n… 還有 {subs.Count - 10} 個" : "";

        return new EmbedBuilder()
            .WithColor(Success)
            .WithTitle($"✅ 已建立 {subs.Count} 個訂閱（{group.Origin.DisplayName} → {group.Destination.DisplayName}）")
            .WithDescription(string.Join("\n", lines) + more)
            .AddField("提前多久通知你？", "選一個時間，之後可以用 `/bus list` 調整。", inline: false)
            .Build();
    }

    /// <summary>
    /// 提前通知時間的按鈕列。
    ///
    /// 2 分鐘是「我現在就要出門」的實用選項 —— 輪詢間隔 30 秒，
    /// 2 分鐘的視窗內大約會看到 4 次更新，足夠通知一次。
    /// ⚠️ Discord 一列最多 5 個按鈕，這裡剛好是上限。
    /// </summary>
    public static MessageComponent NotifyComponents()
        => new ComponentBuilder()
            .WithButton("2 分鐘", Cid.NotifyPrefix + 2, ButtonStyle.Secondary)
            .WithButton("5 分鐘", Cid.NotifyPrefix + 5, ButtonStyle.Secondary)
            .WithButton("10 分鐘", Cid.NotifyPrefix + 10, ButtonStyle.Primary)
            .WithButton("15 分鐘", Cid.NotifyPrefix + 15, ButtonStyle.Secondary)
            .WithButton("30 分鐘", Cid.NotifyPrefix + 30, ButtonStyle.Secondary)
            .Build();

    public static Embed FinalCard(SubscriptionGroup group, IReadOnlyList<Subscription> subs)
    {
        var byRoute = subs.GroupBy(s => s.RouteName)
                          .Select(g => $"**{string.Join("、", g.Select(x => x.RouteName).Distinct())}**")
                          .Distinct();

        return new EmbedBuilder()
            .WithColor(Success)
            .WithTitle("🔔 訂閱完成")
            .WithDescription($"**{group.DescribeRoute()}**")
            .AddField("路線", byRoute.Any() ? string.Join("\n", byRoute) : "—", inline: true)
            .AddField("訂閱數", $"{subs.Count} 個", inline: true)
            .AddField("提前通知", $"{group.NotifyBeforeMinutes} 分鐘", inline: true)
            .AddField("通知方式", group.ChannelId is null ? "私訊（DM）" : "本頻道", inline: true)
            .WithFooter("公車快到時我會通知你。用 /bus list 查看、/bus remove 取消。")
            .Build();
    }

    // ─────────────────────────────────────────────────────
    //  訂閱清單
    // ─────────────────────────────────────────────────────

    public static Embed SubscriptionList(
        IReadOnlyList<SubscriptionGroup> groups,
        Func<string, IReadOnlyList<Subscription>> subsOf)
    {
        var b = new EmbedBuilder().WithColor(Info).WithTitle("📋 我的訂閱");

        if (groups.Count == 0)
        {
            b.WithDescription("你目前沒有任何訂閱。用 `/bus` 開始設定。");
            return b.Build();
        }

        foreach (var g in groups.Take(5))
        {
            var subs = subsOf(g.Id);
            var lines = subs.Take(8).Select(s =>
                $"• **{s.RouteName}**（{DirLabel(s.Direction)}）{s.BoardStopName} → {s.AlightStopName}");
            var more = subs.Count > 8 ? $"\n… 還有 {subs.Count - 8} 個" : "";

            b.AddField(
                $"{g.Origin.DisplayName} → {g.Destination.DisplayName}",
                string.Join("\n", lines) + more + $"\n　⏰ 提前 {g.NotifyBeforeMinutes} 分鐘",
                inline: false);
        }

        if (groups.Count > 5) b.WithFooter($"共 {groups.Count} 組訂閱");
        return b.Build();
    }

    public static MessageComponent SubscriptionListComponents(IReadOnlyList<SubscriptionGroup> groups)
    {
        var builder = new ComponentBuilder();

        if (groups.Count == 0)
            return builder.WithButton("開始設定", Cid.Panel, ButtonStyle.Primary).Build();

        var options = groups.Take(25).Select(g =>
            new SelectMenuOptionBuilder()
                .WithLabel(Truncate($"{g.Origin.DisplayName} → {g.Destination.DisplayName}", 100))
                .WithValue(Truncate(Cid.RemoveGroupPrefix + g.Id, 100))
                .WithDescription(Truncate($"{g.SubscriptionIds.Count} 個訂閱｜提前 {g.NotifyBeforeMinutes} 分", 100))
        ).ToList();

        return builder
            .WithSelectMenu(new SelectMenuBuilder()
                .WithCustomId("bus:rmmenu")
                .WithPlaceholder("選擇要取消的訂閱")
                .WithMinValues(1)
                .WithMaxValues(1)
                .WithOptions(options))
            .Build();
    }

    // ─────────────────────────────────────────────────────
    //  結束追蹤（/bus end）
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// `/bus end` 的結果卡。
    ///
    /// 為什麼要特別顯示「不再查詢的上車站」：輪詢成本只取決於
    /// **不重複的上車站數量**（見 <c>SubscriptionService.GetAllEnabledBoardStopUids</c>），
    /// 這才是「停止追蹤之後 TDX 呼叫真的少多少」的指標 ——
    /// 顯示「取消了 87 筆訂閱」反而看不出省下什麼。
    /// </summary>
    public static Embed TrackingEnded(int groupCount, int subscriptionCount, int boardStopCount, int routeCount)
    {
        var b = new EmbedBuilder()
            .WithColor(Muted)
            .WithTitle("🛑 已停止追蹤")
            .WithDescription(
                "你的訂閱已經全部取消，我不會再查詢這些路線的到站時間，也不會再通知你。\n" +
                "**按下面的「↩️ 復原」就可以全部放回來。**")
            .AddField("取消的訂閱群組", $"{groupCount} 個", inline: true)
            .AddField("取消的訂閱", $"{subscriptionCount} 筆", inline: true)
            .AddField("不再查詢的上車站", $"{boardStopCount} 個", inline: true)
            .AddField("不再查詢的路線", $"{routeCount} 條", inline: true)
            .WithFooter("復原紀錄只存在記憶體，Bot 重啟後就沒辦法復原了。要重新設定請用 /bus panel。");

        return b.Build();
    }

    /// <summary>`/bus end` 之後的按鈕：復原（唯一的救援方式）＋ 重新開始。</summary>
    public static MessageComponent EndComponents(int groupCount)
        => new ComponentBuilder()
            .WithButton($"↩️ 復原（把 {groupCount} 組訂閱放回來）", Cid.Undo, ButtonStyle.Primary)
            .WithButton("開始新訂閱", Cid.Panel, ButtonStyle.Secondary)
            .Build();

    // ─────────────────────────────────────────────────────
    //  通知訊息（真正會送出的長相）
    // ─────────────────────────────────────────────────────

    public static Embed Notification(
        SubscriptionGroup group, Subscription sub, BusEta eta, double liveSeconds,
        IReadOnlyList<BusArrivalNotice.Alternative> alternatives, DateTimeOffset now,
        bool simulation = false)
    {
        var arriveAt = TimeZoneInfo.ConvertTime(now.AddSeconds(liveSeconds), Taipei);
        var timeText = liveSeconds <= 30
            ? "**即將進站**"
            : $"約 **{Math.Round(liveSeconds / 60.0)} 分鐘**到站（{arriveAt:HH:mm}）";

        var b = new EmbedBuilder()
            .WithColor(Alert)
            .WithTitle("🚌 公車快到了！")
            .AddField("路線", $"{sub.RouteName}（{DirLabel(sub.Direction)}）", inline: true)
            .AddField("車牌", string.IsNullOrWhiteSpace(eta.PlateNumb) ? "—" : eta.PlateNumb, inline: true)
            .AddField("上車站", $"{sub.BoardStopName}\n第 {sub.BoardSequence} 站", inline: true)
            .AddField("預計", timeText, inline: true)
            .AddField("目的地", $"{sub.AlightStopName}\n還有 {sub.StopsBetween} 站", inline: true)
            .AddField("起訖", $"{group.LegOf(sub).Origin.DisplayName} → {group.LegOf(sub).Destination.DisplayName}", inline: false);

        if (alternatives.Count > 0)
        {
            var alt = string.Join("\n", alternatives.Select(a =>
                $"• {a.RouteName} 在 {a.StopName}　約 {Math.Round(a.LiveSeconds / 60.0)} 分"));
            b.AddField("其他選擇", alt, inline: false);
        }

        b.WithFooter(simulation
            ? "⚠️ 這是模擬資料（沒有 TDX 金鑰），用來確認通知的內容與格式"
            : "資料來源：TDX 公車預估到站（N1）");

        return b.Build();
    }

    // ─────────────────────────────────────────────────────

    internal static string DirLabel(int direction)
        => direction switch { 0 => "去程", 1 => "返程", 2 => "迴圈", _ => "未知" };

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";
}
