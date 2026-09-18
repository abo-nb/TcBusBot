using Discord;
using Discord.Interactions;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Storage;
using TcBusBot.Core.Subscriptions;
using TcBusBot.Core.Tdx;

namespace TcBusBot.Discord.Modules;

/// <summary>輸入站名關鍵字的 Modal（起點／目的地各一個，因為標題要不同）。</summary>
public sealed class OriginKeywordModal : IModal
{
    public string Title => "設定起點";

    [InputLabel("站名關鍵字")]
    [ModalTextInput("keyword", TextInputStyle.Short,
        "例如：火車站（打「台」也會找到「臺」；也可以只打「靜宜」）", maxLength: 50)]
    [RequiredInput(true)]
    public string Keyword { get; set; } = "";
}

public sealed class DestKeywordModal : IModal
{
    public string Title => "設定目的地";

    [InputLabel("站名關鍵字")]
    [ModalTextInput("keyword", TextInputStyle.Short,
        "例如：靜宜大學（可以只打一部分）", maxLength: 50)]
    [RequiredInput(true)]
    public string Keyword { get; set; } = "";
}

/// <summary>把目前的訂閱存成具名訂閱組。</summary>
public sealed class SaveGroupModal : IModal
{
    public string Title => "存成訂閱組";

    [InputLabel("訂閱組名稱")]
    [ModalTextInput("name", TextInputStyle.Short, "例如：上班通勤、回老家", maxLength: 40)]
    [RequiredInput(true)]
    public string Name { get; set; } = "";
}

/// <summary>訂閱組改名。</summary>
public sealed class RenameGroupModal : IModal
{
    public string Title => "訂閱組改名";

    [InputLabel("新名稱")]
    [ModalTextInput("name", TextInputStyle.Short, "輸入新的名稱", maxLength: 40)]
    [RequiredInput(true)]
    public string Name { get; set; } = "";
}

/// <summary>
/// 合併多個訂閱組。
///
/// 兩個欄位：名稱（同名會覆蓋）與提前通知時間。
/// 時間留白就用原本設定的最早那個 —— 讓「不想調」的人直接按送出。
/// </summary>
public sealed class MergeGroupsModal : IModal
{
    public string Title => "合併成新訂閱組";

    public MergeGroupsModal() { }

    public MergeGroupsModal(int defaultMinutes)
    {
        Minutes = defaultMinutes.ToString();
    }

    [InputLabel("新訂閱組名稱")]
    [ModalTextInput("name", TextInputStyle.Short, "例如：通勤全部、上班＋回家", maxLength: 40)]
    [RequiredInput(true)]
    public string Name { get; set; } = "";

    [InputLabel("提前幾分鐘通知（可留白）")]
    [ModalTextInput("minutes", TextInputStyle.Short, "例如：10（留白 = 用原本設定中最早的）", maxLength: 3)]
    [RequiredInput(false)]
    public string? Minutes { get; set; }
}

/// <summary>斜線指令。入口是 <c>/bus panel</c>。///
/// 為什麼用「群組 + 子指令」而不是單一 <c>/bus</c>：
///   同名的指令與群組在 Discord 是不允許的，而我們需要 /bus list、/bus status 等子指令。
/// </summary>
[Group("bus", "公車訂閱（到站通知）")]
public sealed class BusModule : BusModuleBase
{
    private readonly BusDataService _data;
    private readonly SubscriptionService _subs;
    private readonly BusSessionStore _sessions;
    private readonly BotRuntime _runtime;
    private readonly SavedGroupStore _savedGroups;
    private readonly BusDataCatalog _catalog;
    private readonly UserCityStore _cities;

    public BusModule(
        BusDataService data,
        SubscriptionService subs,
        BusSessionStore sessions,
        BotRuntime runtime,
        SavedGroupStore savedGroups,
        BusDataCatalog catalog,
        UserCityStore cities)
    {
        _data = data;
        _subs = subs;
        _sessions = sessions;
        _runtime = runtime;
        _savedGroups = savedGroups;
        _catalog = catalog;
        _cities = cities;
    }

    /// <summary>這個人目前選的城市（訂公車的人自己決定要看哪個城市）。</summary>
    private string MyCity => _cities.Get(Context.User.Id);

    /// <summary>這個人要用哪一份資料。</summary>
    private BusDataService MyData => _catalog.For(Context.User.Id);

