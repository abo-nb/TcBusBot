using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using TcBusBot.Core.Bus;
using TcBusBot.Core.Chat;
using TcBusBot.Core.DataSources;
using TcBusBot.Core.Hosting;
using TcBusBot.Core.Realtime;
using TcBusBot.Core.Storage;
using TcBusBot.Core.Subscriptions;
using TcBusBot.Core.Tdx;
using TcBusBot.Discord.Modules;

namespace TcBusBot.Discord;

public static class Program
{
    /// <summary>
    /// 從外部要求停止（Android 的 foreground service 用；主控台是 Ctrl+C）。
    /// 這是同一個 Bot 主的兩種「停止方式」，不需要為了手機版複製一份流程。
    /// </summary>
    public static void RequestStop() => _stop?.Cancel();

    private static CancellationTokenSource? _stop;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // Android 等平台可能不支援設定輸出編碼；不影響功能，忽略即可
        }

        _stop = new CancellationTokenSource();

        var cfg = BotConfig.Load(args);
        var dryRun = args.Contains("--dryrun");

        PrintBanner(cfg);

        if (!dryRun && string.IsNullOrWhiteSpace(cfg.Token))
        {
            PrintTokenHelp();
            return 1;
        }

        // ── 健康檢查端點 ───────────────────────────────────
        // 刻意在「連 Discord 之前」就開埠：Render 會在啟動後不久探測這個埠，
        // 等到 Discord 連上才開就已經被判定部署失敗了（Discord 連線本來就要好幾秒）。
        HealthEndpoint? health = null;
        if (!dryRun && cfg.EnableHealthEndpoint)
        {
            health = new HealthEndpoint(cfg.Port, BotStatus.ToJson);
            Console.WriteLine(health.Start(out var healthMessage)
                ? $"  ✅ 健康檢查端點：{healthMessage}"
                : $"  ⚠️  {healthMessage}");
            Console.WriteLine();
        }

        // ── 1) 靜態資料 ────────────────────────────────────
        TaichungBusDataService data;
        TdxApiClient? api = null;
        string sourceDesc;

        try
        {
            if (cfg.EffectiveDataSource == "tdx")
            {
                // 用 TdxApiClient.CreateHttpClient()：它會開啟 AutomaticDecompression，
                // 否則 TDX 回傳的 gzip 內容會讓 JSON 解析失敗。
                api = new TdxApiClient(TdxApiClient.CreateHttpClient(), cfg.Tdx);
                var set = await StaticDataLoader.LoadAsync(api, cfg.Tdx, cfg.Refresh, Console.WriteLine);
                data = new TaichungBusDataService();
                data.Load(set.Stops, set.StopOfRoutes, set.Routes);
                sourceDesc = api.IsVisitorMode
                    ? "TDX 台中公車（訪客模式：沒有 API 金鑰，每日 20 次上限）"
                    : "TDX 台中公車（會員模式）";
                Console.WriteLine(sourceDesc);
            }
            else
            {
                var root = MiniFixtureSource.ResolveRoot(cfg.FixturesPath);
                data = MiniFixtureSource.Load(root);
                sourceDesc = $"內建最小資料集（{root}）";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"❌ 載入靜態資料失敗：{ex.Message}");
            if (ex.InnerException is not null)
                Console.WriteLine($"   內部錯誤：{ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            Console.WriteLine();
            Console.WriteLine("   改用內建最小資料集繼續執行（搜尋範圍僅限臺中車站～靜宜大學走廊）。");
            Console.WriteLine("   修正後可用 --refresh 重新抓取。");
            Console.WriteLine();
            data = MiniFixtureSource.Load(MiniFixtureSource.ResolveRoot(cfg.FixturesPath));
            sourceDesc = "內建最小資料集（TDX 載入失敗後的備援）";
        }

        Console.WriteLine($"✅ 資料就緒：{data.StopCount} 個站牌、{data.TripCount} 筆路線站序");
        Console.WriteLine();

        BotStatus.DataSource = sourceDesc;

        // ── 離線 UI 驗證（不需要 Token）────────────────────
        if (dryRun) return DryRun.Run(data, cfg);

        // ── 2) Discord 客戶端（意圖要在建立時決定）──────────
        // 意圖（intents）：
        //   * Guilds        —— 斜線指令與元件互動
        //   * GuildMessages + MessageContent —— AI 聊天要讀訊息內容
        //     ⚠️ MessageContent 是**特權意圖**，必須同時在 Discord Developer Portal
        //        開啟（Bot → Privileged Gateway Intents → Message Content Intent），
        //        否則閘道會用 4014 把我們踢掉（見 OnLog 的提示）。
        //   * DirectMessages —— 只有允許私訊時才要
        var intents = GatewayIntents.Guilds;

        if (cfg.EnableMessageContentIntent)
        {
            intents |= GatewayIntents.GuildMessages | GatewayIntents.MessageContent;
            if (cfg.Llm.AllowDm) intents |= GatewayIntents.DirectMessages;
        }

        var client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = intents,
            MessageCacheSize = 0,
            LogLevel = cfg.Verbose ? LogSeverity.Verbose : LogSeverity.Info
        });

        // ── 2b) 服務組裝（**DI 容器**，註冊集中在 BotServices）──
        //   `--dryrun` 用的是同一份註冊程式碼，所以離線驗證通過 = 這裡也組得起來。
        var services = BotServices.Create(cfg, data, api, client, sourceDesc, Console.WriteLine);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,     // 有任何參數解析不到就在這裡炸（不是跑到一半才炸）
            ValidateScopes = true
        });

        // ★ 儲存方案：後端（MongoDB／SQLite／文字檔／記憶體）是在註冊時就挑好的，
        //   Program 不需要知道現在是哪一種 —— 只把它印出來並給健康檢查端點用。
        using var scopeForLog = provider.CreateScope();
        var savedGroups = provider.GetRequiredService<SavedGroupStore>();
        Console.WriteLine($"✅ 訂閱組儲存：{savedGroups.Describe()}");
        BotStatus.StorageMode = savedGroups.Describe();

        var subs = provider.GetRequiredService<SubscriptionService>();
        var cache = provider.GetRequiredService<RealtimeBusCache>();
        var budget = provider.GetRequiredService<WeeklyTokenBudget>();
        var llmOptions = provider.GetRequiredService<LlmOptions>();
        var llm = provider.GetRequiredService<ILlmClient>();

        if (llmOptions.IsConfigured && llm.IsConfigured)
        {
            Console.WriteLine($"    每週額度：{budget.Describe()}");
            Console.WriteLine("    對話記憶：只在記憶體（每個頻道各自一份，不同伺服器不相通）");
        }

        Console.WriteLine();

        // 健康檢查端點會讀這裡的數字（訂閱數、快取筆數）
        BotStatus.SubscriptionCounts = () => (subs.GroupCount, subs.SubscriptionCount);
        BotStatus.CachedEtas = () => cache.Count;

        var interactions = provider.GetRequiredService<InteractionService>();

        // 一個模組註冊失敗**不可以**讓整個 Bot 起不來：
        // 公車功能是主要功能，AI 聊天是額外的。
        // （容器已經開了 ValidateOnBuild，所以「參數解析不到」這種錯在 BuildServiceProvider
        //   就會先炸出來 —— 之前那個 ILlmClient 沒註冊導致整個 Bot 起不來的 bug 就屬於這類。）
        async Task RegisterModuleAsync<TModule>(string label) where TModule : class
        {
            try
            {
                await interactions.AddModuleAsync<TModule>(provider);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  模組 {label} 註冊失敗（這個指令群組不會出現）：" +
                                  $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        await RegisterModuleAsync<BusModule>("bus");
        await RegisterModuleAsync<BusComponentModule>("bus（按鈕／選單）");
        await RegisterModuleAsync<SayModule>("say");
        await RegisterModuleAsync<ChatModule>("ai");

        // ── AI 聊天：接上訊息事件 ───────────────────────────
        LlmChatService? chat = null;

        if (llm.IsConfigured)
        {
            chat = provider.GetRequiredService<LlmChatService>();
            chat.Start();   // 訂閱 MessageReceived（只有真的啟用時才接）

            var tools = provider.GetRequiredService<IChatToolProvider>();
            Console.WriteLine($"    工具　　：{(llmOptions.ToolsEnabled ? tools.Describe() : "未啟用（LLM_TOOLS=false）")}");

            BotStatus.Llm = () => chat.Describe();

            if (cfg.EnableMessageContentIntent)
                Console.WriteLine("▶️  AI 聊天已接上（@ 它、或回覆它的訊息就會回話）");
            else
                Console.WriteLine("⚠️  AI 聊天需要 Message Content 意圖，但被 --no-message-intent 關掉了 → 不會回話");
        }
        else
        {
            BotStatus.Llm = () => "未啟用（沒有 LLM_API_KEY）";
        }

        client.Log += msg => OnLog(msg, cfg.Verbose);
        interactions.Log += msg => OnLog(msg, cfg.Verbose);

        var registered = false;
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.Ready += async () =>
        {
            readyTcs.TrySetResult(true);

            // 健康檢查端點會讀這個值（/health 的 discordReady）
            // ⚠️ 要放在 `if (registered) return;` **之前** —— 否則重新連線後
            //    這個值就不會再更新（第一次是 true，之後若斷線重連也不會反映）。
            BotStatus.DiscordReady = true;

            if (registered) return;
            registered = true;

            // 註冊到「已加入的伺服器」→ 立即生效。
            // 全域指令最多要等 1 小時才會出現，對測試很不友善。
            var guilds = cfg.GuildId is { } gid
                ? client.Guilds.Where(g => g.Id == gid).ToList()
                : client.Guilds.ToList();

            if (guilds.Count == 0)
            {
                Console.WriteLine("⚠️  Bot 還沒有加入任何伺服器。請用下面的連結邀請它：");
            }
            else
            {
                foreach (var g in guilds)
                {
                    await interactions.RegisterCommandsToGuildAsync(g.Id);
                    Console.WriteLine($"✅ 已在「{g.Name}」註冊指令");
                }
            }

            PrintReady(client);
        };

        client.JoinedGuild += async guild =>
        {
            await interactions.RegisterCommandsToGuildAsync(guild.Id);
            Console.WriteLine($"✅ 已加入「{guild.Name}」，指令已註冊");
        };

        client.InteractionCreated += async interaction =>
        {
            var ctx = new SocketInteractionContext(client, interaction);
            try
            {
                // ⚠️ InteractionService 會「吃掉」命令處理中的例外並包成 ExecuteResult，
                //    不會往外丟。所以一定要檢查回傳值，否則使用者只會看到
                //    Discord 的「無法提交」，console 也只有一行沒有細節的錯誤。
                var result = await interactions.ExecuteCommandAsync(ctx, provider);

                if (result is ExecuteResult { IsSuccess: false } failed && failed.Exception is not null)
                {
                    Console.WriteLine($"[interaction] {DescribeInteraction(interaction)} 執行失敗：");
                    Console.WriteLine(failed.Exception.ToString());

                    if (!interaction.HasResponded)
                        await interaction.RespondAsync(
                            $"❌ 執行失敗：`{failed.Exception.GetType().Name}: {failed.Exception.Message}`\n" +
                            "詳細堆疊已印在 Bot 的 console。",
                            ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[interaction] {DescribeInteraction(interaction)} 非預期錯誤：");
                Console.WriteLine(ex.ToString());
                try
                {
                    if (!interaction.HasResponded)
                        await interaction.RespondAsync("內部錯誤，請稍後再試。", ephemeral: true);
                }
                catch { /* 已經回應過就算了 */ }
            }
        };

        Console.WriteLine("正在連線到 Discord…");
        try
        {
            await client.LoginAsync(TokenType.Bot, cfg.Token);
            await client.StartAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"❌ 連線失敗：{Describe(ex)}");
            Console.WriteLine();
            Console.WriteLine("常見原因：");
            Console.WriteLine("  • Token 打錯或已重設 → 回 Discord Developer Portal 重新複製");
            Console.WriteLine("  • 這台機器連不到 discord.com（防火牆／代理／沙箱環境）");
            Console.WriteLine("  • 系統時間偏差過大導致 TLS 失敗");
            Console.WriteLine();
            Console.WriteLine($"原始錯誤：{ex.GetType().Name}: {ex.Message}");

            try { await client.LogoutAsync(); } catch { }
            return 1;
        }

        // ── 等 Ready：連不上就不要呆呆掛著 ──────────────────
        // Discord.Net 的 LoginAsync / StartAsync 不會因為閘道連不上而丟例外，
        // 錯誤只在背景重試。所以這裡主動等一下，逾時就給出可行的診斷。
        var ready = await Task.WhenAny(
            readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(cfg.ConnectTimeoutSeconds)));

        if (ready != readyTcs.Task)
        {
            Console.WriteLine();
            Console.WriteLine($"❌ 已等 {cfg.ConnectTimeoutSeconds} 秒仍未連上 Discord 閘道。");
            Console.WriteLine();
            Console.WriteLine("請依序檢查：");
            Console.WriteLine("  1. 這台機器連得到 discord.com 嗎？（防火牆／公司代理／封閉沙箱會擋）");
            Console.WriteLine("     測試：curl https://discord.com/api/v10/gateway");
            Console.WriteLine("  2. Token 是否正確且未被重設？");
            Console.WriteLine("  3. Bot 是否已加入至少一個伺服器？（沒有也不影響連線，但就沒有東西可測）");
            Console.WriteLine("  4. 加 --verbose 可以看到 Discord.Net 的連線日誌：");
            Console.WriteLine("     dotnet run --project src\\TcBusBot.Discord -- --verbose");
            Console.WriteLine();

            try { await client.StopAsync(); await client.LogoutAsync(); } catch { }
            return 2;
        }

        // ── 4) 即時輪詢（只有真的拿得到即時資料時才啟動）──
        //   停止訊號：Ctrl+C（主控台）或 Program.RequestStop()（Android 服務）
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
        }
        catch (Exception)
        {
            // 有些平台（Android）沒有主控台中斷事件
        }

        var runtime = provider.GetRequiredService<BotRuntime>();

        if (api is not null && cfg.EnablePoller)
        {
            runtime.PollerDescription = $"啟用中，每 {cfg.PollIntervalSeconds} 秒一次";
            var poller = new EtaPoller(client, api, subs, cache, cfg.PollIntervalSeconds);
            _ = Task.Run(() => poller.RunAsync(cts.Token), cts.Token);
            Console.WriteLine($"▶️  即時輪詢已啟動（每 {cfg.PollIntervalSeconds} 秒）");
        }
        else
        {
            runtime.PollerDescription = api is null
                ? "未啟動（沒有 TDX 金鑰）"
                : "未啟動（--no-poller）";
            Console.WriteLine("⏸️  即時輪詢未啟動 —— 沒有 TDX 金鑰，因此不會有真實到站通知。");
            Console.WriteLine("   你可以用 /bus panel 完成訂閱後按「模擬一則通知」來驗收通知內容。");
        }

        BotStatus.Poller = runtime.PollerDescription;

        // ── 4b) 對話記憶的清掃 ─────────────────────────────
        // 每個頻道各自一份記憶，長時間掛著會慢慢累積（Bot 可能連續跑好幾天）。
        // 每 30 分鐘清一次「超過 TTL 沒動」的頻道。
        if (chat is not null)
        {
            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromMinutes(30), cts.Token); }
                    catch (OperationCanceledException) { return; }

                    try
                    {
                        var removed = chat.Purge(DateTimeOffset.UtcNow);
                        if (removed > 0)
                            Console.WriteLine($"[llm] 清掉 {removed} 個太久沒動的頻道記憶" +
                                              $"（目前追蹤 {chat.ChannelCount} 個）");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[llm] 清理對話記憶失敗：{ex.GetType().Name}");
                    }
                }
            }, cts.Token);
        }

        // ── 防休眠（Render 免費層閒置約 15 分鐘就會把服務停掉）──
        KeepAliveLoop? keepAlive = null;
        if (!string.IsNullOrWhiteSpace(cfg.AppUrl))
        {
            keepAlive = new KeepAliveLoop(cfg.AppUrl, cfg.KeepAliveMinutes);
            keepAlive.Start();
            Console.WriteLine($"▶️  防休眠已啟動（{keepAlive.Describe()}）");
            Console.WriteLine("    ⚠️ 服務真的睡著之後就沒辦法 ping 自己了 —— " +
                              "要保證隨時醒著，請另外設外部監控（UptimeRobot／cron-job.org）。");
        }
        else
        {
            Console.WriteLine("⏸️  防休眠未啟動（沒有 APP_URL／RENDER_EXTERNAL_URL）");
        }

        Console.WriteLine();
        Console.WriteLine("Bot 已啟動。按 Ctrl+C 結束。（手機版：在 App 裡按「停止服務」）");

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }

        Console.WriteLine("正在關閉…");
        keepAlive?.Dispose();
        health?.Dispose();
        await client.StopAsync();
        await client.LogoutAsync();

        // 把還沒寫下去的每週用量補寫（不然重啟前的那幾次呼叫就不算錢了）
        budget.Flush(DateTimeOffset.UtcNow);
        chat?.Dispose();

        // 容器負責釋放所有 singleton（儲存後端、MongoDB 連線、SQLite 檔案、
        // HttpClient…），所以這裡不需要一個一個 Dispose。
        cts.Dispose();
        _stop = null;
        return 0;
    }

    // ─────────────────────────────────────────────────────

    private static void PrintBanner(BotConfig cfg)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   TcBusBot — 台中公車到站通知 Discord Bot                 ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.WriteLine($"  .env 檔　　　：{cfg.EnvFile}{cfg.EnvFileSource}");
        if (cfg.EnvExportedKeys > 0)
            Console.WriteLine($"　　　　　　　　（已把 {cfg.EnvExportedKeys} 個鍵匯出到環境變數，" +
                              "程式內可用 Environment.GetEnvironmentVariable 讀取）");
        if (cfg.EnvWarning is not null)
        {
            Console.WriteLine($"  ⚠️  {cfg.EnvWarning}");
            Console.WriteLine("     （仍然會用命令列參數與系統環境變數；.env 只是方便放金鑰）");
            Console.WriteLine();
            Console.WriteLine("     用法：--env <路徑>　或　--env <資料夾>　或　設定 TCBUS_ENV");
        }
        Console.WriteLine($"  Discord Token：{cfg.TokenPreview}");
        Console.WriteLine($"  TDX 金鑰　　 ：{(cfg.Tdx.HasCredentials ? "已設定（會員模式）" : "未設定（使用內建資料集）")}");
        Console.WriteLine($"  資料來源　　 ：{(cfg.EffectiveDataSource == "tdx" ? "TDX API" : "內建最小資料集")}");
        Console.WriteLine($"  輪詢間隔　　 ：{cfg.PollIntervalSeconds} 秒／預設提前通知：{cfg.DefaultNotifyMinutes} 分鐘");
        Console.WriteLine($"  訂閱組儲存　 ：{cfg.DatabasePath}");
        Console.WriteLine($"  MongoDB　　　：{cfg.MongoPreview}");
        Console.WriteLine($"  健康檢查端點 ：{(cfg.EnableHealthEndpoint ? $"埠 {cfg.Port}（/ 與 /health）" : "已停用（--no-health）")}");
        Console.WriteLine($"  防休眠　　　 ：{(string.IsNullOrWhiteSpace(cfg.AppUrl) ? "未設定（沒有 APP_URL）" : $"每 {cfg.KeepAliveMinutes} 分鐘 ping {cfg.AppUrl}")}");
        Console.WriteLine($"  AI 聊天　　　：{cfg.Llm.Describe()}");
        Console.WriteLine($"  訊息意圖　　 ：{(cfg.EnableMessageContentIntent ? "Message Content（需要在 Developer Portal 開啟）" : "只收指令（--no-message-intent）")}");
        Console.WriteLine();
    }

    private static void PrintReady(DiscordSocketClient client)
    {
        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────────────");
        Console.WriteLine($"✅ Bot 已上線：{client.CurrentUser.Username}#{client.CurrentUser.Discriminator}");
        Console.WriteLine($"   已加入 {client.Guilds.Count} 個伺服器");
        Console.WriteLine();
        Console.WriteLine("   邀請連結（若還沒加入伺服器，點這個）：");
        Console.WriteLine($"   https://discord.com/api/oauth2/authorize?client_id={client.CurrentUser.Id}" +
                          "&scope=bot%20applications.commands&permissions=3072");
        Console.WriteLine();
        Console.WriteLine("   在 Discord 輸入：");
        Console.WriteLine("     /bus panel    開啟訂閱面板（設定起點 → 目的地 → 選路線）");
        Console.WriteLine("     /bus list     查看我的訂閱");
        Console.WriteLine("     /bus next     看所有訂閱目前的到站時間");
        Console.WriteLine("     /bus groups   我的訂閱組（套用／合併／改名／刪除）");
        Console.WriteLine("     /bus end      結束追蹤（一次取消全部訂閱，可以復原）");
        Console.WriteLine("     /bus status   查看 Bot 狀態");
        Console.WriteLine("     /say <內容>   讓 Bot 幫你說一句話（無用小功能；" +
                          $"可用 {SayModule.AllowListVariable} 限制使用者）");
        Console.WriteLine("     /ai status    AI 聊天狀態（模型、每週 token 用量、這個頻道的記憶）");
        Console.WriteLine("     /ai forget    忘掉這個頻道的 AI 對話記憶");
        Console.WriteLine("──────────────────────────────────────────────────────────");
        Console.WriteLine();
    }

    private static void PrintTokenHelp()
    {
        Console.WriteLine("❌ 沒有設定 Discord Bot Token，無法啟動。");
        Console.WriteLine();
        Console.WriteLine("取得 Token 的步驟：");
        Console.WriteLine("  1. 開 https://discord.com/developers/applications → New Application");
        Console.WriteLine("  2. 左側 Bot → Reset Token → 複製 Token");
        Console.WriteLine("  3. 同一頁把 Privileged Gateway Intents 全部保持關閉即可（本 Bot 不需要）");
        Console.WriteLine("  4. 左側 OAuth2 → URL Generator → 勾 bot + applications.commands");
        Console.WriteLine("     → 產生連結並把 Bot 邀進你的伺服器");
        Console.WriteLine();
        Console.WriteLine("然後用下列任一方式提供 Token（優先序：命令列 > 環境變數 > .env）：");
        Console.WriteLine();
        Console.WriteLine("  【建議】在專案根目錄的 .env 檔寫一行（PowerShell 風格也吃）：");
        Console.WriteLine("      DISCORD_TOKEN=你的Token");
        Console.WriteLine("      $env:DISCORD_TOKEN=你的Token     ← 這個寫法也支援");
        Console.WriteLine();
        Console.WriteLine("  或 設定環境變數：");
        Console.WriteLine("      $env:DISCORD_TOKEN = \"你的Token\"");
        Console.WriteLine();
        Console.WriteLine("  或 直接當參數：");
        Console.WriteLine("      dotnet run --project src\\TcBusBot.Discord -- --token 你的Token");
        Console.WriteLine();
        Console.WriteLine("  .env 放在別的地方（或叫別的名字）時，指定路徑即可：");
        Console.WriteLine("      dotnet run --project src\\TcBusBot.Discord -- --env D:\\secrets\\tcbus.env");
        Console.WriteLine("      --env 也可以指向「資料夾」（會讀裡面的 .env）");
        Console.WriteLine("      或 設定環境變數 $env:TCBUS_ENV = \"D:\\secrets\\tcbus.env\"");
        Console.WriteLine();
        Console.WriteLine("（Token 等同密碼。.env 已經在 .gitignore 裡，不會被提交。）");
    }

    private static string DescribeInteraction(SocketInteraction interaction)
        => interaction switch
        {
            SocketMessageComponent c => $"元件 {c.Data.CustomId}" +
                                        (c.Data.Values.Count > 0 ? $" values=[{string.Join(",", c.Data.Values)}]" : ""),
            SocketModal m => $"Modal {m.Data.CustomId}",
            SocketSlashCommand s => $"指令 /{s.CommandName}",
            _ => interaction.Type.ToString()
        };

    private static Task OnLog(LogMessage msg, bool verbose)
    {
        // 「沒開特權意圖」是**連不上**最常見的原因，而且 Discord.Net 只會給一行
        // 看不出所以然的訊息（Disconnected: Disallowed intent(s)），
        // 所以在這裡翻譯成「去哪個網頁按什麼」。
        var text0 = msg.Message ?? "";
        if (text0.Contains("Disallowed intent", StringComparison.OrdinalIgnoreCase)
            || text0.Contains("4014"))
        {
            Console.WriteLine();
            Console.WriteLine("❌ Discord 拒絕了連線：要求了沒有被允許的特權意圖（4014 Disallowed intent）");
            Console.WriteLine();
            Console.WriteLine("   這是因為 AI 聊天需要「Message Content Intent」，而它必須**手動開啟**：");
            Console.WriteLine("     1. https://discord.com/developers/applications → 選你的 Application");
            Console.WriteLine("     2. 左側 Bot → Privileged Gateway Intents → 打開 **Message Content Intent**");
            Console.WriteLine("     3. 按 Save Changes，然後重新啟動 Bot（Render 會自動重啟）");
            Console.WriteLine();
            Console.WriteLine("   不想開的話也可以只用公車功能（AI 聊天會關掉）：");
            Console.WriteLine("     dotnet run --project src\\TcBusBot.Discord -- --no-message-intent");
            Console.WriteLine();
        }

        // 預設只印錯誤，避免洗版；--verbose 時全部印出（排查連線問題用）
        if (!verbose && msg.Severity is not (LogSeverity.Error or LogSeverity.Critical))
            return Task.CompletedTask;

        var text = $"[{msg.Severity,-8}] {msg.Source}: {msg.Message}";
        if (msg.Exception is not null)
            text += $" | {msg.Exception.GetType().Name}: {msg.Exception.Message}";

        Console.WriteLine(text);
        return Task.CompletedTask;
    }

    /// <summary>把例外翻譯成人看得懂的一句話。</summary>
    /// <remarks>
    /// 注意要寫 global::Discord.Net —— 本專案的命名空間 TcBusBot.Discord
    /// 會把 `Discord.Net` 解析成 TcBusBot.Discord.Net。
    /// </remarks>
    private static string Describe(Exception ex) => ex switch
    {
        global::Discord.Net.HttpException { HttpCode: System.Net.HttpStatusCode.Unauthorized } =>
            "Token 無效（Discord 回 401 Unauthorized）",
        global::Discord.Net.HttpException http => $"Discord API 回傳 HTTP {(int)http.HttpCode}",
        System.Net.Http.HttpRequestException => "連不上 Discord（網路問題）",
        System.Net.Sockets.SocketException => "網路連線被拒絕（防火牆／代理／無對外網路）",
        TaskCanceledException => "連線逾時",
        _ => ex.Message
    };
}
