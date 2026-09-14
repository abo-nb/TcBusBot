using TcBusBot.Core.Models;

namespace TcBusBot.Core.Bus;

/// <summary>
/// 「建議群組」——把同一個站區的多個站牌合成一組，讓使用者一鍵勾選。
///
/// 為什麼需要兩段式（前綴正規化 + 座標叢集）：
///   * 只用站名完全相等 → 會漏掉「臺中車站(臺灣大道)」之類的別名
///   * 只用座標距離     → 會把 340 公尺外的「干城站」錯誤併入「臺中車站」
/// </summary>
public sealed record StopAreaGroup(
    string GroupKey,                       // 正規化後的鍵（內部用）
    string DisplayName,                    // 人類看的站區名（保留原始用字，例：「臺中車站」）
    IReadOnlyList<string> StopUids,        // 群組內全部 StopUID
    double Lon,
    double Lat)
{
    /// <summary>放進 Discord custom_id 用的短鍵：群組內字典序最小的 StopUID。</summary>
    public string ShortKey => StopUids.Count == 0
        ? GroupKey
        : StopUids.OrderBy(u => u, StringComparer.Ordinal).First();
}

public static class StopAreaGrouping
{
    public const double DefaultClusterMeters = 500;

    public static (List<StopAreaGroup> Groups, Dictionary<string, string> GroupKeyByStopUid)
        Build(IEnumerable<BusStop> stops, double clusterMeters = DefaultClusterMeters)
    {
        var byStopUid = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<StopAreaGroup>();

        var buckets = stops
            .Where(s => !string.IsNullOrEmpty(s.StopUID))
            .GroupBy(s => StopNameNormalizer.ForGroup(s.ZhTwName), StringComparer.Ordinal);

        foreach (var bucket in buckets)
        {
            if (string.IsNullOrEmpty(bucket.Key)) continue;

            // 貪婪叢集：以「群組內第一個點」為基準，之後每個點找最近的既有叢集
            var clusters = new List<(List<BusStop> Members, double SumLat, double SumLon)>();

            foreach (var stop in bucket.OrderBy(s => s.StopUID, StringComparer.Ordinal))
            {
                var placed = false;
                for (var i = 0; i < clusters.Count; i++)
                {
                    var c = clusters[i];
                    var cLat = c.SumLat / c.Members.Count;
                    var cLon = c.SumLon / c.Members.Count;
                    if (Geo.HaversineMeters(cLat, cLon, stop.Lat, stop.Lon) > clusterMeters) continue;

                    c.Members.Add(stop);
                    clusters[i] = (c.Members, c.SumLat + stop.Lat, c.SumLon + stop.Lon);
                    placed = true;
                    break;
                }
                if (!placed)
                    clusters.Add((new List<BusStop> { stop }, stop.Lat, stop.Lon));
            }

            foreach (var c in clusters)
            {
                var uids = c.Members.Select(m => m.StopUID).OrderBy(u => u, StringComparer.Ordinal).ToList();
                var group = new StopAreaGroup(
                    GroupKey: bucket.Key,
                    DisplayName: HumanName(c.Members),
                    StopUids: uids,
                    Lon: c.SumLon / c.Members.Count,
                    Lat: c.SumLat / c.Members.Count);

                result.Add(group);
                foreach (var uid in uids) byStopUid[uid] = group.ShortKey;
            }
        }

        return (result, byStopUid);
    }

    /// <summary>取「最常見的、去掉括號後的原始站名」當顯示名。</summary>
    private static string HumanName(List<BusStop> members)
    {
        var names = members
            .Select(m =>
            {
                var raw = m.ZhTwName;
                var idx = raw.IndexOfAny(['(', '（']);
                return (idx >= 0 ? raw[..idx] : raw).Trim();
            })
            .Where(n => n.Length > 0)
            .ToList();

        return names.Count == 0
            ? members[0].ZhTwName
            : names.GroupBy(n => n, StringComparer.Ordinal)
                   .OrderByDescending(g => g.Count())
                   .ThenBy(g => g.Key.Length)
                   .First().Key;
    }
}
