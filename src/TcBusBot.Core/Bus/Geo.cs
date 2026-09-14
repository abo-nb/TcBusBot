namespace TcBusBot.Core.Bus;

/// <summary>
/// 地理計算。第一階段只用於「同名站牌是否屬於同一個站區」的叢集判斷，
/// 以及（第二階段的）附近站牌查詢。
/// 全部是本地計算：**零 API 呼叫、零 TDX 點數**。
/// </summary>
public static class Geo
{
    private const double EarthRadiusMeters = 6_371_008.8;

    /// <summary>Haversine 直線距離（公尺）。注意這是直線距離，不是步行/路網距離。</summary>
    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = double.DegreesToRadians(lat2 - lat1);
        var dLon = double.DegreesToRadians(lon2 - lon1);
        var rLat1 = double.DegreesToRadians(lat1);
        var rLat2 = double.DegreesToRadians(lat2);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(rLat1) * Math.Cos(rLat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

/// <summary>
/// 「路線名稱自然排序」：讓 30 排在 300 前面、2 排在 10 前面。
/// 直接字串排序會得到 "10" &lt; "2" &lt; "30" &lt; "300"，不是使用者預期的順序。
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                // 抓出兩邊的連續數字並以數值比較（忽略前導零）
                var si = i; while (i < x.Length && char.IsDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsDigit(y[j])) j++;

                var nx = x.AsSpan(si, i - si).TrimStart('0');
                var ny = y.AsSpan(sj, j - sj).TrimStart('0');

                if (nx.Length != ny.Length) return nx.Length - ny.Length;
                var cmp = nx.SequenceCompareTo(ny);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = x[i].CompareTo(y[j]);
                if (cmp != 0) return cmp;
                i++; j++;
            }
        }
        return (x.Length - i) - (y.Length - j);
    }
}
