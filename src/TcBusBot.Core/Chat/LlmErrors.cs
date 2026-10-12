using System.Net;
using System.Reflection;
using System.Text;

namespace TcBusBot.Core.Chat;

/// <summary>
/// 把「呼叫模型失敗」翻成**種類 ＋ 看得懂的一句話**（純函式，可離線測試）。
///
/// ── 為什麼要有「種類」而不只是字串 ────────────────────────────
/// `/ai llm` 換金鑰之後會立刻打一次測試，而兩種失敗的處置**完全相反**：
///   * 金鑰被拒絕（401／403）→ 幾乎一定是新設定打錯 → **自動退回舊設定**
///   * 網路不通／逾時／被限流 → 設定可能是對的 → **絕對不能退**
/// 用訊息文字去猜這種事太脆弱，所以分類只做一次、集中在這裡，而且**先看 HTTP 狀態碼**。
///
/// ── 為什麼不能只比對字串（實測）──────────────────────────────
/// 拿一組故意打錯的金鑰去打 DeepSeek，回來的 401 內容是：
/// <code>
/// {"error":{"message":"Authentication Fails, Your api key: ****0000 is invalid ...",
///           "type":"authentication_error","code":"invalid_request_error"}}
/// </code>
/// 裡面**沒有**「401」也**沒有**「Unauthorized」—— 只比對那幾個關鍵字的話，
/// 這個最常見的失敗會被分類成「說不出來」，於是自動回退永遠不會發生
/// （症狀就是「設完之後 Bot 安靜地壞掉，而且沒有人知道為什麼」）。
/// 所以：**狀態碼優先**（例外物件上有 `StatusCode`／訊息裡有 3 位數狀態碼），
/// 文字標記為輔（包含各家服務真實的文案：`Authentication Fails`、`authentication_error`…）。
/// </summary>
public static class LlmErrors
{
    /// <summary>
    /// 分類。<paramref name="status"/> 是 HTTP 狀態碼（拿不到就 null），
    /// <paramref name="text"/> 是例外訊息（含 inner 的訊息）。
    /// </summary>
    public static (LlmErrorKind Kind, string Message) Classify(
        int? status, string? text, string? exceptionType, string host, string model,
        bool networkException = false)
    {
        var raw = Shorten(text);
        var kind = KindOf(status, raw ?? "");

        // 例外型別本身就是網路問題（HttpRequestException／IOException／SocketException）——
        // 有些訊息（SSL 憑證、位址無法解析…）的文字不一定命中上面的標記，但型別是鐵證
        if (kind == LlmErrorKind.Unknown && networkException) kind = LlmErrorKind.Network;

        return (kind, Describe(kind, host, model, raw, exceptionType));
    }

    /// <summary>例外鏈裡有沒有「網路類」的例外（含 inner）。</summary>
    public static bool IsNetwork(Exception? error)
    {
        for (var ex = error; ex is not null; ex = ex.InnerException)
        {
            if (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException)
                return true;
        }

        return false;
    }

    /// <summary>先看狀態碼，再看文字標記。</summary>
    public static LlmErrorKind KindOf(int? status, string text)
    {
        // ── 1) HTTP 狀態碼（最可靠）──────────────────────
        if (status is { } code)
        {
            if (code is 401 or 403) return LlmErrorKind.Auth;
            if (code is 402) return LlmErrorKind.Quota;
            if (code is 404) return LlmErrorKind.ModelNotFound;
            if (code is 408) return LlmErrorKind.Timeout;
            if (code is 429) return LlmErrorKind.RateLimited;
            // 5xx 是**服務端**的問題：可能是暫時的，不是「你設定錯了」→ 不該自動回退
            if (code is >= 500) return LlmErrorKind.Unknown;
        }

        // ── 2) 訊息裡的狀態碼（例外被包裝過、拿不到 StatusCode 時）──
        if (status is null)
        {
            if (text.Contains("401", StringComparison.Ordinal)
                || text.Contains("403", StringComparison.Ordinal)) return LlmErrorKind.Auth;

            if (text.Contains("429", StringComparison.Ordinal)) return LlmErrorKind.RateLimited;
        }

        // ── 3) 各家服務的文案 ────────────────────────────
        if (text.Contains("Authentication Fails", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authentication_error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
            || text.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase)
            || text.Contains("api key is invalid", StringComparison.OrdinalIgnoreCase)
            || text.Contains("incorrect api key", StringComparison.OrdinalIgnoreCase))
            return LlmErrorKind.Auth;

        if (text.Contains("model_not_found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || text.Contains("model not found", StringComparison.OrdinalIgnoreCase))
            return LlmErrorKind.ModelNotFound;

        if (text.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return LlmErrorKind.RateLimited;

        if (text.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
            || text.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || text.Contains("balance", StringComparison.OrdinalIgnoreCase))
            return LlmErrorKind.Quota;

        if (text.Contains("Name or service not known", StringComparison.Ordinal)
            || text.Contains("No such host", StringComparison.Ordinal)
            || text.Contains("Connection refused", StringComparison.Ordinal)
            || text.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return LlmErrorKind.Network;

        return LlmErrorKind.Unknown;
    }

    /// <summary>給使用者看的一句話（絕不含金鑰）。</summary>
    private static string Describe(
        LlmErrorKind kind, string host, string model, string? raw, string? exceptionType)
        => kind switch
        {
            LlmErrorKind.Auth =>
                $"金鑰被拒絕（{host}）：檢查金鑰是否正確、有沒有過期或被撤銷",
            LlmErrorKind.ModelNotFound =>
                $"找不到模型「{model}」（{host}）：檢查模型名稱",
            LlmErrorKind.RateLimited =>
                $"服務端限流（429，{host}）：等一下再試，或換一個模型",
            LlmErrorKind.Quota =>
                $"服務端餘額不足（{host}）：去服務商後台加值",
            LlmErrorKind.Timeout =>
                $"服務端等太久（{host}）：稍後再試",
            LlmErrorKind.Network =>
                $"連不上 {host}（網路或網址錯誤）",
            _ => $"呼叫失敗：{exceptionType ?? "未知錯誤"}: {raw}"
        };

    /// <summary>
    /// 從例外（含 inner）撈 HTTP 狀態碼。
    ///
    /// 為什麼用反射：SK 的 <c>HttpOperationException</c> 有 <c>StatusCode</c>，
    /// .NET 的 <c>HttpRequestException</c> 也有，但型別版本會變 —— 用屬性名稱讀最耐用
    /// （這裡只在**失敗**時跑一次，不是熱路徑）。
    /// </summary>
    public static int? StatusOf(Exception? error)
    {
        for (var ex = error; ex is not null; ex = ex.InnerException)
        {
            var property = ex.GetType().GetProperty("StatusCode", BindingFlags.Public | BindingFlags.Instance);

            switch (property?.GetValue(ex))
            {
                case HttpStatusCode code: return (int)code;
                case int number: return number;
            }
        }

        return null;
    }

    /// <summary>例外的「訊息 ＋ inner 訊息」（分類要看得到全部的線索）。</summary>
    public static string TextOf(Exception error)
    {
        var text = new StringBuilder(error.Message ?? "");

        for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (string.IsNullOrWhiteSpace(inner.Message)) continue;

            text.Append(' ').Append(inner.Message);
        }

        return text.ToString();
    }

    private static string Shorten(string? text)
    {
        var raw = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

        return raw.Length > 200 ? raw[..200] + "…" : raw;
    }
}
