using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Chat;
using TcBusBot.Core.Realtime;
using TcBusBot.Core.Storage;
using TcBusBot.Core.Subscriptions;
using TcBusBot.Core.Tdx;
using TcBusBot.Discord.Modules;

namespace TcBusBot.Discord;

/// <summary>
/// **服務的組裝（composition root）**：整個 Bot 的物件關係只寫在這裡。
///
/// 以前是「自己手寫的 <c>SimpleServiceProvider</c> ＋ Program.cs 裡東註冊一個西註冊一個」，
/// 現在改成標準的 <see cref="IServiceCollection"/>：
///
///   * 儲存後端的選擇（MongoDB → SQLite → 文字檔 → 記憶體）由
///     <see cref="StorageServiceCollectionExtensions.AddBusBotStorage"/> 負責，
///     Program 完全不需要知道現在是用哪一種
///   * `--dryrun` 用的是**同一份**註冊程式碼，所以「離線驗證通過」等於
///     「真正的 Bot 也組得起來」（見 <c>DryRun.AuditDependencyInjection</c>）
///   * 容器可以開 <c>ValidateOnBuild</c>：任何「註冊漏了、建構子參數找不到」都會
///     在建容器時就炸出來 —— 之前那個「沒設定金鑰 → <c>ILlmClient</c> 不存在 →
///     整個 Bot 起不來」的 bug，本來就該在這一關被擋下
///
/// ⚠️ 生命週期：
///   * **singleton** — 服務本身（資料、訂閱、儲存、LLM 客戶端…）
///   * **transient** — Discord 的指令模組。Discord.Net 每次互動都要**新的實例**
///     （它會把 Context 設到模組實例上，共用實例會讓並行的互動互相蓋掉）
/// </summary>
internal static class BotServices
{
    /// <summary>把整個 Bot 需要的服務註冊進容器（不含 Discord 的登入流程）。</summary>
    /// <param name="storage">
    /// 儲存設定。`--dryrun` 會傳記憶體模式進來 —— 離線驗證**不該**去開真正的資料庫。
    /// </param>
    public static IServiceCollection Create(
        BotConfig cfg,
        TaichungBusDataService data,
        TdxApiClient? api,
        DiscordSocketClient client,
        string sourceDesc,
        Action<string> log,
        StorageSettings? storage = null)
    {
        var services = new ServiceCollection();

        // ── 設定與外部資源 ────────────────────────────────
        services.AddSingleton(cfg);
        services.AddSingleton(cfg.Llm);
        services.AddSingleton(data);
        services.AddSingleton(client);

        // ── 核心服務 ──────────────────────────────────────
        services.AddSingleton<SubscriptionService>();
        services.AddSingleton<BusSessionStore>();
        services.AddSingleton<RealtimeBusCache>();
        services.AddSingleton(new ConversationStore(cfg.Llm));

        // ★ 儲存方案：後端選擇（MongoDB → SQLite → 文字檔 → 記憶體）在 Core，
        //    這裡只給設定。SavedGroupStore 與 ILlmStateStore 會是**同一個實例**。
        services.AddBusBotStorage(
            storage ?? new StorageSettings(cfg.DatabasePath, cfg.MongoUri, cfg.MongoDatabase),
            log);

        services.AddSingleton(sp => new WeeklyTokenBudget(
            cfg.Llm, sp.GetRequiredService<ILlmStateStore>()));

        services.AddSingleton(sp => new BotRuntime(sp.GetRequiredService<SubscriptionService>(), api)
        {
            DataSourceDescription = sourceDesc
        });

        // ── LLM 聊天 ──────────────────────────────────────
        // ILlmClient **一定要註冊**（沒設定時是空物件）：模組的建構子參數
        // 只要有一個解析不到，Discord.Net 就建不出模組 → 整個 Bot 起不來。
        services.AddSingleton<ILlmClient>(_ => CreateLlmClient(cfg.Llm, log));

        services.AddSingleton<BusActionService>();
        services.AddSingleton(sp => new GuildPersonaStore(
            sp.GetRequiredService<ILlmStateStore>(), cfg.Llm.BuildPersonaLimits()));

        // 跨伺服器授權（按鈕同意）——只在記憶體，重啟後重新請求即可
        services.AddSingleton<PersonaGrantStore>();

        services.AddSingleton<IChatToolProvider>(sp =>
            cfg.Llm.IsConfigured && cfg.Llm.ToolsEnabled
                ? new BotToolProvider(
                    sp.GetRequiredService<BusActionService>(),
                    sp.GetRequiredService<SubscriptionService>(),
                    sp.GetRequiredService<GuildPersonaStore>(),
                    ArrivalsAsync(sp),
                    cfg.Llm.UiActions ? sp.GetRequiredService<BusSessionStore>() : null,
                    cfg.Llm.UiActions ? sp.GetRequiredService<SavedGroupStore>() : null)
                : NoChatTools.Instance);

        services.AddSingleton(sp => new ChatOrchestrator(
            sp.GetRequiredService<ILlmClient>(),
            cfg.Llm,
            sp.GetRequiredService<ConversationStore>(),
            sp.GetRequiredService<WeeklyTokenBudget>(),
            sp.GetRequiredService<IChatToolProvider>(),
            sp.GetRequiredService<GuildPersonaStore>())
        {
            // 偷聽判斷的提示詞要用 Bot 自己的名字
            BotName = client.CurrentUser?.Username ?? "Bot"
        });

        services.AddSingleton<LlmChatService>();

        // ── Discord 互動 ──────────────────────────────────
        services.AddSingleton(_ => new InteractionService(client.Rest, new InteractionServiceConfig
        {
            DefaultRunMode = RunMode.Async,
            LogLevel = cfg.Verbose ? LogSeverity.Verbose : LogSeverity.Info
        }));

        // 指令模組（transient：每次互動都要新的實例）
        services.AddTransient<BusModule>();
        services.AddTransient<BusComponentModule>();
        services.AddTransient<SayModule>();
        services.AddTransient<ChatModule>();
        services.AddTransient<ResetModule>();

        return services;
    }

