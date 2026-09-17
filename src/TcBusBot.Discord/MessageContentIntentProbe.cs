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

    /// <summary>
    /// 從 application flags 判斷 **Server Members 意圖**（伺服器成員）是否可用。
    ///
    /// 為什麼需要它：**讀得到別人的伺服器暱稱**是這個意圖的功勞。
    /// 沒開的話，Discord 不會把成員資料（含暱稱）給 Bot，
    /// 「讓模型看到暱稱」（`LLM_SHOW_NICKNAMES`）就會**默默退回帳號名** ——
    /// 功能看起來有開，實際上模型還是只看到 `@wuxiaohan0922`。
    ///
    /// Discord 的定義（APPLICATION_FLAGS）：
    ///   * bit 14 `GATEWAY_GUILD_MEMBERS`         —— 未驗證的 Bot（&lt;100 伺服器）打開開關就有
    ///   * bit 15 `GATEWAY_GUILD_MEMBERS_LIMITED` —— 已驗證並通過審核的 Bot
    /// </summary>
    public static bool GuildMembersEnabled(long flags)
        => (flags & (1L << 14)) != 0 || (flags & (1L << 15)) != 0;

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

    /// <summary>一次檢查的結果（<c>null</c> = 問不到，不確定）。</summary>
    public sealed record IntentCheck(bool? MessageContent, bool? GuildMembers);

    /// <summary>
    /// 問一次「兩個特權意圖有沒有被允許」。兩個都問不到（沒網路／Discord 掛掉）時回傳
    /// 兩個 <c>null</c>，呼叫端要照使用者的設定走（不偷偷降級），但**寧可不要特權意圖**：
    /// 要了沒被允許的意圖，閘道會直接用 4014 把連線踢掉。
    /// </summary>
    public static async Task<IntentCheck> CheckAllAsync(
        string token, int timeoutSeconds = 15, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return new IntentCheck(null, null);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)) };
            http.DefaultRequestHeaders.Add("Authorization", $"Bot {token}");

            // ⚠️ User-Agent 一定要有：Discord 對「沒有 UA」的請求回 **403**，
            //    而那會被當成「問不到」→ 退回「照設定要求」，
            //    等於這一整段探測**從來沒有真的生效過**（實測：加 UA 之後才拿得到 flags）。
            http.DefaultRequestHeaders.Add("User-Agent",
                "DiscordBot (https://github.com/abo-nb/TcBusBot, 1.0)");

            using var response = await http.GetAsync("https://discord.com/api/v10/applications/@me");

            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke($"[意圖] 檢查失敗（HTTP {(int)response.StatusCode}）→ 照設定要求");
                return new IntentCheck(null, null);
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var flags = ParseFlags(doc.RootElement);
            var messageContent = MessageContentEnabled(flags);
            var members = GuildMembersEnabled(flags);

            log?.Invoke($"[意圖] Message Content：{(messageContent ? "已開啟 ✅" : "沒有開啟 ❌")}｜" +
                        $"Server Members（讀得到暱稱）：{(members ? "已開啟 ✅" : "沒有開啟 ❌")}（flags={flags}）");

            return new IntentCheck(messageContent, members);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[意圖] 檢查失敗（{ex.GetType().Name}）→ 照設定要求");
            return new IntentCheck(null, null);
        }
    }

    /// <summary>只問 Message Content（相容用）。</summary>
    public static async Task<bool?> CheckAsync(
        string token, int timeoutSeconds = 15, Action<string>? log = null)
        => (await CheckAllAsync(token, timeoutSeconds, log)).MessageContent;

    /// <summary>沒開 Server Members 意圖（讀不到暱稱）時要講的話。</summary>
    public static void PrintHowToEnableMembers(Action<string> log)
    {
        log("");
        log("ℹ️  Discord 沒有允許 **Server Members 意圖** → 這次不要它（要了會被 4014 踢掉）。");
        log("   影響：模型只讀得到帳號名（@wuxiaohan0922），讀不到大家的**伺服器暱稱**（小明）——");
        log("         判斷「這句話是不是在對我說話」時認人會變差。公車功能完全不受影響。");
        log("");
        log("   要讓它看到暱稱：");
        log("     1. https://discord.com/developers/applications → 選你的 Application");
        log("     2. 左側 Bot → Privileged Gateway Intents → 打開 **Server Members Intent**");
        log("     3. Save Changes → 重新啟動這個服務");
        log("");
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
