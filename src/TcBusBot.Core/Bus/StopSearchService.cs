namespace TcBusBot.Core.Bus;

/// <summary>
/// 模糊搜尋索引的一筆。所有欄位都在啟動時算好，查詢時只做字串比對。
/// </summary>
public sealed record StopSearchEntry(
    string StopUid,
    string SearchKey,        // StopNameNormalizer.ForSearch(Zh_tw)
    string SearchKeyEn,      // StopNameNormalizer.ForSearch(En)
    string DisplayName,      // 原始站名（顯示用，例：「臺中車站(A月台)」）
    string GroupKey,         // 所屬建議群組的 ShortKey
    int RouteCount,          // 被幾條 (路線,方向) 經過 → 人氣權重
    double Lon,
    double Lat);

/// <summary>比對成立的**種類**；順序即強弱，讓「顯示」與「預設勾選」可以有不同的門檻。</summary>
public enum StopMatchKind
{
    /// <summary>完全不符合。</summary>
    None = 0,

    /// <summary>編輯距離 ≤ 2：只是「打字打錯」的猜測，純雜訊。</summary>
    Edit = 1,

    /// <summary>鬆散子序列：字都在但跳太多（例：用「台北路」命中「臺灣大道北勢路口」）。</summary>
    Loose = 2,

    /// <summary>子字串：**相關**但不夠精確（例：「车站」命中「臺中車站」）。可以顯示，但不該預設勾選。</summary>
    Substring = 3,

    /// <summary>縮寫：緊湊子序列（例：「台中科大」命中「國立臺中科技大學」）。精確，預設勾選。</summary>
    Abbreviation = 4,

    /// <summary>前綴相符。</summary>
    Prefix = 5,

    /// <summary>完全相同。</summary>
    Exact = 6,
}

public static class StopMatchKindExtensions
{
    /// <summary>值得顯示（會濾掉編輯距離與鬆散子序列的雜訊）。</summary>
    public static bool IsRelevant(this StopMatchKind kind) => kind >= StopMatchKind.Substring;

    /// <summary>夠精確，可以**預設勾選**（搜「车站」不該預設勾 22 個站區）。</summary>
    public static bool IsPrecise(this StopMatchKind kind) => kind >= StopMatchKind.Abbreviation;
}

public sealed record StopSearchHit(StopSearchEntry Entry, int Score, StopMatchKind Kind)
{
    /// <summary>值得顯示。</summary>
    public bool Strong => Kind.IsRelevant();

    /// <summary>夠精確 → UI 預設勾選。</summary>
    public bool Precise => Kind.IsPrecise();
}

/// <summary>搜尋結果按建議群組分桶後的一組。</summary>
public sealed record StopSearchGroupResult(
    string GroupKey,
    string DisplayName,
    int BestScore,
    bool IsStrongMatch,                       // 值得顯示（完全／前綴／子字串／縮寫）
    bool IsDefaultPick,                       // 預設勾選（完全／前綴／縮寫 —— 不含單純的子字串）
    IReadOnlyList<StopSearchHit> Hits);

/// <summary>
/// 模糊站牌搜尋（第一階段的核心互動）。
///
/// 為什麼不用 TDX 的 OData contains()：
///   它只做原始字串的子字串比對，使用者打「台中车站」（台、簡體、無空白）
///   **不可能**命中「臺中車站(A月台)」。本地正規化 + 模糊比對是唯一能滿足需求的作法。
///
/// 為什麼不需要 trie / n-gram / Lucene：
///   台中全部站牌約 5,000+ 筆，每筆的 SearchKey 已預先正規化，
///   線性掃描 + 排序的實測量級是毫秒級，而 Discord 給 3 秒。
/// </summary>
public sealed class StopSearchService
{
    private const int ExactScore = 1000;
    private const int PrefixBase = 800;
    private const int SubstringBase = 600;
    private const int AbbreviationBase = 500;   // 縮寫：命中但不連續（例：台中科大 → 國立臺中科技大學）
    private const int SubsequenceBase = 300;
    private const int EditDistanceBase = 200;

    private readonly StopSearchEntry[] _entries;
    private readonly int _maxResults;
    private readonly int _minQueryLength;
    private readonly double _popularityCap;

    public StopSearchService(
        IEnumerable<StopSearchEntry> entries,
        int maxResults = 25,
        int minQueryLength = 2,
        int popularityCap = 40)
    {
        _entries = entries.ToArray();
        _maxResults = maxResults;
        _minQueryLength = minQueryLength;
        _popularityCap = Math.Max(1, popularityCap);
    }

