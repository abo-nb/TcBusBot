namespace TcBusBot.Core.Hosting;

/// <summary>
/// 防休眠（keep-alive）：定期打自己的公開網址，讓平台不要因為「一段時間沒有流量」而把服務停掉。
///
/// Render 免費層會在**約 15 分鐘沒有連入流量**之後把 Web Service 停掉，
/// 下次有人連進來才重新啟動（要等幾十秒）。每 10 分鐘打一次自己就能避開這個情況。
///
/// ⚠️ 誠實說明它的極限：**服務一旦真的睡著，它就沒有東西可以打自己了**。
/// 所以自我 ping 只能「在醒著的時候維持清醒」，不能「把自己叫醒」。
/// 要保證隨時都醒著，需要的是**外部的**監控服務（UptimeRobot、cron-job.org 之類）
/// 或 Render 的付費方案 —— 這個類別是前者還沒設好之前的簡易做法。
/// </summary>
public sealed class KeepAliveLoop : IDisposable
{
    private readonly string _url;
    private readonly TimeSpan _interval;
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;

    /// <param name="url">要 ping 的網址（例：https://tcbusbot-discord.onrender.com/）。</param>
    /// <param name="intervalMinutes">間隔分鐘數（預設 10）。</param>
    public KeepAliveLoop(string url, int intervalMinutes = 10, TimeSpan? requestTimeout = null)
    {
        _url = url;
        _interval = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));

        _http = new HttpClient { Timeout = requestTimeout ?? TimeSpan.FromSeconds(20) };
    }

    public int SuccessCount { get; private set; }
    public int FailureCount { get; private set; }
    public DateTimeOffset? LastPingAt { get; private set; }
    public string? LastResult { get; private set; }

    /// <summary>在背景開始循環；回傳的 Task 不需要等待。</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // 先等一個週期再開始：啟動時本來就有流量（Render 剛部署完是醒著的）
        using var timer = new PeriodicTimer(_interval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await PingOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常結束
        }
    }

    /// <summary>打一次（也可以手動呼叫，方便測試）。</summary>
    public async Task PingOnceAsync(CancellationToken ct = default)
    {
        LastPingAt = DateTimeOffset.UtcNow;

        try
        {
            using var response = await _http.GetAsync(_url, ct).ConfigureAwait(false);
            SuccessCount++;
            LastResult = $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex)
        {
            FailureCount++;
            LastResult = $"{ex.GetType().Name}: {ex.Message}";
        }

        Console.WriteLine($"[keep-alive] {LastPingAt:HH:mm:ss} ping {_url} → {LastResult}" +
                          $"（成功 {SuccessCount}／失敗 {FailureCount}）");
    }

    public string Describe()
        => $"每 {_interval.TotalMinutes:N0} 分鐘 ping {_url}";

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* 已取消 */ }
        _cts?.Dispose();
        _http.Dispose();
    }
}
