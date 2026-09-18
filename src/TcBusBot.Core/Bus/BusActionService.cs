namespace TcBusBot.Core.Bus;

/// <summary>
/// 「用一句話就完成公車操作」的邏輯層。
///
/// 為什麼要獨立這一層（而不是全部塞進 SK 的 plugin 方法）：
/// LLM 的 tool call 只是**輸入通道**，真正危險的是後面的決策 ——
/// 「台中火車站」要對到哪幾個 StopUID、「臺中科大」要展開成哪些候選站。
/// 這正是整個專案最容易出錯的地方（同名站牌、去回程、站序），
/// 所以它必須能在**沒有 LLM、沒有網路**的情況下離線測試。
///
/// Discord 那邊的 `BusTools`（Semantic Kernel 的 plugin）只是把字串參數
/// 轉呼叫這一層，然後把結果講給模型聽。
/// </summary>
public sealed class BusActionService
{
    private readonly BusDataService _data;
    private readonly BusDataCatalog? _catalog;
    private readonly Subscriptions.SubscriptionService _subs;

    public BusActionService(BusDataService data, Subscriptions.SubscriptionService subs, BusDataCatalog? catalog = null)
    {
        _catalog = catalog;
        _data = data;
        _subs = subs;
    }

    // ─────────────────────────────────────────────────────
    //  站牌搜尋（給模型看的文字）
    // ─────────────────────────────────────────────────────

    /// <summary>問的人要用哪一份資料（他可以用 /bus city 選城市）。</summary>
    private BusDataService DataFor(ulong userId) => _catalog?.For(userId) ?? _data;

    /// <summary>
    /// 搜尋時要「依序試」的資料來源：**先試這個人選的城市，再試其他載入的城市**。
    ///
    /// ⚠️ 為什麼一定要這樣：使用者問「臺南車站到安平要搭幾號」時，
    /// 如果他的城市還是預設的臺中（沒打過 `/bus city`），
    /// 只查一份資料就會回「找不到站牌」—— 使用者看到的是「**它抓不到臺南**」。
    /// 模型沒有能力自己想通「要先切城市」，所以跨城市找是工具自己的責任。
    /// </summary>
    private IReadOnlyList<(string City, BusDataService Data)> SourcesFor(ulong userId)
    {
        if (_catalog is null) return [("", _data)];

        var mine = _catalog.For(userId);

        return _catalog.Available
            .Select(city => (City: city, Data: _catalog.ForCity(city)))
            .OrderByDescending(s => ReferenceEquals(s.Data, mine))
            .ToList();
    }

    /// <summary>結果要標城市嗎（只有一個城市時不必，省字數）。</summary>
    private bool MultiCity => _catalog?.HasChoice ?? false;

    /// <summary>這台主機載入了哪些城市（中文名，給模型看的）。</summary>
    private string AvailableCitiesText()
        => string.Join("、", (_catalog?.Available ?? [BusCity.Default]).Select(BusCity.DisplayOf));

    /// <summary>
    /// 使用者（或模型）**指名**要查某個城市時，把城市名換成資料來源。
    ///
    /// 為什麼要讓模型能指名城市：站名模糊比對本身是跨城市跑的，
    /// 遇到「臺南車站」這種字串，別的城市也可能有長得像的站名（臺中的「日南車站」）。
    /// 使用者自己說得出「我要查臺南」，所以工具必須收得下這個指定，
    /// 而且聽不懂時要**講清楚有哪些城市可以查**（模型才知道怎麼重試）。
    /// </summary>
    private bool TryCitySource(string? city, out (string City, BusDataService Data) source, out string error)
    {
        source = default;
        error = "";

        if (string.IsNullOrWhiteSpace(city)) return true;   // 沒指定 → 用預設的跨城市邏輯

        var code = BusCity.Normalize(city);

        if (!BusCity.IsKnown(code))
        {
            error = $"不認識城市「{city}」。我能查的城市：{AvailableCitiesText()}。";
            return false;
        }

        if (_catalog is not null && !_catalog.Available.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            error = $"我沒有{BusCity.DisplayOf(code)}的公車資料。我能查的城市：{AvailableCitiesText()}。";
            return false;
        }

        source = (code, _catalog?.ForCity(code) ?? _data);
        return true;
    }

