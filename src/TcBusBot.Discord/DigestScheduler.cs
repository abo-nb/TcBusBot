using TcBusBot.Core.Chat.Digest;

namespace TcBusBot.Discord;

/// <summary>
/// 每週摘要的**排程器**（跟 <see cref="EtaPoller"/> 一樣是「一段時間問一次」的迴圈）。
///
/// 為什麼不是「算出下一次時間然後睡到那時候」：
/// 那種寫法在**時間被改、程式被暫停（Render 休眠）、或跑到一半失敗**時會整組歪掉，
/// 而且「下一次是什麼時候」變成藏在一個 Task 裡的狀態，重啟就沒了。
/// 這裡的作法是把「下一次該跑的時間」**存進資料庫**（<see cref="ChannelDigestConfig.NextRunAt"/>），
/// 排程器每一分鐘只問一句「有沒有誰過期了」—— 重啟、暫停、失敗都只是「這次沒跑到，下次問到就跑」。
///
/// 而且每個頻道的時間是**各自設定的**（`/digest enable schedule:週三 20:00`）。
/// </summary>
public sealed class DigestScheduler
{
    private readonly DigestStore _store;
    private readonly DigestRunner _runner;
    private readonly DigestOptions _options;

    private int _running;   // 0 = 閒置；避免同一輪還沒跑完就又進來

    public DigestScheduler(DigestStore store, DigestRunner runner, DigestOptions options)
    {
        _store = store;
        _runner = runner;
        _options = options;
    }

    /// <summary>已經跑了幾輪（`/health` 與診斷用）。</summary>
    public int Ticks { get; private set; }

    /// <summary>上一次有頻道被處理的時間。</summary>
    public DateTimeOffset? LastRunAt { get; private set; }

    public string Describe()
        => _options.Enabled
            ? $"啟用中（每 {_options.SchedulerInterval.TotalSeconds:0} 秒檢查一次｜" +
              $"{_store.Enabled().Count} 個頻道啟用｜已檢查 {Ticks} 次）"
            : "停用（LLM_DIGEST=false）";

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            Console.WriteLine("[摘要] 每週摘要未啟用（LLM_DIGEST=false）");
            return;
        }

        Console.WriteLine($"[摘要] 排程器啟動：每 {_options.SchedulerInterval.TotalSeconds:0} 秒檢查一次、" +
                          $"時區 UTC{(int)_options.ZoneOffset.TotalHours:+0;-0}、" +
                          $"思考 {_options.Reasoning}");

        using var timer = new PeriodicTimer(_options.SchedulerInterval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 排程器**絕對不可以**因為一次失敗就停掉（那樣要重啟服務才會恢復）
                Console.WriteLine($"[摘要] 排程迴圈發生意外：{ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(ct)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>一次檢查：把過期的頻道**一個一個**跑完（不並行 —— 摘要會花錢，跑太快很難看出是誰花的）。</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        Ticks++;

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        try
        {
            var due = _store.Due(DateTimeOffset.UtcNow);

            foreach (var config in due)
            {
                if (ct.IsCancellationRequested) break;

                LastRunAt = DateTimeOffset.UtcNow;

                try
                {
                    await _runner.RunAsync(config.ChannelId, manual: false, ct);
                }
                catch (Exception ex)
                {
                    // 單一頻道失敗不可以影響其他頻道（RunAsync 內部多半已經處理，這裡是最後一道）
                    _store.MarkFailed(config.ChannelId, $"{ex.GetType().Name}: {ex.Message}",
                        DateTimeOffset.UtcNow, _options.RetryDelay);

                    Console.WriteLine($"[摘要] 頻道 {config.ChannelId} 執行時丟出例外：{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
