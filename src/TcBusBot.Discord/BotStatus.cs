using System.Text.Json;

namespace TcBusBot.Discord;

/// <summary>
/// 目前狀態的快照，給健康檢查端點用。
///
/// 為什麼要一個共用的靜態類別：健康檢查端點**必須在啟動後幾秒內就開好埠**
/// （Render 會探測，太慢就判定部署失敗），所以它比 Discord 連線、靜態資料載入都早啟動。
/// 那時 `subs`／`cache` 這些物件還不存在，用區域變數會抓不到（C# 也不允許
/// 區域函式捕捉之後才宣告的變數）。所以由 <see cref="Program"/> 邊建立邊填進來，
/// 端點只負責讀。
/// </summary>
internal static class BotStatus
{
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public static bool DiscordReady { get; set; }
    public static string StorageMode { get; set; } = "（尚未初始化）";
    public static string DataSource { get; set; } = "（尚未載入）";
    public static string Poller { get; set; } = "（尚未啟動）";

    /// <summary>由 Program 在建立快取後填入（回傳目前快取筆數）。</summary>
    public static Func<int>? CachedEtas { get; set; }

    /// <summary>由 Program 在建立訂閱服務後填入。</summary>
    public static Func<(int Groups, int Subscriptions)>? SubscriptionCounts { get; set; }

    /// <summary>AI 聊天狀態（模型、每週用量）——由 Program 在建立 LLM 後填入。</summary>
    public static Func<string>? Llm { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ToJson()
    {
        try
        {
            var (groups, subs) = SubscriptionCounts?.Invoke() ?? (0, 0);

            return JsonSerializer.Serialize(new
            {
                status = "ok",
                service = "TcBusBot",
                uptimeSeconds = (int)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
                startedAt = StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                discordReady = DiscordReady,
                storage = StorageMode,
                dataSource = DataSource,
                poller = Poller,
                subscriptionGroups = groups,
                subscriptions = subs,
                cachedEtas = CachedEtas?.Invoke() ?? 0,
                llm = Llm?.Invoke() ?? "未啟用"
            }, Json);
        }
        catch (Exception ex)
        {
            // 健康檢查本身絕對不能丟例外（那會讓平台以為服務掛了）
            return $"{{\"status\":\"error\",\"message\":\"{ex.GetType().Name}\"}}";
        }
    }
}
