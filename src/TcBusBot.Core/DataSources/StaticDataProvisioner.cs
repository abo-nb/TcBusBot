using TcBusBot.Core.Bus;
using TcBusBot.Core.Tdx;

namespace TcBusBot.Core.DataSources;

/// <summary>
/// 一次「把某個城市的靜態資料弄到手」的結果（含來源與錯誤，讓 UI 能誠實說明）。
///
/// 這裡交出來的是**已經建好索引的 <see cref="BusDataService"/>**（而不是原始的三份清單）：
/// 備援路徑之一（內建最小資料集）本來就是直接做出一份服務，硬要轉成清單再建一次索引
/// 只是多繞一圈；而且交出來的東西可以直接換進 <see cref="BusDataCatalog"/>。
/// </summary>
public sealed record StaticCityLoad(
    string City,
    BusDataService Service,
    string Source,
    bool Fresh,
    Exception? Error = null)
{
    /// <summary>有資料可以用嗎（哪怕只是舊快取／最小資料集）。</summary>
    public bool Available => Service.StopCount > 0;

    public string Describe() => $"{Service.StopCount:N0} 個站牌、{Service.TripCount:N0} 筆路線站序";

    public StaticCityState ToState(DateTimeOffset? lastAttemptAt = null, DateTimeOffset? nextRetryAt = null,
        int attempts = 0)
        => new(
            City: BusCity.Normalize(City),
            StopCount: Service.StopCount,
            Source: Source,
            Available: Available,
            Error: Error?.Message,
            LastAttemptAt: lastAttemptAt,
            NextRetryAt: nextRetryAt,
            Attempts: attempts,
            Fresh: Fresh);
}

/// <summary>
/// 「一個城市的靜態資料要從哪裡來」的**唯一順序**：
///
/// <code>
/// 新鮮快取 → TDX → 過期的快取 → （臺中）內建最小資料集 → 空的資料集
/// </code>
///
/// ── 為什麼最後一定要有「空的資料集」而不是丟例外 ──────────────
/// 以前 TDX 回 429（速率限制）就會讓**整個行程結束**：
/// Render 的健康檢查失敗 → 部署失敗 → Bot 完全不會上線。
/// 但 429 是**幾分鐘後就沒事**的暫時狀態，而公車查詢只是這個 Bot 的其中一個功能 ——
/// 「AI 聊天、已訂好的到站通知都還能用」遠比「什麼都沒有」好。
///
/// 所以這裡的規則很簡單：**永遠回一份資料服務**（最差是空的），把「失敗」放在
/// <see cref="StaticCityLoad.Error"/> 裡往上傳，由 <see cref="StaticDataStatus"/> 負責
/// 告訴使用者「還沒好、正在重試」，由 <see cref="StaticDataRetryLoop"/> 負責重試。
/// </summary>
public static class StaticDataProvisioner
{
    /// <summary>把三份原始清單建成記憶體索引（空清單也可以，就是「沒有資料」）。</summary>
    public static BusDataService Build(
        IEnumerable<Models.BusStop> stops,
        IEnumerable<Models.BusStopOfRoute> stopOfRoutes,
        IEnumerable<Models.BusRoute> routes)
    {
        var service = new BusDataService();
        service.Load(stops, stopOfRoutes, routes);

        return service;
    }

    /// <summary>
    /// 取得一個城市的靜態資料。**不會丟例外**（除了取消）。
    /// </summary>
    /// <param name="api">TDX 用戶端；null ＝ 這台主機沒有 TDX 金鑰（只能用內建資料集）。</param>
    /// <param name="template">該城市的 TDX 設定（BaseUrl／金鑰／快取目錄）。</param>
    /// <param name="refresh">true ＝ 就算快取是新鮮的也重新抓（背景重試用）。</param>
    /// <param name="fixturesRoot">內建最小資料集的根目錄（null ＝ 不試）。</param>
    public static async Task<StaticCityLoad> LoadCityAsync(
        string city,
        TdxApiClient? api,
        TdxOptions template,
        bool refresh = false,
        string? fixturesRoot = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var cityOptions = new TdxOptions
        {
            BaseUrl = template.BaseUrl,
            City = city,
            Cities = [city],
            CacheDirectory = template.CacheDirectory,
            ClientId = template.ClientId,
            ClientSecret = template.ClientSecret
        };

        // ── 沒有 TDX 金鑰（或 --data fixture）→ 只有內建最小資料集 ──
        if (api is null)
        {
            if (TryMiniFixture(city, fixturesRoot, out var fixture, out var whyNot))
                return new StaticCityLoad(city, fixture, "內建最小資料集（沒有 TDX 金鑰）", Fresh: true);

            return new StaticCityLoad(city, Build([], [], []), "沒有資料", Fresh: true,
                new InvalidOperationException(whyNot ?? "沒有可用的資料來源"));
        }

        try
        {
            var result = await StaticDataLoader.LoadAsync(api, cityOptions, refresh, log, ct)
                .ConfigureAwait(false);

            return new StaticCityLoad(
                city,
                Build(result.Data.Stops, result.Data.StopOfRoutes, result.Data.Routes),
                result.DescribeSource(),
                Fresh: result.Source != StaticLoadSource.StaleCache,
                Error: result.Error);
        }
        catch (OperationCanceledException)
        {
            throw;   // 關機中 → 讓上層結束（不要當成「載入失敗」）
        }
        catch (Exception ex)
        {
            // 連舊快取都沒有 → 還有內建最小資料集（只有臺中），最後才是空的
            if (TryMiniFixture(city, fixturesRoot, out var fixture, out var whyNot))
                return new StaticCityLoad(city, fixture,
                    "內建最小資料集（只有這條走廊；TDX 抓取失敗）", Fresh: false, ex);

            return new StaticCityLoad(city, Build([], [], []), "沒有資料", Fresh: false,
                new InvalidOperationException($"{ex.Message}（{whyNot}）", ex));
        }
    }

    /// <summary>
    /// 內建最小資料集只有**臺中**的走廊 —— 拿它去服務臺南會出現「查得到臺中站牌」
    /// 這種更難懂的錯誤，所以其他城市直接拒絕（並說明為什麼）。
    /// </summary>
    private static bool TryMiniFixture(
        string city, string? fixturesRoot, out BusDataService data, out string? whyNot)
    {
        data = Build([], [], []);
        whyNot = null;

        if (string.IsNullOrWhiteSpace(fixturesRoot))
        {
            whyNot = "這台主機沒有內建資料集可用";
            return false;
        }

        if (!string.Equals(BusCity.Normalize(city), BusCity.Default, StringComparison.OrdinalIgnoreCase))
        {
            whyNot = $"內建最小資料集只有臺中的資料，沒有{BusCity.DisplayOf(city)}的資料" +
                     "（請確認 TDX_CLIENT_ID／TDX_CLIENT_SECRET，以及 TDX 的速率限制）";
            return false;
        }

        try
        {
            data = MiniFixtureSource.Load(fixturesRoot);
            return true;
        }
        catch (Exception ex)
        {
            whyNot = $"讀取內建資料集失敗：{ex.Message}";
            return false;
        }
    }
}
