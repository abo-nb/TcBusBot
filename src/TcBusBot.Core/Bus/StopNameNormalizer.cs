using System.Text;

namespace TcBusBot.Core.Bus;

/// <summary>
/// 站名正規化。
///
/// ⚠️ 這裡有「兩套」正規化，用途不同，**絕對不要混用**：
///   <see cref="ForSearch"/>  → 模糊搜尋用，**保留**括號內容（打「A月台」要能找到）
///   <see cref="ForGroup"/>   → 建議群組用，**去掉**括號內容（把 A月台與臺灣大道併成「臺中車站」）
///
/// 混用會導致「打 A月台 找不到」或「把不同站錯誤併成一組」。
///
/// 兩者都把字轉成**簡體**當作比對用的鍵（見 <see cref="ChineseText.ToSimplified"/>），
/// 但顯示用的站名仍然是 TDX 的原始繁體。
/// </summary>
public static class StopNameNormalizer
{
    /// <summary>
    /// 搜尋用正規化：全形→半形、簡繁折疊、大小寫、台灣地名變體、口語用語，最後移除空白與標點。
    /// 標點會被移除但**括號內的文字會保留**（例如 "臺中車站(A月台)" → "台中车站a月台"）。
    /// </summary>
    public static string ForSearch(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        // 1) 相容字元折疊：全形英數、全形括號 → 半形
        var t = raw.Normalize(NormalizationForm.FormKC);

        // 2) 簡繁折疊 → 一律轉成簡體（雙向都通：打「臺中車站」或「台中车站」都會得到同一個鍵）
        t = ChineseText.ToSimplified(t);

        // 3) 大小寫（英文站名）
        t = t.ToLowerInvariant();

        // 4) 台灣地名／用語變體。
        //    Windows 的對照表已經會處理大部分（臺→台、檯→台、裏→里…），
        //    這裡留著是為了非 Windows 的內建字表（覆蓋率較低，可能漏字）。
        t = t.Replace('臺', '台')
             .Replace('檯', '台')
             .Replace('裏', '里')
             .Replace('裡', '里')
             .Replace('爲', '为')
             .Replace('恆', '恒')
             .Replace('堃', '坤');

        // 5) 口語 vs 官方用語（使用者說「火車站」，TDX 資料是「車站」）
        //    兩種寫法都留著：轉換成功時看到的是簡體，轉換失敗時看到的是繁體
        t = t.Replace("火车站", "车站")
             .Replace("火車站", "車站");

        // 6) 移除空白與標點（括號本身去掉，內容留著）
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 分組用正規化：取第一個 '(' 之前的部分，並統一變體。
    /// "臺中車站(A月台)" → "台中车站"、"靜宜大學靜園餐廳" → "静宜大学静园餐厅"（無括號者不變）
    /// </summary>
    public static string ForGroup(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var t = raw.Normalize(NormalizationForm.FormKC);
        t = ChineseText.ToSimplified(t);

        var idx = t.IndexOfAny(['(', '（']);
        if (idx >= 0) t = t[..idx];

        t = t.Trim().ToLowerInvariant()
             .Replace('臺', '台')
             .Replace('檯', '台')
             .Replace('裏', '里')
             .Replace('裡', '里')
             .Replace('爲', '为')
             .Replace('恆', '恒')
             .Replace('堃', '坤');

        // 分組用的鍵不保留標點，避免「中正路(一)」與「中正路-一」被拆開
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
