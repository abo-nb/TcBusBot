using TcBusBot.Core.Models;

namespace TcBusBot.Core.Bus;

/// <summary>某個 StopUID 在某條路線某個方向上的出現位置。</summary>
public readonly record struct StopOccurrence(string RouteUid, int Direction, int Sequence);

/// <summary>一條 (路線, 方向) 的站序。Stops 已依 StopSequence 排序。</summary>
internal sealed class TripStops
{
    public required string RouteUid { get; init; }
    public required string RouteName { get; init; }
    public required int Direction { get; init; }
    public required string Headsign { get; init; }
    public required string[] StopUids { get; init; }
    public required string[] StopNames { get; init; }
    public required Dictionary<string, int> FirstSeqByStopUid { get; init; }

    /// <summary>
    /// 站名（正規化後）→ 第一次出現的站序。
    ///
    /// 為什麼要有「按站名」的索引：台中同一條路上常有**同名但不同 StopUID** 的月台
    /// （去回程各登記一次、專用道與慢車道各一組），所以「轉乘」用 UID 比對會找不到
    /// （A 路線停在 TXG19265、B 路線停在 TXG1865，其實是同一個路口）。
    /// 用人類的方式比對（站名）才找得到合理的轉乘點。
    /// </summary>
    public required Dictionary<string, int> FirstSeqByNameKey { get; init; }

    public required string[] NameKeys { get; init; }

    /// <summary>StopSequence → StopNames／NameKeys 的索引（TDX 的站序不保證從 0 或 1 開始）。</summary>
    public required Dictionary<int, int> IndexBySeq { get; init; }

    /// <summary>依站序排好的 StopSequence（與 StopNames／NameKeys 同一個順序）。</summary>
    public required int[] StopSequences { get; init; }
}

/// <summary>
/// 台中公車靜態資料的記憶體索引。
///
/// 啟動時呼叫 <see cref="Load"/> 一次，之後所有查詢都是純記憶體運算：
///   * 模糊站牌搜尋     → 線性掃描 + 排序
///   * 候選集合路線匹配 → 倒排索引 + 站序比較
/// **建索引之後完全不需要再打 TDX 靜態資料 API。**
/// </summary>
public sealed class BusDataService
{
    private readonly Dictionary<(string RouteUid, int Direction), TripStops> _trips = new();
    private readonly Dictionary<string, List<StopOccurrence>> _byStopUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _routeCountByStopUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _displayNameByStopUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StopAreaGroup> _groupByShortKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _groupKeyByStopUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BusRoute> _routeMeta = new(StringComparer.Ordinal);
    private readonly List<StopAreaGroup> _stopGroups = new();
    private StopSearchEntry[] _searchEntries = Array.Empty<StopSearchEntry>();

    public StopSearchService Search { get; private set; } = new(Array.Empty<StopSearchEntry>());
    public IReadOnlyList<StopAreaGroup> StopGroups => _stopGroups;
    public int TripCount => _trips.Count;
    public int StopCount => _displayNameByStopUid.Count;

    /// <summary>
    /// 資料集裡「最有名」的幾個站名（依經過路線數排序）。
    /// 用來在搜尋不到時給使用者可用的範例 —— 小資料集時特別重要。
    /// </summary>
    public IReadOnlyList<string> ExampleStopNames(int count = 6)
        => _searchEntries
            .OrderByDescending(e => e.RouteCount)
            .ThenBy(e => e.DisplayName.Length)
            .Select(e => e.DisplayName)
            .Distinct(StringComparer.Ordinal)
            .Take(count)
            .ToList();

