namespace TcBusBot.Core.Storage;

/// <summary>
/// 儲存層的訊息出口。
///
/// 為什麼需要：「SQLite 開不起來，改用文字檔」這種事**一定要讓使用者看到** ——
/// 否則他會以為訂閱組存好了。但 Core 是零相依的函式庫，
/// 不該直接假設有主控台（Android 版是把 Console 接到畫面上）。
///
/// 所以：預設寫到 Console（桌面版看得到），主機可以換掉
/// （Android 版在 <c>MobileHost.Init</c> 之後，Console 已經被接到 App 日誌區）。
/// </summary>
public static class BotLog
{
    /// <summary>訊息出口；null = 寫到 Console。</summary>
    public static Action<string>? Sink { get; set; }

    public static void Warn(string message)
    {
        if (Sink is not null)
        {
            Sink(message);
            return;
        }

        try { Console.WriteLine(message); }
        catch (Exception) { /* 有些平台沒有主控台 */ }
    }
}
