namespace TcBusBot.Core.Bus;

/// <summary>
/// 「這個伺服器要用哪一份公車資料」的唯一入口。
///
/// ── 為什麼要用「每個城市各一份資料」而不是「合併成一份再過濾」──────
/// 合併的做法要在**每一個查詢路徑**都記得加上城市過濾（站牌搜尋、站區群組、
/// 路線號碼、轉乘建議、面板選項…）。只要漏掉一個，那個路徑就會吐出**別的城市**的站牌 ——
/// 而且不會報錯（使用者只會覺得「怎麼查到高雄的站」）。
///
/// 這個專案已經吃過同一類的虧（快取檔名沒帶城市 → 讀到上一個城市的資料、
/// ETA 沒按城市分批 → 一直沒有到站時間），所以這裡刻意選**不可能看錯**的作法：
/// 一個城市一份索引，要用哪一份由 <see cref="For(ulong)"/> 決定。
/// </summary>
public sealed class BusDataCatalog
{
    private readonly Dictionary<string, BusDataService> _byCity;
    private readonly UserCityStore _cities;

    public BusDataCatalog(IReadOnlyDictionary<string, BusDataService> byCity, UserCityStore cities)
    {
        _byCity = new Dictionary<string, BusDataService>(byCity, StringComparer.OrdinalIgnoreCase);
        _cities = cities;
    }

    /// <summary>可用城市（依 `BUS_CITY` 的順序）。</summary>
    public IReadOnlyList<string> Available => _cities.Available;

    /// <summary>預設城市（沒選過的伺服器用這個）。</summary>
    public string Default => _cities.Default;

    public bool HasChoice => _cities.HasChoice;

    /// <summary>這個**人**要用哪一份資料（訂公車的人自己選的城市）。</summary>
    public BusDataService For(ulong userId) => ForCity(_cities.Get(userId));

    /// <summary>這個人的城市（顯示用）。</summary>
    public string CityFor(ulong userId) => _cities.Get(userId);

    /// <summary>指定城市的資料。認不出來的城市回傳預設那份（不會丟例外）。</summary>
    public BusDataService ForCity(string? city)
    {
        var code = BusCity.Normalize(city);

        return _byCity.TryGetValue(code, out var data) ? data : DefaultData;
    }

    /// <summary>預設城市的資料。</summary>
    public BusDataService DefaultData
        => _byCity.TryGetValue(_cities.Default, out var data) ? data : _byCity.Values.First();

    /// <summary>某個城市載入了幾個站牌（診斷用）。</summary>
    public string Describe()
        => string.Join("｜", _byCity.Select(kv =>
            $"{BusCity.DisplayOf(kv.Key)}：{kv.Value.StopCount} 個站牌、{kv.Value.TripCount} 筆路線站序"));

    /// <summary>這個站牌屬於哪個城市（掃過每一份資料；找不到就回 <see cref="Default"/>）。</summary>
    public string CityOfStop(string? stopUid)
    {
        if (string.IsNullOrWhiteSpace(stopUid)) return Default;

        foreach (var (city, data) in _byCity)
            if (data.CityOf(stopUid) is not null) return city;

        return Default;
    }

    /// <summary>
    /// 把一批站牌依城市分組（即時到站要用的：ETA 的網址裡有城市）。
    ///
    /// 訂閱可能是「同一個人以前訂臺中、後來訂臺南」，所以這裡不能只用一個城市查。
    /// </summary>
    public IReadOnlyList<(string City, List<string> StopUids)> GroupByCity(IEnumerable<string> stopUids)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var uid in stopUids)
        {
            var city = CityOfStop(uid);

            if (!grouped.TryGetValue(city, out var list)) grouped[city] = list = [];
            list.Add(uid);
        }

        return grouped.Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
