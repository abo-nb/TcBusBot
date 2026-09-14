using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TcBusBot.Core.Models;

namespace TcBusBot.Core.Tdx;

public sealed class TdxOptions
{
    public string BaseUrl { get; set; } = "https://tdx.transportdata.tw";
    public string City { get; set; } = "Taichung";

    /// <summary>TDX 會員中心的 API 金鑰。留空則嘗試「訪客模式」（每 IP 每日 20 次）。</summary>
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>靜態資料的快取目錄；有的話就不必每次啟動都重抓（省點數）。</summary>
    public string CacheDirectory { get; set; } = "cache";
}

/// <summary>Access Token 的快取與自動更新。</summary>
public sealed class TdxTokenProvider
{
    private readonly HttpClient _http;
    private readonly TdxOptions _opt;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTimeOffset _expiresAt;

    public TdxTokenProvider(HttpClient http, TdxOptions opt)
    {
        _http = http;
        _opt = opt;
    }

    /// <summary>目前是否為「訪客模式」（沒有金鑰）。</summary>
    public bool IsVisitorMode => !_opt.HasCredentials;

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        if (!_opt.HasCredentials) return null;      // 訪客模式：不帶 Authorization

        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;

            // 官方限制：token endpoint 每 IP 每分鐘最多 20 次 → 一定要快取
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _opt.ClientId,
                ["client_secret"] = _opt.ClientSecret
            });

            using var res = await _http.PostAsync(
                "/auth/realms/TDXConnect/protocol/openid-connect/token", form, ct);

            if (!res.IsSuccessStatusCode)
                throw new TdxException($"取得 Access Token 失敗：HTTP {(int)res.StatusCode} " +
                                       "（請確認 Client Id / Client Secret 是否正確）");

            var doc = await res.Content.ReadFromJsonAsync<TokenResponse>(ct)
                      ?? throw new TdxException("Token 回應為空");

            _token = doc.AccessToken;
            // expires_in 預設 86400 秒；提前 30 分鐘視為過期，避免邊界失效
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, doc.ExpiresIn - 1800));
            return _token;
        }
        finally { _gate.Release(); }
    }

    /// <summary>遇到 401 時呼叫，強制下次重新取得。</summary>
    public void Invalidate()
    {
        _token = null;
        _expiresAt = default;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

public sealed class TdxException : Exception
{
    public TdxException(string message) : base(message) { }
    public TdxException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 唯一的 TDX HTTP 出入口。所有 API 呼叫都經過這裡，方便統一加重試、gzip 與錯誤處理。
/// </summary>
public sealed class TdxApiClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly TdxOptions _opt;
    private readonly TdxTokenProvider _token;

    /// <summary>
    /// ★ 建立 HTTP handler 時**必須**開啟自動解壓縮。
    ///
    /// 我們會送 <c>Accept-Encoding: gzip</c>，而 .NET 的
    /// <see cref="SocketsHttpHandler.AutomaticDecompression"/> 預設是 <c>None</c>。
    /// 少了這個設定，就會拿到 gzip 原始位元組，JSON 解析直接失敗：
    /// <code>'0x1F' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0.</code>
    /// （0x1F 0x8B 正是 gzip 的魔數。實際踩過。）
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,   // gzip / deflate / brotli
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>建立已正確設定的 HttpClient。請用這個，不要自己 new HttpClient()。</summary>
    public static HttpClient CreateHttpClient() => new(CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(120)   // 靜態資料一次好幾 MB，給寬一點
    };

    public TdxApiClient(HttpClient http, TdxOptions opt)
    {
        _http = http;
        _opt = opt;
        _http.BaseAddress ??= new Uri(opt.BaseUrl);
        if (_http.Timeout < TimeSpan.FromSeconds(60))
            _http.Timeout = TimeSpan.FromSeconds(60);
        _token = new TdxTokenProvider(http, opt);
    }

    public bool IsVisitorMode => _token.IsVisitorMode;

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await _token.GetTokenAsync(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            if (token is not null)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.AcceptEncoding.ParseAdd("gzip");

            using var res = await _http.SendAsync(req, ct);

            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt < 1)
            {
                _token.Invalidate();
                continue;
            }

            // TDX 官方錯誤碼：429 超過 API 速率、423 超過 50 次/秒、416 超過 60 並行連線
            if ((int)res.StatusCode is 429 or 423 or 416 && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct);
                continue;
            }

            if (!res.IsSuccessStatusCode)
            {
                var hint = (int)res.StatusCode switch
                {
                    401 => "（無 token / token 無效 / scope 不足）",
                    403 => "（權限不足。訪客模式限制較多，建議申請免費 API 金鑰）",
                    429 => "（超過 API 呼叫速率限制）",
                    416 => "（超過每 IP 60 條並行連線）",
                    423 => "（超過每秒 50 次）",
                    _ => ""
                };
                throw new TdxException($"TDX API 失敗：HTTP {(int)res.StatusCode} {res.ReasonPhrase} {hint}\n{relativeUrl}");
            }

            var value = await ReadJsonAsync<T>(res, relativeUrl, ct);
            return value;
        }
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage res, string relativeUrl, CancellationToken ct)
    {
        try
        {
            var value = await res.Content.ReadFromJsonAsync<T>(Json, ct);
            return value ?? throw new TdxException($"TDX 回應為空：{relativeUrl}");
        }
        catch (JsonException ex)
        {
            // 最常見的原因：回應是壓縮過的，但 HttpClient 沒有開自動解壓縮
            var hint = ex.Message.Contains("0x1F") || ex.Message.Contains("invalid start of a value")
                ? "\n提示：內容看起來是壓縮過的（gzip 開頭是 0x1F 0x8B）。" +
                  "請用 TdxApiClient.CreateHttpClient() 建立 HttpClient，" +
                  "它會開啟 AutomaticDecompression。"
                : "";

            var encoding = string.Join(",", res.Content.Headers.ContentEncoding);
            throw new TdxException(
                $"TDX 回應不是有效的 JSON：{ex.Message}{hint}\n" +
                $"Content-Encoding: [{encoding}]　URL: {relativeUrl}", ex);
        }
    }

    // ── 靜態資料 ───────────────────────────────────────────

    public Task<List<BusStop>> GetStopsAsync(CancellationToken ct)
        => GetAsync<List<BusStop>>($"/api/basic/v2/Bus/Stop/City/{_opt.City}?$format=JSON", ct);

    public Task<List<BusStopOfRoute>> GetStopOfRoutesAsync(CancellationToken ct)
        => GetAsync<List<BusStopOfRoute>>(
            $"/api/basic/v2/Bus/StopOfRoute/City/{_opt.City}?$format=JSON", ct);

    public Task<List<BusRoute>> GetRoutesAsync(CancellationToken ct)
        => GetAsync<List<BusRoute>>($"/api/basic/v2/Bus/Route/City/{_opt.City}?$format=JSON", ct);

    // ── 即時資料（N1 預估到站）──────────────────────────────

    /// <summary>
    /// **只要求真正會用到的欄位。**
    ///
    /// TDX 的點數公式是「呼叫次數 / 1500 + 回傳資料量(MB) / 150」——
    /// 資料量是要算錢的，所以每個位元組都值得省。
    /// 原始回應裡下面這些欄位我們一律用不到（實測佔了約四成的位元組）：
    ///
    /// | 欄位 | 為什麼不要 |
    /// | --- | --- |
    /// | `RouteName` / `SubRouteName` | `{Zh_tw, En}` 物件（含英文名）；路線名在訂閱裡就有 |
    /// | `StopName` | 同上；站名在靜態索引裡就有 |
    /// | `RouteID` / `SubRouteUID` / `SubRouteID` / `StopID` | 站牌與路線一律用 UID |
    /// | `Estimates[]` | 多班車陣列；目前只通知最快的那一班 |
    /// | `UpdateTime` | 只用 `SrcUpdateTime` 判斷資料新鮮度 |
    ///
    /// ⚠️ 少要欄位會讓那些屬性留在預設值（null），所以要改動前先確認
    /// 「這個欄位真的沒有人在讀」——`tcbus selftest` 有一項在量這件事。
    /// </summary>
    public static readonly string[] EtasByStopsFields =
    [
        "RouteUID",      // 配對鍵
        "Direction",     // 配對鍵（去程/返程）
        "StopUID",       // 快取鍵
        "EstimateTime",  // 剩餘秒數（要自行遞減，見 SubscriptionMatcher）
        "StopStatus",    // 0 正常 / 1 尚未發車 / 2 交管 / 3 末班車已過 / 4 今日未營運
        "NextBusTime",   // StopStatus=1 時顯示「幾點發車」
        "PlateNumb",     // 去重鍵（同一台車只通知一次）＋顯示
        "SrcUpdateTime", // 資料新鮮度＋時間遞減
    ];

    private static readonly string EtaSelect = "$select=" + string.Join(",", EtasByStopsFields);

    /// <summary>
    /// 組出「以多個上車站過濾」的查詢 URL。
    ///
    /// 公開是為了讓離線工具（`--dryrun`）能印出**實際會送出的那條 URL**，
    /// 使用者可以拿去自己驗證。也讓「一個批次幾個站」的限制可以被檢查。
    /// </summary>
    public static string BuildEtasByStopsUrl(string city, IEnumerable<string> stopUids)
    {
        var filter = string.Join(" or ", stopUids.Select(u => $"StopUID eq '{u}'"));
        return $"/api/basic/v2/Bus/EstimatedTimeOfArrival/City/{city}" +
               $"?$filter={Uri.EscapeDataString(filter)}&{EtaSelect}&$format=JSON";
    }

    /// <summary>
    /// 以「上車站」集合過濾。**一次呼叫涵蓋多個站**，這是不浪費點數的關鍵。
    ///
    /// 為什麼不是用 `RouteName eq '300' or ...` 過濾路線：
    ///   1. 那個過濾條件會回傳**整條路線所有站牌**的資料（300 路去回程上百站），
    ///      我們只用得到其中 1~2 站 → 資料量暴增，而資料量要算點數。
    ///   2. 以站牌過濾時，同一個站的所有路線都會回來（本來就需要），
    ///      而且站牌 UID 是短 ASCII 字串，沒有中文編碼／大小寫的意外。
    /// </summary>
    public Task<List<BusEta>> GetEtasByStopsAsync(IEnumerable<string> stopUids, CancellationToken ct)
        => GetAsync<List<BusEta>>(BuildEtasByStopsUrl(_opt.City, stopUids), ct);
}
