using System.Globalization;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Models;

namespace TcBusBot.Core.DataSources;

/// <summary>
/// 讀取「最小驗收資料集」（PSV 格式，用 '|' 分隔）。
///
/// 為什麼需要它：真實的 StopOfRoute/City/Taichung 約 751~765 筆、Stop 超過 5,000 筆，
/// 沒有 TDX 金鑰時抓不到。這份最小資料集只涵蓋「臺中車站 → 靜宜大學」走廊，
/// 足以在**完全離線**的情況下驗證搜尋、匹配與整個 Discord 互動流程。
/// </summary>
public static class MiniFixtureSource
{
    public const string StopsFile = "mini/Stops.psv";
    public const string StopOfRouteFile = "mini/StopOfRoute.psv";

    /// <summary>
    /// 從「目前工作目錄」與「執行檔目錄」往上找 tests/fixtures。
    /// 兩者都要找：從專案根目錄執行時靠前者，從 bin/ 執行時靠後者。
    /// </summary>
    public static string ResolveRoot(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath))
            return explicitPath!;

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = start;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "tests", "fixtures");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tests/fixtures。請用 --fixtures <路徑> 指定，或在專案根目錄執行。");
    }

    public static BusDataService Load(string fixturesRoot)
    {
        var stops = LoadStops(Path.Combine(fixturesRoot, StopsFile.Replace('/', Path.DirectorySeparatorChar)));
        var (stopOfRoutes, routes) = LoadStopOfRoutes(
            Path.Combine(fixturesRoot, StopOfRouteFile.Replace('/', Path.DirectorySeparatorChar)));

        var svc = new BusDataService();
        svc.Load(stops, stopOfRoutes, routes);
        return svc;
    }

    private static List<BusStop> LoadStops(string path)
    {
        var list = new List<BusStop>();
        foreach (var f in ReadRows(path))
        {
            list.Add(new BusStop
            {
                StopUID = f[0],
                StopName = new LocalizedName(f[1], f.Count > 5 ? f[5] : null),
                StopPosition = new StopPosition(Num(f[2]), Num(f[3]), null),
                StationID = f.Count > 4 ? f[4] : null,
                StationGroupID = f.Count > 4 ? f[4] : null,
                City = "Taichung",
                CityCode = "TXG"
            });
        }
        return list;
    }

    private static (List<BusStopOfRoute>, List<BusRoute>) LoadStopOfRoutes(string path)
    {
        var sors = new List<BusStopOfRoute>();
        var routes = new Dictionary<string, BusRoute>(StringComparer.Ordinal);

        foreach (var f in ReadRows(path))
        {
            var routeUid = f[0];
            var routeId = f[1];
            var routeName = f[2];
            var direction = int.Parse(f[3], CultureInfo.InvariantCulture);
            var headsign = f.Count > 4 ? f[4] : routeName;
            var uids = (f.Count > 5 ? f[5] : "").Split(',', StringSplitOptions.RemoveEmptyEntries);

            sors.Add(new BusStopOfRoute
            {
                RouteUID = routeUid,
                RouteID = routeId,
                RouteName = new LocalizedName(routeName, null),
                SubRouteUID = routeUid,
                SubRouteID = routeId,
                SubRouteName = new LocalizedName(routeName, null),
                Direction = direction,
                City = "Taichung",
                CityCode = "TXG",
                Stops = uids.Select((uid, i) => new StopOfRouteStop
                {
                    StopUID = uid,
                    StopSequence = i + 1
                }).ToList()
            });

            if (!routes.TryGetValue(routeUid, out var route))
            {
                route = new BusRoute
                {
                    RouteUID = routeUid,
                    RouteID = routeId,
                    RouteName = new LocalizedName(routeName, null),
                    HasSubRoutes = true,
                    City = "Taichung",
                    CityCode = "TXG",
                    SubRoutes = new List<BusSubRoute>()
                };
                routes[routeUid] = route;
            }

            route.SubRoutes.Add(new BusSubRoute
            {
                SubRouteUID = routeUid,
                SubRouteID = routeId,
                SubRouteName = new LocalizedName(routeName, null),
                Direction = direction,
                Headsign = headsign
            });
        }

        return (sors, routes.Values.ToList());
    }

    private static IEnumerable<List<string>> ReadRows(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd('\r', '\n');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            yield return line.Split('|').Select(x => x.Trim()).ToList();
        }
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