    public int EntryCount => _entries.Length;

    /// <summary>平鋪的搜尋結果（依分數排序）。</summary>
    public IReadOnlyList<StopSearchHit> Search(string? query, int? limit = null)
    {
        var q = StopNameNormalizer.ForSearch(query);
        var take = limit ?? _maxResults;
        if (q.Length < _minQueryLength) return Array.Empty<StopSearchHit>();

        var hits = new List<StopSearchHit>();

        foreach (var e in _entries)
        {
            var zh = ScoreDetail(e.SearchKey, q);

            var en = (Score: 0, Kind: StopMatchKind.None);
            if (e.SearchKeyEn.Length > 0)
            {
                var raw = ScoreDetail(e.SearchKeyEn, q);
                en = ((int)(raw.Score * 0.9), raw.Kind);   // 英文站名是輔助，分數打九折
            }

            var best = en.Score > zh.Score ? en : zh;
            if (best.Score <= 0) continue;

            // 人氣權重：主要站牌（經過路線多）應該排在只被一條路線經過的同名冷僻站前面
            var popularity = 1.0 + Math.Min(0.5, e.RouteCount / _popularityCap);
            hits.Add(new StopSearchHit(e, (int)(best.Score * popularity), best.Kind));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Entry.DisplayName.Length)          // 同名時短名優先
            .ThenBy(h => h.Entry.StopUid, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }

    /// <summary>
    /// 分組後的搜尋結果。
    ///
    /// ⚠️ 名額分配刻意做成兩段式：先讓每個群組各取最高分的一筆，再補齊剩餘名額。
    /// 否則某個熱門站牌的命中太多時，會把其他群組全部擠出前 25 名。
    /// </summary>
    public IReadOnlyList<StopSearchGroupResult> SearchGrouped(string? query)
    {
        var flat = Search(query, _maxResults * 4);
        if (flat.Count == 0) return Array.Empty<StopSearchGroupResult>();

        var buckets = new List<(string GroupKey, List<StopSearchHit> Hits)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var hit in flat)
        {
            var key = hit.Entry.GroupKey;
            if (!index.TryGetValue(key, out var i))
            {
                i = buckets.Count;
                index[key] = i;
                buckets.Add((key, new List<StopSearchHit>()));
            }
            buckets[i].Hits.Add(hit);
        }

        buckets.Sort((a, b) => b.Hits[0].Score.CompareTo(a.Hits[0].Score));

        var selected = new List<StopSearchHit>();
        var takenPerBucket = new int[buckets.Count];

        // 第一段：每組先取最高分的一筆
        for (var i = 0; i < buckets.Count; i++)
        {
            if (selected.Count >= _maxResults) break;
            selected.Add(buckets[i].Hits[0]);
            takenPerBucket[i] = 1;
        }

        // 第二段：補齊剩餘名額（依全域分數）
        if (selected.Count < _maxResults)
        {
            var rest = new List<StopSearchHit>();
            for (var i = 0; i < buckets.Count; i++)
                for (var k = takenPerBucket[i]; k < buckets[i].Hits.Count; k++)
                    rest.Add(buckets[i].Hits[k]);

            rest.Sort((a, b) => b.Score.CompareTo(a.Score));
            foreach (var h in rest)
            {
                if (selected.Count >= _maxResults) break;
                selected.Add(h);
            }
        }

        var byGroup = selected
            .GroupBy(h => h.Entry.GroupKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Score).ToList(), StringComparer.Ordinal);

        var results = new List<StopSearchGroupResult>();
        foreach (var b in buckets)
        {
            if (!byGroup.TryGetValue(b.GroupKey, out var hits) || hits.Count == 0) continue;
            var best = hits[0];
            results.Add(new StopSearchGroupResult(
                GroupKey: b.GroupKey,
                DisplayName: ShortGroupName(best.Entry.DisplayName),
                BestScore: best.Score,
                IsStrongMatch: best.Strong,
                IsDefaultPick: hits.Any(h => h.Precise),   // 只要有一個變體名稱是精確命中就算
                Hits: hits));
        }

