using Discord.Net;
using Discord;
using Discord.WebSocket;
using TcBusBot.Core.Realtime;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Subscriptions;
using TcBusBot.Core.Tdx;

namespace TcBusBot.Discord;

/// <summary>
/// 唯一的即時資料輪詢迴圈。
///
/// ★ 全體使用者共用：把所有訂閱的「上車站」去重後，一次（或少數幾次）呼叫 TDX，
///   再交給 SubscriptionMatcher 判斷誰要通知。
///   100 個使用者關注同一個站 → 仍然只有 1 次呼叫。
/// </summary>
public sealed class EtaPoller
{
    private readonly DiscordSocketClient _client;
    private readonly TdxApiClient _api;
    private readonly SubscriptionService _subs;
    private readonly RealtimeBusCache _cache;
    private readonly SubscriptionMatcher _matcher;
    private readonly BusDataCatalog _catalog;
    private readonly int _intervalSeconds;
    private readonly int _batchSize;

    public EtaPoller(
        DiscordSocketClient client,
        TdxApiClient api,
        SubscriptionService subs,
        RealtimeBusCache cache,
        int intervalSeconds,
        BusDataCatalog catalog,
        int batchSize = 40)
    {
        _client = client;
        _api = api;
        _subs = subs;
        _cache = cache;
        _catalog = catalog;
        _matcher = new SubscriptionMatcher(subs, cache, staleDataSeconds: 180);
        _intervalSeconds = intervalSeconds;
        _batchSize = batchSize;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_intervalSeconds);
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Console.WriteLine($"[poller] 輪詢失敗，下一週期重試：{ex.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var stops = _subs.GetAllEnabledBoardStopUids();

        if (stops.Count == 0)
        {
            _cache.Clear();      // 沒有任何訂閱 → 完全不呼叫 API（省點數）
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var fresh = new List<Core.Models.BusEta>();

        // ⚠️ 即時到站的網址裡有城市（`/City/{City}`），所以**一定要按城市分批**：
        //    多城市同時跑（臺中＋臺南）時，把臺南的站牌拿去問臺中端點只會回空資料，
        //    使用者看到的是「明明訂閱了卻一直沒有到站時間」。
        //    這裡用**站牌自己的城市**（不是訂閱者現在選的城市）——
        //    同一個人可能兩邊都訂了，而且換城市不該讓舊訂閱失效。
        foreach (var (city, cityStops) in _catalog.GroupByCity(stops))
        {
            foreach (var chunk in cityStops.Chunk(_batchSize))
            {
                fresh.AddRange(await _api.GetEtasByStopsAsync(chunk, ct, city));
            }
        }

        _cache.ReplaceAll(fresh, now);
        Console.WriteLine($"[poller] {now:HH:mm:ss} 查詢 {stops.Count} 個上車站 → {fresh.Count} 筆 ETA");

        foreach (var notice in _matcher.EvaluateAll(now))
        {
            await SendAsync(notice);
        }
    }

    private async Task SendAsync(BusArrivalNotice notice)
    {
        var embed = BusUi.Notification(
            notice.Group, notice.Subscription, notice.Eta, notice.LiveSeconds,
            notice.Alternatives, DateTimeOffset.UtcNow);

        // 使用者關閉私訊會拿到 403；那是 IP 層級的 ban 來源，所以絕不重試。
        try
        {
            if (notice.Group.ChannelId is { } channelId)
            {
                var channel = await _client.GetChannelAsync(channelId) as IMessageChannel;
                if (channel is not null)
                {
                    await channel.SendMessageAsync(text: $"<@{notice.Group.UserId}>", embed: embed);
                    return;
                }
            }

            if (await _client.GetUserAsync(notice.Group.UserId) is { } user)
                await user.SendMessageAsync(embed: embed);

            await Task.Delay(300);       // 保守節流（Discord 全域上限是 50 rps）
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            Console.WriteLine($"[poller] 使用者 {notice.Group.UserId} 無法接收訊息，已略過。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[poller] 通知送出失敗：{ex.Message}");
        }
    }
}
