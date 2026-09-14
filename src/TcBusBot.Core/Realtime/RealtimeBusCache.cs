using TcBusBot.Core.Models;

namespace TcBusBot.Core.Realtime;

/// <summary>
/// 即時 ETA 快取。**全體使用者共用**：
/// 輪詢迴圈每週期打一次（或少數幾次）TDX，把結果放進來，再由訂閱系統判斷誰要通知。
/// 這確保「100 個使用者關注同一站」仍然只有 1 次 API 呼叫。
/// </summary>
public sealed class RealtimeBusCache
{
    private volatile Dictionary<(string RouteUid, int Direction, string StopUid), BusEta> _byKey = new();
    private volatile Dictionary<string, List<BusEta>> _byStop = new(StringComparer.Ordinal);

    public DateTimeOffset LastUpdated { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>超過這個秒數的資料視為過期，整批不發通知（避免用過期資料誤報）。</summary>
    public int StaleDataSeconds { get; set; } = 180;

    public bool IsEmpty { get; private set; } = true;

    public bool IsStale(DateTimeOffset now)
        => IsEmpty || now - LastUpdated > TimeSpan.FromSeconds(StaleDataSeconds * 3);

    public void ReplaceAll(IEnumerable<BusEta> fresh, DateTimeOffset now)
    {
        var byKey = new Dictionary<(string, int, string), BusEta>();
        var byStop = new Dictionary<string, List<BusEta>>(StringComparer.Ordinal);

        foreach (var e in fresh)
        {
            byKey[(e.RouteUID, e.Direction, e.StopUID)] = e;

            if (!byStop.TryGetValue(e.StopUID, out var list))
                byStop[e.StopUID] = list = new List<BusEta>();
            list.Add(e);
        }

        _byKey = byKey;
        _byStop = byStop;
        LastUpdated = now;
        IsEmpty = byKey.Count == 0;
    }

    public void Clear()
    {
        _byKey = new Dictionary<(string, int, string), BusEta>();
        _byStop = new Dictionary<string, List<BusEta>>(StringComparer.Ordinal);
        LastUpdated = DateTimeOffset.MinValue;
        IsEmpty = true;
    }

    public BusEta? Get(string routeUid, int direction, string stopUid)
        => _byKey.TryGetValue((routeUid, direction, stopUid), out var e) ? e : null;

    public IReadOnlyList<BusEta> GetAllForStop(string stopUid)
        => _byStop.TryGetValue(stopUid, out var list) ? list : Array.Empty<BusEta>();

    public int Count => _byKey.Count;
}