        return results;
    }

    // ── 評分 ────────────────────────────────────────────────

    /// <summary>
    /// 回傳分數 **與「這算哪一種相符」**。
    ///
    /// 為什麼要回傳 <c>StopMatchKind</c> 而不是讓呼叫端比對分數門檻：
    /// 分數會被長度差與人氣權重調整，門檻會誤判
    /// （前綴相符但名字很長 → 分數掉到門檻下 → 明明是好結果卻被當成雜訊）。
    /// 「哪一種比對成立」才是強弱的真正依據。
    ///
    /// 公開是因為離線工具（`tcbus` / `--dryrun`）要能直接檢查評分行為。
    /// </summary>
    public static (int Score, StopMatchKind Kind) ScoreDetail(string key, string q)
    {
        if (key.Length == 0 || q.Length == 0) return (0, StopMatchKind.None);

        if (string.Equals(key, q, StringComparison.Ordinal))
            return (ExactScore, StopMatchKind.Exact);

        if (key.StartsWith(q, StringComparison.Ordinal))
            return (PrefixBase - Math.Min(200, key.Length - q.Length), StopMatchKind.Prefix);

        var idx = key.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0)
            return (Math.Max(1, SubstringBase - idx * 5 - Math.Min(200, key.Length - q.Length)),
                    StopMatchKind.Substring);

        // 子序列：容忍漏字。
        // ⚠️ 只對 3 字元以上生效。2 字元的子序列幾乎什麼都命中
        //    （實測「台北」會命中「臺灣大道北勢路口」），那是雜訊不是搜尋結果。
        if (q.Length >= 3 && TrySubsequence(q, key, out var m))
        {
            // ★ 縮寫：使用者打「台中科大」，資料是「國立臺中科技大學」。
            //   判準是「中間跳過幾個字」：
            //     國立臺中[技]科大   → 內部只跳 1 個字，而且整段很緊湊 → 這是縮寫
            //     臺[灣]大[道]北[勢]路口 → 跳太多 → 只是碰巧有同樣的字 → 雜訊
            if (m.InternalGaps <= 1 && m.Span <= q.Length + 2)
            {
                return (Math.Max(1, AbbreviationBase
                                    - m.InternalGaps * 40
                                    - Math.Min(120, m.Leading * 15)     // 前面的「國立」「市立」扣一點
                                    - Math.Min(120, m.Trailing * 15)),  // 後面的「大學」「校區」扣一點
                        StopMatchKind.Abbreviation);
            }

            return (Math.Max(1, SubsequenceBase - m.InternalGaps * 10), StopMatchKind.Loose);
        }

        // 編輯距離：最後手段，且只在長度差 <= 2 時才值得算
        if (Math.Abs(key.Length - q.Length) <= 2)
        {
            var d = Levenshtein(key, q, max: 2);
            if (d <= 2) return (Math.Max(1, EditDistanceBase - d * 50), StopMatchKind.Edit);
        }

        return (0, StopMatchKind.None);
    }

    /// <summary>一次子序列比對的細節；這些數字決定它是「縮寫」還是「雜訊」。</summary>
    internal readonly record struct SubsequenceMatch(int First, int Last, int InternalGaps, int Trailing)
    {
        /// <summary>從第一個命中到最後一個命中涵蓋幾個字元。</summary>
        public int Span => Last - First + 1;

        /// <summary>第一個命中前面有幾個字（「國立」「市立」這種前綴）。</summary>
        public int Leading => First;
    }

    private static bool TrySubsequence(string query, string key, out SubsequenceMatch match)
    {
        var first = -1;
        var last = -1;
        var internalGaps = 0;
        var qi = 0;

        for (var ki = 0; ki < key.Length && qi < query.Length; ki++)
        {
            if (key[ki] != query[qi])
            {
                // 只算「第一個命中之後」的跳字；前面的是行政區或學校全名之類的前綴，
                // 它由 Leading 另外計分，不該混進 gaps（否則「國立」會被當成兩個漏字）
                if (first >= 0) internalGaps++;
                continue;
            }

            if (first < 0) first = ki;
            last = ki;
            qi++;
        }

        if (qi != query.Length)
        {
            match = default;
            return false;
        }

        match = new SubsequenceMatch(first, last, internalGaps, key.Length - last - 1);
        return true;
    }

    /// <summary>有上限的 Levenshtein 距離；超過 max 就提早回傳 max+1。</summary>
    private static int Levenshtein(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (cur[j] < rowMin) rowMin = cur[j];
            }
            if (rowMin > max) return max + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>把「臺中車站(A月台)」縮成顯示用的群組名「臺中車站」。</summary>
    private static string ShortGroupName(string displayName)
    {
        var idx = displayName.IndexOfAny(['(', '（']);
        return (idx >= 0 ? displayName[..idx] : displayName).Trim();
    }
}