    /// <summary>
    /// 建立 LLM 客戶端。**不丟例外** —— 設定錯、連不上都只印警告，
    /// 讓公車功能照常運作（AI 是額外功能，不該拖垮主要功能）。
    /// </summary>
    private static ILlmClient CreateLlmClient(LlmOptions options, Action<string> log)
    {
        if (!options.IsConfigured)
        {
            log("⏸️  AI 聊天未啟用（沒有設定 LLM_API_KEY）");
            return DisabledLlmClient.Instance;
        }

        var (client, message) = SemanticKernelLlmClient.Create(options);
        log(message);

        return (ILlmClient?)client ?? DisabledLlmClient.Instance;
    }

    /// <summary>
    /// 給 LLM 的「到站時間」工具。TDX 查詢與排序在 <see cref="BotRuntime"/>（Discord 這一層），
    /// 所以用一個 delegate 接進工具裡；純文字格式化也放在這裡。
    /// </summary>
    private static Func<ChatToolContext, string?, CancellationToken, Task<string>> ArrivalsAsync(
        IServiceProvider provider)
        => async (ctx, route, _) =>
        {
            var subs = provider.GetRequiredService<SubscriptionService>();
            var runtime = provider.GetRequiredService<BotRuntime>();

            var groups = subs.GetGroupsByUser(ctx.UserId).ToList();

            if (groups.Count == 0)
                return "使用者目前沒有任何訂閱，所以沒有到站時間可以查。" +
                       "（可以問他要不要用 subscribe_bus 訂閱）";

            // 以前只看「最後一組」訂閱 —— 使用者常常有好幾組（上班／回家），
            // 只回最後一組會讓他以為其他訂閱消失了。這裡把每一組都查一遍。
            var blocks = new List<string>();

            foreach (var group in groups.Take(3))
            {
                var result = await runtime.BuildEtaTableAsync(group, DateTimeOffset.UtcNow);
                var rows = result.Rows.AsEnumerable();

                if (!string.IsNullOrWhiteSpace(route))
                {
                    var filtered = rows
                        .Where(r => r.Subscription.RouteName.Contains(route!, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (filtered.Count > 0) rows = filtered;
                }

                var lines = rows.Take(12).Select(r =>
                {
                    var when = r.LiveSeconds is { } sec ? $"約 {Math.Round(sec / 60.0)} 分鐘" : r.StatusText;
                    return $"　• {r.Subscription.RouteName}（{(r.Subscription.Direction == 0 ? "去程" : "返程")}）" +
                           $"{r.Subscription.BoardStopName}：{when}";
                }).ToList();

                if (lines.Count == 0 && !string.IsNullOrWhiteSpace(route))
                    continue;   // 這一組沒有使用者問的路線就整組略過

                var header = result.Simulation ? "（⚠️ 模擬資料，主機沒有 TDX 金鑰）" : "";
                var error = result.Error is null ? "" : $"（查詢部分失敗：{result.Error}）";

                blocks.Add($"{group.DescribeRoute()} {header}{error}\n" + string.Join("\n", lines));
            }

            if (blocks.Count == 0)
                return string.IsNullOrWhiteSpace(route)
                    ? "目前查不到任何到站時間。"
                    : $"使用者訂閱的路線裡沒有「{route}」這一條（可以用 list_subscriptions 確認他訂了什麼）。";

            return "目前訂閱的到站狀況：\n" + string.Join("\n\n", blocks);
        };
}
