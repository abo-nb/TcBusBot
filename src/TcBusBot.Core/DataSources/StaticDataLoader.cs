using System.Text.Json;
using TcBusBot.Core.Bus;
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

/// <summary>某個城市的快取檔路徑。</summary>
public sealed record StaticCachePaths(string Directory, string Stops, string StopOfRoutes, string Routes);

/// <summary>
/// 靜態資料的載入策略：本機快取 → TDX API。
///
/// 靜態資料平台每 4 小時才更新一次，重新抓的代價是「計次 + 計量」兩種點數。
/// 所以啟動時優先讀本機快取，只有過期或不存在才打 API。
///
/// ⚠️ **快取檔名一定要帶城市**：以前是固定的 `Stop.json`／`StopOfRoute.json`／`Route.json`，
///    換城市（例如改成臺南）時會**讀到上一個城市的快取**，而且因為檔案是新鮮的，
///    它連重新抓都不會 —— 使用者看到的是「臺南的公車站名全部變成臺中」，
///    沒有錯誤訊息，只有一頭霧水。舊檔名仍然相容（見 <see cref="Paths"/>）。
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

    /// <summary>某個城市的快取檔路徑（純函式，可離線測試）。</summary>
    public static StaticCachePaths Paths(string cacheDirectory, string? city)
    {
        var code = BusCity.Normalize(city);
        var dir = string.IsNullOrWhiteSpace(cacheDirectory) ? "cache" : cacheDirectory;

        return new StaticCachePaths(
            Directory: dir,
            Stops: Path.Combine(dir, $"Stop.{code}.json"),
            StopOfRoutes: Path.Combine(dir, $"StopOfRoute.{code}.json"),
            Routes: Path.Combine(dir, $"Route.{code}.json"));
    }

    public static async Task<StaticDataSet> LoadAsync(
        TdxApiClient api,
        TdxOptions options,
        bool refresh = false,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var paths = Paths(options.CacheDirectory, options.City);
        var dir = paths.Directory;
        var stopPath = paths.Stops;
        var sorPath = paths.StopOfRoutes;
        var routePath = paths.Routes;

        if (!refresh && IsFresh(stopPath) && IsFresh(sorPath) && IsFresh(routePath))
        {
            try
            {
                log?.Invoke($"讀取本機快取（{BusCity.DisplayOf(options.City)}：{dir}）…");
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
            ? $"向 TDX 抓取{BusCity.DisplayOf(options.City)}靜態資料（訪客模式，未帶 API 金鑰）…"
            : $"向 TDX 抓取{BusCity.DisplayOf(options.City)}靜態資料（會員模式）…");

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
    ///
    /// 舊版（沒有城市）的檔名是 `Stop.json` 那三個，只有**臺中**會退回讀它們，
    /// 免得升級之後發現「快取不見了、要重新抓」。
    /// </summary>
    public static StaticDataSet? TryReadCache(string cacheDirectory, string? city = BusCity.Default)
    {
        var paths = Paths(cacheDirectory, city);

        var stopPath = paths.Stops;
        var sorPath = paths.StopOfRoutes;
        var routePath = paths.Routes;

        // 相容舊檔名：只有臺中（預設城市）才可能會有那組檔案
        if ((!File.Exists(stopPath) || !File.Exists(sorPath) || !File.Exists(routePath))
            && string.Equals(BusCity.Normalize(city), BusCity.Default, StringComparison.OrdinalIgnoreCase))
        {
            var legacyStops = Path.Combine(cacheDirectory, "Stop.json");
            var legacySor = Path.Combine(cacheDirectory, "StopOfRoute.json");
            var legacyRoute = Path.Combine(cacheDirectory, "Route.json");

            if (File.Exists(legacyStops) && File.Exists(legacySor) && File.Exists(legacyRoute))
            {
                stopPath = legacyStops;
                sorPath = legacySor;
                routePath = legacyRoute;
            }
        }

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

    /// <summary>
    /// **多城市**載入：每個城市各自讀快取／抓 API，再合併成一份資料集。
    ///
    /// 為什麼可以直接合併：站牌 UID 有城市前綴（臺中 `TXG…`、臺南 `TNN…`）、
    /// 路線 UID／SubRouteUID 也一樣，所以兩個城市的清單接起來不會撞。
    /// 站名索引仍然照站名建立 —— 於是「臺南車站」與「臺中車站」都查得到
    /// （同名站牌各自屬於不同城市，查詢結果會分開列）。
    /// </summary>
    public static async Task<StaticDataSet> LoadManyAsync(
        TdxApiClient api,
        TdxOptions options,
        bool refresh = false,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var cities = options.EffectiveCities;

        if (cities.Count == 1) return await LoadAsync(api, options, refresh, log, ct);

        var stops = new List<BusStop>();
        var sors = new List<BusStopOfRoute>();
        var routes = new List<BusRoute>();

        foreach (var city in cities)
        {
            var cityOptions = new TdxOptions
            {
                BaseUrl = options.BaseUrl,
                City = city,
                CacheDirectory = options.CacheDirectory,
                ClientId = options.ClientId,
                ClientSecret = options.ClientSecret
            };

            log?.Invoke($"[資料] {BusCity.DisplayOf(city)}：開始載入…");

            var set = await LoadAsync(api, cityOptions, refresh, log, ct);

            stops.AddRange(set.Stops);
            sors.AddRange(set.StopOfRoutes);
            routes.AddRange(set.Routes);

            log?.Invoke($"[資料] {BusCity.DisplayOf(city)}：{set.Describe()}");
        }

        return new StaticDataSet(stops, sors, routes);
    }

    /// <summary>快取檔存在嗎（不論新舊；有帶城市的新檔名優先，其次才是舊檔名）。</summary>
    public static bool CacheExists(string cacheDirectory, string? city = BusCity.Default)
    {
        var paths = Paths(cacheDirectory, city);

        return File.Exists(paths.StopOfRoutes)
               || (string.Equals(BusCity.Normalize(city), BusCity.Default, StringComparison.OrdinalIgnoreCase)
                   && File.Exists(Path.Combine(cacheDirectory, "StopOfRoute.json")));
    }

    /// <summary>快取的最後更新時間。</summary>
    public static DateTimeOffset? CacheTimestamp(string cacheDirectory, string? city = BusCity.Default)
    {
        var paths = Paths(cacheDirectory, city);

        var path = File.Exists(paths.StopOfRoutes)
            ? paths.StopOfRoutes
            : Path.Combine(cacheDirectory, "StopOfRoute.json");

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
