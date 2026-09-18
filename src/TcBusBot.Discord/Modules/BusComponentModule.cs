using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Storage;
using TcBusBot.Core.Subscriptions;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// 所有按鈕、選單與 Modal 的處理。
///
/// 全部是記憶體運算，回應時間遠低於 Discord 的 3 秒限制。
/// </summary>
public sealed class BusComponentModule : BusModuleBase
{
    private readonly BusDataCatalog _catalog;

    /// <summary>**這個人**選的城市要用哪一份資料（訂公車的人自己選，見 `/bus city`）。</summary>
    private BusDataService MyData => _catalog.For(Context.User.Id);
    private readonly SubscriptionService _subs;
    private readonly BusSessionStore _sessions;
    private readonly BotRuntime _runtime;
    private readonly SavedGroupStore _savedGroups;

    public BusComponentModule(
        BusDataCatalog catalog,
        SubscriptionService subs,
        BusSessionStore sessions,
        BotRuntime runtime,
        SavedGroupStore savedGroups)
    {
        _catalog = catalog;
        _subs = subs;
        _sessions = sessions;
        _runtime = runtime;
        _savedGroups = savedGroups;
    }

    private BusSession? Session => _sessions.Get(Context.User.Id, Context.Channel.Id);

    private async Task<bool> RequireSessionAsync()
    {
        if (Session is not null) return true;

        await RespondAsync(
            "這個面板已經失效（Bot 可能重新啟動過，面板狀態只存在記憶體）。\n" +
            "請重新執行 `/bus panel`。",
            ephemeral: true);
        return false;
    }

    // ─────────────────────────────────────────────────────
    //  面板
    // ─────────────────────────────────────────────────────

    [ComponentInteraction(Cid.Panel)]
    public async Task OpenPanelAsync()
    {
        var session = _sessions.GetOrCreate(Context.User.Id, Context.Channel.Id);
        await UpdateAsync(
            embed: BusUi.Panel(session, _subs.GetGroupsByUser(Context.User.Id).Count()),
            components: BusUi.PanelComponents(session));
    }

    [ComponentInteraction(Cid.Reset)]
    public async Task ResetAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        if (session.CreatedGroupId is { } gid) _subs.RemoveGroup(gid);
        session.Origin = null;
        session.Destination = null;
        session.CreatedGroupId = null;
        session.LastRoutes.Clear();
        session.ResetPicks();

