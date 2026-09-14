using System.Text.Json;
using TcBusBot.Core.Models;
using TcBusBot.Core.Tdx;

namespace TcBusBot.Core.DataSources;

public sealed record StaticDataSet(
    List<BusStop> Stops,
    List<BusStopOfRoute> StopOfRoutes,
    List<BusRoute> Routes)
{
    public string Describe() =>
        $"{Stops.Count} 個站牌、{StopOfRoutes.Count} 筆路線站序、{Routes.Count} 條路線";
}

/// <summary>
/// 靜態資料的載入策略：本機快取 → TDX API。
///
/// 靜態資料平台每 4 小時才更新一次，重新抓的代價是「計次 + 計量」兩種點數。
/// 所以啟動時優先讀本機快取，只有過期或不存在才打 API。
/// </summary>
public static class StaticDataLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>快取有效時間（預設 12 小時）。</summary>
    public static TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(12);

    public static async Task<StaticDataSet> LoadAsync(
        TdxApiClient api,
        TdxOptions options,
        bool refresh = false,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var dir = options.CacheDirectory;
        var stopPath = Path.Combine(dir, "Stop.json");
        var sorPath = Path.Combine(dir, "StopOfRoute.json");
        var routePath = Path.Combine(dir, "Route.json");

        if (!refresh && IsFresh(stopPath) && IsFresh(sorPath) && IsFresh(routePath))
        {
            try
            {
                log?.Invoke($"讀取本機快取（{dir}）…");
                var cached = new StaticDataSet(
                    Read<List<BusStop>>(stopPath),
                    Read<List<BusStopOfRoute>>(sorPath),
                    Read<List<BusRoute>>(routePath));

                if (cached.Stops.Count > 0 && cached.StopOfRoutes.Count > 0)
                    return cached;
            }
            catch (Exception ex)
            {
                log?.Invoke($"快取讀取失敗（{ex.Message}），改為重新抓取。");
            }
        }

        log?.Invoke(api.IsVisitorMode
            ? "向 TDX 抓取靜態資料（訪客模式，未帶 API 金鑰）…"
            : "向 TDX 抓取靜態資料（會員模式）…");

        // 三次呼叫。並行可以，但 TDX 對並行連線有上限（每 IP 60 條），三次沒問題。
        var stopsTask = api.GetStopsAsync(ct);
        var sorsTask = api.GetStopOfRoutesAsync(ct);
        var routesTask = api.GetRoutesAsync(ct);
        await Task.WhenAll(stopsTask, sorsTask, routesTask);

        var data = new StaticDataSet(
            await stopsTask,
            await sorsTask,
            await routesTask);

        TryWriteCache(dir, stopPath, data.Stops, log);
        TryWriteCache(dir, sorPath, data.StopOfRoutes, log);
        TryWriteCache(dir, routePath, data.Routes, log);

        return data;
    }

    private static bool IsFresh(string path)
        => File.Exists(path) && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) < CacheTtl;

    /// <summary>
    /// 只讀本機快取，**完全不碰網路**。
    /// 給離線工具（tcbus diag/route）用 —— 這樣就能拿真實的全量資料做診斷，
    /// 不需要每次重新抓、也不會浪費 TDX 點數。
    /// </summary>
    public static StaticDataSet? TryReadCache(string cacheDirectory)
    {
        var stopPath = Path.Combine(cacheDirectory, "Stop.json");
        var sorPath = Path.Combine(cacheDirectory, "StopOfRoute.json");
        var routePath = Path.Combine(cacheDirectory, "Route.json");

        if (!File.Exists(stopPath) || !File.Exists(sorPath) || !File.Exists(routePath))
            return null;

        try
        {
            var data = new StaticDataSet(
                Read<List<BusStop>>(stopPath),
                Read<List<BusStopOfRoute>>(sorPath),
                Read<List<BusRoute>>(routePath));

            return data.Stops.Count > 0 && data.StopOfRoutes.Count > 0 ? data : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>快取檔存在嗎（不論新舊）。</summary>
    public static bool CacheExists(string cacheDirectory)
        => File.Exists(Path.Combine(cacheDirectory, "StopOfRoute.json"));

    /// <summary>快取的最後更新時間。</summary>
    public static DateTimeOffset? CacheTimestamp(string cacheDirectory)
    {
        var path = Path.Combine(cacheDirectory, "StopOfRoute.json");
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    private static T Read<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
           ?? throw new InvalidOperationException($"快取檔內容為空：{path}");

    private static void TryWriteCache<T>(string dir, string path, T value, Action<string>? log)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
        }
        catch (Exception ex)
        {
            log?.Invoke($"快取寫入失敗（不影響運作）：{ex.Message}");
        }
    }
}
