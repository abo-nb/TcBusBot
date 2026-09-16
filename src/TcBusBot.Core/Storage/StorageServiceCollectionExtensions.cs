using Microsoft.Extensions.DependencyInjection;
using TcBusBot.Core.Chat;

namespace TcBusBot.Core.Storage;

/// <summary>
/// 儲存方案的 **DI 註冊**。
///
/// 以前「挑後端」是寫死在 <c>SavedGroupStore</c> 的 private static 裡，
/// 而且整個程式用的是自己手寫的 <c>SimpleServiceProvider</c>。
/// 現在改成：
///
/// ```csharp
/// services.AddBusBotStorage(new StorageSettings(cfg.DatabasePath, cfg.MongoUri, cfg.MongoDatabase));
/// ```
///
/// 註冊三件事，而且**共用同一個實例**（同一個後端連線不該開兩份）：
///
/// | 註冊的型別 | 誰會用到 |
/// | --- | --- |
/// | `SavedGroupStore` | 訂閱組（`/bus groups`、面板） |
/// | `ILlmStateStore` | LLM 的每週 token 用量（跨重啟要保留） |
/// | `ISavedGroupRepository`（internal） | Core 內部的測試與診斷 |
///
/// 生命週期都是 singleton，並由容器的 <c>Dispose</c> 負責關閉
/// （MongoDB 連線、SQLite 檔案、背景重試迴圈都在那裡收）。
/// </summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// 依設定挑一個後端並註冊（MongoDB → SQLite → 文字檔 → 記憶體）。
    /// 連不上時**不會丟例外**，而是退回本機儲存並留下警告 ——
    /// 儲存壞掉不該讓整個 Bot 起不來。
    /// </summary>
    public static IServiceCollection AddBusBotStorage(
        this IServiceCollection services,
        StorageSettings settings,
        Action<string>? log = null)
    {
        // 後端在這裡就決定（連線失敗的警告也會在啟動時印出來，不是等到第一次寫入）。
        // 用「實例註冊」而不是工廠：連線由 SavedGroupStore 持有並負責關閉，
        // 容器只要釋放 store 就好（避免同一條連線被釋放兩次）。
        var repository = StorageBackendFactory.Open(settings, log);

        services.AddSingleton(repository);

        services.AddSingleton(sp => new SavedGroupStore(sp.GetRequiredService<ISavedGroupRepository>()));

        // 同一個實例：每週用量與訂閱組共用同一條後端鏈
        services.AddSingleton<ILlmStateStore>(sp => sp.GetRequiredService<SavedGroupStore>());

        return services;
    }
}
