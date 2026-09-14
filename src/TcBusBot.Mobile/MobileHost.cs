using Android.Content;
using TcBusBot.Core.Storage;

namespace TcBusBot.Mobile;

/// <summary>
/// Android 宿主的一次性初始化：把 Console 接到畫面、把 SQLite 換成 Android 的實作。
///
/// ⚠️ 為什麼不放在自訂的 <c>[Application]</c> 子類別（那看起來最自然，但會當掉）：
///
///   .NET Android 為每個 Java 可見型別產生的 stub，是靠**建構子裡的
///   <c>mono.android.TypeManager.Activate(...)</c>** 把 Java 物件接上受管理的實例、
///   並註冊 JNI native 方法。實測（`obj/.../android/src/crc64.../*.java`）：
///
///     MainActivity()  → TypeManager.Activate("TcBusBot.Mobile.MainActivity, …")   ✅
///     BotService()    → TypeManager.Activate("TcBusBot.Mobile.BotService, …")     ✅
///     MainApplication() → 只有 mono.MonoPackageManager.setContext(this)           ❌
///
///   Application 的 stub 沒有那個 Activate 呼叫，於是
///   <c>Application.onCreate</c> → <c>n_onCreate()</c> 找不到實作：
///
///     java.lang.UnsatisfiedLinkError: No implementation found for void
///       crc64…MainApplication.n_onCreate()
///
///   所以改在 **Activity 與 Service** 這兩個「接線確定正常」的進入點做初始化。
///   原本做在 Application 只是為了「服務自行重啟時也初始化得到」——
///   而 Service.OnCreate 本來就一定會跑到，所以功能沒有損失。
/// </summary>
public static class MobileHost
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void Init(Context context)
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;

            // Console → App 日誌區 + logcat（Bot 完全不改一行輸出程式碼）
            BotConsole.Install();

            // ★ Android 的 SQLite（Windows 版是內建的 winsqlite3；Android 沒有它）
            SqliteBackends.Factory = path => new AndroidSqliteBackend(path);
        }

        // 工作目錄結構（cache/、fixtures/）與可寫性檢查
        AppSettings.EnsureBootstrapped(context);

        BotConsole.Write($"[宿主] 初始化完成，工作目錄：{AppSettings.Dir(context)}\n");
    }
}
