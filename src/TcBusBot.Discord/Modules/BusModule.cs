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
        "例如：台中車站（打「台」也會找到「臺」；也可以只打「靜宜」）", maxLength: 50)]
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
[Group("bus", "台中公車訂閱")]
public sealed class BusModule : BusModuleBase
{
    private readonly BusDataService _data;
    private readonly SubscriptionService _subs;
    private readonly BusSessionStore _sessions;
    private readonly BotRuntime _runtime;
    private readonly SavedGroupStore _savedGroups;

    public BusModule(
        BusDataService data,
        SubscriptionService subs,
        BusSessionStore sessions,
        BotRuntime runtime,
        SavedGroupStore savedGroups)
    {
        _data = data;
        _subs = subs;
        _sessions = sessions;
        _runtime = runtime;
        _savedGroups = savedGroups;
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
            .AddField("靜態資料", $"{_data.StopCount} 個站牌、{_data.TripCount} 筆路線站序", inline: false)
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
