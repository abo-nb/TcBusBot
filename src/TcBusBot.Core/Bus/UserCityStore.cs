using System.Text.Json;
using TcBusBot.Core.Chat;

namespace TcBusBot.Core.Bus;

/// <summary>
/// **每個使用者自己選公車城市**（`/bus city`）的儲存。
///
/// 為什麼是「按使用者」而不是按伺服器、也不是環境變數：
/// 同一個伺服器裡可能有人通勤看臺中、有人回老家看臺南。
/// 寫在環境變數只能整個行程一套（兩種站牌混在一起，使用者分不出哪個是哪個）；
/// 按伺服器則要整群人共用一個選擇。按使用者最貼近實際：
/// **誰要訂公車，就由誰決定要看哪個城市**。
///
/// 預設＝<see cref="Available"/> 的第一個（`BUS_CITY` 的第一個，通常是臺中），
/// 所以「沒選過」的人行為跟以前完全一樣。
///
/// 儲存沿用共用的 blob 區（與主人規則、每週用量同一條後端鏈）——重啟後還在。
/// </summary>
public sealed class UserCityStore
{
    /// <summary>持久化用的鍵名（與其他 blob 共用同一個儲存區）。</summary>
    public const string BlobKey = "user_cities";

    private ILlmStateStore? _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _byUser = new(StringComparer.Ordinal);

    public UserCityStore(IReadOnlyList<string> available, string? defaultCity = null)
    {
        Available = available.Count > 0 ? available : [BusCity.Default];
        Default = string.IsNullOrWhiteSpace(defaultCity)
            ? Available[0]
            : (Available.FirstOrDefault(c => string.Equals(c, BusCity.Normalize(defaultCity), StringComparison.OrdinalIgnoreCase))
               ?? Available[0]);

    }

    /// <summary>
    /// 接上儲存區（建立順序的關係：DI 容器比靜態資料晚建好，
    /// 所以先建立這個物件、之後再把儲存區接進來並讀取已存的選擇）。
    /// </summary>
    public void Attach(ILlmStateStore? store)
    {
        _store = store;
        Load();
    }

    /// <summary>這個行程**載入過資料**的城市（`BUS_CITY` 的內容）。</summary>
    public IReadOnlyList<string> Available { get; }

    /// <summary>沒選過的人用哪一個（`BUS_CITY` 的第一個）。</summary>
    public string Default { get; }

    /// <summary>只有一個城市時，「選城市」沒有意義（呼叫端可以據此少做一點事）。</summary>
    public bool HasChoice => Available.Count > 1;

    /// <summary>這個人目前選哪個城市。</summary>
    public string Get(ulong userId)
    {
        lock (_gate)
            return _byUser.TryGetValue(Key(userId), out var city) && Available.Contains(city, StringComparer.OrdinalIgnoreCase)
                ? city
                : Default;
    }

    /// <summary>這個人有沒有自己選過。</summary>
    public bool HasOwnChoice(ulong userId)
    {
        lock (_gate) return _byUser.ContainsKey(Key(userId));
    }

    /// <summary>設定這個人的城市。回傳 false＝這個城市不在可用清單裡（不會亂設）。</summary>
    public bool Set(ulong userId, string? city)
    {
        var code = BusCity.Normalize(city);

        var matched = Available.FirstOrDefault(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
        if (matched is null) return false;

        lock (_gate) _byUser[Key(userId)] = matched;

        Save();
        return true;
    }

    /// <summary>回到預設（把這個人的選擇忘掉）。</summary>
    public bool Clear(ulong userId)
    {
        lock (_gate)
        {
            if (!_byUser.Remove(Key(userId))) return false;
        }

        Save();
        return true;
    }

    /// <summary>有自己選過的人數（log／診斷用）。</summary>
    public int Count
    {
        get { lock (_gate) return _byUser.Count; }
    }

    /// <summary>給使用者看的一行說明。</summary>
    public string Describe(ulong userId)
        => HasChoice
            ? $"{BusCity.DisplayOf(Get(userId))}（可選：{BusCity.DisplayOfMany(Available)}）"
            : $"{BusCity.DisplayOf(Default)}（主機只載入了這一個城市）";

    private static string Key(ulong userId) => userId.ToString();

    private void Load()
    {
        if (_store is null) return;

        try
        {
            var json = _store.GetBlob(BlobKey);
            if (string.IsNullOrWhiteSpace(json)) return;

            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null) return;

            lock (_gate)
            {
                foreach (var (user, city) in parsed)
                {
                    var matched = Available.FirstOrDefault(c =>
                        string.Equals(c, BusCity.Normalize(city), StringComparison.OrdinalIgnoreCase));

                    // 只留「這個行程真的載入過」的城市：換了 BUS_CITY 之後舊的選擇會失效
                    if (matched is not null) _byUser[user] = matched;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[城市] 讀取失敗（沿用預設）：{ex.Message}");
        }
    }

    private void Save()
    {
        if (_store is null) return;

        try
        {
            lock (_gate) _store.SetBlob(BlobKey, JsonSerializer.Serialize(_byUser));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[城市] 儲存失敗：{ex.Message}");
        }
    }
}