    /// <summary>
    /// `/bus city`：**選要看哪個城市的公車**。
    ///
    /// 為什麼是「按使用者」而不是伺服器或主機設定：
    /// 同一個伺服器裡可能有人通勤看臺中、有人回老家看臺南。
    /// 主機設 `BUS_CITY` 只能整個行程一套（兩種站牌混在一起，分不出哪個是哪個），
    /// 所以由**要訂公車的那個人**自己選，預設是主機載入的第一個城市（通常是臺中）。
    ///
    /// 只影響「搜尋與面板看到哪個城市的站牌」——
    /// **已經訂好的訂閱不會失效**（即時到站是依站牌自己的城市去查的）。
    /// </summary>
    [SlashCommand("city", "選擇你要查哪個城市的公車（臺中／臺南…；只影響你自己的搜尋與面板）")]
    public async Task CityAsync(
        [Summary("城市", "要查的城市（留空＝顯示目前設定與可選清單）")] string? city = null)
    {
        var available = _cities.Available;

        if (string.IsNullOrWhiteSpace(city))
        {
            await RespondAsync(embed: new EmbedBuilder()
                .WithColor(new Color(0x2B, 0x6C, 0xB0))
                .WithTitle("🌏 你要查哪個城市的公車？")
                .WithDescription(
                    $"目前：**{BusCity.DisplayOf(MyCity)}**" +
                    (_cities.HasOwnChoice(Context.User.Id) ? "（你自己選的）" : "（預設）") + "\n\n" +
                    (available.Count > 1
                        ? "可以選：\n" + string.Join("\n", available.Select(c =>
                            $"• `{BusCity.DisplayOf(c)}`（`/bus city 城市:{c}`）"))
                        : "⚠️ 主機目前只載入了這一個城市（`BUS_CITY`）。\n" +
                          "要能選多個城市，請在主機端設定 `BUS_CITY=Taichung,Tainan`。"))
                .WithFooter("只影響你自己看到的站牌搜尋與面板；已訂閱的路線不受影響")
                .Build(),
                ephemeral: true);

            return;
        }

        if (!_cities.Set(Context.User.Id, city))
        {
            await RespondAsync(
                $"❌ 沒有「{city}」這個城市可選。\n" +
                $"目前可以選：{string.Join("、", available.Select(BusCity.DisplayOf))}" +
                (available.Count == 1 ? "（主機只載入了這一個；要更多請設 `BUS_CITY`）" : ""),
                ephemeral: true);
            return;
        }

        var chosen = MyCity;
        var data = MyData;

        await RespondAsync(embed: new EmbedBuilder()
            .WithColor(new Color(0x2E, 0x8B, 0x57))
            .WithTitle($"✅ 改成查「{BusCity.DisplayOf(chosen)}」的公車")
            .WithDescription(
                $"資料集：{data.StopCount} 個站牌、{data.TripCount} 筆路線站序\n\n" +
                "接下來 `/bus panel` 的站牌搜尋、以及 @ 我問路線，都只會看這個城市。\n" +
                "（已經訂好的訂閱不會變 —— 它們各自記著自己的站牌。）")
            .WithFooter("要換回來就再打一次 /bus city")
            .Build(),
            ephemeral: true);
    }

    [SlashCommand("panel", "開啟訂閱面板：設定起點與目的地，找出可以搭的路線")]
    public async Task PanelAsync()
    {
        var session = _sessions.GetOrCreate(Context.User.Id, Context.Channel.Id);
        await RespondAsync(
            embed: BusUi.Panel(session, _subs.GetGroupsByUser(Context.User.Id).Count()),
            components: BusUi.PanelComponents(session),
            ephemeral: true);
    }

    [SlashCommand("groups", "我的訂閱組：一次套用多個、合併、改名、刪除（存在 SQLite，重啟後還在）")]
    public async Task GroupsAsync()
    {
        var session = _sessions.GetOrCreate(Context.User.Id, Context.Channel.Id);
        var groups = _savedGroups.ListByUser(Context.User.Id);

        await RespondAsync(
            embed: BusUi.SavedGroupsList(groups, session.SelectedSavedGroupIds,
                                         SavedGroupStore.MaxGroupsPerUser, PayloadOf),
            components: BusUi.SavedGroupsComponents(groups, session.SelectedSavedGroupIds, session.Undo.Peek()),
            ephemeral: true);
    }

    private SavedGroupPayload? PayloadOf(long id) => _savedGroups.GetPayload(id, Context.User.Id);

    [SlashCommand("list", "查看我的訂閱")]
    public async Task ListAsync()
    {
        var groups = _subs.GetGroupsByUser(Context.User.Id).ToList();
        await RespondAsync(
            embed: BusUi.SubscriptionList(groups, g => _subs.GetSubscriptions(_subs.GetGroup(g)!)),
            components: BusUi.SubscriptionListComponents(groups),
            ephemeral: true);
    }

