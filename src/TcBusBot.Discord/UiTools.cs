using System.ComponentModel;
using Microsoft.SemanticKernel;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Chat;
using TcBusBot.Core.Subscriptions;

namespace TcBusBot.Discord;

/// <summary>模型剛剛要求「畫面上要出現什麼元件」（讓回覆附上真正的按鈕）。</summary>
public sealed record UiRequest(string Kind, string Note)
{
    public const string Panel = "panel";     // 面板（設定起點／目的地…）
    public const string Routes = "routes";   // 路線清單（勾選要訂閱的）
    public const string Etas = "etas";       // 到站時間（重新整理／模擬／存成訂閱組）
}

/// <summary>
/// **讓模型「幫使用者按按鈕」**（`LLM_UI_ACTIONS`）。
///
/// 使用者要的是「不用自己點那一串」：直接說「幫我把起點設成火車站」、
/// 「找一下路線」、「訂 300 跟 304」、「復原剛剛那個」。
///
/// 這些動作**完全對應面板上的按鈕**，而且是動同一份 session
/// （<see cref="BusSession"/>）—— 所以模型做完之後，使用者看到的面板狀態是真的，
/// 想接手用手指點也可以（回覆會附上真正的元件）。
///
/// ⚠️ 為什麼不讓模型「假裝按了按鈕」就好：那樣面板狀態不會變，
///    使用者之後按「搜尋路線」會發現什麼都沒有。所以這裡是真的改 session。
/// </summary>
public sealed class UiTools
{
    private readonly BusActionService _actions;
    private readonly SubscriptionService _subs;
    private readonly BusSessionStore _sessions;
    private readonly Core.Storage.SavedGroupStore _savedGroups;
    private readonly ChatToolContext _context;
    private readonly ToolCallLog _log;

    /// <summary>問的人是誰 —— 他可以用 `/bus city` 選城市，工具要跟著看同一份資料。</summary>
    private ulong _userId => _context.UserId;

    /// <summary>模型要求附上的元件（由 LlmChatService 負責真的畫出來）。</summary>
    public UiRequest? PendingUi { get; private set; }

    /// <summary>剛剛按了「復原」時放這裡，讓回覆可以再給一次復原按鈕。</summary>
    public bool UndoApplied { get; private set; }

    public UiTools(
        BusActionService actions,
        SubscriptionService subs,
        BusSessionStore sessions,
        Core.Storage.SavedGroupStore savedGroups,
        ChatToolContext context,
        ToolCallLog log)
    {
        _actions = actions;
        _subs = subs;
        _sessions = sessions;
        _savedGroups = savedGroups;
        _context = context;
        _log = log;
    }

    /// <summary>這個使用者在這個頻道的面板 session（沒開過就建一個）。</summary>
    private BusSession Session => _sessions.GetOrCreate(_context.UserId, _context.ChannelId);

    // ─────────────────────────────────────────────────────
    //  開面板 / 設定起訖
    // ─────────────────────────────────────────────────────

    [KernelFunction("open_panel")]
    [Description("幫使用者打開訂閱面板（等於他按了 /bus panel）。" +
                 "使用者說「開面板」「我想設定訂閱」「幫我弄一下」時用這個 —— " +
                 "回覆會附上真正的按鈕，他可以接著自己點。")]
    public string OpenPanel()
    {
        var session = Session;
        PendingUi = new UiRequest(UiRequest.Panel, "面板");

        var groups = _subs.GetGroupsByUser(_context.UserId).Count();
        _log.Record("open_panel", $"目前 {groups} 組訂閱");

        var state = (session.Origin is null ? "起點：還沒設定" : $"起點：{session.Origin.DisplayName}") +
                    "；" +
                    (session.Destination is null ? "目的地：還沒設定" : $"目的地：{session.Destination.DisplayName}");

        return $"✅ 面板已打開（{state}）。使用者可以直接用下面的按鈕繼續，" +
               "也可以叫我幫他設定（例如「起點設成火車站」）。";
    }

