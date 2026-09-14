using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using TcBusBot.Core.Storage;

namespace TcBusBot.Mobile;

/// <summary>
/// 前景服務：在手機上跑既有的 Bot。
///
/// 為什麼一定要前景服務：
///   Android 會隨時殺掉一般背景程序，而這個服務要「整晚掛著」等公車快到。
///   前景服務會顯示常駐通知（Android 的規定），系統就不會隨便回收它。
///
/// 為什麼是「一條執行緒直接跑 <c>Program.Main</c>」：
///   桌面版與手機版共用同一個進入點 —— Bot 的啟動流程、指令註冊、
///   輪詢、訂閱組都在 <c>TcBusBot.Discord</c> 裡，這裡不做任何複製。
///   停止則靠 <c>Program.RequestStop()</c>（桌面版是 Ctrl+C）。
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class BotService : Service
{
    public const string ChannelId = "tcbusbot";
    public const int NotificationId = 1;

    public const string ActionStart = "com.tcbusbot.mobile.START";
    public const string ActionStop = "com.tcbusbot.mobile.STOP";

    /// <summary>服務是否正在跑（畫面用來顯示狀態）。</summary>
    public static bool IsRunning { get; private set; }

    private Thread? _thread;

    public override void OnCreate()
    {
        base.OnCreate();

        // ★ 這裡一定要初始化：服務被系統以 START_STICKY 重新拉起時，
        //   Activity 不會執行，只有這個 OnCreate 會跑。
        MobileHost.Init(this);

        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case ActionStop:
                StopBot();
                StopSelf();
                return StartCommandResult.NotSticky;

            default:
                StartForeground(NotificationId, BuildNotification("Bot 啟動中…"));
                StartBot();
                // 被系統殺掉後自動重來 —— 手機版最需要的就是這個
                return StartCommandResult.Sticky;
        }
    }

    private void StartBot()
    {
        if (IsRunning) return;

        IsRunning = true;

        _thread = new Thread(() =>
        {
            try
            {
                // ★ 與桌面版完全相同的進入點
                var exit = TcBusBot.Discord.Program.Main(AppSettings.BuildBotArgs(this)).GetAwaiter().GetResult();
                BotConsole.Write($"\n[宿主] Bot 已結束（exit {exit}）\n");
            }
            catch (Exception ex)
            {
                BotConsole.Write($"\n[宿主] Bot 當掉了：{ex.GetType().Name}: {ex.Message}\n{ex}\n");
            }
            finally
            {
                IsRunning = false;
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
            }
        })
        {
            IsBackground = true,
            Name = "tcbusbot"
        };

        _thread.Start();
    }

    private void StopBot()
    {
        if (!IsRunning) return;

        BotConsole.Write("\n[宿主] 收到停止指令…\n");
        TcBusBot.Discord.Program.RequestStop();
    }

    public override void OnDestroy()
    {
        StopBot();
        base.OnDestroy();
    }

    /// <summary>這是一個「啟動式」服務，不需要繫結（給 Android 的樣板要求）。</summary>
    public override IBinder? OnBind(Intent? intent) => null;

    /// <summary>Android 8+ 一定要有通知管道，否則前景服務的通知會被丟掉。</summary>
    private void CreateNotificationChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

        var channel = new NotificationChannel(ChannelId, "公車通知服務", NotificationImportance.Low)
        {
            Description = "讓 Bot 在背景持續運作（等公車快到時通知你）"
        };

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(string text)
    {
        var open = PendingIntent.GetActivity(
            this, 0,
            new Intent(this, typeof(MainActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        builder.SetContentTitle("台中公車通知")
               .SetContentText(text)
               .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)
               .SetOngoing(true)
               .SetContentIntent(open);

        return builder.Build()!;
    }

    // ── 給畫面用的靜態控制 ────────────────────────────────

    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(BotService));
        intent.SetAction(ActionStart);

        if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
        else context.StartService(intent);
    }

    public static void Stop(Context context)
    {
        var intent = new Intent(context, typeof(BotService));
        intent.SetAction(ActionStop);
        context.StartService(intent);
    }
}
