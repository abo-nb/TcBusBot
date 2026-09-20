using TcBusBot.Discord.Modules;

namespace TcBusBot.Discord;

/// <summary>
/// Discord 的指令模組清單（**唯一的來源**）。
///
/// 為什麼要集中成一份：模組要出現在三個地方 ——
///   1. DI 註冊（`BotServices`，transient：每次互動都要新實例）
///   2. 啟動時註冊到 `InteractionService`（`Program`）
///   3. 離線驗證（`DryRun` 的按鈕↔處理函式接線檢查）
///
/// 以前這三份是各寫各的，結果 `PersonaGrantModule` **只出現在第 2 份** ——
/// 沒進 DI、也沒被接線檢查掃到。它剛好還是能動（Discord.Net 會用
/// `ActivatorUtilities` 現場建實例），但那就代表：
///   * `ValidateOnBuild` 擋不到它的建構子依賴寫錯（建構子少一個註冊 → 執行時才炸）
///   * 它的按鈕完全沒被「按了有沒有反應」的檢查涵蓋
///
/// 這種「靠剛好能動」的狀態不會有任何症狀，所以改成只有一份清單，三邊都從它長出來。
/// </summary>
public static class BotModules
{
    /// <summary>（型別, 顯示標籤）。標籤只用在啟動訊息與驗證輸出。</summary>
    public static readonly (Type Type, string Label)[] All =
    [
        (typeof(BusModule), "bus"),
        (typeof(BusComponentModule), "bus（按鈕／選單）"),
        (typeof(SayModule), "say"),
        (typeof(ChatModule), "ai"),
        (typeof(PersonaGrantModule), "ai（授權按鈕）"),
        (typeof(QuotaResetModule), "ai（額度重置按鈕）"),
        (typeof(ResetModule), "rest")
    ];
}
