using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace TcBusBot.Core.Bus;

/// <summary>
/// 簡繁轉換。**搜尋用的標準形式是「簡體」**（見 <see cref="ToSimplified"/>）。
///
/// ★ 為什麼是「繁 → 簡」而不是「簡 → 繁」（最重要的設計決定）
///
/// 簡→繁是**一對多**，Windows 的對照表只能挑一個，實測會挑錯：
/// <code>
///   ToTraditional("头发") = "頭發"   ← TDX 資料是「頭髮」→ 永遠對不上
///   ToTraditional("干净") = "干凈"   ← TDX 資料是「乾淨」→ 永遠對不上
///   ToTraditional("里面") = "里面"   ← 根本沒轉
/// </code>
/// 繁→簡是**多對一**，不會有這個問題，而且是幂等的：
/// <code>
///   "頭髮" → "头发"        "头發" → "头发"        "头发" → "头发"
/// </code>
/// 所以把「站名索引」與「使用者的查詢」都轉成簡體再比對，
/// 打繁體的人、打簡體的人、甚至打成半簡半繁，都會命中同一筆資料。
/// 站名顯示仍然用 TDX 的原始繁體（只有比對用的鍵是簡體）。
///
/// Windows 內建的 <c>LCMapStringEx</c> 就是完整的對照表（零維護、零套件）。
/// 實測 56 個台灣站牌常見詞，只有 12 個轉不出標準簡體，而且**全部都是「一對多」的字**
/// （乾→干、於→于、麼→么…），這些用一張小手寫表 <see cref="AmbiguousSimplifications"/> 補上。
/// </summary>
public static class ChineseText
{
    // LCMAP_SIMPLIFIED_CHINESE / LCMAP_TRADITIONAL_CHINESE
    private const uint LcmapSimplifiedChinese = 0x02000000;
    private const uint LcmapTraditionalChinese = 0x04000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string localeName, uint dwMapFlags, string lpSrcStr, int cchSrc,
        [Out] char[] lpDestStr, int cchDest,
        IntPtr lpVersionInformation, IntPtr lpReserved, IntPtr sortHandle);

    /// <summary>是否使用 Windows 內建轉換（true）或退回內建字表（false）。</summary>
    public static bool UsesWindowsApi { get; private set; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // 站名索引有 1.4 萬筆，同名站牌（「國立臺中科技大學」44 筆）字串完全一樣，
    // 快取可以省掉絕大多數的 P/Invoke。
    private static readonly ConcurrentDictionary<string, string> ToSimplifiedCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> ToTraditionalCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 搜尋用的標準形式：一律轉成簡體。
    /// 「頭髮」「头發」「头发」都會得到「头发」，所以三種輸入都能命中同一筆站牌。
    /// </summary>
    public static string ToSimplified(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        if (IsAscii(input)) return input;                      // 英文站名不需要轉

        if (ToSimplifiedCache.TryGetValue(input, out var cached)) return cached;

        var mapped = Map(input, LcmapSimplifiedChinese, "zh-CN") ?? FallbackToSimplified(input);
        var result = ApplyOneToManyFolds(mapped);

        ToSimplifiedCache[input] = result;
        return result;
    }

    /// <summary>
    /// 繁體化（顯示／除錯用，**搜尋不要用這個**）。
    ///
    /// ⚠️ 簡→繁是一對多，這裡只能給出「其中一個」繁體形式：
    /// 打「头发」會得到「頭發」而不是「頭髮」。要做比對請用 <see cref="ToSimplified"/>。
    /// </summary>
    public static string ToTraditional(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        if (IsAscii(input)) return input;

        if (ToTraditionalCache.TryGetValue(input, out var cached)) return cached;

        var result = Map(input, LcmapTraditionalChinese, "zh-TW") ?? FallbackToTraditional(input);
        ToTraditionalCache[input] = result;
        return result;
    }

    // ── Windows 對照表 ─────────────────────────────────────

    /// <summary>呼叫 Windows 的對照表；這個平台沒有這個 API 時回傳 null（並永久改用內建字表）。</summary>
    private static string? Map(string input, uint flags, string locale)
    {
        if (!UsesWindowsApi) return null;

        try
        {
            // 轉換後長度最多持平或略增，留一點餘裕
            var buffer = new char[input.Length * 2 + 8];
            var written = LCMapStringEx(
                locale, flags, input, input.Length,
                buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (written <= 0) return null;
            return new string(buffer, 0, written);
        }
        catch (Exception)
        {
            // DllNotFound / EntryPointNotFound（Wine、精簡版 Windows）→ 退回字表
            UsesWindowsApi = false;
            return null;
        }
    }

    /// <summary>只查有沒有一個字需要動，避免每個查詢都做一次 P/Invoke。</summary>
    private static bool IsAscii(string input)
    {
        foreach (var c in input)
            if (c > 127) return false;

        return true;
    }

    // ── Windows 表補不起來的「一對多」字 ────────────────────

    /// <summary>
    /// 一對多的字：兩個以上的繁體字簡化成同一個簡體字（發/髮→发、隻/只→只、乾/干→干…）。
    ///
    /// 這種字有兩個問題：
    ///   1. 反向（簡→繁）不可能做對 —— 只能挑一個，所以我們不往那個方向做（見類別說明）
    ///   2. Windows 的簡化表遇到這種字常常直接放棄（實測「乾淨」只轉成「乾净」）
    ///
    /// 這些字在台灣地名裡很常見，所以手動折疊。
    /// 對**搜尋**來說是安全的：即使某個名字裡的字本來就該是繁體（例如「乾隆」），
    /// 兩邊都會折疊成同一個形式，所以「乾溝」與「干溝」仍然互相找得到 ——
    /// 搜尋鍵不需要「正確」，只需要**一致**。
    /// </summary>
    private static readonly Dictionary<char, char> OneToManyFolds = new()
    {
        ['乾'] = '干',   // 乾淨→干净（乾淨/干淨是同一個詞的兩種寫法）
        ['於'] = '于',   // 於→于
        ['麼'] = '么',   // 什麼→什么（Windows 會給出「麽」這個異體字，也要一起折疊）
        ['麽'] = '么',
        ['甯'] = '宁',
        ['藉'] = '借',
        ['祗'] = '只',
        ['彷'] = '仿',
        ['菸'] = '烟',
        ['蔴'] = '麻',
        ['摺'] = '折',
        ['罣'] = '挂',
        ['燻'] = '熏',
        ['慾'] = '欲',
        ['堃'] = '坤',
        ['爲'] = '为',
        ['塚'] = '冢',
        ['蹟'] = '迹',
        ['裏'] = '里',   // Windows 已經會轉，這裡是給內建字表用的保險
        ['祕'] = '秘',
        ['發'] = '发',   // Windows 會轉，內建字表也需要
        ['髮'] = '发',
        ['隻'] = '只',
        ['麵'] = '面',
        ['週'] = '周',
        ['裡'] = '里',
        ['淨'] = '净',
        ['著'] = '着',
        ['畫'] = '画',
        ['彙'] = '汇',
        ['甦'] = '苏',
        ['儘'] = '尽',
        ['佈'] = '布',
        ['佔'] = '占',
        ['睏'] = '困',
    };

    private static string ApplyOneToManyFolds(string input)
    {
        var needsWork = false;
        foreach (var c in input)
            if (OneToManyFolds.ContainsKey(c)) { needsWork = true; break; }

        if (!needsWork) return input;

        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
            sb.Append(OneToManyFolds.TryGetValue(c, out var s) ? s : c);

        return sb.ToString();
    }

    // ── 非 Windows 的內建字表 ──────────────────────────────

    /// <summary>
    /// 備援用的常用簡→繁對照表（只有非 Windows 才會用到）。
    /// 收錄台灣公車站牌常見的字，不追求完整；
    /// <see cref="TraditionalToSimplified"/> 由這張表反轉而來，所以只要維護這一張。
    /// </summary>
    private static readonly Dictionary<char, char> SimplifiedToTraditional = new()
    {
        ['车'] = '車', ['台'] = '臺', ['湾'] = '灣', ['静'] = '靜', ['学'] = '學',
        ['国'] = '國', ['园'] = '園', ['场'] = '場', ['门'] = '門', ['医'] = '醫',
        ['荣'] = '榮', ['总'] = '總', ['东'] = '東', ['华'] = '華', ['兴'] = '興',
        ['运'] = '運', ['营'] = '營', ['铁'] = '鐵', ['桥'] = '橋', ['头'] = '頭',
        ['区'] = '區', ['号'] = '號', ['庄'] = '莊', ['丰'] = '豐', ['龙'] = '龍',
        ['凤'] = '鳳', ['庙'] = '廟', ['馆'] = '館', ['图'] = '圖', ['书'] = '書',
        ['体'] = '體', ['网'] = '網', ['银'] = '銀', ['钱'] = '錢',
        ['农'] = '農', ['业'] = '業', ['产'] = '產', ['电'] = '電', ['话'] = '話',
        ['机'] = '機', ['厂'] = '廠', ['广'] = '廣', ['义'] = '義', ['记'] = '記',
        ['认'] = '認', ['讲'] = '講', ['说'] = '說', ['语'] = '語', ['读'] = '讀',
        ['报'] = '報', ['纸'] = '紙', ['笔'] = '筆', ['题'] = '題',
        ['资'] = '資', ['讯'] = '訊', ['乐'] = '樂', ['药'] = '藥', ['护'] = '護',
        ['养'] = '養', ['疗'] = '療', ['诊'] = '診', ['剂'] = '劑', ['动'] = '動',
        ['劳'] = '勞', ['务'] = '務', ['员'] = '員', ['长'] = '長', ['团'] = '團',
        ['会'] = '會', ['议'] = '議', ['办'] = '辦', ['处'] = '處',
        ['师'] = '師', ['范'] = '範', ['贸'] = '貿', ['购'] = '購', ['货'] = '貨',
        ['价'] = '價', ['费'] = '費', ['财'] = '財', ['赛'] = '賽',
        ['厅'] = '廳', ['楼'] = '樓', ['层'] = '層', ['阶'] = '階', ['别'] = '別',
        ['对'] = '對', ['将'] = '將', ['专'] = '專', ['兰'] = '蘭', ['关'] = '關',
        ['单'] = '單', ['双'] = '雙', ['边'] = '邊', ['过'] = '過', ['还'] = '還',
        ['进'] = '進', ['远'] = '遠', ['连'] = '連', ['际'] = '際', ['陆'] = '陸',
        ['陈'] = '陳', ['阳'] = '陽', ['阴'] = '陰', ['马'] = '馬', ['鸟'] = '鳥',
        ['鱼'] = '魚', ['鹤'] = '鶴', ['狮'] = '獅', ['鸡'] = '雞', ['猪'] = '豬',
        ['径'] = '徑', ['树'] = '樹', ['环'] = '環', ['沟'] = '溝', ['坝'] = '壩',
        ['岭'] = '嶺', ['岗'] = '崗', ['旧'] = '舊', ['县'] = '縣', ['乡'] = '鄉',
        ['镇'] = '鎮', ['盘'] = '盤', ['矿'] = '礦', ['窑'] = '窯', ['仑'] = '崙',
        ['红'] = '紅', ['绿'] = '綠', ['蓝'] = '藍', ['黄'] = '黃', ['顺'] = '順',
        ['从'] = '從', ['众'] = '眾', ['条'] = '條', ['钟'] = '鐘', ['钢'] = '鋼',
        ['铝'] = '鋁', ['铺'] = '鋪', ['邮'] = '郵', ['递'] = '遞', ['汇'] = '匯',
        ['积'] = '積', ['亩'] = '畝', ['万'] = '萬', ['亿'] = '億', ['仅'] = '僅',
        ['们'] = '們', ['个'] = '個', ['为'] = '為', ['这'] = '這', ['来'] = '來',
        ['时'] = '時', ['间'] = '間', ['问'] = '問', ['现'] = '現', ['见'] = '見',
        ['觉'] = '覺', ['让'] = '讓', ['请'] = '請', ['谢'] = '謝', ['谁'] = '誰',
        ['课'] = '課', ['试'] = '試', ['验'] = '驗', ['结'] = '結', ['给'] = '給',
        ['统'] = '統', ['级'] = '級', ['约'] = '約', ['纪'] = '紀', ['纯'] = '純',
        ['线'] = '線', ['织'] = '織', ['经'] = '經', ['紧'] = '緊', ['练'] = '練',
        ['组'] = '組', ['细'] = '細', ['终'] = '終', ['继'] = '繼', ['绩'] = '績',
        ['缘'] = '緣', ['编'] = '編', ['发'] = '發', ['里'] = '裡', ['只'] = '隻',
        ['面'] = '麵', ['周'] = '週', ['着'] = '著', ['净'] = '淨', ['宁'] = '寧',
        ['涂'] = '塗', ['借'] = '藉', ['准'] = '準', ['凶'] = '兇', ['冲'] = '衝',
        ['划'] = '劃', ['铲'] = '剷', ['掺'] = '摻', ['困'] = '睏', ['杰'] = '傑',
        ['苏'] = '蘇', ['抵'] = '牴', ['欲'] = '慾', ['熏'] = '燻', ['折'] = '摺',
        ['挂'] = '掛', ['才'] = '纔', ['恒'] = '恆', ['瓮'] = '甕',
    };

    /// <summary>
    /// 繁體 → 簡體（非 Windows 用）：由 <see cref="SimplifiedToTraditional"/> 反轉，
    /// 再加上 <see cref="OneToManyFolds"/>（一對多的字反轉不回來，只能手動指定）。
    ///
    /// ⚠️ 這張表必須宣告在它依賴的兩張表之後 —— 靜態欄位依宣告順序初始化。
    /// </summary>
    private static readonly Dictionary<char, char> TraditionalToSimplified = BuildTraditionalToSimplified();

    private static Dictionary<char, char> BuildTraditionalToSimplified()
    {
        var map = new Dictionary<char, char>();

        // 多個簡體字對到同一個繁體時取第一個 —— 反正是搜尋鍵，一致就好
        foreach (var (simplified, traditional) in SimplifiedToTraditional)
            map.TryAdd(traditional, simplified);

        foreach (var (traditional, simplified) in OneToManyFolds)
            map[traditional] = simplified;

        return map;
    }

    private static string FallbackToSimplified(string input) => FallbackMap(input, TraditionalToSimplified);

    private static string FallbackToTraditional(string input) => FallbackMap(input, SimplifiedToTraditional);

    private static string FallbackMap(string input, Dictionary<char, char> map)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
            sb.Append(map.TryGetValue(c, out var t) ? t : c);

        return sb.ToString();
    }
}
