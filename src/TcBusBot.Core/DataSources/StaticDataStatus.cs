namespace TcBusBot.Core.DataSources;

using TcBusBot.Core.Bus;

/// <summary>一個城市的靜態資料目前是什麼狀態（給 `/health`、`/bus status` 與使用者的訊息用）。</summary>
public sealed record StaticCityState(
    string City,
    /// <summary>載入了幾個站牌（0 ＝ 還沒有資料可用）。</summary>
    int StopCount,
    /// <summary>這份資料是哪裡來的（人類看得懂的一句話）。</summary>
    string Source,
    /// <summary>資料可用了嗎（站牌數 &gt; 0）。</summary>
    bool Available,
    /// <summary>最後一次的錯誤（沒有就是 null）。</summary>
    string? Error = null,
    /// <summary>最後一次嘗試的時間。</summary>
    DateTimeOffset? LastAttemptAt = null,
    /// <summary>下一次自動重試的時間（沒有在重試就是 null）。</summary>
    DateTimeOffset? NextRetryAt = null,
    /// <summary>試過幾次。</summary>
    int Attempts = 0,
    /// <summary>還在新鮮期內嗎（false ＝ 用的是舊快取，或根本沒有資料）。</summary>
    bool Fresh = false)
{
    /// <summary>還需要背景重試嗎（沒有資料，或只有舊快取）。</summary>
    public bool NeedsRetry => !Fresh;

    /// <summary>
    /// 一行摘要（`/health` 與啟動訊息用）。
    ///
    /// ⚠️ 錯誤訊息要**截短**：這裡塞的是「為什麼抓不到」的完整說明（可能上百字），
    ///    而 `/health` 是給人一眼掃過去的地方（完整說明在 <see cref="DescribeForUser"/>）。
    /// </summary>
    public string Describe()
    {
        var data = Available ? $"{StopCount:N0} 個站牌" : "**還沒有資料**";
        var text = $"{BusCity.DisplayOf(City)}：{data}（{Source}）";

        if (Error is not null)
            text += Available ? $"　上次重試失敗：{Shorten(Error)}" : $"　⚠️ {Shorten(Error)}";

        if (NextRetryAt is { } next) text += $"　下次重試 {next.ToLocalTime():HH:mm:ss}";

        return text;
    }

    private static string Shorten(string text)
        => text.Length <= 60 ? text : text[..60] + "…";
}

/// <summary>
/// 「每個城市的靜態資料現在能不能用」的**唯一記錄**。
///
/// 為什麼需要它（而不是只看 <c>BusDataService.StopCount</c>）：
///   * 站牌數 0 有很多原因（TDX 429、金鑰錯、城市不認識、根本沒設金鑰），
///     訊息完全不同 —— 使用者需要知道是哪一種，還有「系統會不會自己好」
///   * `/health` 需要一個「資料就緒了嗎」的欄位（Render 的健康檢查與人工排查都靠它）
///   * 背景重試需要知道「哪幾個城市還沒好、試過幾次、下次什麼時候」
///
/// 執行緒安全：重試迴圈在背景執行緒寫、Discord 互動在別的執行緒讀。
/// </summary>
public sealed class StaticDataStatus
{
    private readonly Dictionary<string, StaticCityState> _byCity = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public StaticDataStatus(IEnumerable<string> cities)
    {
        foreach (var city in cities)
        {
            var code = BusCity.Normalize(city);
            _byCity[code] = new StaticCityState(code, 0, "尚未載入", Available: false);
        }
    }

    /// <summary>所有城市的狀態（依原本的順序）。</summary>
    public IReadOnlyList<StaticCityState> All
    {
        get { lock (_gate) return _byCity.Values.ToList(); }
    }

    public StaticCityState For(string city)
    {
        var code = BusCity.Normalize(city);

        lock (_gate)
            return _byCity.TryGetValue(code, out var state)
                ? state
                : new StaticCityState(code, 0, "未設定", Available: false);
    }

    /// <summary>有城市還沒好。</summary>
    public bool AnyMissing
        => All.Any(s => !s.Available);

    /// <summary>還有城市需要（或正在）背景重試。</summary>
    public bool AnyNeedsRetry
        => All.Any(s => s.NeedsRetry);

    /// <summary>這個人用的那個城市的資料可以用了嗎（Discord 那一層的守門用）。</summary>
    public bool Ready(string city) => For(city).Available;

    /// <summary>更新一個城市的狀態。</summary>
    public void Report(StaticCityState state)
    {
        lock (_gate) _byCity[BusCity.Normalize(state.City)] = state with { City = BusCity.Normalize(state.City) };
    }

    /// <summary>一行摘要（`/health` 與啟動訊息用）。</summary>
    public string Describe()
        => All.Count == 0
            ? "（沒有設定城市）"
            : string.Join("｜", All.Select(s => s.Describe()));

    /// <summary>
    /// 給使用者看的一句話：**為什麼現在查不到、以及會不會自己好**。
    ///
    /// 這一句很重要：資料沒好卻讓使用者看到「找不到站牌」，他會以為是站名打錯
    /// 或 Bot 壞掉，於是反覆重試、甚至放棄使用 —— 必須直接說清楚。
    /// </summary>
    public string DescribeForUser(string city)
    {
        var state = For(city);
        var name = BusCity.DisplayOf(state.City);

        // 有資料但只有舊快取／最小資料集 → 不要說「還沒好」（查詢其實能用），
        // 但要說清楚「可能不是最新、系統正在重試」
        if (state.Available && !state.Fresh)
        {
            return $"ℹ️ {name}的公車資料目前用的是 **{state.Source}**，" +
                   "可能不是最新的 —— 系統會在背景自動重試取得完整資料，不需要重啟。" +
                   (state.Error is null ? "" : $"\n（上次失敗：{state.Error}）") +
                   "\n管理員可以用 `/bus status` 看目前狀態。";
        }

        var lines = new List<string>
        {
            $"⚠️ **{name}的公車資料還沒載入完成**",
            state.Error is null ? "（主機還沒抓到資料）" : $"原因：{state.Error}"
        };

        lines.Add(state.NextRetryAt is { } next
            ? $"系統會在 **{next.ToLocalTime():HH:mm}** 自動再試一次；資料一好就會正常查詢，**不需要重啟**。"
            : "目前沒有自動重試（主機可能沒有設定 TDX 金鑰）—— 請管理員確認 `TDX_CLIENT_ID`／`TDX_CLIENT_SECRET`。");

        lines.Add("管理員可以用 `/bus status` 看目前狀態。");

        return string.Join("\n", lines);
    }
}
