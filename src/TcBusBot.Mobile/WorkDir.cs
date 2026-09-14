using Android.Content;
using Android.OS;

namespace TcBusBot.Mobile;

/// <summary>
/// 工作目錄：Bot 的 <c>app.env</c>／<c>tcbus.db</c>／<c>cache/</c>／<c>fixtures/</c> 要放哪裡。
///
/// 為什麼需要這個：App 私有目錄（<c>/data/data/（套件名稱）/files</c>）在**沒 root 的手機上
/// 幾乎拿不出來** —— 檔案管理員看不到、MTP 看不到、只有 adb 能撈。
/// 想要「用電腦看一下日誌、把 tcbus.db 拿出來、改個 app.env」就會很痛苦。
///
/// 所以提供三個選項：
///
/// | 選項 | 路徑 | 誰看得到 |
/// | --- | --- | --- |
/// | 私有（預設） | `/data/data/（套件名稱）/files` | 只有 adb（`run-as`） |
/// | App 專屬外部 | `/storage/emulated/0/Android/data/（套件名稱）/files` | Android 10 以前：檔案管理員／MTP；11 以後被系統擋住 |
/// | **手機儲存空間** | `/storage/emulated/0/TcBusBot` | **檔案管理員、USB、任何同步 App** |
/// | 自訂 | 使用者輸入的絕對路徑 | 看那個位置 |
///
/// ⚠️ 公開目錄在 Android 10 以前只要 `WRITE_EXTERNAL_STORAGE` 執行期權限；
///    Android 11 以後需要「所有檔案存取權」（`MANAGE_EXTERNAL_STORAGE`），
///    所以 UI 會依版本提示對應的授權步驟。
/// </summary>
public static class WorkDir
{
    private const string Prefs = "tcbus";
    private const string KeyPath = "workdir";

    public const string PublicFolderName = "TcBusBot";

    /// <summary>App 私有目錄（一定可寫，不需要任何權限）。</summary>
    public static string PrivateBase(Context ctx) => ctx.FilesDir!.AbsolutePath;

    /// <summary>App 專屬外部目錄（Android 10 以前檔案管理員看得到）。</summary>
    public static string? ExternalAppBase(Context ctx) => ctx.GetExternalFilesDir(null)?.AbsolutePath;

    /// <summary>手機儲存空間的公開目錄（真正好用、用 USB 就看得到的那個）。</summary>
    public static string PublicBase
        => Path.Combine(global::Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath!, PublicFolderName);

    /// <summary>目前選定的工作目錄（沒設定過 = 私有目錄）。</summary>
    public static string Resolve(Context ctx)
    {
        var prefs = ctx.GetSharedPreferences(Prefs, FileCreationMode.Private);
        var saved = prefs?.GetString(KeyPath, null);

        if (!string.IsNullOrWhiteSpace(saved)) return saved!;

        // 第一次啟動：試著用公開目錄（好拿檔案）；不行就退回私有目錄
        var pub = PublicBase;
        return CanWrite(pub) ? pub : PrivateBase(ctx);
    }

    public static void Set(Context ctx, string path)
    {
        var prefs = ctx.GetSharedPreferences(Prefs, FileCreationMode.Private);
        prefs?.Edit()?.PutString(KeyPath, path)?.Apply();
    }

    public static void ResetToPrivate(Context ctx)
    {
        var prefs = ctx.GetSharedPreferences(Prefs, FileCreationMode.Private);
        prefs?.Edit()?.Remove(KeyPath)?.Apply();
    }

    /// <summary>目前這台手機可以選哪些目錄（含「需不需要授權」的說明）。</summary>
    public static List<WorkDirOption> Candidates(Context ctx)
    {
        var list = new List<WorkDirOption>
        {
            new("App 私有目錄（預設）", PrivateBase(ctx), "一定可寫；要 adb 才拿得出來"),
        };

        if (ExternalAppBase(ctx) is { } ext)
            list.Add(new("App 專屬外部目錄", ext, "Android 10 以前檔案管理員看得到；11 以後被系統隱藏"));

        list.Add(new("手機儲存空間 /TcBusBot", PublicBase,
            HasStorageAccess(ctx) ? "用檔案管理員／USB 就看得到（建議）" : "需要先授權儲存權限"));

        return list;
    }

    /// <summary>公開目錄需要什麼權限、給了沒。</summary>
    public static bool HasStorageAccess(Context ctx)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // Android 11+：公開目錄要「所有檔案存取權」
            return global::Android.OS.Environment.IsExternalStorageManager;
        }

        return ctx.CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage)
               == global::Android.Content.PM.Permission.Granted;
    }

    /// <summary>這個版本要請使用者做什麼（沒授權時顯示在對話框裡）。</summary>
    public static string StoragePermissionHint
        => OperatingSystem.IsAndroidVersionAtLeast(30)
            ? "Android 11 以後要按「所有檔案存取權」→ 允許「管理所有檔案」"
            : "需要允許「儲存」權限";

    /// <summary>
    /// 把目錄準備好：建立 <c>cache/</c>、<c>fixtures/mini/</c>（沒有 TDX 金鑰時用的離線資料集），
    /// 並且實際試寫一個檔案確認可寫。回傳 false 時 <paramref name="error"/> 說明原因。
    /// </summary>
    public static bool EnsureStructure(Context ctx, string root, out string error)
    {
        error = "";

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "cache"));

            var mini = Path.Combine(root, "fixtures", "mini");
            Directory.CreateDirectory(mini);

            CopyAsset(ctx, "mini/Stops.psv", Path.Combine(mini, "Stops.psv"));
            CopyAsset(ctx, "mini/StopOfRoute.psv", Path.Combine(mini, "StopOfRoute.psv"));

            // 真的寫一個檔案：目錄存在不代表有寫入權限（尤其 Android 11+ 的公開目錄）
            var probe = Path.Combine(root, ".tcbus-write-test");
            File.WriteAllText(probe, DateTimeOffset.UtcNow.ToString("O"));
            File.Delete(probe);

            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>只檢查可不可以寫（低成本的探測）。</summary>
    public static bool CanWrite(string root)
    {
        try
        {
            Directory.CreateDirectory(root);

            var probe = Path.Combine(root, ".tcbus-write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void CopyAsset(Context ctx, string assetName, string target)
    {
        if (File.Exists(target)) return;

        try
        {
            using var input = ctx.Assets!.Open(assetName);
            using var output = File.Create(target);
            input.CopyTo(output);
        }
        catch (Exception ex)
        {
            BotConsole.Write($"[目錄] 無法複製離線資料集 {assetName}：{ex.Message}\n");
        }
    }
}

/// <summary>對話框裡的一個選項。</summary>
public sealed record WorkDirOption(string Label, string Path, string Note);