    [KernelFunction("set_origin")]
    [Description("幫使用者設定面板的「起點」（等於他按了設定起點 → 搜尋 → 確認）。" +
                 "站名關鍵字支援模糊比對；不確定站名時先用 search_stops 查。")]
    public string SetOrigin(
        [Description("起點站名關鍵字，例如「火車站」「靜宜」「科大」")] string keyword)
        => SetStop(keyword, isOrigin: true);

    [KernelFunction("set_destination")]
    [Description("幫使用者設定面板的「目的地」（等於他按了設定目的地 → 搜尋 → 確認）。")]
    public string SetDestination(
        [Description("目的地站名關鍵字，例如 靜宜大學、大坑口")] string keyword)
        => SetStop(keyword, isOrigin: false);

    private string SetStop(string keyword, bool isOrigin)
    {
        var target = _actions.ResolveKeyword(keyword, out var message, _userId);
        var label = isOrigin ? "起點" : "目的地";

        if (target is null)
        {
            _log.Record(isOrigin ? "set_origin" : "set_destination", $"失敗：{keyword}");
            return $"{label}設定失敗：{message}";
        }

        var session = Session;
        if (isOrigin) session.Origin = target;
        else session.Destination = target;

        session.ResetPicks();

        _log.Record(isOrigin ? "set_origin" : "set_destination", message, changedState: true);

        var other = isOrigin ? session.Destination : session.Origin;
        var next = session.Ready
            ? "起訖都好了 → 可以接著用 search_panel_routes 找路線。"
            : $"還缺{(isOrigin ? "目的地" : "起點")}。";

        return $"✅ {label}已設為 {message}。" +
               (other is null ? "" : $"（{(isOrigin ? "目的地" : "起點")}：{other.DisplayName}）") + next;
    }

    // ─────────────────────────────────────────────────────
    //  找路線 / 訂閱 / 復原
    // ─────────────────────────────────────────────────────

    [KernelFunction("search_panel_routes")]
    [Description("用面板目前的起訖找可以搭的路線（等於他按了「搜尋路線」）。" +
                 "回覆會附上路線清單的勾選元件，使用者也可以自己勾。" +
                 "⚠️ 起訖都設定好之後才能用。")]
    public string SearchPanelRoutes()
    {
        var session = Session;

        if (!session.Ready)
        {
            _log.Record("search_panel_routes", "起訖未設定");
            return "起點或目的地還沒設定好，請先問使用者（或用 set_origin / set_destination 設定）。";
        }

        var routes = _data_FindRoutes(session);
        session.LastRoutes = routes.ToList();
        session.PickedRouteValues = [];

        PendingUi = new UiRequest(UiRequest.Routes, "路線清單");

        if (routes.Count == 0)
        {
            _log.Record("search_panel_routes", "0 條");
            return $"{session.Origin!.DisplayName} → {session.Destination!.DisplayName} 找不到直達路線" +
                   "（可以試試 find_routes 看轉乘建議）。";
        }

        _log.Record("search_panel_routes", $"{routes.Count} 條");

        var list = routes.Take(12).Select(r =>
            $"• {r.RouteName}（{(r.Direction == 0 ? "去程" : "返程")}）" +
            $"上車：{r.BoardChoices.FirstOrDefault()?.BoardStopName ?? "—"}");

        return $"{session.Origin.DisplayName} → {session.Destination.DisplayName} 共 {routes.Count} 條路線" +
               "（下面附上清單，使用者也可以自己勾選）：\n" + string.Join("\n", list);
    }

    private IReadOnlyList<RouteOption> _data_FindRoutes(BusSession session)
        => _actions.FindRoutesFor(session.Origin!, session.Destination!, _userId);