    public void Load(
        IEnumerable<BusStop> stops,
        IEnumerable<BusStopOfRoute> stopOfRoutes,
        IEnumerable<BusRoute> routes,
        double clusterMeters = StopAreaGrouping.DefaultClusterMeters)
    {
        _trips.Clear(); _byStopUid.Clear(); _routeCountByStopUid.Clear();
        _displayNameByStopUid.Clear(); _groupByShortKey.Clear();
        _groupKeyByStopUid.Clear(); _routeMeta.Clear(); _stopGroups.Clear();

        foreach (var r in routes)
            if (!string.IsNullOrEmpty(r.RouteUID)) _routeMeta[r.RouteUID] = r;

        var stopList = stops.Where(s => !string.IsNullOrEmpty(s.StopUID)).ToList();

        // ── 1) 路線站序索引 ───────────────────────────────
        foreach (var sor in stopOfRoutes)
        {
            if (string.IsNullOrEmpty(sor.RouteUID)) continue;

            var ordered = sor.Stops.OrderBy(s => s.StopSequence).ToList();
            if (ordered.Count == 0) continue;

            var firstSeq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var s in ordered)
                firstSeq.TryAdd(s.StopUID, s.StopSequence);   // 環狀路線同站重複出現時取第一次

            // 站名索引（給「轉乘」用：同名不同月台也要算同一個轉乘點）
            var nameKeys = ordered.Select(s => StopNameNormalizer.ForSearch(s.ZhTwName)).ToArray();
            var firstSeqByName = new Dictionary<string, int>(StringComparer.Ordinal);
            var indexBySeq = new Dictionary<int, int>();

            for (var i = 0; i < nameKeys.Length; i++)
            {
                firstSeqByName.TryAdd(nameKeys[i], ordered[i].StopSequence);
                indexBySeq.TryAdd(ordered[i].StopSequence, i);
            }

            var key = (sor.RouteUID, sor.Direction);
            var trip = new TripStops
            {
                RouteUid = sor.RouteUID,
                RouteName = sor.RouteZhTwName,
                Direction = sor.Direction,
                Headsign = ResolveHeadsign(sor),
                StopUids = ordered.Select(s => s.StopUID).ToArray(),
                StopNames = ordered.Select(s => s.ZhTwName).ToArray(),
                FirstSeqByStopUid = firstSeq,
                FirstSeqByNameKey = firstSeqByName,
                NameKeys = nameKeys,
                IndexBySeq = indexBySeq,
                StopSequences = ordered.Select(s => s.StopSequence).ToArray()
            };
            _trips[key] = trip;

            foreach (var s in ordered)
            {
                if (!_byStopUid.TryGetValue(s.StopUID, out var list))
                    _byStopUid[s.StopUID] = list = new List<StopOccurrence>();
                list.Add(new StopOccurrence(sor.RouteUID, sor.Direction, s.StopSequence));

                _displayNameByStopUid.TryAdd(s.StopUID, s.ZhTwName);
            }
        }

        foreach (var kv in _byStopUid)
            _routeCountByStopUid[kv.Key] = kv.Value.Count;

        // ── 2) 站牌清單（Stop 為主，缺的用 StopOfRoute 補）────
        var merged = new Dictionary<string, BusStop>(StringComparer.Ordinal);
        foreach (var s in stopList) merged[s.StopUID] = s;

        foreach (var sor in stopOfRoutes)
        {
            foreach (var s in sor.Stops)
            {
                if (merged.ContainsKey(s.StopUID)) continue;
                merged[s.StopUID] = new BusStop
                {
                    StopUID = s.StopUID,
                    StopID = s.StopID,
                    StopName = s.StopName,
                    StopPosition = s.StopPosition,
                    StationID = s.StationID,
                    StationGroupID = s.StationGroupID
                };
            }
        }

        var allStops = merged.Values.ToList();
        foreach (var s in allStops)
            _displayNameByStopUid[s.StopUID] = s.ZhTwName;

