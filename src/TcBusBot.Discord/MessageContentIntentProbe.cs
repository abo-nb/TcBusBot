using System.Text.Json;

namespace TcBusBot.Discord;

/// <summary>
/// 開機前先問 Discord：「這個應用程式有沒有被允許 Message Content 意圖？」
///
/// ── 為什麼需要這個 ─────────────────────────────────────
/// Message Content 是**特權意圖**，必須在 Developer Portal 手動打開。
/// 如果程式要了卻沒被允許，Discord 會用 close code **4014** 把連線踢掉，
/// 而 Discord.Net 3.18 對這件事只印一行
/// `WebSocketException: WebSocket connection was closed` ——
/// 完全看不出原因（我把組件裡的字串翻過一次，沒有 "Disallowed intent" 這種訊息）。
///
/// 更糟的是：先前的流程會在「等不到 Ready」時**直接結束行程**，
/// 於是 Render 看到的是 502 + 不斷重啟，而使用者只看得到一行 WebSocketException。
///
/// 所以在建立閘道連線**之前**先用 REST 問一次：`GET /applications/@me` 的 `flags`
/// 會告訴我們意圖有沒有開（見 <see cref="MessageContentEnabled"/>）。
/// 沒開就不要要它 —— Bot 照常上線（公車功能全部可用），並把「去哪裡按什麼」印出來。
///
/// ⚠️ 問不到（沒網路、Discord 掛掉、權限問題）時回傳 <c>null</c>，
///    代表「不確定」——這時候**照使用者的設定走**，不要自己偷偷降級。
/// </summary>
public static class MessageContentIntentProbe
{
    /// <summary>
    /// 從 application flags 判斷 Message Content 意圖是否可用。
    ///
    /// Discord 的定義（APPLICATION_FLAGS）：
    ///   * bit 18 `GATEWAY_MESSAGE_CONTENT`         —— 未驗證的 Bot（&lt;100 伺服器）打開開關就有
    ///   * bit 19 `GATEWAY_MESSAGE_CONTENT_LIMITED` —— 已驗證並通過審核的 Bot
    /// 兩個其中一個成立就代表可以要這個意圖。
    /// </summary>
    public static bool MessageContentEnabled(long flags)
        => (flags & (1L << 18)) != 0 || (flags & (1L << 19)) != 0;

    /// <summary>解析 `flags` 欄位（Discord 有時給數字、有時給字串）。</summary>
    public static long ParseFlags(JsonElement root)
    {
        if (!root.TryGetProperty("flags", out var flags)) return 0;

        return flags.ValueKind switch
        {
            JsonValueKind.Number => flags.TryGetInt64(out var n) ? n : 0,
            JsonValueKind.String => long.TryParse(flags.GetString(), out var s) ? s : 0,
            _ => 0
        };
    }

    /// <summary>
    /// 問一次。回傳 true = 已開啟、false = 確定沒開、null = 問不到（不確定）。
    /// </summary>
    public static async Task<bool?> CheckAsync(
        string token, int timeoutSeconds = 15, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)) };
            http.DefaultRequestHeaders.Add("Authorization", $"Bot {token}");

            using var response = await http.GetAsync("https://discord.com/api/v10/applications/@me");

            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke($"[意圖] 檢查失敗（HTTP {(int)response.StatusCode}）→ 照設定要求");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var flags = ParseFlags(doc.RootElement);
            var enabled = MessageContentEnabled(flags);

            log?.Invoke($"[意圖] Message Content：{(enabled ? "已開啟 ✅" : "沒有開啟 ❌")}（flags={flags}）");
            return enabled;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[意圖] 檢查失敗（{ex.GetType().Name}）→ 照設定要求");
            return null;
        }
    }

    /// <summary>沒開意圖時要講的話（照著做就會好）。</summary>
    public static void PrintHowToEnable(Action<string> log)
    {
        log("");
        log("⚠️  Discord 沒有允許 Message Content 意圖 → **這次先不要它**，Bot 照常上線。");
        log("");
        log("   影響：只有「@ 提及 Bot」的訊息收得到（Discord 對提及會例外給內容），");
        log("         用「回覆 Bot 訊息」的方式聊天會收不到內容。公車功能完全不受影響。");
        log("");
        log("   要完整開啟 AI 聊天的話：");
        log("     1. https://discord.com/developers/applications → 選你的 Application");
        log("     2. 左側 Bot → Privileged Gateway Intents → 打開 **Message Content Intent**");
        log("     3. Save Changes → 重新啟動這個服務（Render 後台 Manual Deploy 或改一個環境變數）");
        log("");
    }
}