    /// <summary>
    /// 在某個城市裡找關鍵字，並回報「這次找到的算不算**強相符**」。
    ///
    /// ⚠️ 為什麼要有「強弱」這個概念：模糊比對很寬鬆 ——
    /// 在臺中的資料裡搜「臺南車站」也會撈到「臺中車站」（編輯距離 1，只是打字猜測）。
    /// 如果照著「第一個有回應的城市」走，使用者問臺南就會拿到臺中的站牌，
    /// 而且看起來像真的答案。所以一律**強相符的城市優先**（精確／前綴／子字串／縮寫），
    /// 兩個城市都只有模糊相符時，才回頭用「這個人自己選的城市」。
    /// </summary>
    private static (List<StopSearchGroupResult> Shown, int HiddenFuzzy, bool Strong) Lookup(
        BusDataService data, string keyword)
    {
        var groups = data.Search.SearchGrouped(keyword);

        if (groups.Count == 0) return ([], 0, false);

        var strong = groups.Where(g => g.IsStrongMatch).ToList();

        // 注意：強相符時「隱藏數」是 groups - strong；全都是模糊相符時 strong 是空的，
        // 這時候**不能**用「隱藏數」判斷強弱（它會是 0），要看 Strong 這個旗標。
        return strong.Count > 0 ? (strong, groups.Count - strong.Count, true) : ([.. groups], 0, false);
    }

    /// <summary>依「強相符優先」的順序排出要試的城市（先自己選的城市，再其他）。</summary>
    private IEnumerable<(string City, BusDataService Data)> SourcesByStrength(string keyword, ulong userId)
    {
        var sources = SourcesFor(userId);
        var strong = sources.Where(s => Lookup(s.Data, keyword).Strong).ToList();

        return strong.Concat(sources.Where(s => !strong.Contains(s)));
    }

