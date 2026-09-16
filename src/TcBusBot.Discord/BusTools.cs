using System.ComponentModel;
using Microsoft.SemanticKernel;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Chat;
using TcBusBot.Core.Subscriptions;

namespace TcBusBot.Discord;

/// <summary>
/// 給 LLM 用的公車工具（Semantic Kernel 的 plugin）。
///
/// **這一層刻意很薄**：每個方法只做「把 LLM 給的字串參數轉呼叫
/// <see cref="BusActionService"/>」，然後把結果講成模型看得懂的文字。
/// 為什麼要這樣分：真正容易出錯的是「口語站名 → 候選站牌 → 站序匹配」那段決策，
/// 它必須能在沒有 LLM、沒有網路的情況下測試（`tcbus selftest` 第 21 節），
/// 而不是綁在一個只有模型會呼叫的方法裡。
///
/// ⚠️ 每個工具都只作用在 <see cref="ChatToolContext"/> 指出的那個人與那個頻道 ——
/// 模型看得到的參數只有「站名」與「分鐘數」，沒有任何「幫誰訂」的欄位，
/// 所以它不可能幫別人訂閱，也不可能讀到別的伺服器的訂閱。
///
/// 取消訂閱會回傳 <see cref="PendingUndo"/>，讓 Discord 那一層可以推出
/// 「↩️ 復原」按鈕 —— LLM 做的破壞性操作也必須可以一鍵還原。
/// </summary>
public sealed class BusTools
{
    private readonly BusActionService _actions;
    private readonly SubscriptionService _subs;
    private readonly ChatToolContext _context;
    private readonly ToolCallLog _log;
    private readonly Func<ChatToolContext, string?, CancellationToken, Task<string>>? _arrivals;

    /// <summary>模型剛剛取消了訂閱時放這裡，讓呼叫端可以掛「↩️ 復原」。</summary>
    public IReadOnlyList<(SubscriptionGroup Group, IReadOnlyList<Subscription> Subscriptions)>? PendingUndo { get; private set; }

    public BusTools(
        BusActionService actions,
        SubscriptionService subs,
        ChatToolContext context,
        ToolCallLog log,
        Func<ChatToolContext, string?, CancellationToken, Task<string>>? arrivals = null)
    {
        _actions = actions;
        _subs = subs;
        _context = context;
        _log = log;
        _arrivals = arrivals;
    }

    // ─────────────────────────────────────────────────────
    //  搜尋與查詢（唯讀）
    // ─────────────────────────────────────────────────────

    [KernelFunction("search_stops")]
    [Description("用關鍵字查台中公車站牌的正確名稱（支援模糊比對：打「台中」也會找到「臺中」、" +
                 "打「台中科大」也會找到「國立臺中科技大學」）。站名不確定時先用這個查，不要用猜的。")]
    public string SearchStops(
        [Description("站名關鍵字，例如 台中車站、靜宜、台中科大")] string keyword)
    {
        var result = _actions.SearchStops(keyword);
        _log.Record("search_stops", keyword);
        return result;
    }

    [KernelFunction("find_routes")]
    [Description("查「從某一站到某一站」可以搭哪些公車（只查詢，不會建立訂閱）。" +
                 "使用者只是想知道有什麼車可以搭時用這個。")]
    public string FindRoutes(
        [Description("起點站名，例如 台中車站")] string origin,
        [Description("終點站名，例如 靜宜大學")] string destination)
    {
        var result = _actions.FindRoutes(origin, destination);
        _log.Record("find_routes", $"{origin} → {destination}");
        return result;
    }

    [KernelFunction("list_subscriptions")]
    [Description("列出目前這位使用者已經訂閱的公車（含路線、上車站、提前通知幾分鐘）。" +
                 "使用者問「我訂了什麼」「有沒有訂成功」時用這個確認。")]
    public string ListSubscriptions()
    {
        var result = _actions.ListSubscriptions(_context.UserId);
        _log.Record("list_subscriptions", $"{_subs.GetGroupsByUser(_context.UserId).Count()} 組");
        return result;
    }

