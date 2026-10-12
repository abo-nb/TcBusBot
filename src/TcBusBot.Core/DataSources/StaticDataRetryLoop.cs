using TcBusBot.Core.Bus;

namespace TcBusBot.Core.DataSources;

/// <summary>
/// 「TDX 抓不到資料時，過一會自己再試」的背景迴圈。
///
/// ── 為什麼一定要有它 ────────────────────────────────────────
/// TDX 最常見的失敗是 **429（超過 API 呼叫速率限制）**，而那是**幾分鐘後就沒事**的狀態。
/// 以前遇到 429 就直接讓行程結束（Render 上＝健康檢查失敗、部署失敗、Bot 完全不上線），
/// 但正確的行為是：**先用能拿到的資料把服務跑起來，然後在背景把資料補齊**。
///
/// 這也是「Render 每次都重新部署 → 檔案系統是空的 → 每次開機都要重新抓」這個情況下
/// 唯一能自動恢復的作法（本機快取幫不上忙，因為根本沒有快取）。
///
/// ── 節奏 ────────────────────────────────────────────────
/// 1 分 → 2 分 → 5 分 → 10 分 → 15 分 → 之後固定 30 分（<see cref="Delays"/>）。
/// 為什麼第一次是 1 分鐘：速率限制的視窗通常是一分鐘，等一下就好了 ——
/// 不需要叫使用者去重啟服務。
///
/// ⚠️ 成功之後**同一個城市就不再重試**（<see cref="StaticDataStatus"/> 會把它標成新鮮）。
///    「資料會過期」是原本啟動時的行為（重啟才重抓），這個迴圈不改變那件事。
/// </summary>
public sealed class StaticDataRetryLoop
{
    /// <summary>重試間隔（最後一個會一直重複用）。</summary>
    public static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30)
    ];

    private readonly Func<string, CancellationToken, Task<StaticCityLoad>> _load;
    private readonly Action<StaticCityLoad> _install;
    private readonly StaticDataStatus _status;
    private readonly Action<string>? _log;
    private readonly Func<DateTimeOffset> _now;

    private int _passes;

    /// <param name="load">重試一次（由主機端提供：它才知道 TDX 設定；refresh 交給它決定）。</param>
    /// <param name="install">成功時把資料換進正在服務的那一份（**不必重啟**）。</param>
    /// <param name="status">共用同一份狀態（UI 與 /health 看的就是它）。</param>
    public StaticDataRetryLoop(
        Func<string, CancellationToken, Task<StaticCityLoad>> load,
        Action<StaticCityLoad> install,
        StaticDataStatus status,
        Action<string>? log = null,
        Func<DateTimeOffset>? now = null)
    {
        _load = load;
        _install = install;
        _status = status;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>跑過幾輪（診斷用）。</summary>
    public int Passes => _passes;

    /// <summary>下一輪要等多久（第 <paramref name="pass"/> 輪，從 0 起算）。</summary>
    public static TimeSpan DelayFor(int pass)
        => Delays[Math.Min(Math.Max(0, pass), Delays.Length - 1)];

    /// <summary>
    /// 重試一輪：**只碰「還沒好或只有舊快取」的城市**，回傳這一輪成功補上的城市。
    ///
    /// 拆出來是為了可以離線測試（不用真的等 1 分鐘）。
    /// </summary>
    public async Task<IReadOnlyList<string>> TryOnceAsync(CancellationToken ct = default)
    {
        var fixedUp = new List<string>();

        foreach (var state in _status.All)
        {
            if (ct.IsCancellationRequested) break;

            // 已經新鮮了就不要再打 TDX（省額度，也避免自己製造 429）
            if (!state.NeedsRetry) continue;

            var city = state.City;
            var attempts = state.Attempts + 1;
            var next = _now() + DelayFor(_passes);

            try
            {
                var load = await _load(city, ct).ConfigureAwait(false);

                // ⚠️ 只有「這次真的拿到東西」或「本來就什麼都沒有」才換上去。
                //    重試失敗時會回一份**空的**資料集；如果照單全收，就會把目前還堪用的
                //    舊快取蓋成「什麼都沒有」—— 那是重試機制自己製造的災難。
                var keepCurrentData = !load.Available && state.Available;

                if (!keepCurrentData) _install(load);

                // 狀態也要跟著同一個判斷：沒換資料時，「現在服務的是什麼」不能變成空的
                // （否則 UI 會說「還沒有資料」，但使用者其實查得到站牌 —— 兩邊說法不一致）
                var reported = new StaticCityState(
                    City: city,
                    StopCount: keepCurrentData ? state.StopCount : load.Service.StopCount,
                    Source: keepCurrentData ? state.Source : load.Source,
                    Available: keepCurrentData ? state.Available : load.Available,
                    Error: load.Error?.Message,
                    LastAttemptAt: _now(),
                    NextRetryAt: load.Fresh ? null : next,
                    Attempts: attempts,
                    Fresh: load.Fresh);

                _status.Report(reported);

                if (load.Fresh)
                {
                    fixedUp.Add(city);

                    _log?.Invoke($"[資料] ✅ {BusCity.DisplayOf(city)}的資料補上了（{load.Describe()}，" +
                                 $"來源：{load.Source}）—— 不需要重啟，現在就可以查了");
                }
                else if (load.Available)
                {
                    // 拿到的是「舊快取／最小資料集」這種非最新資料：已經比原本好，
                    // 所以照樣換上去，但要誠實說「這不是最新的，會繼續試」
                    _log?.Invoke($"[資料] ⏳ {BusCity.DisplayOf(city)}拿到的是**非最新**資料" +
                                 $"（{load.Describe()}，來源：{load.Source}）—— 先換上去用，會繼續重試" +
                                 (load.Error is null ? "" : $"；上次失敗：{load.Error.Message}"));
                }
                else
                {
                    _log?.Invoke($"[資料] ⏳ {BusCity.DisplayOf(city)}還是沒拿到（{load.Error?.Message ?? load.Source}），" +
                                 $"下一次 {next.ToLocalTime():HH:mm}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _status.Report(state with
                {
                    Error = ex.Message,
                    LastAttemptAt = _now(),
                    NextRetryAt = next,
                    Attempts = attempts
                });

                _log?.Invoke($"[資料] ⏳ {BusCity.DisplayOf(city)}重試失敗：{ex.Message}（下一次 {next.ToLocalTime():HH:mm}）");
            }
        }

        _passes++;
        return fixedUp;
    }

    /// <summary>
    /// 背景迴圈：只要還有城市需要重試就一直跑（全部就緒就結束）。
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _status.AnyNeedsRetry)
        {
            var delay = DelayFor(_passes);

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await TryOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 重試迴圈本身絕對不能把行程弄死
                _log?.Invoke($"[資料] 重試迴圈發生意外（會繼續試）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (!ct.IsCancellationRequested)
            _log?.Invoke("[資料] 全部城市的靜態資料都就緒了，停止重試");
    }
}