    [KernelFunction("subscribe_panel_routes")]
    [Description("用面板目前的起訖訂閱公車（等於他勾了路線之後按「訂閱這 N 條路線」）。" +
                 "可以指定路線號碼（例如 \"300,304\"）只訂那幾條；留白＝全部直達路線。")]
    public string SubscribePanelRoutes(
        [Description("要訂的路線號碼，逗號分隔（留白＝全部），例如 300,304")] string? routes = null,
        [Description("提前幾分鐘通知，1~60，預設 10")] int notifyMinutes = 10)
    {
        var session = Session;

        if (!session.Ready)
        {
            _log.Record("subscribe_panel_routes", "起訖未設定");
            return "起點或目的地還沒設定好，所以還沒有建立訂閱。";
        }

        var all = session.LastRoutes.Count > 0 ? session.LastRoutes : _data_FindRoutes(session).ToList();

        var wanted = (routes ?? "")
            .Split([',', '，', '、', ' ', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        IReadOnlyList<RouteOption>? only = null;

        if (wanted.Count > 0)
        {
            only = all.Where(r => wanted.Any(w =>
                        r.RouteName.Equals(w, StringComparison.OrdinalIgnoreCase) ||
                        r.RouteName.Contains(w, StringComparison.OrdinalIgnoreCase)))
                      .ToList();

            if (only.Count == 0)
            {
                _log.Record("subscribe_panel_routes", $"找不到路線 {routes}");
                return $"面板目前的路線裡找不到「{routes}」。" +
                       $"有的是：{string.Join("、", all.Select(r => r.RouteName).Distinct().Take(12))}";
            }
        }

        var outcome = _actions.SubscribeTargets(
            userId: _context.UserId,
            origin: session.Origin!,
            destination: session.Destination!,
            onlyRoutes: only,
            notifyMinutes: notifyMinutes,
            guildId: _context.IsDirectMessage ? null : _context.GuildId,
            channelId: _context.ChannelId);

        if (outcome.Ok && outcome.Group is not null)
        {
            // 讓面板指向剛剛訂的那一組（跟按按鈕的行為一致）
            session.CreatedGroupId = outcome.Group.Id;
            session.NotifyMinutes = notifyMinutes;
            PendingUi = new UiRequest(UiRequest.Etas, "到站時間");

            // ★ 也要推進復原堆疊 —— 按鈕那條路會做這件事，
            //   模型幫你訂的時候當然也要能「↩️ 復原」。
            //   （沒有這一步的話，使用者說「訂錯了幫我復原」時模型會**兩手一攤**，
            //     甚至更糟：它會以為自己復原了。）
            session.Undo.Push(new UndoAppliedSubscriptions(
                $"AI 幫你訂閱 {only?.Count ?? all.Count} 條路線",
                [outcome.Group.Id],
                PreviousSessionGroupId: null));
        }

        _log.Record("subscribe_panel_routes",
            outcome.Ok ? $"{only?.Count ?? all.Count} 條 → {outcome.Group?.SubscriptionIds.Count} 筆訂閱" : "失敗",
            changedState: outcome.Ok);

        return outcome.Message;
    }

    [KernelFunction("undo_last_action")]
    [Description("幫使用者按「↩️ 復原」（還原上一個動作：套用訂閱／合併／刪除／改名／結束追蹤）。" +
                 "使用者說「復原」「弄錯了」「退回上一步」時用這個。")]
    public string UndoLastAction()
    {
        var session = Session;
        var entry = session.Undo.Peek();

        if (entry is null)
        {
            _log.Record("undo_last_action", "沒有可復原的動作");
            return "目前沒有可以復原的動作。";
        }

        var message = session.Undo.Undo(_subs, _savedGroups, _context.UserId);
        UndoApplied = true;

        // 復原「批次套用」時，面板要跟著回到上一組
        if (entry is UndoAppliedSubscriptions applied)
        {
            session.CreatedGroupId = applied.PreviousSessionGroupId is { } pid && _subs.GetGroup(pid) is not null
                ? pid
                : null;
        }

        _log.Record("undo_last_action", entry.Description, changedState: true);

        return $"↩️ 已復原「{entry.Description}」：{message}";
    }
}