    /// <summary>搜尋站牌，回傳「模型看得懂」的文字清單（會跨城市找；指定城市時只查那個城市）。</summary>
    public string SearchStops(string keyword, int limit = 8, ulong userId = 0, string? city = null)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length < 2) return "關鍵字太短（至少 2 個字）。";

        if (!TryCitySource(city, out var requested, out var cityError)) return cityError;

        var sources = string.IsNullOrWhiteSpace(city) ? SourcesFor(userId) : [requested];
        BusDataService? hitCity = null;
        List<StopSearchGroupResult> shown = [];
        var hidden = 0;

        // 兩輪：先只收「強相符」的城市，都沒有才用模糊相符（見 Lookup）
        foreach (var strongOnly in new[] { true, false })
        {
            foreach (var (_, data) in sources)
            {
                var (s, h, isStrong) = Lookup(data, keyword);

                if (s.Count == 0) continue;
                if (strongOnly && !isStrong) continue;

                hitCity = data;
                shown = s;
                hidden = h;
                break;
            }

            if (hitCity is not null) break;
        }

        if (hitCity is null)
        {
            var examples = string.Join("｜", sources.Select(s =>
                $"{CityLabel(s.City)}{string.Join("、", s.Data.ExampleStopNames(4))}"));

            return $"找不到符合「{keyword}」的站牌。" +
                   (MultiCity ? $"\n目前查得到的城市與站名例子：{examples}" : $"\n這份資料集裡有的站名例如：{examples}");
        }

        var cityName = sources.First(s => ReferenceEquals(s.Data, hitCity)).City;

        var lines = shown.Take(Math.Max(1, limit)).Select(g =>
        {
            // 除了站名，也把「有哪幾條路線經過」講出來 —— 模型很常需要這個才能判斷
            // 「使用者講的是哪一個站」（同名站牌在不同路口時，經過的路線不一樣）
            var routes = g.Hits
                .SelectMany(h => hitCity.GetOccurrences(h.Entry.StopUid))
                .Select(o => hitCity.GetRouteName(o.RouteUid))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .Take(5)
                .ToList();

            var names = g.Hits.Select(h => h.Entry.DisplayName)
                              .Distinct(StringComparer.Ordinal)
                              .Take(4);

            return $"• {g.DisplayName}（{g.Hits.Count} 個站牌：{string.Join("、", names)}）" +
                   (routes.Count > 0 ? $"\n　　經過的路線：{string.Join("、", routes)}" : "");
        });

        return $"「{keyword}」找到 {shown.Count} 組站牌" +
               (hidden > 0 ? $"（另外濾掉 {hidden} 組模糊相符）" : "") +
               (MultiCity ? $"（城市：{BusCity.DisplayOf(cityName)}）" : "") + "：\n" + string.Join("\n", lines);
    }

    /// <summary>多城市時把城市名標在結果前面（只有一個城市時不必）。</summary>
    private string CityLabel(string city)
        => MultiCity && !string.IsNullOrEmpty(city) ? $"{BusCity.DisplayOf(city)}：" : "";


    // ─────────────────────────────────────────────────────
    //  依路線號碼查（使用者常常只知道號碼）
    // ─────────────────────────────────────────────────────

    /// <summary>用路線號碼查路線（「300」「304」「藍1」…）——**跨城市找**（號碼可能兩邊都有）。</summary>
    public string SearchRoutes(string number, int limit = 8, ulong userId = 0)
    {
        number = (number ?? "").Trim();
        if (number.Length == 0) return "請給我路線號碼，例如 300、304、藍1。";

        var sources = SourcesFor(userId);
        var found = new List<(string City, BusDataService.RouteSummary Route)>();

        foreach (var (city, data) in sources)
            foreach (var route in data.FindRoutesByNumber(number, limit))
                found.Add((city, route));

        if (found.Count == 0)
            return $"找不到號碼符合「{number}」的路線。（可以先用 search_stops 查站牌，" +
                   "再用 find_routes 看有哪些路線可以搭）";

        var lines = found.Select(f =>
            $"• {CityLabel(f.City)}{f.Route.Describe()}" +
            (f.Route.SampleStops.Count > 0
                ? $"\n　　經過：{string.Join(" → ", f.Route.SampleStops)}"
                : ""));

        return $"號碼符合「{number}」的路線共 {found.Count} 筆（同一條路線的去回程分開列）：\n" +
               string.Join("\n", lines);
    }

    // ─────────────────────────────────────────────────────
    //  查路線（不建立訂閱）
    // ─────────────────────────────────────────────────────

    public string FindRoutes(string origin, string destination, ulong userId = 0, string? city = null)
    {
        if (!TryCitySource(city, out var requested, out var cityError)) return cityError;

        // 起訖**必須在同一個城市**才算得出來（跨城市的站牌沒有共同路線索引），
        // 所以依序試每一個城市：先試這個人選的，再試其他（見 SourcesFor 的說明）；
        // 而且**強相符的城市先試**，免得臺中的模糊相符搶走臺南的查詢（見 SourcesByStrength）。
        // 使用者（或模型）指名城市時，就只查那個城市（見 TryCitySource）。
        var sources = string.IsNullOrWhiteSpace(city)
            ? SourcesByStrength(origin, userId)
            : [requested];

        Resolved from = default!, to = default!;
        BusDataService? data = null;
        var hitCity = "";

        foreach (var source in sources)
        {
            var tryFrom = Resolve(origin, source.Data);
            if (tryFrom.Uids.Count == 0) continue;

            var tryTo = Resolve(destination, source.Data);
            if (tryTo.Uids.Count == 0) continue;

            from = tryFrom;
            to = tryTo;
            data = source.Data;
            hitCity = source.City;
            break;
        }

        if (data is null)
        {
            // 兩個都找不到 → 用第一個（或指定的）城市的說明（順便講清楚有哪些城市可以查）
            var first = string.IsNullOrWhiteSpace(city) ? SourcesFor(userId)[0] : requested;
            var onlyFrom = Resolve(origin, first.Data);

            return $"起點：{DescribeUnresolved(origin, onlyFrom, userId)}";
        }

        var routes = data.FindRoutes(from.Target, to.Target);
        var label = CityLabel(hitCity);

        if (routes.Count == 0)
        {
            // 沒有直達 → 幫忙找「轉一次」的走法（以前這裡就只回「沒有直達」，等於幫不上忙）
            var transfers = data.FindTransferRoutes(from.Target, to.Target);
            var head = $"{label}{from.Target.DisplayName} → {to.Target.DisplayName} **沒有直達路線**。";

            if (transfers.Count == 0)
                return head + "也找不到只轉一次就到的走法（可能要轉兩次以上，或其中一段方向不對）。";

            var lines = transfers.Select((t, i) => $"{i + 1}. {t.Describe()}（約 {t.TotalStops} 站）");

            return head + "\n以下是**轉一次**的走法（依總站數排序，僅供參考）：\n" + string.Join("\n", lines);
        }

        var direct = routes.Take(15).Select(r =>
            $"• {r.RouteName}（{(r.Direction == 0 ? "去程" : "返程")}）" +
            $"上車：{string.Join("、", r.BoardChoices.Select(b => $"{b.BoardStopName}（第 {b.BoardSequence} 站）"))}");

        var text = $"{label}{from.Target.DisplayName} → {to.Target.DisplayName} 共 {routes.Count} 條直達路線：\n" +
                   string.Join("\n", direct);

        if (routes.Count == 1)
        {
            // 只有一條時，順便提一下轉乘的可能（使用者常常想比較哪個快）
            var transfers = data.FindTransferRoutes(from.Target, to.Target, max: 1);

            if (transfers.Count > 0 && transfers[0].TotalStops < 40)
                text += $"\n（另外也可以轉車：{transfers[0].Describe()}）";
        }

        return text;
    }

    // ─────────────────────────────────────────────────────
    //  訂閱（會真的建立）
    // ─────────────────────────────────────────────────────

    /// <summary>訂閱結果（文字給模型、結構給 Bot 記錄與除錯）。</summary>
    public sealed record SubscribeOutcome(
        bool Ok,
        string Message,
        Subscriptions.SubscriptionGroup? Group,
        string? OriginName,
        string? DestinationName,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// 用「站名關鍵字」訂閱：把使用者的口語站名展開成候選站集合，再比對站序找出可搭的路線。
    ///
    /// ★ 這個方法做的事**與面板完全相同**（模糊搜尋 → 強相符候選 → 站序匹配），
    ///   所以「用嘴巴講」與「用面板點」得到的結果一模一樣。
    /// </summary>
    public SubscribeOutcome Subscribe(
        ulong userId,
        string origin,
        string destination,
        int notifyMinutes = 10,
        ulong? guildId = null,
        ulong? channelId = null,
        string? city = null)
    {
        if (!TryCitySource(city, out var requested, out var cityError))
            return new SubscribeOutcome(false, cityError, null, null, null, []);

        // 起訖要在**同一個城市**才訂得起來 → 依序試每一個城市
        //（強相符優先，見 SourcesByStrength；否則會被另一個城市的模糊相符攔走）
        // 指名城市時就只查那個城市（見 TryCitySource）。
        var sources = string.IsNullOrWhiteSpace(city)
            ? SourcesByStrength(origin, userId)
            : [requested];

        Resolved? from = null;
        Resolved? to = null;

        foreach (var source in sources)
        {
            var tryFrom = Resolve(origin, source.Data);
            if (tryFrom.Uids.Count == 0) continue;

            var tryTo = Resolve(destination, source.Data);
            if (tryTo.Uids.Count == 0) continue;

            from = tryFrom;
            to = tryTo;
            break;
        }

        var firstData = (string.IsNullOrWhiteSpace(city) ? SourcesFor(userId)[0] : requested).Data;

        if (from is null || from.Uids.Count == 0)
            return new SubscribeOutcome(false, $"起點：{DescribeUnresolved(origin, Resolve(origin, firstData), userId)}",
                null, null, null, []);

        if (to is null || to.Uids.Count == 0)
            return new SubscribeOutcome(false, $"終點：{DescribeUnresolved(destination, Resolve(destination, firstData), userId)}",
                null, null, null, []);

        return SubscribeTargets(userId, from.Target, to.Target, null, notifyMinutes, guildId, channelId);
    }

    /// <summary>
    /// 用「已經解析好的起訖」訂閱 —— 給「模型幫你按按鈕」那條路用：
    /// 面板已經把起訖存進 session 了，不需要再從關鍵字解析一次。
    /// </summary>
    public SubscribeOutcome SubscribeTargets(
        ulong userId,
        LocationTarget origin,
        LocationTarget destination,
        IReadOnlyList<RouteOption>? onlyRoutes = null,
        int notifyMinutes = 10,
        ulong? guildId = null,
        ulong? channelId = null)
    {
        if (origin.CandidateStopUids.Count == 0 || destination.CandidateStopUids.Count == 0)
            return new SubscribeOutcome(false, "起點或終點還沒有站牌，請先設定起訖。", null, null, null, []);

        var routes = DataForTargets(userId, origin, destination).FindRoutes(origin, destination);

        if (onlyRoutes is { Count: > 0 })
        {
            var wanted = onlyRoutes.Select(r => (r.RouteUid, r.Direction)).ToHashSet();
            routes = routes.Where(r => wanted.Contains((r.RouteUid, r.Direction))).ToList();
        }

        if (routes.Count == 0)
            return new SubscribeOutcome(false,
                $"「{origin.DisplayName}」到「{destination.DisplayName}」找不到可以直接搭的路線，" +
                "所以沒有建立訂閱。請告訴使用者這個結果（可以建議他換方向或轉乘）。",
                null, null, null, []);

        // 夾在合理範圍內：太短會錯過公車，太長只是多一則訊息
        notifyMinutes = Math.Clamp(notifyMinutes <= 0 ? 10 : notifyMinutes, 1, 60);

        var group = _subs.CreateGroup(
            userId: userId,
            origin: origin,
            destination: destination,
            options: routes,
            notifyBeforeMinutes: notifyMinutes,
            guildId: guildId,
            channelId: channelId);

        var lines = _subs.GetSubscriptions(group).Take(10).Select(s =>
            $"• {s.RouteName}（{(s.Direction == 0 ? "去程" : "返程")}）" +
            $"在 {s.BoardStopName} 上車 → 在 {s.AlightStopName} 下車");

        var message =
            $"✅ 已建立訂閱（{group.SubscriptionIds.Count} 筆、{routes.Count} 條路線，提前 {notifyMinutes} 分鐘通知）：\n" +
            string.Join("\n", lines) +
            (group.SubscriptionIds.Count > 10 ? $"\n…還有 {group.SubscriptionIds.Count - 10} 筆" : "");

        return new SubscribeOutcome(true, message, group,
            origin.DisplayName, destination.DisplayName, []);
    }

    /// <summary>
    /// 把「站名關鍵字」解析成候選站集合（面板／UI 動作共用同一套）。
    /// 回傳 null 代表找不到、或模糊到不能自己挑（原因寫在 <paramref name="message"/>）。
    /// </summary>
    public LocationTarget? ResolveKeyword(string keyword, out string message, ulong userId = 0)
    {
        var resolved = ResolveAnyCity(keyword, userId);

        if (resolved.Uids.Count == 0)
        {
            message = DescribeUnresolved(keyword, resolved, userId);
            return null;
        }

        message = $"{resolved.Target.DisplayName}（{resolved.Uids.Count} 個候選站牌）";
        return resolved.Target;
    }

    /// <summary>用「已經解析好的起訖」找路線（面板／UI 動作用）。</summary>
    public IReadOnlyList<RouteOption> FindRoutesFor(LocationTarget origin, LocationTarget destination, ulong userId = 0)
        => DataForTargets(userId, origin, destination).FindRoutes(origin, destination);

    /// <summary>
    /// 起訖的站牌落在哪個城市，就用那一份資料。
    ///
    /// 為什麼看**站牌**而不是看「問的人選的城市」：面板的 session 可能是他還是臺中時建的，
    /// 之後他打了 `/bus city 城市:臺南` —— 這時**站牌本身就是答案**（它們是臺南的站牌）。
    /// </summary>
    private BusDataService DataForTargets(ulong userId, LocationTarget origin, LocationTarget destination)
    {
        if (_catalog is null) return _data;

        var uid = origin.CandidateStopUids.FirstOrDefault() ?? destination.CandidateStopUids.FirstOrDefault();
        if (uid is null) return DataFor(userId);

        return _catalog.ForCity(_catalog.CityOfStop(uid));
    }

    /// <summary>跨城市解析關鍵字（強相符的城市先試，再試其他，見 SourcesByStrength）。</summary>
    private Resolved ResolveAnyCity(string keyword, ulong userId)
    {
        foreach (var source in SourcesByStrength(keyword, userId))
        {
            var resolved = Resolve(keyword, source.Data);
            if (resolved.Uids.Count > 0 || resolved.Ambiguous) return resolved;
        }

        return Resolve(keyword, SourcesFor(userId)[0].Data);
    }

    // ─────────────────────────────────────────────────────
    //  查詢／取消自己的訂閱
    // ─────────────────────────────────────────────────────

    public string ListSubscriptions(ulong userId)
    {
        var groups = _subs.GetGroupsByUser(userId).ToList();

        if (groups.Count == 0) return "使用者目前沒有任何訂閱。";

        var blocks = groups.Select((g, index) =>
        {
            var subs = _subs.GetSubscriptions(g);
            var lines = subs.Take(6).Select(s =>
                $"　　• {s.RouteName}（{(s.Direction == 0 ? "去程" : "返程")}）{s.BoardStopName} → {s.AlightStopName}");
            var more = subs.Count > 6 ? $"\n　　…還有 {subs.Count - 6} 筆" : "";

            return $"{index + 1}. {g.DescribeRoute()}（{subs.Count} 筆，提前 {g.NotifyBeforeMinutes} 分鐘）\n" +
                   string.Join("\n", lines) + more;
        });

        return $"使用者目前有 {groups.Count} 組訂閱：\n" + string.Join("\n", blocks);
    }

    /// <summary>取消全部訂閱（可以復原：回傳被移除的內容讓呼叫端推進 Undo 堆疊）。</summary>
    public sealed record CancelOutcome(
        int GroupCount,
        int SubscriptionCount,
        int BoardStopCount,
        string Message,
        IReadOnlyList<(Subscriptions.SubscriptionGroup Group, IReadOnlyList<Subscriptions.Subscription> Subscriptions)> Removed);

    public CancelOutcome CancelAll(ulong userId)
    {
        var removed = _subs.RemoveAllForUser(userId);

        if (removed.Count == 0)
            return new CancelOutcome(0, 0, 0, "使用者目前沒有任何訂閱，所以沒有東西可以取消。", removed);

        var subscriptions = removed.SelectMany(r => r.Subscriptions).ToList();
        var stops = subscriptions.Select(s => s.BoardStopUid).Distinct(StringComparer.Ordinal).Count();

        return new CancelOutcome(
            removed.Count,
            subscriptions.Count,
            stops,
            $"✅ 已取消全部訂閱：{removed.Count} 組、{subscriptions.Count} 筆、" +
            $"不再查詢 {stops} 個上車站。",
            removed);
    }

    // ─────────────────────────────────────────────────────
    //  內部：站名關鍵字 → 候選站牌集合
    // ─────────────────────────────────────────────────────

    private sealed record Resolved(
        LocationTarget Target,
        List<string> Uids,
        bool WeakMatch,
        bool Ambiguous,
        IReadOnlyList<string> Candidates);

    /// <summary>
    /// 把口語站名展開成候選 StopUID 集合。
    ///
    /// ★ **與面板（<c>/bus panel</c>）走完全同一份實作**（<see cref="StopPicks"/>）：
    ///   模糊搜尋 → 只留強相符的站區 → 取「精確命中」的預設勾選 → 用短鍵解出站牌。
    ///
    /// 為什麼一定要共用：以前這裡自己寫了一套（拿**搜尋命中**當候選），
    /// 而命中有上限（整體 25 筆、每組只留符合關鍵字的那些），
    /// 於是「臺中車站」38 個月台只會被放進 2~3 個 ——
    /// 停在其他月台的路線就整條找不到（面板按 `g:` 時是取整個站區，所以面板找得到）。
    /// 這就是「同名站牌找不到」的根因。
    /// </summary>
    private Resolved Resolve(string keyword, BusDataService? data = null)
    {
        data ??= _data;
        keyword = (keyword ?? "").Trim();

        if (keyword.Length == 0)
            return new Resolved(EmptyTarget(), [], true, false, []);

        // 與面板相同：有強相符時只留強相符，不要被模糊相符的雜訊塞滿
        var (shown, _) = PreferStrong(data.Search.SearchGrouped(keyword));

        if (shown.Count == 0)
            return new Resolved(EmptyTarget(), [], true, false, []);

        var (uids, ambiguous, candidates) = StopPicks.ResolveDefaults(shown, data);

        if (ambiguous)
            return new Resolved(EmptyTarget(), [], true, true, candidates);

        return new Resolved(data.TargetFromStops(uids), uids, false, false, []);
    }

    /// <summary>找不到站牌／對到多個站區時要講的話（讓模型去問使用者，而不是自己猜）。</summary>
    private string DescribeUnresolved(string keyword, Resolved resolved, ulong userId = 0)
    {
        if (resolved.Ambiguous)
        {
            var list = resolved.Candidates.Count > 0
                ? "候選有：" + string.Join("、", resolved.Candidates)
                : "";

            return $"「{keyword}」對到好幾個不同的站區，我不確定是哪一個，所以先不動手。" +
                   $"請使用者說清楚（例如加上路口名或行政區），{list}";
        }

        return $"找不到符合「{keyword}」的站牌。請先問使用者正確的站名，或用 search_stops 查。" +
               $"這份資料集裡有的站名例如：{string.Join("、", DataFor(userId).ExampleStopNames(6))}。";
    }

    private static LocationTarget EmptyTarget() => new() { DisplayName = "", CandidateStopUids = [] };

    /// <summary>與面板相同的過濾：有強相符時只留強相符，不要被模糊相符的雜訊塞滿。</summary>
    private static (List<StopSearchGroupResult> Shown, int HiddenFuzzy) PreferStrong(
        IReadOnlyList<StopSearchGroupResult> groups)
    {
        var strong = groups.Where(g => g.IsStrongMatch).ToList();
        return strong.Count > 0 ? (strong, groups.Count - strong.Count) : (groups.ToList(), 0);
    }
}
