using System.Collections.Concurrent;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Subscriptions;

namespace TcBusBot.Discord;

/// <summary>
/// 使用者在 Discord 上「進行到一半」的訂閱設定。
///
/// 刻意放在記憶體：既然訂閱本身重啟就會消失，面板狀態跟著消失才是一致的行為
/// （失效時回覆明確訊息，不讓使用者卡住）。
/// </summary>
public sealed class BusSession
{
    public required ulong UserId { get; init; }
    public required ulong ChannelId { get; init; }

    public LocationTarget? Origin { get; set; }
    public LocationTarget? Destination { get; set; }

    /// <summary>搜尋結果中被勾選的站牌／群組（`g:` 或 `s:` 開頭）。</summary>
    public List<string> PendingPick { get; set; } = new();

    /// <summary>最近一次搜尋的結果，供「全選」與「確認」重建候選集合。</summary>
    public List<StopSearchGroupResult> LastSearch { get; set; } = new();
    public string LastKeyword { get; set; } = "";
    public bool PendingIsOrigin { get; set; } = true;

    /// <summary>最近一次建立路線清單的結果（供建立訂閱時使用）。</summary>
    public List<RouteOption> LastRoutes { get; set; } = new();

    /// <summary>
    /// 路線清單中已被勾選的項目（值形如 `r:TXG300|1`）。
    /// 勾選只是「選取」，要按下「訂閱」才會真的建立訂閱。
    /// </summary>
    public List<string> PickedRouteValues { get; set; } = new();

    /// <summary>這次面板流程已經建立的訂閱群組（重新選路線時會先刪掉它）。</summary>
    public string? CreatedGroupId { get; set; }

    public int NotifyMinutes { get; set; } = 10;

    /// <summary>
    /// 訂閱組清單中目前被選取的組（**可以多選**，尚未套用）。
    /// 一次訂閱多個組就是靠這裡 accumulating。
    /// </summary>
    public List<long> SelectedSavedGroupIds { get; set; } = new();

    /// <summary>這個面板的復原堆疊（合併／批次套用／刪除都可以一鍵還原）。</summary>
    public UndoStack Undo { get; } = new();

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public bool Ready => Origin is not null && Destination is not null;

    /// <summary>只選了一組時的捷徑（改名需要剛好一組）。</summary>
    public long? SingleSelectedSavedGroupId
        => SelectedSavedGroupIds.Count == 1 ? SelectedSavedGroupIds[0] : null;

    public void ResetPicks()
    {
        PendingPick = new List<string>();
        LastSearch = new List<StopSearchGroupResult>();
        LastKeyword = "";
        PickedRouteValues = new List<string>();
    }
}

public sealed class BusSessionStore
{
    private readonly ConcurrentDictionary<(ulong UserId, ulong ChannelId), BusSession> _map = new();

    public int Count => _map.Count;

    public BusSession GetOrCreate(ulong userId, ulong channelId)
        => _map.GetOrAdd((userId, channelId), k => new BusSession
        {
            UserId = k.UserId,
            ChannelId = k.ChannelId
        });

    public BusSession? Get(ulong userId, ulong channelId)
        => _map.TryGetValue((userId, channelId), out var s) ? s : null;

    /// <summary>超過 30 分鐘沒動的 session 就清掉，避免長時間執行後累積。</summary>
    public void Prune(TimeSpan ttl)
    {
        var cutoff = DateTimeOffset.UtcNow - ttl;
        foreach (var kv in _map)
            if (kv.Value.CreatedAt < cutoff)
                _map.TryRemove(kv.Key, out _);
    }
}