    [KernelFunction("next_arrivals")]
    [Description("查目前訂閱的公車「還要幾分鐘到站」（需要主機設定 TDX 金鑰；" +
                 "沒有金鑰或查不到時會回報原因，請老實告訴使用者）。")]
    public async Task<string> NextArrivals(
        [Description("使用者提到的路線名稱（可留白代表全部），例如 300")] string? route = null)
    {
        if (_arrivals is null)
        {
            _log.Record("next_arrivals", "未啟用");
            return "這台主機沒有設定 TDX 金鑰，所以查不到即時到站時間。請告訴使用者可以改用 /bus next 或稍後再試。";
        }

        var result = await _arrivals(_context, route, CancellationToken.None);
        _log.Record("next_arrivals", route ?? "全部");
        return result;
    }

    // ─────────────────────────────────────────────────────
    //  會改變資料的操作
    // ─────────────────────────────────────────────────────

    [KernelFunction("subscribe_bus")]
    [Description("幫使用者訂閱「從某一站到某一站」的公車，公車快到時會通知他。" +
                 "使用者明確說「幫我訂」「我要收到通知」時才呼叫。站名不確定請先用 search_stops 查或直接問使用者。")]
    public string SubscribeBus(
        [Description("起點站名，例如 台中車站")] string origin,
        [Description("終點站名，例如 靜宜大學")] string destination,
        [Description("提前幾分鐘通知，1~60，預設 10")] int notifyMinutes = 10)
    {
        var outcome = _actions.Subscribe(
            userId: _context.UserId,
            origin: origin,
            destination: destination,
            notifyMinutes: notifyMinutes,
            guildId: _context.IsDirectMessage ? null : _context.GuildId,
            channelId: _context.ChannelId);

        _log.Record("subscribe_bus",
            outcome.Ok
                ? $"{outcome.OriginName} → {outcome.DestinationName}，{outcome.Group?.SubscriptionIds.Count} 筆訂閱"
                : $"失敗：{origin} → {destination}",
            changedState: outcome.Ok);

        return outcome.Message;
    }

    [KernelFunction("cancel_all_subscriptions")]
    [Description("取消使用者的**全部**公車訂閱（他就不會再收到任何到站通知）。" +
                 "只有使用者明確說「全部取消」「不想搭了」「停止通知」時才呼叫，" +
                 "不確定的話先問他一次。")]
    public string CancelAllSubscriptions()
    {
        var outcome = _actions.CancelAll(_context.UserId);

        if (outcome.GroupCount > 0) PendingUndo = outcome.Removed;

        _log.Record("cancel_all_subscriptions",
            $"{outcome.GroupCount} 組、{outcome.SubscriptionCount} 筆",
            changedState: outcome.GroupCount > 0);

        return outcome.Message;
    }
}

/// <summary>
/// 把公車工具包成「每次提問現做」的 provider。
///
/// 這裡是唯一知道「工具有哪些」的地方 —— 加一個新工具只要在這裡加一行，
/// 系統提示裡的能力說明（<see cref="ChatOrchestrator.ToolInstructions"/>）也要跟著更新。
/// </summary>
public sealed class BusToolProvider : IChatToolProvider
{
    private readonly BusActionService _actions;
    private readonly SubscriptionService _subs;
    private readonly Func<ChatToolContext, string?, CancellationToken, Task<string>>? _arrivals;

    /// <summary>最近一次提問的工具實例（用來拿 <see cref="BusTools.PendingUndo"/>）。</summary>
    private BusTools? _last;

    public BusToolProvider(
        BusActionService actions,
        SubscriptionService subs,
        Func<ChatToolContext, string?, CancellationToken, Task<string>>? arrivals = null)
    {
        _actions = actions;
        _subs = subs;
        _arrivals = arrivals;
    }

    public IReadOnlyList<KernelPlugin> CreateFor(ChatToolContext context, ToolCallLog log)
    {
        var tools = new BusTools(_actions, _subs, context, log, _arrivals);
        _last = tools;

        return [KernelPluginFactory.CreateFromObject(tools, "bus")];
    }

    public string Describe()
        => "公車工具 5 個（查站牌／查路線／訂閱／列出訂閱／取消訂閱／到站時間）";

    /// <summary>這一輪如果模型取消了訂閱，把內容交出來讓呼叫端掛「↩️ 復原」。</summary>
    public IReadOnlyList<(SubscriptionGroup Group, IReadOnlyList<Subscription> Subscriptions)>? TakePendingUndo()
    {
        var pending = _last?.PendingUndo;
        if (_last is not null) _last = null;
        return pending;
    }
}