        // ── 2b) 站序裡的站名補齊 ──────────────────────────
        // ⚠️ 不是每份資料的 StopOfRoute 都帶站名（我們的離線 fixture 就只有 StopUID），
        //    而「路線查詢」「轉乘建議」都是拿站序裡的站名在講話 ——
        //    少了這一步，那些功能會吐出 TXG13567 這種代號，模型也看不懂。
        //    站名統一到 _displayNameByStopUid（Stop 為主、StopOfRoute 為輔）。
        foreach (var trip in _trips.Values)
        {
            trip.FirstSeqByNameKey.Clear();

            for (var i = 0; i < trip.StopNames.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(trip.StopNames[i])
                    && _displayNameByStopUid.TryGetValue(trip.StopUids[i], out var name))
                    trip.StopNames[i] = name;

                trip.NameKeys[i] = StopNameNormalizer.ForSearch(trip.StopNames[i]);
                trip.FirstSeqByNameKey.TryAdd(trip.NameKeys[i], trip.StopSequences[i]);
            }
        }

        // ── 3) 建議群組 ───────────────────────────────────
        var (groups, groupKeyByStopUid) = StopAreaGrouping.Build(allStops, clusterMeters);
        _stopGroups.AddRange(groups);
        foreach (var kv in groupKeyByStopUid) _groupKeyByStopUid[kv.Key] = kv.Value;
        foreach (var g in groups) _groupByShortKey[g.ShortKey] = g;

        // ── 4) 模糊搜尋索引 ───────────────────────────────
        var entries = allStops.Select(s => new StopSearchEntry(
            StopUid: s.StopUID,
            SearchKey: StopNameNormalizer.ForSearch(s.ZhTwName),
            SearchKeyEn: StopNameNormalizer.ForSearch(s.EnName),
            DisplayName: s.ZhTwName,
            GroupKey: _groupKeyByStopUid.TryGetValue(s.StopUID, out var gk) ? gk : s.StopUID,
            RouteCount: _routeCountByStopUid.TryGetValue(s.StopUID, out var rc) ? rc : 0,
            Lon: s.Lon,
            Lat: s.Lat)).ToArray();

        _searchEntries = entries;
        Search = new StopSearchService(entries);
    }

    private string ResolveHeadsign(BusStopOfRoute sor)
    {
        if (_routeMeta.TryGetValue(sor.RouteUID, out var meta))
        {
            var sub = meta.SubRoutes.FirstOrDefault(x => x.Direction == sor.Direction
                                                        && !string.IsNullOrWhiteSpace(x.Headsign));
            if (sub?.Headsign is { Length: > 0 } h) return h;

            if (!string.IsNullOrWhiteSpace(meta.DepartureStopNameZh) &&
                !string.IsNullOrWhiteSpace(meta.DestinationStopNameZh))
                return $"{meta.DepartureStopNameZh} - {meta.DestinationStopNameZh}";
        }
        return sor.RouteZhTwName;
    }

    public string GetDisplayName(string stopUid)
        => _displayNameByStopUid.TryGetValue(stopUid, out var n) ? n : stopUid;

    public StopAreaGroup? GetGroupByShortKey(string shortKey)
        => _groupByShortKey.TryGetValue(shortKey, out var g) ? g : null;

    public int GetRouteCount(string stopUid)
        => _routeCountByStopUid.TryGetValue(stopUid, out var c) ? c : 0;

    /// <summary>這個站牌屬於哪一個建議群組（回傳群組的 ShortKey；找不到時回傳 StopUID 本身）。</summary>
    public string GetGroupKeyForStop(string stopUid)
        => _groupKeyByStopUid.TryGetValue(stopUid, out var k) ? k : stopUid;

    /// <summary>從建議群組建立一個 LocationTarget（UI 一鍵勾選整組時用）。</summary>
    public LocationTarget TargetFromGroup(StopAreaGroup group)
        => new()
        {
            DisplayName = group.DisplayName,
            CandidateStopUids = group.StopUids.ToArray()
        };

    /// <summary>從一串 StopUID 建立 LocationTarget，顯示名自動以站名組出（同名會去重）。</summary>
    public LocationTarget TargetFromStops(IEnumerable<string> stopUids)
    {
        var uids = stopUids.Distinct(StringComparer.Ordinal).ToArray();
        return new LocationTarget
        {
            DisplayName = LocationTarget.BuildDisplayName(
                uids.Select(GetDisplayName), uids.Length),
            CandidateStopUids = uids
        };
    }

    /// <summary>
    /// 把一個建議群組依「站名」拆解成**穩定順序**的清單（依首個 StopUID 排序）。
    ///
    /// 為什麼需要：Discord 的 Select 選項 value 上限是 100 字元。
    /// 「國立臺中科技大學」有 **44 個同名站牌**（每個路線方向各自登記一個 StopUID），
    /// 全部串進 value 會約 400 字元而被截斷 —— 截斷後的候選集合會少掉大半，
    /// 造成「明明有 11 條路線卻只找到 1 條」（實際踩過）。
    ///
    /// 所以選項的 value 改用短鍵 `n:{群組ShortKey}:{索引}`，由這裡還原。
    /// </summary>
    public IReadOnlyList<(string Name, IReadOnlyList<string> StopUids, int RouteCount)>
        GetGroupNameBreakdown(string groupShortKey)
    {
        if (!_groupByShortKey.TryGetValue(groupShortKey, out var group))
            return Array.Empty<(string, IReadOnlyList<string>, int)>();

        return group.StopUids
            .OrderBy(u => u, StringComparer.Ordinal)
            .GroupBy(u => GetDisplayName(u), StringComparer.Ordinal)
            .Select(g => (
                Name: g.Key,
                StopUids: (IReadOnlyList<string>)g.OrderBy(u => u, StringComparer.Ordinal).ToList(),
                RouteCount: g.Max(GetRouteCount)))
            .OrderBy(x => x.StopUids[0], StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>由 `n:{群組ShortKey}:{索引}` 還原出該站名的所有 StopUID。</summary>
    public IReadOnlyList<string> GetGroupNameStopUids(string groupShortKey, int index)
    {
        var breakdown = GetGroupNameBreakdown(groupShortKey);
        return index >= 0 && index < breakdown.Count
            ? breakdown[index].StopUids
            : Array.Empty<string>();
    }

    // ─────────────────────────────────────────────────────
    //  診斷：為什麼某些路線沒有被找出來
    // ─────────────────────────────────────────────────────

    /// <summary>一條路線為什麼被排除。</summary>
    public sealed record RejectedRoute(string RouteName, string RouteUid, int Direction, string Reason);

    /// <summary>一次「起點 → 目的地」匹配的完整診斷。</summary>
    public sealed record MatchDiagnosis(
        int OriginStopCount,
        int DestinationStopCount,
        int TripsServingOrigin,
        int TripsServingBoth,
        int TripsCorrectDirection,
        IReadOnlyList<RouteOption> Matched,
        IReadOnlyList<RejectedRoute> Rejected);

    /// <summary>
    /// 跟 <see cref="FindRoutes"/> 做一樣的掃描，但**記錄每個被排除的原因**。
    /// 用途：使用者說「明明有這條路線卻找不到」時，能一眼看出是哪一關卡掉的。
    /// </summary>
    public MatchDiagnosis Diagnose(LocationTarget origin, LocationTarget destination, int maxRejected = 40)
    {
        var servingOrigin = new HashSet<(string RouteUid, int Direction)>();
        foreach (var uid in origin.CandidateStopUids)
            if (_byStopUid.TryGetValue(uid, out var occs))
                foreach (var o in occs) servingOrigin.Add((o.RouteUid, o.Direction));

        var both = 0;
        var correct = 0;
        var matched = new List<RouteOption>();
        var rejected = new List<RejectedRoute>();

        foreach (var key in servingOrigin)
        {
            if (!_trips.TryGetValue(key, out var trip)) continue;

            var destHits = destination.CandidateStopUids
                .Where(u => trip.FirstSeqByStopUid.ContainsKey(u))
                .Select(u => (Uid: u, Seq: trip.FirstSeqByStopUid[u]))
                .OrderBy(x => x.Seq)
                .ToList();

            if (destHits.Count == 0)
            {
                if (rejected.Count < maxRejected)
                    rejected.Add(new RejectedRoute(trip.RouteName, key.RouteUid, key.Direction,
                        "這條路線沒有停靠任何目的地候選站"));
                continue;
            }

            both++;

            var boards = new List<BoardChoice>();
            foreach (var b in origin.CandidateStopUids
                         .Where(u => trip.FirstSeqByStopUid.ContainsKey(u))
                         .Select(u => (Uid: u, Seq: trip.FirstSeqByStopUid[u]))
                         .OrderBy(x => x.Seq))
            {
                var alight = destHits.FirstOrDefault(x => x.Uid != b.Uid && x.Seq > b.Seq);
                if (alight.Uid is null) continue;

                boards.Add(new BoardChoice(b.Uid, GetDisplayName(b.Uid), b.Seq,
                                           alight.Uid, GetDisplayName(alight.Uid), alight.Seq));
            }

            if (boards.Count == 0)
            {
                // 兩站都在這條路線上，但上車站全部在目的地之後 → 方向相反
                var originSeq = trip.FirstSeqByStopUid[origin.CandidateStopUids.First(u => trip.FirstSeqByStopUid.ContainsKey(u))];
                var destSeq = destHits[0].Seq;
                var detail = $"方向相反（上車站第 {originSeq} 站、目的地第 {destSeq} 站）";

                if (rejected.Count < maxRejected)
                    rejected.Add(new RejectedRoute(trip.RouteName, key.RouteUid, key.Direction, detail));
                continue;
            }

            correct++;
            matched.Add(new RouteOption(key.RouteUid, trip.RouteName, key.Direction,
                key.Direction switch { 0 => "去程", 1 => "返程", 2 => "迴圈", _ => "未知" },
                trip.Headsign, boards));
        }

        matched.Sort((a, b) =>
        {
            var c = a.StopsBetween.CompareTo(b.StopsBetween);
            return c != 0 ? c : NaturalComparer.Instance.Compare(a.RouteName, b.RouteName);
        });

        return new MatchDiagnosis(
            origin.CandidateStopUids.Count, destination.CandidateStopUids.Count,
            servingOrigin.Count, both, correct, matched, rejected);
    }

    /// <summary>某個站牌在某條路線某個方向上的站序（找不到時回傳 null）。</summary>
    public int? GetSequence(string routeUid, int direction, string stopUid)
        => _trips.TryGetValue((routeUid, direction), out var trip)
           && trip.FirstSeqByStopUid.TryGetValue(stopUid, out var seq)
            ? seq
            : null;

    /// <summary>某個站名對應到的所有 StopUID（診斷用）。</summary>
    public IReadOnlyList<string> FindStopUidsByName(string name)
    {
        var key = StopNameNormalizer.ForSearch(name);
        return _searchEntries
            .Where(e => e.SearchKey == key)
            .Select(e => e.StopUid)
            .ToList();
    }

    /// <summary>這個站牌被哪些 (路線, 方向) 經過。</summary>
    public IReadOnlyList<StopOccurrence> GetOccurrences(string stopUid)
        => _byStopUid.TryGetValue(stopUid, out var list) ? list : Array.Empty<StopOccurrence>();

    public string GetRouteName(string routeUid)
        => _routeMeta.TryGetValue(routeUid, out var r) ? r.RouteZhTwName : routeUid;

    public string GetHeadsign(string routeUid, int direction)
        => _trips.TryGetValue((routeUid, direction), out var trip) ? trip.Headsign : "";

    // ─────────────────────────────────────────────────────
    //  依「路線號碼」查（使用者常常只知道號碼）
    // ─────────────────────────────────────────────────────

    /// <summary>一條 (路線, 方向) 的摘要（給「查路線號碼」用）。</summary>
    public sealed record RouteSummary(
        string RouteUid,
        string RouteName,
        int Direction,
        string Headsign,
        int StopCount,
        string FirstStopName,
        string LastStopName)
    {
        public string Describe()
            => $"{RouteName}（{(Direction == 0 ? "去程" : "返程")}）{Headsign}｜共 {StopCount} 站｜" +
               $"{FirstStopName} → {LastStopName}";

        /// <summary>關鍵站（給模型看的：頭尾＋中間抽樣）。</summary>
        public IReadOnlyList<string> SampleStops { get; init; } = [];
    }

    /// <summary>
    /// 用路線號碼找路線（「300」「304」「藍1」「5」…）。
    ///
    /// 為什麼需要：使用者最常說的是「300 多久來一班」，而不是站名。
    /// 站牌搜尋（<see cref="Search"/>）只找得到站，找不到路線。
    /// </summary>
    public IReadOnlyList<RouteSummary> FindRoutesByNumber(string number, int max = 12)
    {
        var q = StopNameNormalizer.ForSearch(number ?? "");
        if (q.Length == 0) return Array.Empty<RouteSummary>();

        var results = new List<RouteSummary>();

        foreach (var trip in _trips.Values)
        {
            var nameKey = StopNameNormalizer.ForSearch(trip.RouteName);

            // 完全相符優先，其次前綴（打「3」不要把 300/304/35 全部倒出來時就靠這個排序）
            if (!nameKey.Contains(q, StringComparison.Ordinal)) continue;

            var sample = trip.StopNames.Length <= 6
                ? trip.StopNames
                : new[]
                {
                    trip.StopNames[0],
                    trip.StopNames[trip.StopNames.Length / 2],
                    trip.StopNames[^1]
                };

            results.Add(new RouteSummary(
                RouteUid: trip.RouteUid,
                RouteName: trip.RouteName,
                Direction: trip.Direction,
                Headsign: trip.Headsign,
                StopCount: trip.StopNames.Length,
                FirstStopName: trip.StopNames[0],
                LastStopName: trip.StopNames[^1])
            {
                SampleStops = sample
            });
        }

        return results
            .OrderBy(r => StopNameNormalizer.ForSearch(r.RouteName) == q ? 0 : 1)
            .ThenBy(r => r.RouteName, StringComparer.Ordinal)
            .ThenBy(r => r.Direction)
            .Take(Math.Max(1, max))
            .ToList();
    }

    // ─────────────────────────────────────────────────────
    //  轉乘（沒有直達時）
    // ─────────────────────────────────────────────────────

    /// <summary>一次轉乘的建議。</summary>
    public sealed record TransferOption(
        string RouteNameA, int DirectionA, string BoardStopName,
        string TransferStopName,
        string RouteNameB, int DirectionB, string AlightStopName,
        int StopsToTransfer, int StopsAfterTransfer, string RouteUidA, string RouteUidB)
    {
        public int TotalStops => StopsToTransfer + StopsAfterTransfer;

        public string Describe()
            => $"搭 {RouteNameA}（{(DirectionA == 0 ? "去程" : "返程")}）在 {BoardStopName} 上車 → " +
               $"在 {TransferStopName} 下車（{StopsToTransfer} 站），" +
               $"轉 {RouteNameB}（{(DirectionB == 0 ? "去程" : "返程")}）→ 在 {AlightStopName} 下車" +
               $"（{StopsAfterTransfer} 站）";
    }

    /// <summary>
    /// 找「轉一次」的走法（沒有直達路線時用）。
    ///
    /// ⚠️ **轉乘點用站名比對，不是 StopUID** —— 台中同一條路上常有同名不同 UID 的月台
    /// （去回程各一組、專用道與慢車道各一組），用 UID 比對幾乎找不到轉乘點，
    /// 但用站名比對就完全符合「同一個路口換車」的直覺。
    ///
    /// 成本刻意壓在很小的範圍：起點可搭的路線取前 <paramref name="maxLegA"/> 條、
    /// 每條最多看 <see cref="MaxStopsPerLeg"/> 站、目的地候選站全部（通常 ≤ 25）。
    /// 這是「幫忙找一個可行方案」，不是完整的最短路徑演算法 ——
    /// 回傳的選項依「總站數」排序，越短的越可能合理。
    /// </summary>
    public IReadOnlyList<TransferOption> FindTransferRoutes(
        LocationTarget origin, LocationTarget destination, int max = 3, int maxLegA = 10)
    {
        if (origin.CandidateStopUids.Count == 0 || destination.CandidateStopUids.Count == 0)
            return Array.Empty<TransferOption>();

        // 目的地候選站 → 可以搭哪些 (路線, 方向) 抵達（= 第二段候選）
        var legB = new List<(TripStops Trip, int AlightIndex)>();
        foreach (var destUid in destination.CandidateStopUids)
        {
            foreach (var occ in GetOccurrences(destUid))
            {
                if (!_trips.TryGetValue((occ.RouteUid, occ.Direction), out var trip)) continue;
                if (!trip.IndexBySeq.TryGetValue(occ.Sequence, out var alightIndex)) continue;

                legB.Add((trip, alightIndex));
            }
        }

        if (legB.Count == 0) return Array.Empty<TransferOption>();

        // 起點候選站 → 可以搭哪些 (路線, 方向)（= 第一段候選）
        var legA = new List<(TripStops Trip, int BoardIndex)>();
        foreach (var fromUid in origin.CandidateStopUids)
        {
            foreach (var occ in GetOccurrences(fromUid))
            {
                if (!_trips.TryGetValue((occ.RouteUid, occ.Direction), out var trip)) continue;
                if (!trip.IndexBySeq.TryGetValue(occ.Sequence, out var boardIndex)) continue;

                legA.Add((trip, boardIndex));
            }
        }

        var results = new List<TransferOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (tripA, boardIndex) in legA.Take(Math.Max(1, maxLegA)))
        {
            var last = Math.Min(tripA.StopNames.Length, boardIndex + MaxStopsPerLeg);

            // ⚠️ 從 boardIndex + 1 開始：轉乘點必須是「搭了一段之後才到的地方」。
            //    從 boardIndex 開始會產生「在干城站上車、在干城站下車（0 站）、轉 XXX」
            //    這種廢話（真實資料上真的出現過）——那不是轉乘，只是換月台。
            for (var i = boardIndex + 1; i < last; i++)
            {
                var nameKey = tripA.NameKeys[i];

                foreach (var (tripB, alightIndex) in legB)
                {
                    if (tripB.RouteUid == tripA.RouteUid && tripB.Direction == tripA.Direction) continue;
                    if (!tripB.FirstSeqByNameKey.TryGetValue(nameKey, out var transferSeqB)) continue;
                    if (!tripB.IndexBySeq.TryGetValue(transferSeqB, out var transferIndexB)) continue;
                    if (transferIndexB >= alightIndex) continue;   // 第二段必須往目的地方向走
                    if (alightIndex - transferIndexB < 1) continue; // 第二段至少也要搭 1 站

                    var key = $"{tripA.RouteUid}|{tripA.Direction}|{nameKey}|{tripB.RouteUid}|{tripB.Direction}";
                    if (!seen.Add(key)) continue;

                    results.Add(new TransferOption(
                        RouteNameA: tripA.RouteName,
                        DirectionA: tripA.Direction,
                        BoardStopName: tripA.StopNames[boardIndex],
                        TransferStopName: tripA.StopNames[i],
                        RouteNameB: tripB.RouteName,
                        DirectionB: tripB.Direction,
                        AlightStopName: tripB.StopNames[alightIndex],
                        StopsToTransfer: i - boardIndex,
                        StopsAfterTransfer: alightIndex - transferIndexB,
                        RouteUidA: tripA.RouteUid,
                        RouteUidB: tripB.RouteUid));
                }
            }
        }

        return results
            .OrderBy(r => r.TotalStops)
            .ThenBy(r => r.RouteNameA, StringComparer.Ordinal)
            .Take(Math.Max(1, max))
            .ToList();
    }

    /// <summary>第一段最多往後看幾站（越大越找得到，但成本越高）。</summary>
    private const int MaxStopsPerLeg = 70;

    // ─────────────────────────────────────────────────────
    //  核心：候選集合 × 候選集合 的路線匹配
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 找出所有能從「起點候選集合」的任一站，到「目的地候選集合」的任一站的路線。
    ///
    /// 每條 (RouteUID, Direction) 只回傳一筆，但會帶上**所有**合法的候選上車站
    /// （BoardChoices）。使用者勾選路線後，每個 BoardChoice 會變成一個獨立訂閱。
    /// </summary>
    public IReadOnlyList<RouteOption> FindRoutes(
        LocationTarget origin, LocationTarget destination, int max = 200)
    {
        if (origin.CandidateStopUids.Count == 0 || destination.CandidateStopUids.Count == 0)
            return Array.Empty<RouteOption>();

        // 1) 候選 (RouteUID, Direction)：必須同時經過起點候選與目的地候選
        var candidates = new HashSet<(string RouteUid, int Direction)>();
        foreach (var uid in origin.CandidateStopUids)
            if (_byStopUid.TryGetValue(uid, out var occs))
                foreach (var o in occs)
                    candidates.Add((o.RouteUid, o.Direction));

        var results = new List<RouteOption>();

        foreach (var key in candidates)
        {
            if (!_trips.TryGetValue(key, out var trip)) continue;

            // 目的地候選在這條路線上的站序（依站序排序）
            var destSeqs = destination.CandidateStopUids
                .Where(u => trip.FirstSeqByStopUid.ContainsKey(u))
                .Select(u => (Uid: u, Seq: trip.FirstSeqByStopUid[u]))
                .OrderBy(x => x.Seq)
                .ToList();
            if (destSeqs.Count == 0) continue;

            var boards = new List<BoardChoice>();
            foreach (var b in origin.CandidateStopUids
                         .Where(u => trip.FirstSeqByStopUid.ContainsKey(u))
                         .Select(u => (Uid: u, Seq: trip.FirstSeqByStopUid[u]))
                         .OrderBy(x => x.Seq))
            {
                // ★ 合法配對：下車站必須在上車站之後，且不能是同一個站牌
                var alight = destSeqs.FirstOrDefault(x => x.Uid != b.Uid && x.Seq > b.Seq);
                if (alight.Uid is null) continue;

                boards.Add(new BoardChoice(
                    BoardStopUid: b.Uid,
                    BoardStopName: GetDisplayName(b.Uid),
                    BoardSequence: b.Seq,
                    AlightStopUid: alight.Uid,
                    AlightStopName: GetDisplayName(alight.Uid),
                    AlightSequence: alight.Seq));
            }

            if (boards.Count == 0) continue;   // 只經過但方向相反 → 不算

            results.Add(new RouteOption(
                RouteUid: key.RouteUid,
                RouteName: trip.RouteName,
                Direction: key.Direction,
                DirectionLabel: key.Direction switch
                {
                    0 => "去程",
                    1 => "返程",
                    2 => "迴圈",
                    _ => "未知"
                },
                Headsign: trip.Headsign,
                BoardChoices: boards));
        }

        return results
            .OrderBy(r => r.StopsBetween)                                   // 站數少的先
            .ThenBy(r => r.RouteName, NaturalComparer.Instance)              // 300 排在 30 後面
            .ThenBy(r => r.Direction)
            .Take(max)
            .ToList();
    }
}
