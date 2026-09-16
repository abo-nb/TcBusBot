namespace TcBusBot.Core.Bus;

/// <summary>一個「挑站牌」的選項（純資料，不含任何 Discord 型別）。</summary>
public sealed record StopPickOption(
    string Value,          // `g:{GroupKey}` 或 `n:{GroupKey}:{index}`
    string Label,
    string Description,
    bool IsDefault);

/// <summary>一次搜尋結果展開後的選項、預設勾選、全部值。</summary>
public sealed record StopPickSet(
    IReadOnlyList<StopPickOption> Options,
    IReadOnlyList<string> Defaults,
    IReadOnlyList<string> AllValues);

/// <summary>
/// **「搜尋結果 → 可選站牌 → StopUID 集合」的唯一一份實作**。
///
/// 為什麼要抽出來（原本在 <c>BusUi</c> 裡，只有面板在用）：
/// 面板與 LLM 工具各自實作一次，結果就出現「同名站牌找不到」的災難 ——
/// LLM 那一版是拿**搜尋命中**（<see cref="StopSearchGroupResult.Hits"/>）當候選，
/// 而命中數有上限（整體 25 筆，每組只留符合關鍵字的那些），
/// 所以「臺中車站」38 個月台只會被放進 2~3 個，
/// 走到其他月台的路線就整條找不到（而面板按 `g:` 短鍵時是取**整個站區**的站牌）。
///
/// 現在兩邊都走這一份：
///   * 面板：<c>BusUi.BuildStopOptions</c> 拿這裡的選項去組 Select Menu
///   * LLM ：<see cref="BusActionService"/> 拿 <see cref="StopPickSet.Defaults"/> 再 <see cref="Resolve"/>
///
/// 幾個關鍵規則（與面板完全一致，改壞了會直接影響使用者）：
///   * 站區內**只有一種站名**時不需要「整個站區」選項（會與那唯一的選項重複）
///   * 每個站名一個選項，同名的多個 StopUID 合併
///   * 值用短鍵（`g:` / `n:`），**不能塞 StopUID** ——「國立臺中科技大學」有 44 個同名站牌，
///     串起來約 400 字元會超過 Discord 的 100 字元上限而被截斷（實際踩過：
///     明明有 11 條路線卻只找到 1 條）
///   * 預設勾選用 <see cref="StopSearchGroupResult.IsDefaultPick"/>（精確命中），
///     不是 <see cref="StopSearchGroupResult.IsStrongMatch"/> —— 搜「车站」會顯示 22 組，
///     但那些只是子字串相符，不該全部預設勾起來
///   * 只有一個選項時就預設勾選它（使用者的意圖再明顯不過）
/// </summary>
public static class StopPicks
{
    public const int MaxOptions = 25;

    public static StopPickSet Build(
        IReadOnlyList<StopSearchGroupResult> groups,
        TaichungBusDataService data,
        IReadOnlyCollection<string>? selected = null)
    {
        var options = new List<StopPickOption>();
        var defaults = new List<string>();
        var all = new List<string>();
        var picked = new HashSet<string>(selected ?? [], StringComparer.Ordinal);

        void Add(string value, string label, string description, bool isDefault)
        {
            options.Add(new StopPickOption(value, label, description, isDefault));
            all.Add(value);
            if (isDefault) defaults.Add(value);
        }

        foreach (var g in groups)
        {
            if (options.Count >= MaxOptions) break;

            // ⚠️ 由資料服務推導（**不是**用搜尋命中），確保與之後解析短鍵時看到的是同一份。
            //    這是「同名站牌找不到」的關鍵：面板按 g: 時解出的是整個站區的站牌。
            var byName = data.GetGroupNameBreakdown(g.GroupKey);
            if (byName.Count == 0) continue;

            // 只有 1 種站名時不需要「整個站區」選項
            if (byName.Count > 1 && options.Count < MaxOptions)
            {
                var groupValue = $"g:{g.GroupKey}";
                var totalStops = byName.Sum(n => n.StopUids.Count);

                Add(groupValue,
                    $"{g.DisplayName}（全部 {totalStops} 個）",
                    "整個站區",
                    picked.Contains(groupValue) || (picked.Count == 0 && g.IsDefaultPick));
            }

            for (var i = 0; i < byName.Count; i++)
            {
                if (options.Count >= MaxOptions) break;

                var (name, uids, routeCount) = byName[i];
                var value = $"n:{g.GroupKey}:{i}";

                var isDefault = picked.Contains(value)
                                || (picked.Count == 0 && g.IsDefaultPick && byName.Count == 1);

                var desc = uids.Count > 1
                    ? $"{g.DisplayName}｜同站 {uids.Count} 個站牌｜經過 {routeCount} 條路線"
                    : $"{g.DisplayName}｜經過 {routeCount} 條路線";

                Add(value, name, desc, isDefault);
            }
        }

        // 只有一個選項時就直接預設勾選 —— 使用者的意圖再明顯不過
        if (all.Count == 1 && defaults.Count == 0)
        {
            options[0] = options[0] with { IsDefault = true };
            defaults.Add(all[0]);
        }

        return new StopPickSet(options, defaults, all);
    }

    /// <summary>
    /// 把選項的值解析成 StopUID 清單 —— 與 <see cref="Build"/> 對稱。
    ///
    ///   `g:{群組ShortKey}`        → 該建議群組的**全部**站牌
    ///   `n:{群組ShortKey}:{索引}`  → 該群組裡第 N 種站名的全部站牌
    ///   `s:UID1,UID2,...`         → 直接指定（保留相容用）
    /// </summary>
    public static List<string> Resolve(IEnumerable<string> values, TaichungBusDataService data)
    {
        var uids = new List<string>();

        foreach (var v in values)
        {
            if (v.StartsWith("g:", StringComparison.Ordinal))
            {
                var group = data.GetGroupByShortKey(v[2..]);
                if (group is not null) uids.AddRange(group.StopUids);
            }
            else if (v.StartsWith("n:", StringComparison.Ordinal))
            {
                var parts = v[2..].Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out var idx))
                    uids.AddRange(data.GetGroupNameStopUids(parts[0], idx));
            }
            else if (v.StartsWith("s:", StringComparison.Ordinal))
            {
                foreach (var uid in v[2..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    uids.Add(uid.Trim());
            }
        }

        return uids.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>搜尋結果 → 預設勾選的 StopUID（面板的「一鍵套用」與 LLM 的預設都用這一條）。</summary>
    public static (List<string> Uids, bool Ambiguous, IReadOnlyList<string> Candidates) ResolveDefaults(
        IReadOnlyList<StopSearchGroupResult> groups, TaichungBusDataService data)
    {
        var set = Build(groups, data);
        var uids = Resolve(set.Defaults, data);

        if (uids.Count > 0) return (uids, false, []);

        // 沒有任何「精確命中」→ 不能自己挑一個（那正是訂錯路線的來源），
        // 把候選站區列出來讓呼叫端去問使用者。
        var candidates = set.Options
            .Where(o => o.Value.StartsWith("n:", StringComparison.Ordinal))
            .Select(o => o.Label)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();

        return ([], true, candidates);
    }
}