        await UpdateAsync(
            embed: BusUi.Panel(session, _subs.GetGroupsByUser(Context.User.Id).Count()),
            components: BusUi.PanelComponents(session));
    }

    // ─────────────────────────────────────────────────────
    //  開啟站名搜尋 Modal
    // ─────────────────────────────────────────────────────

    [ComponentInteraction(Cid.SetOrigin)]
    public async Task OpenOriginModalAsync()
        => await Context.Interaction.RespondWithModalAsync<OriginKeywordModal>(Cid.ModalOrigin);

    [ComponentInteraction(Cid.SetDest)]
    public async Task OpenDestModalAsync()
        => await Context.Interaction.RespondWithModalAsync<DestKeywordModal>(Cid.ModalDest);

    // ─────────────────────────────────────────────────────
    //  Modal 送出 → 模糊搜尋
    // ─────────────────────────────────────────────────────

    [ModalInteraction(Cid.ModalOrigin)]
    public Task ModalOriginAsync(OriginKeywordModal modal) => RunSearchSafelyAsync(modal.Keyword, isOrigin: true);

    [ModalInteraction(Cid.ModalDest)]
    public Task ModalDestAsync(DestKeywordModal modal) => RunSearchSafelyAsync(modal.Keyword, isOrigin: false);

    /// <summary>搜尋不到任何站牌時的「重新輸入」按鈕。</summary>
    [ComponentInteraction(Cid.RetryOrigin)]
    public async Task RetryOriginAsync()
        => await Context.Interaction.RespondWithModalAsync<OriginKeywordModal>(Cid.ModalOrigin);

    [ComponentInteraction(Cid.RetryDest)]
    public async Task RetryDestAsync()
        => await Context.Interaction.RespondWithModalAsync<DestKeywordModal>(Cid.ModalDest);

    /// <summary>
    /// 包一層 try/catch。
    /// Modal 的處理若直接丟例外，Discord 端只會顯示「無法提交」，
    /// 使用者完全不知道發生什麼事 —— 至少要把原因講出來。
    /// </summary>
    private async Task RunSearchSafelyAsync(string keyword, bool isOrigin)
    {
        try
        {
            await RunSearchAsync(keyword, isOrigin);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[modal] 搜尋「{keyword}」失敗：{ex}");

            var message = $"❌ 搜尋「{keyword}」時發生錯誤（詳細訊息已印在 Bot 的 console）：\n" +
                          $"```{ex.GetType().Name}: {ex.Message}```";

            if (Context.Interaction.HasResponded) await FollowupAsync(message, ephemeral: true);
            else await RespondAsync(message, ephemeral: true);
        }
    }

    private async Task RunSearchAsync(string keyword, bool isOrigin)
    {
        var session = _sessions.GetOrCreate(Context.User.Id, Context.Channel.Id);

        session.PendingIsOrigin = isOrigin;
        session.LastKeyword = keyword ?? "";

        // 有「完全／前綴相符」的結果時就只顯示那些，避免被模糊相符的雜訊塞滿 25 個選項
        var all = MyData.Search.SearchGrouped(keyword).ToList();
        var (shown, hiddenFuzzy) = BusUi.PreferStrongMatches(all);

        session.LastSearch = shown;
        session.PendingPick = new List<string>();

        // Modal 的回應只能是一則新訊息（不能在 Modal 上做 Update），
        // 所以這裡送出一則新的 ephemeral 訊息，之後的步驟都會更新它。
        await RespondAsync(
            embed: BusUi.SearchResult(session.LastKeyword, shown, MyData, hiddenFuzzy),
            components: BusUi.SearchComponents(isOrigin, shown, MyData, Array.Empty<string>()),
            ephemeral: true);
    }

    // ─────────────────────────────────────────────────────
    //  勾選站牌／群組
    // ─────────────────────────────────────────────────────

    [ComponentInteraction(Cid.PickOrigin)]
    public Task PickOriginAsync() => HandlePickAsync(isOrigin: true);

    [ComponentInteraction(Cid.PickDest)]
    public Task PickDestAsync() => HandlePickAsync(isOrigin: false);

    private async Task HandlePickAsync(bool isOrigin)
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var values = ((SocketMessageComponent)Context.Interaction).Data.Values.ToList();
        session.PendingPick = values;

        var effective = values.Count > 0 ? values : DefaultPicks(session.LastSearch);
        var uids = ResolveStopUids(effective);

        await UpdateAsync(
            embed: BusUi.PickedSummary(isOrigin, uids, values, MyData),
            components: BusUi.SearchComponents(isOrigin, session.LastSearch, MyData, values));
    }

    [ComponentInteraction(Cid.SelectAllOrigin)]
    public Task SelectAllOriginAsync() => HandleSelectAllAsync(isOrigin: true);

    [ComponentInteraction(Cid.SelectAllDest)]
    public Task SelectAllDestAsync() => HandleSelectAllAsync(isOrigin: false);

    private async Task HandleSelectAllAsync(bool isOrigin)
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        // 全選 = 實際選項清單裡的全部值（不能自己拼，否則會帶入不存在的值）
        var all = BusUi.AllStopValues(session.LastSearch, MyData);

        session.PendingPick = all;
        var uids = ResolveStopUids(all);

        await UpdateAsync(
            embed: BusUi.PickedSummary(isOrigin, uids, all, MyData),
            components: BusUi.SearchComponents(isOrigin, session.LastSearch, MyData, all));
    }

    [ComponentInteraction(Cid.ConfirmOrigin)]
    public Task ConfirmOriginAsync() => HandleConfirmAsync(isOrigin: true);

    [ComponentInteraction(Cid.ConfirmDest)]
    public Task ConfirmDestAsync() => HandleConfirmAsync(isOrigin: false);

    private async Task HandleConfirmAsync(bool isOrigin)
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var effective = session.PendingPick.Count > 0 ? session.PendingPick : DefaultPicks(session.LastSearch);
        var uids = ResolveStopUids(effective);

        if (uids.Count == 0)
        {
            await UpdateAsync(
                embed: new EmbedBuilder()
                    .WithColor(new Color(0xE6, 0x7E, 0x22))
                    .WithTitle("沒有選到任何站牌")
                    .WithDescription("請至少勾選一個站牌或一個群組，再按「確認」。")
                    .Build(),
                components: BusUi.SearchComponents(isOrigin, session.LastSearch, MyData, session.PendingPick));
            return;
        }

        var target = MyData.TargetFromStops(uids);
        if (isOrigin) session.Origin = target;
        else session.Destination = target;

        session.ResetPicks();

        await UpdateAsync(
            embed: BusUi.Panel(session, _subs.GetGroupsByUser(Context.User.Id).Count()),
            components: BusUi.PanelComponents(session));
    }

    // ─────────────────────────────────────────────────────
    //  搜尋路線
    // ─────────────────────────────────────────────────────

    [ComponentInteraction(Cid.Find)]
    public async Task FindAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        if (!session.Ready)
        {
            await UpdateAsync(
                embed: new EmbedBuilder()
                    .WithColor(new Color(0xE6, 0x7E, 0x22))
                    .WithTitle("還沒設定完")
                    .WithDescription("請先設定起點與目的地。")
                    .Build(),
                components: BusUi.PanelComponents(session));
            return;
        }

        var routes = MyData.FindRoutes(session.Origin!, session.Destination!);
        session.LastRoutes = routes.ToList();
        session.PickedRouteValues = new List<string>();   // 重新搜尋就清掉先前的勾選

        await UpdateAsync(
            embed: BusUi.RouteList(session.Origin!, session.Destination!, routes),
            components: BusUi.RouteComponents(routes));
    }

    // ─────────────────────────────────────────────────────
    //  選路線 → 建立訂閱
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 勾選路線。
    ///
    /// ⚠️ 這裡**只記錄選擇，不建立訂閱**。
    /// 使用者需要明確按下「✅ 訂閱這 N 條路線」才會真的建立 ——
    /// 光是操作選單就默默生效會讓人搞不清楚到底訂了沒。
    /// </summary>
    [ComponentInteraction(Cid.Routes)]
    public async Task RoutesSelectedAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var values = ((SocketMessageComponent)Context.Interaction).Data.Values.ToList();
        session.PickedRouteValues = values;

        var pickedKeys = values
            .Select(ParseRouteValue)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToHashSet();

        var chosen = session.LastRoutes
            .Where(r => pickedKeys.Contains((r.RouteUid, r.Direction)))
            .ToList();

        await UpdateAsync(
            embed: BusUi.RoutePicked(session.Origin!, session.Destination!, session.LastRoutes, chosen),
            components: BusUi.RouteComponents(session.LastRoutes, values));
    }

    /// <summary>按下「✅ 訂閱這 N 條路線」才會真的建立訂閱。</summary>
    [ComponentInteraction(Cid.Subscribe)]
    public async Task SubscribeAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        if (!session.Ready)
        {
            await RespondAsync("起點或目的地還沒設定完成，請重新執行 `/bus panel`。", ephemeral: true);
            return;
        }

        var pickedKeys = session.PickedRouteValues
            .Select(ParseRouteValue)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToHashSet();

        var chosen = session.LastRoutes
            .Where(r => pickedKeys.Contains((r.RouteUid, r.Direction)))
            .ToList();

        if (chosen.Count == 0)
        {
            await UpdateAsync(
                embed: new EmbedBuilder()
                    .WithColor(new Color(0xE6, 0x7E, 0x22))
                    .WithTitle("還沒有選擇路線")
                    .WithDescription("請先在上面選單勾選要訂閱的路線，再按「訂閱」。")
                    .Build(),
                components: BusUi.RouteComponents(session.LastRoutes, session.PickedRouteValues));
            return;
        }

        // 重新訂閱時，先把上一輪建立的群組移除，避免重複
        if (session.CreatedGroupId is { } old) _subs.RemoveGroup(old);

        var group = _subs.CreateGroup(
            userId: Context.User.Id,
            origin: session.Origin!,
            destination: session.Destination!,
            options: chosen,
            notifyBeforeMinutes: session.NotifyMinutes,
            guildId: Context.Guild?.Id,
            channelId: Context.Channel.Id);

        session.CreatedGroupId = group.Id;

        Console.WriteLine($"[subscribe] 使用者 {Context.User.Id} 建立 {chosen.Count} 條路線、" +
                          $"{group.SubscriptionIds.Count} 個訂閱（{session.Origin.DisplayName} → {session.Destination.DisplayName}）");

        await UpdateAsync(
            embed: BusUi.NotifyChooser(group, _subs.GetSubscriptions(group)),
            components: BusUi.NotifyComponents());
    }

    // ─────────────────────────────────────────────────────
    //  到站時間總表（依剩餘時間排序）
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 顯示所有訂閱的到站時間，依剩餘時間由近到遠排序。
    ///
    /// 有 TDX 金鑰時查真實 ETA（一次呼叫涵蓋整個群組的上車站）；
    /// 沒有金鑰時用模擬資料，讓功能仍然可以被看到。
    /// </summary>
    [ComponentInteraction(Cid.ShowEtas)]
    public async Task ShowEtasAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var group = CurrentGroupOf(session);
        if (group is null)
        {
            await RespondAsync("找不到可查詢的訂閱。請先用 `/bus panel` 完成訂閱。", ephemeral: true);
            return;
        }

        // TDX 查詢可能需要一點時間 → 先 defer，避免超過 3 秒的回應期限
        await DeferAsync();

        var now = DateTimeOffset.UtcNow;
        var result = await _runtime.BuildEtaTableAsync(group, now);

        Console.WriteLine($"[etas] 使用者 {Context.User.Id} 查詢 {result.Rows.Count} 條訂閱" +
                          $"（{(result.Simulation ? "模擬" : "真實")}資料）");

        await SetMessageAsync(
            embeds:
            [
                BusUi.FinalCard(group, _subs.GetSubscriptions(group)),
                BusUi.EtaTable(group, result.Rows, now, result.Simulation, result.Error)
            ],
            components: BusUi.EtaComponents());
    }

    private SubscriptionGroup? CurrentGroupOf(BusSession session)
        => session.CreatedGroupId is { } id
            ? _subs.GetGroup(id)
            : _subs.GetGroupsByUser(Context.User.Id).LastOrDefault();

    // ─────────────────────────────────────────────────────
    //  訂閱組（存到 SQLite，重啟後還在）
    // ─────────────────────────────────────────────────────

    /// <summary>把「目前這一組訂閱」存成具名訂閱組。</summary>
    [ComponentInteraction(Cid.SaveGroup)]
    public async Task SaveGroupAsync()
    {
        if (!await RequireSessionAsync()) return;

        var group = CurrentGroupOf(Session!);
        if (group is null)
        {
            await RespondAsync("你還沒有建立任何訂閱。請先用 `/bus panel` 完成一次訂閱。", ephemeral: true);
            return;
        }

        await Context.Interaction.RespondWithModalAsync<SaveGroupModal>(Cid.SaveGroupModal);
    }

    [ModalInteraction(Cid.SaveGroupModal)]
    public async Task SaveGroupModalAsync(SaveGroupModal modal)
    {
        var session = _sessions.Get(Context.User.Id, Context.Channel.Id);
        var group = session is null ? null : CurrentGroupOf(session);

        if (session is null || group is null)
        {
            await RespondAsync("面板已失效，請重新執行 `/bus panel`。", ephemeral: true);
            return;
        }

        var (result, message) = _savedGroups.Save(Context.User.Id, modal.Name, BuildPayloadFrom(group));

        var ok = result is SaveGroupResult.Created or SaveGroupResult.Updated;
        var verb = result == SaveGroupResult.Created ? "已新增" : "已更新";

        Console.WriteLine($"[groups] 使用者 {Context.User.Id} {(ok ? verb : "儲存失敗")}訂閱組「{modal.Name}」（{result}）");

        var embed = new EmbedBuilder()
            .WithColor(ok ? new Color(0x2D, 0x9C, 0x4F) : new Color(0xE6, 0x7E, 0x22))
            .WithTitle(ok ? $"💾 {verb}訂閱組「{modal.Name}」" : "⚠️ 無法儲存訂閱組")
            .WithDescription(ok
                ? $"包含 **{group.SubscriptionIds.Count}** 個訂閱（{group.Origin.DisplayName} → {group.Destination.DisplayName}）。\n" +
                  "下次可以用「📂 我的訂閱組」一鍵套用 —— **重開 Bot 之後還在**。"
                : DescribeSaveFailure(result, message))
            .Build();

        var groups = _savedGroups.ListByUser(Context.User.Id);
        await RespondAsync(embed: embed, components: BusUi.SavedGroupsComponents(groups, new List<long>()), ephemeral: true);
    }

    /// <summary>開啟訂閱組清單（可以多選）。</summary>
    [ComponentInteraction(Cid.OpenGroups)]
    public async Task OpenGroupsAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var groups = _savedGroups.ListByUser(Context.User.Id);
        DropStaleSelection(session, groups);

        await UpdateAsync(
            embed: BusUi.SavedGroupsList(groups, session.SelectedSavedGroupIds,
                                        SavedGroupStore.MaxGroupsPerUser, PayloadOf),
            components: BusUi.SavedGroupsComponents(groups, session.SelectedSavedGroupIds, session.Undo.Peek()));
    }

    /// <summary>多選：把選單裡勾選的組記到 session，後續的套用／合併／刪除都針對這一組清單。</summary>
    [ComponentInteraction(Cid.GroupSelect)]
    public async Task GroupSelectAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var picked = ((SocketMessageComponent)Context.Interaction).Data.Values
            .Select(v => v.StartsWith("sg:", StringComparison.Ordinal) && long.TryParse(v[3..], out var id)
                ? id
                : (long?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var groups = _savedGroups.ListByUser(Context.User.Id);
        var known = groups.Select(g => g.Id).ToHashSet();

        // 只看得到自己現有的組（避免有人亂送別人的 id）
        session.SelectedSavedGroupIds = picked.Where(known.Contains).ToList();

        await UpdateAsync(
            embed: BusUi.SavedGroupsList(groups, session.SelectedSavedGroupIds,
                                        SavedGroupStore.MaxGroupsPerUser, PayloadOf),
            components: BusUi.SavedGroupsComponents(groups, session.SelectedSavedGroupIds, session.Undo.Peek()));
    }

    /// <summary>各自獨立套用：每一組各自成為一個訂閱群組（各通知各的）。</summary>
    [ComponentInteraction(Cid.GroupUse)]
    public Task GroupUseAsync() => ApplySelectedAsync(mergeIntoOneStream: false);

    /// <summary>合併成一個通知流：所有選取的行程合成一個訂閱群組，只通知最快的那一班。</summary>
    [ComponentInteraction(Cid.GroupUseMerged)]
    public Task GroupUseMergedAsync() => ApplySelectedAsync(mergeIntoOneStream: true);

    private async Task ApplySelectedAsync(bool mergeIntoOneStream)
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var selected = SelectedGroups(session);
        if (selected.Count == 0)
        {
            await RespondAsync("請先在上面選單勾選一或多個訂閱組。", ephemeral: true);
            return;
        }

        // 重新匹配：一段一段用「目前」的站序資料算出來
        var plans = new List<LegPlan>();
        var warnings = new List<string>();
        var applied = new List<SavedGroup>();

        foreach (var (saved, payload) in selected)
        {
            var legs = BuildLegPlans(saved, payload, warnings);
            if (legs.Count == 0) continue;

            plans.AddRange(legs);
            applied.Add(saved);
        }

        if (plans.Count == 0)
        {
            await RespondAsync(
                $"⚠️ {(warnings.Count > 0 ? string.Join("；", warnings) : "選取的訂閱組在目前的資料裡都找不到路線。")}",
                ephemeral: true);
            return;
        }

        // 先移除這個 session 先前建立的訂閱，避免重複累積（那一步本身也可以復原）
        var previousSessionGroupId = session.CreatedGroupId;
        if (session.CreatedGroupId is { } oldGroupId)
        {
            _subs.RemoveGroup(oldGroupId);
            session.CreatedGroupId = null;
        }

        // 取最大的（= 提醒最早的那個）：晚通知會讓人錯過公車，早通知只是多一則訊息
        var notifyMinutes = applied.Max(a => a.NotifyMinutes);

        var created = new List<SubscriptionGroup>();
        if (mergeIntoOneStream)
        {
            created.Add(_subs.CreateMultiLegGroup(
                userId: Context.User.Id,
                legs: plans,
                notifyBeforeMinutes: notifyMinutes,
                guildId: Context.Guild?.Id,
                channelId: Context.Channel.Id));
        }
        else
        {
            foreach (var plan in plans)
            {
                created.Add(_subs.CreateGroup(
                    userId: Context.User.Id,
                    origin: plan.Origin,
                    destination: plan.Destination,
                    options: plan.Options,
                    notifyBeforeMinutes: notifyMinutes,
                    guildId: Context.Guild?.Id,
                    channelId: Context.Channel.Id));
            }
        }

        foreach (var a in applied) _savedGroups.Touch(a.Id, Context.User.Id);

        // ★ 記下「怎麼還原」——一次套用很多組時，這是最容易點錯的操作
        session.Undo.Push(new UndoAppliedSubscriptions(
            Description: mergeIntoOneStream
                ? $"套用 {applied.Count} 組（合併成 1 個通知流）"
                : $"套用 {applied.Count} 個訂閱組",
            CreatedGroupIds: created.Select(g => g.Id).ToList(),
            PreviousSessionGroupId: previousSessionGroupId));

        session.CreatedGroupId = created[0].Id;
        session.Origin = created[0].Origin;
        session.Destination = created[0].Destination;
        session.NotifyMinutes = notifyMinutes;

        var allSubs = created.SelectMany(_subs.GetSubscriptions).ToList();

        Console.WriteLine($"[groups] 使用者 {Context.User.Id} 套用 {applied.Count} 個訂閱組" +
                          $"（{(mergeIntoOneStream ? "合併成一個通知流" : "各自獨立")}）→ " +
                          $"{created.Count} 個群組、{allSubs.Count} 個訂閱");

        await DeferAsync();

        var now = DateTimeOffset.UtcNow;
        var etas = await _runtime.BuildEtaTableAsync(created[0], now);

        await SetMessageAsync(
            embeds:
            [
                BusUi.GroupsApplied(applied, created, allSubs, mergeIntoOneStream, warnings),
                BusUi.EtaTable(created[0], etas.Rows, now, etas.Simulation, etas.Error)
            ],
            components: BusUi.EtaComponents());
    }

    /// <summary>把選取的訂閱組合併成一個新的訂閱組（多段行程），存進資料庫。</summary>
    [ComponentInteraction(Cid.GroupMerge)]
    public async Task GroupMergeAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var selected = SelectedGroups(session);
        if (selected.Count < 2)
        {
            await RespondAsync("合併至少需要勾選 2 個訂閱組。", ephemeral: true);
            return;
        }

        var defaultMinutes = selected.Max(s => s.Group.NotifyMinutes);
        await Context.Interaction.RespondWithModalAsync<MergeGroupsModal>(
            Cid.GroupMergeModal, new MergeGroupsModal(defaultMinutes));
    }

    [ModalInteraction(Cid.GroupMergeModal)]
    public async Task GroupMergeModalAsync(MergeGroupsModal modal)
    {
        var session = _sessions.Get(Context.User.Id, Context.Channel.Id);
        if (session is null)
        {
            await RespondAsync("面板已失效，請重新執行 `/bus panel`。", ephemeral: true);
            return;
        }

        var selected = SelectedGroups(session);
        if (selected.Count < 2)
        {
            await RespondAsync("請重新勾選要合併的訂閱組（至少 2 個）。", ephemeral: true);
            return;
        }

        var name = (modal.Name ?? "").Trim();
        var minutes = int.TryParse((modal.Minutes ?? "").Trim(), out var m) && m is >= 1 and <= 120
            ? m
            : selected.Max(s => s.Group.NotifyMinutes);

        // 同名覆蓋時先備份，復原才有東西可以還原
        var existing = _savedGroups.GetByName(Context.User.Id, name);
        var overwritten = existing is null ? null : _savedGroups.GetPayload(existing.Id, Context.User.Id);

        var merged = SavedGroupPayloadFactory.Merge(selected.Select(s => s.Payload), minutes);
        var (result, message) = _savedGroups.Save(Context.User.Id, name, merged);

        if (result is not (SaveGroupResult.Created or SaveGroupResult.Updated))
        {
            await RespondAsync($"⚠️ 合併失敗：{DescribeSaveFailure(result, message)}", ephemeral: true);
            return;
        }

        var savedRow = _savedGroups.GetByName(Context.User.Id, name);
        if (savedRow is not null)
        {
            session.Undo.Push(new UndoMergedSavedGroup(
                Description: $"合併成「{name}」",
                NewGroupId: savedRow.Id,
                Name: name,
                Overwritten: overwritten));
        }

        session.SelectedSavedGroupIds = savedRow is null ? new List<long>() : new List<long> { savedRow.Id };

        Console.WriteLine($"[groups] 使用者 {Context.User.Id} 合併 {selected.Count} 個訂閱組 → " +
                          $"「{name}」（{merged.LegCount} 段、{merged.RouteCount} 條路線，{result}）");

        var groups = _savedGroups.ListByUser(Context.User.Id);

        await RespondAsync(
            text: $"🔗 已把 {selected.Count} 個組合併成「**{name}**」：" +
                  $"{merged.LegCount} 段行程、共 {merged.RouteCount} 條路線" +
                  (overwritten is null ? "。" : "（覆蓋了同名的舊組，按「↩️ 復原」可以還原）"),
            embed: BusUi.SavedGroupsList(groups, session.SelectedSavedGroupIds,
                                         SavedGroupStore.MaxGroupsPerUser, PayloadOf),
            components: BusUi.SavedGroupsComponents(groups, session.SelectedSavedGroupIds, session.Undo.Peek()),
            ephemeral: true);
    }

    /// <summary>刪除選取的所有訂閱組（可以復原）。</summary>
    [ComponentInteraction(Cid.GroupDelete)]
    public async Task GroupDeleteAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var selected = SelectedGroups(session);
        if (selected.Count == 0)
        {
            await RespondAsync("請先在上面選單勾選要刪除的訂閱組。", ephemeral: true);
            return;
        }

        var snapshots = new List<UndoDeletedSavedGroups.Snapshot>();
        var deletedNames = new List<string>();

        foreach (var (saved, payload) in selected)
        {
            if (!_savedGroups.Delete(saved.Id, Context.User.Id)) continue;

            snapshots.Add(new UndoDeletedSavedGroups.Snapshot(saved.Name, payload));
            deletedNames.Add(saved.Name);
        }

        if (snapshots.Count > 0)
        {
            session.Undo.Push(new UndoDeletedSavedGroups(
                Description: $"刪除 {snapshots.Count} 個訂閱組",
                Deleted: snapshots));
        }

        session.SelectedSavedGroupIds = new List<long>();

        Console.WriteLine($"[groups] 使用者 {Context.User.Id} 刪除 {snapshots.Count} 個訂閱組");

        var groups = _savedGroups.ListByUser(Context.User.Id);

        await UpdateAsync(
            text: snapshots.Count == 0
                ? "⚠️ 沒有刪除任何東西。"
                : $"🗑 已刪除 {snapshots.Count} 個訂閱組：{string.Join("、", deletedNames)}" +
                  "（按「↩️ 復原」可以還原）",
            embed: BusUi.SavedGroupsList(groups, session.SelectedSavedGroupIds,
                                        SavedGroupStore.MaxGroupsPerUser, PayloadOf),
            components: BusUi.SavedGroupsComponents(groups, session.SelectedSavedGroupIds, session.Undo.Peek()));
    }

    /// <summary>復原上一動作（合併／批次套用／刪除／改名）。</summary>
    [ComponentInteraction(Cid.Undo)]
    public async Task UndoAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var entry = session.Undo.Peek();
        if (entry is null)
        {
            await RespondAsync("沒有可以復原的動作了。", ephemeral: true);
            return;
        }

        // 套用訂閱的復原要順便處理 session 指向哪個群組
        var previousSessionGroupId = (entry as UndoAppliedSubscriptions)?.PreviousSessionGroupId;

        var message = session.Undo.Undo(_subs, _savedGroups, Context.User.Id);

        if (entry is UndoAppliedSubscriptions)
        {
            session.CreatedGroupId = previousSessionGroupId is { } pid && _subs.GetGroup(pid) is not null
                ? pid
                : null;
        }
        else if (entry is UndoEndedTracking ended
                 && ended.PreviousSessionGroupId is { } endedGroupId
                 && _subs.GetGroup(endedGroupId) is not null)
        {
            // 「結束追蹤」的復原：面板本來指著的那一組回來了，就繼續指著它
            session.CreatedGroupId = endedGroupId;
        }

        Console.WriteLine($"[undo] 使用者 {Context.User.Id} 復原「{entry.Description}」→ {message}");

        // 「結束追蹤」復原回來的是**訂閱**，不是「訂閱組」——
        // 這時候顯示訂閱組清單會讓人以為什麼都沒回來，所以改顯示訂閱清單。
        if (entry is UndoEndedTracking)
        {
            var restored = _subs.GetGroupsByUser(Context.User.Id).ToList();

            await UpdateAsync(
                text: $"↩️ 已復原「{entry.Description}」\n{message}\n（用 `/bus next` 看目前的到站時間）",
                embed: BusUi.SubscriptionList(restored, g => _subs.GetSubscriptions(_subs.GetGroup(g)!)),
                components: BusUi.SubscriptionListComponents(restored));
            return;
        }

        var groups = _savedGroups.ListByUser(Context.User.Id);
        session.SelectedSavedGroupIds = session.SelectedSavedGroupIds.Where(id => groups.Any(g => g.Id == id)).ToList();

        await UpdateAsync(
            text: $"↩️ 已復原「{entry.Description}」\n{message}",
            embed: BusUi.SavedGroupsList(groups, session.SelectedSavedGroupIds,
                                        SavedGroupStore.MaxGroupsPerUser, PayloadOf),
            components: BusUi.SavedGroupsComponents(groups, session.SelectedSavedGroupIds, session.Undo.Peek()));
    }

    [ComponentInteraction(Cid.GroupRename)]
    public async Task GroupRenameAsync()
    {
        if (!await RequireSessionAsync()) return;

        if (Session!.SelectedSavedGroupIds.Count != 1)
        {
            await RespondAsync("改名一次只能改一個，請只勾選一個訂閱組。", ephemeral: true);
            return;
        }

        await Context.Interaction.RespondWithModalAsync<RenameGroupModal>(Cid.GroupRenameModal);
    }

    [ModalInteraction(Cid.GroupRenameModal)]
    public async Task GroupRenameModalAsync(RenameGroupModal modal)
    {
        var session = _sessions.Get(Context.User.Id, Context.Channel.Id);

        if (session?.SingleSelectedSavedGroupId is not { } id)
        {
            await RespondAsync("面板已失效或選取了多個組，請重新執行 `/bus panel`。", ephemeral: true);
            return;
        }

        var before = _savedGroups.Get(id, Context.User.Id);
        var ok = _savedGroups.Rename(id, Context.User.Id, modal.Name);

        if (ok && before is not null)
        {
            session.Undo.Push(new UndoRenamedSavedGroup(
                Description: $"改名「{before.Name}」→「{modal.Name}」",
                GroupId: id,
                OldName: before.Name));
        }

        var groups = _savedGroups.ListByUser(Context.User.Id);

        await RespondAsync(
            text: ok ? $"✏️ 已改名為「{modal.Name}」。" : "⚠️ 改名失敗（名稱可能重複、太長或空白）。",
            embed: BusUi.SavedGroupsList(groups, session.SelectedSavedGroupIds,
                                         SavedGroupStore.MaxGroupsPerUser, PayloadOf),
            components: BusUi.SavedGroupsComponents(groups, session.SelectedSavedGroupIds, session.Undo.Peek()),
            ephemeral: true);
    }

    // ── 訂閱組的內部工具 ──────────────────────────────

    /// <summary>目前勾選的訂閱組（含內容）。查不到的（被刪掉）直接忽略。</summary>
    private List<(SavedGroup Group, SavedGroupPayload Payload)> SelectedGroups(BusSession session)
    {
        var result = new List<(SavedGroup, SavedGroupPayload)>();

        foreach (var id in session.SelectedSavedGroupIds)
        {
            var saved = _savedGroups.Get(id, Context.User.Id);
            var payload = saved is null ? null : _savedGroups.GetPayload(id, Context.User.Id);
            if (saved is not null && payload is not null) result.Add((saved, payload));
        }

        return result;
    }

    private SavedGroupPayload? PayloadOf(long id) => _savedGroups.GetPayload(id, Context.User.Id);

    private static void DropStaleSelection(BusSession session, IReadOnlyList<SavedGroup> groups)
    {
        var known = groups.Select(g => g.Id).ToHashSet();
        session.SelectedSavedGroupIds = session.SelectedSavedGroupIds.Where(known.Contains).ToList();
    }

    /// <summary>
    /// 把訂閱組的每一段行程，用**目前**的站序資料重新匹配成可訂閱的計畫。
    ///
    /// ★ 這是「不直接複製舊訂閱」的關鍵：路線改道或站牌遷移後，
    ///   舊的 BoardStopUid / BoardSequence 會指向錯誤的月台，而且不會報錯。
    /// </summary>
    private List<LegPlan> BuildLegPlans(
        SavedGroup saved, SavedGroupPayload payload, List<string> warnings)
    {
        var plans = new List<LegPlan>();

        foreach (var (leg, index) in payload.Legs.Select((l, i) => (l, i)))
        {
            var origin = new LocationTarget
            {
                DisplayName = leg.Origin.DisplayName,
                CandidateStopUids = leg.Origin.StopUids.Distinct(StringComparer.Ordinal).ToList()
            };
            var destination = new LocationTarget
            {
                DisplayName = leg.Destination.DisplayName,
                CandidateStopUids = leg.Destination.StopUids.Distinct(StringComparer.Ordinal).ToList()
            };

            if (origin.CandidateStopUids.Count == 0 || destination.CandidateStopUids.Count == 0)
            {
                warnings.Add($"「{saved.Name}」第 {index + 1} 段沒有記錄站牌");
                continue;
            }

            var wanted = leg.Routes.Select(r => (r.RouteUid, r.Direction)).ToHashSet();
            var chosen = MyData.FindRoutes(origin, destination)
                               .Where(r => wanted.Contains((r.RouteUid, r.Direction)))
                               .ToList();

            if (chosen.Count == 0)
            {
                warnings.Add($"「{saved.Name}」第 {index + 1} 段（{leg.Origin.DisplayName} → " +
                             $"{leg.Destination.DisplayName}）的路線目前都找不到了");
                continue;
            }

            var missing = wanted.Except(chosen.Select(r => (r.RouteUid, r.Direction))).Count();
            if (missing > 0)
                warnings.Add($"「{saved.Name}」第 {index + 1} 段有 {missing} 條路線已停駛或改道，已略過");

            plans.Add(new LegPlan(origin, destination, chosen));
        }

        return plans;
    }

    /// <summary>把目前的訂閱群組轉成可儲存的內容（共用 SavedGroupPayloadFactory）。</summary>
    private SavedGroupPayload BuildPayloadFrom(SubscriptionGroup group)
        => SavedGroupPayloadFactory.FromSubscriptions(
            new SavedTarget(group.Origin.DisplayName, group.Origin.CandidateStopUids.ToList()),
            new SavedTarget(group.Destination.DisplayName, group.Destination.CandidateStopUids.ToList()),
            _subs.GetSubscriptions(group)
                 .Select(s => (s.RouteUid, s.Direction, s.RouteName)),
            group.NotifyBeforeMinutes);

    private static string DescribeSaveFailure(SaveGroupResult result, string? message) => result switch
    {
        SaveGroupResult.NameEmpty => "請輸入名稱。",
        SaveGroupResult.NameTooLong => message ?? "名稱太長。",
        SaveGroupResult.TooManyGroups => message ?? "訂閱組數量已達上限，請先刪除一些。",
        SaveGroupResult.RouteCountMismatch => message ?? "這個訂閱的路線數量不合法。",
        SaveGroupResult.TooManyLegs => message ?? "行程段數太多。",
        _ => message ?? "儲存失敗。"
    };

    // ─────────────────────────────────────────────────────
    //  通知時間
    // ─────────────────────────────────────────────────────

    [ComponentInteraction("bus:notify:*")]
    public async Task NotifyAsync(int minutes)
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var group = session.CreatedGroupId is { } id ? _subs.GetGroup(id) : null;
        if (group is null)
        {
            await RespondAsync("這個訂閱已經不在了。請重新執行 `/bus panel`。", ephemeral: true);
            return;
        }

        group.NotifyBeforeMinutes = minutes;
        session.NotifyMinutes = minutes;

        // 完成訂閱時直接把「所有訂閱的到站時間」一起顯示出來
        await DeferAsync();

        var now = DateTimeOffset.UtcNow;
        var result = await _runtime.BuildEtaTableAsync(group, now);

        await SetMessageAsync(
            embeds:
            [
                BusUi.FinalCard(group, _subs.GetSubscriptions(group)),
                BusUi.EtaTable(group, result.Rows, now, result.Simulation, result.Error)
            ],
            components: BusUi.EtaComponents());
    }

    // ─────────────────────────────────────────────────────
    //  通知預覽（沒有 TDX 金鑰也能驗收）
    // ─────────────────────────────────────────────────────

    [ComponentInteraction(Cid.Simulate)]
    public async Task SimulateAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var group = session.CreatedGroupId is { } id
            ? _subs.GetGroup(id)
            : _subs.GetGroupsByUser(Context.User.Id).LastOrDefault();

        if (group is null)
        {
            await RespondAsync("你還沒有任何訂閱，無法模擬。請先用 `/bus panel` 完成訂閱。", ephemeral: true);
            return;
        }

        await UpdateAsync(
            embed: _runtime.PreviewWithSyntheticData(group, DateTimeOffset.UtcNow),
            components: new ComponentBuilder()
                .WithButton("發送到我的私訊", Cid.SendDm, ButtonStyle.Primary)
                .WithButton("返回面板", "bus:back", ButtonStyle.Secondary)
                .Build());
    }

    [ComponentInteraction("bus:back")]
    public async Task BackAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        await UpdateAsync(
            embed: BusUi.Panel(session, _subs.GetGroupsByUser(Context.User.Id).Count()),
            components: BusUi.PanelComponents(session));
    }

    [ComponentInteraction(Cid.SendDm)]
    public async Task SendDmAsync()
    {
        if (!await RequireSessionAsync()) return;
        var session = Session!;

        var group = session.CreatedGroupId is { } id
            ? _subs.GetGroup(id)
            : _subs.GetGroupsByUser(Context.User.Id).LastOrDefault();

        if (group is null)
        {
            await RespondAsync("找不到可模擬的訂閱。", ephemeral: true);
            return;
        }

        var embed = _runtime.PreviewWithSyntheticData(group, DateTimeOffset.UtcNow);

        try
        {
            await Context.User.SendMessageAsync(embed: embed);
            await RespondAsync("✅ 已把模擬通知送到你的私訊 —— 這就是公車快到時你會收到的樣子。",
                               ephemeral: true);
        }
        catch (Exception ex)
        {
            await RespondAsync(
                "❌ 送不出去。常見原因：你的 Discord 隱私設定不接受伺服器成員的私訊。\n" +
                "（伺服器設定 → 隱私設定 → 允許來自伺服器成員的私訊）\n" +
                $"錯誤：{ex.GetType().Name}",
                ephemeral: true);
        }
    }

    // ─────────────────────────────────────────────────────
    //  訂閱清單 / 取消
    // ─────────────────────────────────────────────────────

    [ComponentInteraction(Cid.ToList)]
    public async Task ToListAsync()
    {
        var groups = _subs.GetGroupsByUser(Context.User.Id).ToList();
        await UpdateAsync(
            embed: BusUi.SubscriptionList(groups, g => _subs.GetSubscriptions(_subs.GetGroup(g)!)),
            components: BusUi.SubscriptionListComponents(groups));
    }

    [ComponentInteraction("bus:rmmenu")]
    public async Task RemoveSelectedAsync()
    {
        var value = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "";
        var groupId = value.StartsWith(Cid.RemoveGroupPrefix, StringComparison.Ordinal)
            ? value[Cid.RemoveGroupPrefix.Length..]
            : value;

        var group = _subs.GetGroup(groupId);

        // 安全性：只能刪自己的訂閱
        if (group is null || group.UserId != Context.User.Id)
        {
            await RespondAsync("找不到這個訂閱，或它不是你的。", ephemeral: true);
            return;
        }

        _subs.RemoveGroup(groupId);

        var session = _sessions.Get(Context.User.Id, Context.Channel.Id);
        if (session?.CreatedGroupId == groupId) session.CreatedGroupId = null;

        var groups = _subs.GetGroupsByUser(Context.User.Id).ToList();
        await UpdateAsync(
            embed: BusUi.SubscriptionList(groups, g => _subs.GetSubscriptions(_subs.GetGroup(g)!)),
            components: BusUi.SubscriptionListComponents(groups));
    }

    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 把選項的值解析成一串 StopUID。
    /// 實際邏輯放在 <see cref="BusUi.ResolveStopValues"/>，
    /// 這樣「值怎麼編碼」與「怎麼解碼」在同一處，也讓離線驗證能一起測。
    /// </summary>
    private List<string> ResolveStopUids(IEnumerable<string> values)
        => BusUi.ResolveStopValues(values, MyData);

    /// <summary>
    /// 使用者沒有手動勾選時，預設用「完全相符／前綴相符」的群組。
    /// 由 BusUi 統一推導，確保與實際存在的選項一致。
    /// </summary>
    private List<string> DefaultPicks(IReadOnlyList<StopSearchGroupResult> groups)
        => BusUi.DefaultStopPicks(groups, MyData);

    private static (string RouteUid, int Direction)? ParseRouteValue(string value)
    {
        if (!value.StartsWith("r:", StringComparison.Ordinal)) return null;

        var parts = value[2..].Split('|');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[1], out var dir)) return null;

        return (parts[0], dir);
    }
}