    [SlashCommand("end", "結束追蹤：一次取消我的全部訂閱（按「復原」可以放回來）")]
    public async Task EndAsync()
    {
        var session = _sessions.GetOrCreate(Context.User.Id, Context.Channel.Id);

        // 先把內容抄下來再刪 —— 「結束追蹤」是一次影響很多東西的操作，必須可以復原
        var removed = _subs.RemoveAllForUser(Context.User.Id);

        var groupCount = removed.Count;
        var subscriptions = removed.SelectMany(r => r.Subscriptions).ToList();
        var subCount = subscriptions.Count;
        var boardStops = subscriptions.Select(s => s.BoardStopUid).Distinct(StringComparer.Ordinal).Count();
        var routes = subscriptions.Select(s => (s.RouteUid, s.Direction)).Distinct().Count();

        // 面板還指著剛才被取消的群組 → 一定要清掉，
        // 否則之後按「查看到站時間」會操作到一個已經不存在的群組。
        var previousSessionGroupId = session.CreatedGroupId;
        session.CreatedGroupId = null;
        session.Origin = null;
        session.Destination = null;
        session.LastRoutes.Clear();
        session.ResetPicks();

        if (groupCount == 0)
        {
            await RespondAsync(
                "你目前沒有任何訂閱，沒有東西需要停止。\n用 `/bus panel` 開始設定起點與目的地。",
                ephemeral: true);
            return;
        }

        session.Undo.Push(new UndoEndedTracking(
            Description: $"結束追蹤（{groupCount} 組訂閱）",
            Removed: removed,
            PreviousSessionGroupId: previousSessionGroupId));

        Console.WriteLine($"[end] 使用者 {Context.User.Id} 結束追蹤：" +
                          $"{groupCount} 個訂閱群組、{subCount} 筆訂閱、{boardStops} 個上車站");

        await RespondAsync(
            embed: BusUi.TrackingEnded(groupCount, subCount, boardStops, routes),
            components: BusUi.EndComponents(groupCount),
            ephemeral: true);
    }

    [SlashCommand("status", "顯示 Bot 目前的狀態（資料集、訂閱數、資料來源）")]
    public async Task StatusAsync()
    {
        var uptime = DateTimeOffset.UtcNow - _runtime.StartedAt;

        var embed = new EmbedBuilder()
            .WithTitle("🤖 TcBusBot 狀態")
            .WithColor(new Color(0x2B, 0x6C, 0xB0))
            .AddField("你要查的城市",
                $"{BusCity.DisplayOf(MyCity)}" +
                (_cities.HasOwnChoice(Context.User.Id) ? "（你自己選的）" : "（預設）") +
                (_cities.HasChoice ? $"　可換：{BusCity.DisplayOfMany(_cities.Available)}（`/bus city`）" : ""),
                inline: false)
            .AddField("靜態資料（這個城市）", $"{MyData.StopCount} 個站牌、{MyData.TripCount} 筆路線站序", inline: false)
            .AddField("資料來源", _runtime.DataSourceDescription, inline: false)
            .AddField("訂閱", $"{_subs.GroupCount} 組、{_subs.SubscriptionCount} 個訂閱", inline: true)
            .AddField("面板 session", $"{_sessions.Count} 個", inline: true)
            .AddField("已運行", $"{(int)uptime.TotalHours} 小時 {uptime.Minutes} 分", inline: true)
            .AddField("即時輪詢", _runtime.PollerDescription, inline: false)
            .AddField("訂閱組", $"{_savedGroups.CountByUser(Context.User.Id)}/{SavedGroupStore.MaxGroupsPerUser} 組　" +
                              $"{(_savedGroups.IsPersistent ? "已存檔" : "記憶體模式")}", inline: false)
            .WithFooter($"Discord.Net / .NET {Environment.Version}")
            .Build();

        await RespondAsync(embed: embed, ephemeral: true);
    }

    [SlashCommand("next", "查看所有訂閱目前的到站時間（依剩餘時間排序）")]
    public async Task NextAsync()
    {
        var groups = _subs.GetGroupsByUser(Context.User.Id).ToList();
        if (groups.Count == 0)
        {
            await RespondAsync("你還沒有任何訂閱。先用 `/bus panel` 設定起點與目的地。", ephemeral: true);
            return;
        }

        if (!_runtime.CanQueryRealtime)
        {
            await RespondAsync(
                "這個功能需要 TDX API 金鑰（免費的基礎會員即可）。\n" +
                "目前沒有金鑰，所以查不到真實的即時資料。\n\n" +
                "你可以先用 **模擬通知** 看看通知長什麼樣子 —— 在 `/bus panel` 完成訂閱後，" +
                "按「模擬一則通知」即可。\n\n" +
                "設定方式：啟動時加上 `--tdx-id <ClientId> --tdx-secret <ClientSecret>`，" +
                "或設定環境變數 `TDX_CLIENT_ID` / `TDX_CLIENT_SECRET`。",
                ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var group = groups.Last();
        var now = DateTimeOffset.UtcNow;
        var result = await _runtime.BuildEtaTableAsync(group, now);

        await FollowupAsync(
            embed: BusUi.EtaTable(group, result.Rows, now, result.Simulation, result.Error),
            ephemeral: true);
    }
}
