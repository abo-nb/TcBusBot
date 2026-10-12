using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Chat;
using TcBusBot.Core.DataSources;
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
        BusDataService data,
        TdxApiClient? api,
        DiscordSocketClient client,
        string sourceDesc,
        Action<string> log,
        StorageSettings? storage = null,
        BusDataCatalog? catalog = null,
        UserCityStore? userCities = null,
        StaticDataStatus? dataStatus = null)
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
        //
        // LlmDiagnostics 是「LLM 有沒有接上」的可觀測性：最後幾次呼叫的結果
        // （成功、錯誤訊息、耗時）會留著，`/ai status` 與 `/ai test` 直接講給使用者聽。
        services.AddSingleton<LlmDiagnostics>();

        // 「他在想什麼」的暫存區：HTTP 層把思考原文寫進來、`/ai think` 讀出去。
        // 一定要是**同一個實例**（`/ai think` 才看得到剛剛那次呼叫的思考）。
        services.AddSingleton<ReasoningCapture>();

        // ── 「整台 Bot 要用哪一組模型連線」的唯一來源 ──────────────
        //    ⚠️ 建構子會**立刻**把資料庫裡的覆寫套進 cfg.Llm（見類別說明），
        //       所以它一定要比 ReloadableLlmClient 先被建出來 ——
        //       下面那個 factory 的第一行就是在保證這件事。
        services.AddSingleton(sp => new LlmEndpointStore(
            cfg.Llm, sp.GetRequiredService<ILlmStateStore>()));

        // ILlmClient 是**可熱抽換**的：`/ai llm` 換金鑰／端點／模型時整批重建連線，
        // 但外面那些單例（聊天、摘要、指令）拿到的是同一顆物件，所以不必重啟。
        services.AddSingleton(sp =>
        {
            sp.GetRequiredService<LlmEndpointStore>();   // 先套用覆寫，再建連線
            return new ReloadableLlmClient(
                cfg.Llm,
                sp.GetRequiredService<LlmDiagnostics>(),
                sp.GetRequiredService<ReasoningCapture>(),
                log: log);
        });

        services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<ReloadableLlmClient>());

        // ── 公車資料目錄：**「哪個使用者要用哪一份資料」的唯一入口** ──
        //     `/bus city` 讓訂公車的人自己選城市；沒選過的人用預設（BUS_CITY 的第一個）。
        // ⚠️ 這兩個**永遠要註冊**（呼叫端沒傳時就用「只有一個城市」的預設組合）：
        //    少註冊一個，`ValidateOnBuild` 會在啟動時直接炸掉整個 Bot
        //    （`--dryrun` 也走同一份註冊程式碼，所以它會先抓到）。
        //    公車資料目錄＝「哪個使用者要用哪一份資料」的唯一入口；
        //    城市由使用者自己選（`/bus city`），沒選過的人用預設。
        var cities = userCities ?? new UserCityStore(
            cfg.Tdx.EffectiveCities.Count > 0 ? cfg.Tdx.EffectiveCities : [BusCity.Default],
            cfg.Tdx.City);

        var dataCatalog = catalog ?? new BusDataCatalog(
            new Dictionary<string, BusDataService> { [cities.Default] = data }, cities);

        services.AddSingleton(cities);
        services.AddSingleton(dataCatalog);

        // 「每個城市的靜態資料現在能不能用」——UI 的「資料還沒好」訊息與 /health 都讀它。
        // ⚠️ 一定要是**同一個實例**：背景重試迴圈改的是這一份，Discord 互動讀的也是這一份
        //    （分成兩份的話，重試成功了但 UI 還是會說「還沒好」）。
        //    沒傳進來時（`--dryrun`／測試）就照**目前實際載入的資料**自己生一份，
        //    不然 UI 會說「還沒有資料」而實際上查得到站牌 —— 兩邊說法不一致。
        services.AddSingleton(dataStatus ?? SeedDataStatus(cities.Available, dataCatalog));

        services.AddSingleton(sp => new BusActionService(
            dataCatalog.DefaultData,
            sp.GetRequiredService<SubscriptionService>(),
            dataCatalog,
            sp.GetRequiredService<StaticDataStatus>()));   // 「資料還沒好」要據實回報給模型
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
                    cfg.Llm.UiActions ? sp.GetRequiredService<SavedGroupStore>() : null,
                    sp.GetRequiredService<UserCityStore>())   // set_city 工具（使用者自己選城市）
                : NoChatTools.Instance);

        // 「誰在對誰說話」的判斷紀錄（`/ai status` 看得到）——同一個實例，
        // 這樣使用者問「它分不分得出誰跟誰在說話」時有東西可以看。
        services.AddSingleton<AddresseeLog>();

        services.AddSingleton(sp => new ChatOrchestrator(
            sp.GetRequiredService<ILlmClient>(),
            cfg.Llm,
            sp.GetRequiredService<ConversationStore>(),
            sp.GetRequiredService<WeeklyTokenBudget>(),
            sp.GetRequiredService<IChatToolProvider>(),
            sp.GetRequiredService<GuildPersonaStore>(),
            userCities,      // 城市是「問的人」自己選的（/bus city），提示詞要跟著他
            sp.GetRequiredService<AddresseeLog>())   // 「誰在對誰說話」的判斷紀錄
        {
            // 偷聽判斷的提示詞要用 Bot 自己的名字
            BotName = client.CurrentUser?.Username ?? "Bot"
        });

        // 「跟另一個 App 對話」的煞車（`/stop` ＋ 每小時上限）——**持久化**，
        // 所以重啟（Render 會自己重啟）不會讓兩個機器人的迴圈從頭再來一次。
        services.AddSingleton(sp => new AppChatGuard(sp.GetRequiredService<ILlmStateStore>()));

        services.AddSingleton<LlmChatService>();

        // ── 每週頻道摘要（/digest）────────────────────────────
        //
        // 儲存：設定（含游標）與歷次摘要的歸檔各是一份**獨立**的 blob（永久保留）。
        // 為什麼跟聊天記憶分開：生命週期不一樣（記憶是暫存的、歸檔是永久的）。
        services.AddSingleton(sp => new Core.Chat.Digest.DigestStore(sp.GetRequiredService<ILlmStateStore>()));

        // 摘要設定本身也是服務（排程器、指令、摘要流程共用同一份）
        services.AddSingleton(cfg.Llm.Digest);

        services.AddSingleton(sp => new Core.Chat.Digest.ChannelDigestService(
            sp.GetRequiredService<ILlmClient>(),
            sp.GetRequiredService<Core.Chat.Digest.DigestStore>(),
            sp.GetRequiredService<WeeklyTokenBudget>(),
            cfg.Llm.Digest));

        services.AddSingleton<DigestRunner>();
        services.AddSingleton<DigestScheduler>();

        // ── Discord 互動 ──────────────────────────────────
        services.AddSingleton(_ => new InteractionService(client.Rest, new InteractionServiceConfig
        {
            DefaultRunMode = RunMode.Async,
            LogLevel = cfg.Verbose ? LogSeverity.Verbose : LogSeverity.Info
        }));

        // 指令模組（transient：每次互動都要新的實例）
        // 清單集中在 BotModules（啟動註冊與離線驗證都從同一份長出來 —— 見那裡的說明）
        foreach (var (type, _) in BotModules.All)
            services.AddTransient(type);

        return services;
    }

    /// <summary>
    /// 沒有傳入 <see cref="StaticDataStatus"/> 時，照「現在實際載入的資料」生一份。
    ///
    /// 為什麼不能只用 `new StaticDataStatus(cities)`（全部標成「還沒好」）：
    /// `--dryrun`／測試走的就是這條路，那樣會讓 UI 的訊息與事實相反
    /// （狀態說沒有資料，但查詢其實查得到）。
    /// </summary>
    private static StaticDataStatus SeedDataStatus(
        IReadOnlyList<string> cities, BusDataCatalog catalog)
    {
        var status = new StaticDataStatus(cities);

        foreach (var city in cities)
        {
            var stops = catalog.ForCity(city).StopCount;

            status.Report(new StaticCityState(
                City: city,
                StopCount: stops,
                Source: stops > 0 ? "啟動時載入" : "尚未載入",
                Available: stops > 0,
                Fresh: stops > 0));
        }

        return status;
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
