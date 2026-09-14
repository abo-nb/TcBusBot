namespace TcBusBot.Core.Bus;

/// <summary>
/// 使用者定義的「一個地點」＝ 一組候選站牌。
///
/// 路線匹配**只認這個型別**，不關心候選站牌是怎麼來的
/// （第一階段：模糊搜尋；第二階段可能加上地圖座標 + 半徑）。
/// 未來的擴充點被隔離在「建立 LocationTarget」的階段，匹配邏輯完全不用改。
/// </summary>
public sealed class LocationTarget
{
    public string DisplayName { get; init; } = "";

    /// <summary>★ 唯一參與匹配的欄位。</summary>
    public IReadOnlyList<string> CandidateStopUids { get; init; } = Array.Empty<string>();

    public override string ToString()
        => DisplayName.Contains("個站牌", StringComparison.Ordinal)
            ? DisplayName
            : $"{DisplayName}（{CandidateStopUids.Count} 個候選站牌）";

    /// <summary>
    /// 建立顯示名稱。同名站牌會被去重（例如「靜宜大學(專用道)」有 3 個 StopUID，
    /// 只顯示一次並註明有幾個站牌），避免出現「靜宜大學、靜宜大學、靜宜大學」。
    /// </summary>
    public static string BuildDisplayName(IEnumerable<string> stopNames, int stopCount)
    {
        var names = stopNames.Where(n => !string.IsNullOrWhiteSpace(n))
                             .Distinct(StringComparer.Ordinal)
                             .ToList();

        var label = names.Count switch
        {
            0 => "（未設定）",
            <= 3 => string.Join("、", names),
            _ => $"{names[0]} 等 {names.Count} 種站名"
        };

        // 有多個站牌但站名相同時（同一個站區的不同月台）補上數量
        if (stopCount > names.Count && names.Count <= 3)
            label += $"（{stopCount} 個站牌）";

        return label;
    }
}

/// <summary>
/// 一個「候選上車站」選項：因為起點可能有多個候選站，同一條路線可能有多個合法的上車點。
/// 每一個 BoardChoice 之後都會變成一個獨立的訂閱（見 §8）。
/// </summary>
public sealed record BoardChoice(
    string BoardStopUid,
    string BoardStopName,
    int BoardSequence,
    string AlightStopUid,
    string AlightStopName,
    int AlightSequence)
{
    public int StopsBetween => AlightSequence - BoardSequence;
}

/// <summary>
/// 一條「可用路線」的查詢結果。**每條 (路線, 方向) 只會出現一次**，
/// 但可能帶著多個候選上車站（BoardChoices）。
/// </summary>
public sealed record RouteOption(
    string RouteUid,
    string RouteName,
    int Direction,
    string DirectionLabel,
    string Headsign,
    IReadOnlyList<BoardChoice> BoardChoices)
{
    /// <summary>顯示用的站數：以最早上車的那個候選站為準（第一時間可搭上的班次）。</summary>
    public BoardChoice Earliest => BoardChoices[0];

    public int StopsBetween => Earliest.StopsBetween;

    /// <summary>例：「300（返程）臺中車站(A月台) 或 干城站 上車 · 23 站」</summary>
    public string Describe()
    {
        var boards = string.Join(" 或 ", BoardChoices.Select(b => b.BoardStopName).Distinct());
        return $"{RouteName}（{DirectionLabel}）{boards} 上車 · {StopsBetween} 站";
    }
}
