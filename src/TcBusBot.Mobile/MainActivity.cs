using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Text.Method;
using Android.Views;
using Android.Widget;
using TcBusBot.Core.Storage;

namespace TcBusBot.Mobile;

/// <summary>
/// 手機版畫面：填設定 → 啟動服務 → 看日誌。
///
/// 刻意用程式碼建立 UI（不用 XML 版面、不用 AppCompat）：
/// 少一層相依、少一個會壞掉的資源檔，而且整個畫面只有三個區塊。
/// </summary>
[Activity(
    Label = "台中公車通知",
    MainLauncher = true,
    Icon = "@android:drawable/ic_menu_info_details",
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : Activity
{
    private EditText _token = null!;
    private EditText _tdxId = null!;
    private EditText _tdxSecret = null!;
    private EditText _mongoUri = null!;
    private EditText _poll = null!;
    private EditText _notify = null!;
    private TextView _status = null!;
    private TextView _log = null!;
    private ScrollView _logScroll = null!;
    private Button _startStop = null!;
    private Handler _handler = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Console 與 SQLite 後端（Service 那邊也會呼叫，這裡是為了直接開 App 的情況）
        MobileHost.Init(this);

        SetContentView(BuildLayout());

        RequestNotificationPermissionIfNeeded();

        _handler = new Handler(Looper.MainLooper!);
        _handler.Post(RefreshLoop);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Refresh();
    }

    // ── 版面 ──────────────────────────────────────────────

    private View BuildLayout()
    {
        var scroll = new ScrollView(this);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));
        scroll.AddView(root);

        root.AddView(Header("🚌 台中公車通知"));
        _status = new TextView(this) { TextSize = 14 };
        _status.SetPadding(0, Dp(4), 0, Dp(12));
        root.AddView(_status);

        var saved = AppSettings.Load(this);

        _token = Field(root, "Discord Bot Token", saved.Token, password: true);
        _tdxId = Field(root, "TDX Client ID（可留空）", saved.TdxId);
        _tdxSecret = Field(root, "TDX Client Secret（可留空）", saved.TdxSecret, password: true);
        _mongoUri = Field(root, "MongoDB 連線字串（可留空；填了就所有機器共用同一份訂閱組）",
                          saved.MongoUri, password: true);
        _poll = Field(root, "輪詢間隔（秒，預設 30；低於 20 沒有意義）", saved.PollSeconds.ToString(), numeric: true);
        _notify = Field(root, "預設提前通知（分鐘，預設 10）", saved.NotifyMinutes.ToString(), numeric: true);

        var save = new Button(this) { Text = "💾 儲存設定" };
        save.Click += (_, _) => SaveSettings();
        root.AddView(save);

        var workDir = new Button(this) { Text = "📁 選擇工作目錄" };
        workDir.Click += (_, _) => ShowWorkDirDialog();
        root.AddView(workDir);

        _startStop = new Button(this) { Text = "▶ 啟動服務" };
        _startStop.Click += (_, _) => ToggleService();
        root.AddView(_startStop);

        var battery = new Button(this) { Text = "🔋 關閉電池最佳化（讓它掛得住）" };
        battery.Click += (_, _) => RequestIgnoreBatteryOptimizations();
        root.AddView(battery);

        var help = new TextView(this)
        {
            TextSize = 12,
            Text = "說明：\n" +
                   "• 手機版與桌面版共用同一套 Bot（同一份 Discord 指令、面板、輪詢、訂閱組）\n" +
                   "• 服務在前景執行，會顯示常駐通知；Android 才不會把它殺掉\n" +
                   "• 建議按上面的按鈕把電池最佳化關掉，並且插著電、連著 WiFi\n" +
                   "• 沒有 TDX 金鑰也能用（用內建的離線資料集，但不會有真實到站時間）"
        };
        help.SetPadding(0, Dp(12), 0, Dp(12));
        root.AddView(help);

        var logHeader = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        logHeader.AddView(Header("📋 日誌"));

        var clear = new Button(this) { Text = "清除" };
        clear.Click += (_, _) => BotConsole.Clear();
        logHeader.AddView(clear);
        root.AddView(logHeader);

        _logScroll = new ScrollView(this);
        _logScroll.SetBackgroundColor(Color.ParseColor("#111111"));
        _logScroll.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(260));

        _log = new TextView(this)
        {
            TextSize = 11,
            Typeface = Typeface.Monospace,
            Text = "（等待訊息…）"
        };
        _log.SetTextColor(Color.ParseColor("#DDDDDD"));
        _log.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
        _log.SetHorizontallyScrolling(true);
        _logScroll.AddView(_log);
        root.AddView(_logScroll);

        return scroll;
    }

    private TextView Header(string text) => new(this)
    {
        Text = text,
        TextSize = 18
    };

    private EditText Field(LinearLayout parent, string label, string value,
                           bool password = false, bool numeric = false)
    {
        parent.AddView(new TextView(this) { Text = label, TextSize = 13 });

        var edit = new EditText(this) { Text = value };
        edit.SetSingleLine(true);
        if (numeric) edit.InputType = Android.Text.InputTypes.ClassNumber;
        if (password) edit.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationPassword;
        if (password) edit.TransformationMethod = PasswordTransformationMethod.Instance;

        parent.AddView(edit);
        return edit;
    }

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density);

    // ── 行為 ──────────────────────────────────────────────

    private AppSettingsValues ReadForm()
    {
        static int Parse(string? s, int fallback)
            => int.TryParse((s ?? "").Trim(), out var v) && v > 0 ? v : fallback;

        return new AppSettingsValues(
            Token: _token.Text ?? "",
            TdxId: _tdxId.Text ?? "",
            TdxSecret: _tdxSecret.Text ?? "",
            MongoUri: _mongoUri.Text ?? "",
            PollSeconds: Parse(_poll.Text, 30),
            NotifyMinutes: Parse(_notify.Text, 10));
    }

    private void SaveSettings()
    {
        var values = ReadForm();

        if (string.IsNullOrWhiteSpace(values.Token))
        {
            Toast.MakeText(this, "請先填 Discord Bot Token", ToastLength.Long)?.Show();
            return;
        }

        AppSettings.Save(this, values);
        Toast.MakeText(this, "已儲存，可以按「啟動服務」", ToastLength.Short)?.Show();
        Refresh();

        // 服務跑著的時候改設定 → 重啟服務才會生效
        if (BotService.IsRunning)
        {
            Toast.MakeText(this, "服務正在執行，將重新啟動以套用新設定", ToastLength.Long)?.Show();
            BotService.Stop(this);
            _handler.PostDelayed(() => BotService.Start(this), 3000);
        }
    }

    private void ToggleService()
    {
        if (BotService.IsRunning)
        {
            BotService.Stop(this);
            Toast.MakeText(this, "正在停止…", ToastLength.Short)?.Show();
            return;
        }

        var values = AppSettings.Load(this);
        if (string.IsNullOrWhiteSpace(values.Token))
        {
            Toast.MakeText(this, "請先填 Token 並按「儲存設定」", ToastLength.Long)?.Show();
            return;
        }

        RequestNotificationPermissionIfNeeded();
        BotService.Start(this);
        Toast.MakeText(this, "服務已啟動", ToastLength.Short)?.Show();
    }

    /// <summary>
    /// 選工作目錄。
    ///
    /// 為什麼需要：App 私有目錄（`/data/data/...`）在沒 root 的手機上幾乎拿不出來 ——
    /// 檔案管理員看不到、MTP 看不到，只有 adb 能撈。
    /// 換到「手機儲存空間 /TcBusBot」之後，`app.env` 與 `tcbus.db`
    /// 用檔案管理員或插 USB 就直接看得到、改得到。
    /// </summary>
    private void ShowWorkDirDialog()
    {
        var current = WorkDir.Resolve(this);
        var options = WorkDir.Candidates(this);

        var labels = options
            .Select(o => $"{o.Label}\n{o.Path}\n（{o.Note}）")
            .Append("✏️ 自訂路徑…")
            .ToArray();

        new AlertDialog.Builder(this)!
            .SetTitle($"📁 工作目錄\n目前：{current}")!
            .SetItems(labels, (_, e) =>
            {
                if (e.Which == options.Count) { ShowCustomWorkDirDialog(current); return; }
                ChooseWorkDir(options[e.Which]);
            })!
            .SetNegativeButton("取消", (_, _) => { })!
            .Show();
    }

    private void ShowCustomWorkDirDialog(string current)
    {
        var input = new EditText(this) { Text = current };
        input.SetSingleLine(true);

        new AlertDialog.Builder(this)!
            .SetTitle("自訂工作目錄（絕對路徑）")!
            .SetMessage("例如：/storage/emulated/0/Documents/TcBusBot\n" +
                        "或 /sdcard/TcBusBot")!
            .SetView(input)!
            .SetPositiveButton("使用", (_, _) => ChooseWorkDir(
                new WorkDirOption("自訂路徑", (input.Text ?? "").Trim(), "使用者指定")))!
            .SetNegativeButton("取消", (_, _) => { })!
            .Show();
    }

    private void ChooseWorkDir(WorkDirOption option)
    {
        if (string.IsNullOrWhiteSpace(option.Path))
        {
            Toast.MakeText(this, "路徑不能是空的", ToastLength.Short)?.Show();
            return;
        }

        // 公開目錄（或自訂到公開位置）→ 先確認/請求儲存權限
        var needsStorage = option.Path.StartsWith("/storage/emulated/0", StringComparison.Ordinal)
                           && !option.Path.Contains("/Android/data/", StringComparison.Ordinal);

        if (needsStorage && !WorkDir.HasStorageAccess(this))
        {
            RequestStorageAccess();
            Toast.MakeText(this, $"請先授權：{WorkDir.StoragePermissionHint}", ToastLength.Long)?.Show();
            return;
        }

        if (!WorkDir.EnsureStructure(this, option.Path, out var error))
        {
            Toast.MakeText(this, $"這個位置不能寫：{error}\n試試別的目錄，或先授權儲存權限",
                           ToastLength.Long)?.Show();
            return;
        }

        WorkDir.Set(this, option.Path);
        BotConsole.Write($"[目錄] 工作目錄改為 {option.Path}\n");
        Toast.MakeText(this, $"已切換到：{option.Path}", ToastLength.Long)?.Show();

        Refresh();

        // 服務跑著的時候換目錄 → 重啟才會生效
        if (BotService.IsRunning)
        {
            Toast.MakeText(this, "服務正在執行，將重新啟動以套用新目錄", ToastLength.Long)?.Show();
            BotService.Stop(this);
            _handler.PostDelayed(() => BotService.Start(this), 3000);
        }
        else
        {
            new AlertDialog.Builder(this)!
                .SetTitle("已切換工作目錄")!
                .SetMessage($"新的檔案位置：\n{option.Path}\n\n" +
                            "• app.env（設定）\n• tcbus.db（訂閱組）\n• cache/（TDX 快取）\n\n" +
                            "原本目錄裡的檔案不會被搬過來。如果裡面已經有 app.env，" +
                            "重新啟動服務後會直接用那一份。")!
                .SetPositiveButton("好", (_, _) => { })!
                .Show();
        }
    }

    /// <summary>依 Android 版本請求對應的儲存權限。</summary>
    private void RequestStorageAccess()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                // Android 11+：公開目錄需要「所有檔案存取權」
                var intent = new Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission);
                intent.SetData(Android.Net.Uri.Parse("package:" + PackageName));
                StartActivity(intent);
                return;
            }

            RequestPermissions(new[] { Manifest.Permission.WriteExternalStorage, Manifest.Permission.ReadExternalStorage }, 2);
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, "請手動到「設定 → 應用程式 → 權限」開啟儲存權限：" + ex.Message,
                           ToastLength.Long)?.Show();
        }
    }

    /// <summary>
    /// 請使用者把這個 App 排除在電池最佳化之外。
    /// 不這樣做的話，手機螢幕關掉一陣子之後 Doze 會斷掉網路，
    /// 輪詢就停了 —— 這是「掛在舊手機上」最常見的失敗原因。
    /// </summary>
    private void RequestIgnoreBatteryOptimizations()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
            {
                var power = (PowerManager?)GetSystemService(PowerService);
                if (power?.IsIgnoringBatteryOptimizations(PackageName!) == true)
                {
                    Toast.MakeText(this, "已經關閉電池最佳化了", ToastLength.Short)?.Show();
                    return;
                }

                var intent = new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
                intent.SetData(Android.Net.Uri.Parse("package:" + PackageName));
                StartActivity(intent);
                return;
            }

            // Android 6 以下：帶使用者去電池設定的頁面
            StartActivity(new Intent(Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings));
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, "請手動到「設定 → 電池」把本 App 設為不受限制：" + ex.Message,
                           ToastLength.Long)?.Show();
        }
    }

    private void RequestNotificationPermissionIfNeeded()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        if (CheckSelfPermission(Manifest.Permission.PostNotifications) == Permission.Granted) return;

        RequestPermissions(new[] { Manifest.Permission.PostNotifications }, 1);
    }

    // ── 更新畫面 ──────────────────────────────────────────

    private void RefreshLoop()
    {
        Refresh();
        _handler.PostDelayed(RefreshLoop, 2000);
    }

    private void Refresh()
    {
        var values = AppSettings.Load(this);
        var db = new SavedGroupStore(AppSettings.DbPath(this));

        var dir = AppSettings.Dir(this);

        _status.Text =
            $"服務狀態：{(BotService.IsRunning ? "🟢 執行中" : "🔴 已停止")}\n" +
            $"Token：{(string.IsNullOrWhiteSpace(values.Token) ? "（未設定）" : $"{values.Token.Length} 字元")}　" +
            $"TDX：{(string.IsNullOrWhiteSpace(values.TdxId) ? "未設定（用離線資料集）" : "已設定")}\n" +
            $"輪詢：每 {values.PollSeconds} 秒　訂閱組：{db.CountByUser(0)} 組\n" +
            $"📁 工作目錄：{dir}\n" +
            $"　　app.env／tcbus.db／cache 都在這裡（按上面的按鈕可以換）\n" +
            (dir.StartsWith(AppSettings.PrivateDirHint, StringComparison.Ordinal)
                ? "　　⚠️ 這裡是 App 私有目錄，只能用 adb 拿檔案 —— 建議按「📁 選擇工作目錄」換到手機儲存空間\n"
                : "") +
            $"儲存：{db.Describe()}";

        _startStop.Text = BotService.IsRunning ? "⏹ 停止服務" : "▶ 啟動服務";

        var text = BotConsole.Dump();
        if (!string.IsNullOrEmpty(text) && _log.Text != text)
        {
            _log.Text = text;
            _logScroll.Post(() => _logScroll.FullScroll(FocusSearchDirection.Down));
        }
    }
}
