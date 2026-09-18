namespace TcBusBot.Core.Bus;

/// <summary>
/// 支援哪些縣市的公車，以及「使用者怎麼打」對到「TDX 的城市代碼」。
///
/// ── 為什麼需要這一層 ────────────────────────────────────
/// TDX 的網址是 `…/City/{City}`，那個 `City` 是**英文代碼**（`Taichung`、`Tainan`），
/// 而使用者與 log 想看的是中文（臺中、臺南）。兩邊要有一個地方對起來，
/// 而且要在**啟動時**就發現打錯（打錯的話 TDX 只會回一個空清單或錯誤，
/// 使用者看到的是「這個城市沒有公車」——比什麼都不說更難查）。
///
/// 預設是**臺中**（這個 Bot 一開始就是為台中做的，舊的環境變數、快取檔名、
/// 文件全部都以台中為主）。
/// </summary>
public static class BusCity
{
    /// <summary>沒設定時用的城市。</summary>
    public const string Default = "Taichung";

    /// <summary>TDX 城市代碼 ↔ 中文顯示名 ↔ 使用者可能打的寫法。</summary>
    private static readonly (string Code, string Display, string[] Aliases)[] Cities =
    [
        ("Keelung", "基隆", ["keelung", "基隆", "基隆市"]),
        ("Taipei", "臺北", ["taipei", "台北", "臺北", "台北市", "臺北市"]),
        ("NewTaipei", "新北", ["newtaipei", "new_taipei", "新北", "新北市"]),
        ("Taoyuan", "桃園", ["taoyuan", "桃園", "桃園市"]),
        ("Hsinchu", "新竹", ["hsinchu", "新竹", "新竹市"]),
        ("HsinchuCounty", "新竹縣", ["hsinchucounty", "新竹縣", "新竹县"]),
        ("MiaoliCounty", "苗栗", ["miaolicounty", "苗栗", "苗栗縣", "苗栗县"]),
        ("Taichung", "臺中", ["taichung", "台中", "臺中", "台中市", "臺中市"]),
        ("ChanghuaCounty", "彰化", ["changhuacounty", "彰化", "彰化縣", "彰化县"]),
        ("NantouCounty", "南投", ["nantoucounty", "南投", "南投縣", "南投县"]),
        ("YunlinCounty", "雲林", ["yunlincounty", "雲林", "雲林縣", "雲林县"]),
        ("Chiayi", "嘉義", ["chiayi", "嘉義", "嘉义", "嘉義市"]),
        ("ChiayiCounty", "嘉義縣", ["chiayicounty", "嘉義縣", "嘉义县"]),
        ("Tainan", "臺南", ["tainan", "台南", "臺南", "台南市", "臺南市"]),
        ("Kaohsiung", "高雄", ["kaohsiung", "高雄", "高雄市"]),
        ("PingtungCounty", "屏東", ["pingtungcounty", "屏東", "屏东", "屏東縣"]),
        ("YilanCounty", "宜蘭", ["yilancounty", "宜蘭", "宜兰", "宜蘭縣"]),
        ("HualienCounty", "花蓮", ["hualiencounty", "花蓮", "花莲", "花蓮縣"]),
        ("TaitungCounty", "臺東", ["taitungcounty", "台東", "臺東", "台东", "臺東縣"]),
        ("PenghuCounty", "澎湖", ["penghucounty", "澎湖", "澎湖縣"]),
        ("KinmenCounty", "金門", ["kinmencounty", "金門", "金门", "金門縣"]),
        ("LienchiangCounty", "連江", ["lienchiangcounty", "連江", "馬祖", "連江縣"])
    ];

    /// <summary>全部支援的城市（顯示名，依中文排序習慣：本島由北而南）。</summary>
    public static IReadOnlyList<string> Displays => Cities.Select(c => c.Display).ToList();

    /// <summary>
    /// 把使用者打的城市轉成 TDX 代碼。
    ///
    /// 接受：英文代碼（`Taichung`／大小寫不拘／含底線）、中文（`臺中`／`台中`／`台中市`）。
    /// 認不出來時**原樣回傳**（修剪過）—— 讓它去 TDX 撞一次比在這裡亂猜好，
    /// 但呼叫端應該把「不認識這個城市」印出來提醒。
    /// </summary>
    public static string Normalize(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return Default;

        var key = text.Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();

        foreach (var (code, _, aliases) in Cities)
        {
            if (string.Equals(code, text, StringComparison.OrdinalIgnoreCase)) return code;

            if (aliases.Any(a => string.Equals(a.Replace("_", "").ToLowerInvariant(), key, StringComparison.Ordinal)))
                return code;
        }

        return text;
    }

    /// <summary>城市的中文顯示名（認不出來就原樣回傳）。</summary>
    public static string DisplayOf(string? city)
    {
        var code = Normalize(city);

        foreach (var (c, display, _) in Cities)
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return display;

        return code;
    }

    /// <summary>這是我們認識的城市嗎（不認識還是可以用，只是啟動時要提醒一下）。</summary>
    public static bool IsKnown(string? city)
        => Cities.Any(c => string.Equals(c.Code, Normalize(city), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 錯誤訊息用的說明（例：「臺中（Taichung）、臺南（Tainan）…」）。
    /// </summary>
    public static string SupportedList(int take = 6)
        => string.Join("、", Cities.Take(take).Select(c => $"{c.Display}（{c.Code}）")) +
           (Cities.Length > take ? " …" : "");
}
