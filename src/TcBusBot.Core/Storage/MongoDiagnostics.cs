using DnsClient;
using MongoDB.Bson;
using MongoDB.Driver;

namespace TcBusBot.Core.Storage;

/// <summary>
/// MongoDB 連線失敗時的診斷。
///
/// 為什麼需要：`TimeoutException: A timeout occurred after 3000ms selecting a server…`
/// 這行訊息**完全看不出原因**。實際上雲端（Render／Heroku 這類）連 Atlas 失敗
/// 幾乎都是這四件事之一：
///
///   1. **Atlas 的 IP 白名單**（Network Access）—— 雲端平台的對外 IP 是動態的，
///      免費方案通常只能允許 `0.0.0.0/0`。**這是最常見的原因。**
///   2. 使用者名稱／密碼錯（密碼有特殊字元時要 URL encode）
///   3. 免費叢集閒置會被**暫停**，第一次連線要等它喚醒（可能 30 秒以上）
///   4. 連線字串少了 `/<dbname>` 或 `?retryWrites=true&w=majority`
///
/// 所以這裡做兩件事：把「到底是哪一種」問出來（DNS SRV 查詢），以及把檢查順序印出來。
/// </summary>
public static class MongoDiagnostics
{
    /// <summary>連線字串的遮罩（只留 scheme 與 host，不印帳密）。</summary>
    public static string Mask(string connectionString) => MongoSavedGroupRepository.Mask(connectionString);

    /// <summary>
    /// 檢查 `mongodb+srv` 的 SRV 記錄。
    ///
    /// 這一步能把問題一刀切開：
    ///   * **查得到節點** → DNS 沒問題，連不上就是白名單／帳密／TLS／叢集休眠
    ///   * **查不到** → DNS 被擋、叢集名稱打錯，或叢集已被刪除
    /// </summary>
    public static string ProbeSrv(string connectionString)
    {
        try
        {
            var host = ExtractHost(connectionString);
            if (host is null) return "解析不出主機名稱（連線字串格式可能不對）";

            if (!connectionString.StartsWith("mongodb+srv://", StringComparison.OrdinalIgnoreCase))
                return $"不是 mongodb+srv（直接連 {host}，沒有 SRV 可查）";

            var lookup = new LookupClient { Timeout = TimeSpan.FromSeconds(5) };
            var result = lookup.Query($"_mongodb._tcp.{host}", QueryType.SRV);

            var targets = result.Answers.SrvRecords()
                .Select(r => $"{r.Target.Value.TrimEnd('.')}:{r.Port}")
                .Distinct()
                .ToList();

            return targets.Count > 0
                ? $"查到 {targets.Count} 個節點（{string.Join("、", targets.Take(3))}）"
                : "查不到 SRV 記錄（DNS 被擋、叢集名稱錯誤，或叢集已被刪除）";
        }
        catch (Exception ex)
        {
            return $"SRV 查詢失敗（{ex.GetType().Name}: {ex.Message}）";
        }
    }

    /// <summary>
    /// 網路連通性探測：把 SRV 指向的每個節點拿來**解析 IP → 試 TCP → 試 TLS**。
    ///
    /// 為什麼要這麼細：`TimeoutException … selecting a server` 只代表「15 秒內沒挑到可用的伺服器」，
    /// 可能的原因有五种，但用這一層就能直接分辨：
    ///
    /// | 探測結果 | 結論 |
    /// | --- | --- |
    /// | SRV 查不到 | DNS 被擋、叢集名稱錯，或叢集已刪除 |
    /// | SRV 有節點、**節點名稱解析不出 IP** | DNS 只給得出 SRV，A 記錄被擋（常見於公司網路） |
    /// | 解析出 IP、**TCP 連不上** | 對外 27017 被防火牆擋，或 Atlas IP 白名單沒開 |
    /// | TCP 連上、**TLS 失敗** | TLS 被中間人攔截／憑證問題 |
    /// | TCP + TLS 都成功 | 網路沒問題 → 問題在帳密，或叢集被暫停（Atlas 顯示 Paused） |
    /// </summary>
    public static string ProbeNetwork(string connectionString)
    {
        var host = ExtractHost(connectionString);
        if (host is null) return "解析不出主機名稱";

        var lines = new List<string>();

        try
        {
            var targets = new List<string>();

            if (connectionString.StartsWith("mongodb+srv://", StringComparison.OrdinalIgnoreCase))
            {
                var lookup = new LookupClient { Timeout = TimeSpan.FromSeconds(5) };
                var srv = lookup.Query($"_mongodb._tcp.{host}", QueryType.SRV);

                targets.AddRange(srv.Answers.SrvRecords()
                    .Select(r => $"{r.Target.Value.TrimEnd('.')}:{r.Port}")
                    .Distinct());
            }
            else
            {
                targets.Add(host.Contains(':') ? host : $"{host}:27017");
            }

            if (targets.Count == 0) return "SRV 查不到節點（見上一行的 SRV 檢查）";

            foreach (var target in targets.Take(4))
            {
                lines.Add($"    {ProbeTarget(target)}");
            }

            if (targets.Count > 4) lines.Add($"    …另有 {targets.Count - 4} 個節點");
        }
        catch (Exception ex)
        {
            return $"探測失敗（{ex.GetType().Name}: {ex.Message}）";
        }

        return string.Join("\n", lines);
    }

    private static string ProbeTarget(string target)
    {
        var parts = target.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 27017;

        // 1) 解析 IP
        System.Net.IPAddress[] addresses;
        try
        {
            addresses = System.Net.Dns.GetHostAddresses(host);
        }
        catch (Exception ex)
        {
            return $"{target} → ❌ 解析不出 IP（{ex.GetType().Name}）";
        }

        if (addresses.Length == 0) return $"{target} → ❌ 沒有 A 記錄";

        var address = addresses[0];

        // 2) TCP 連線（3 秒）
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync(address, port);

            if (!task.Wait(TimeSpan.FromSeconds(3)))
                return $"{target} → {address} → ❌ TCP 逾時（防火牆／白名單）";

            // 3) TLS 握手（3 秒）—— 分辨「連得上但 TLS 被擋」
            using var ssl = new System.Net.Security.SslStream(client.GetStream(), false,
                (_, _, _, _) => true);   // 只測連通性，不驗憑證

            var tlsTask = ssl.AuthenticateAsClientAsync(host);
            if (!tlsTask.Wait(TimeSpan.FromSeconds(3)))
                return $"{target} → {address} → ⚠️ TCP 成功但 TLS 逾時";

            return $"{target} → {address} → ✅ TCP ＋ TLS 都成功";
        }
        catch (Exception ex)
        {
            // ⚠️ 一定要挖到最內層：.NET 的 TLS 失敗常常只說
            //    「Authentication failed, see inner exception」，
            //    真正的答案（SEC_E_NO_CREDENTIALS、憑證錯誤、連線被關…）在最裡面。
            return $"{target} → {address} → ❌ {RootCause(ex)}";
        }
    }

    /// <summary>把例外鏈裡「最有意義的那幾層」抓出來（外層訊息常常沒有資訊）。</summary>
    private static string RootCause(Exception ex)
    {
        var chain = new List<string>();

        for (var e = ex; e is not null && chain.Count < 4; e = e.InnerException)
            chain.Add($"{e.GetType().Name}: {Truncate((e.Message ?? "").Replace('\n', ' ').Trim(), 90)}");

        return string.Join(" ← ", chain);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>連不上時印給使用者的檢查清單（照發生機率排序）。</summary>
    public static string Checklist(string connectionString)
    {
        var host = ExtractHost(connectionString) ?? "（解析不出主機）";

        return $"""
                  連線字串 ：{Mask(connectionString)}
                  SRV 檢查 ：{ProbeSrv(connectionString)}
                  主機     ：{host}
                  網路探測 ：{ProbeNetwork(connectionString)}
                  請對照上面的探測結果判斷：
                    SRV 查不到 ........................ DNS 被擋／叢集名稱錯／叢集已刪除
                    節點解析不出 IP ................... A 記錄被擋（公司網路常見）
                    IP 有了但 TCP 連不上 .............. 對外 27017 被擋，或 **Atlas IP 白名單沒開**
                    TCP 過但 TLS 失敗 ................. TLS 被中間人攔截／憑證問題
                    TCP ＋ TLS 都成功 ................. 網路沒問題 → 帳密錯，或叢集被暫停（Atlas 顯示 Paused，按 Resume）
                  補充：
                    • 雲端平台（Render／Heroku…）對外 IP 是動態的，Atlas 幾乎只能允許 0.0.0.0/0
                    • 密碼含 @ : / ? 等字元要 URL encode（用 Atlas 的 Connect 按鈕複製最保險）
                    • 連線字串可以不含 /dbname，本程式會用 TCBUS_MONGO_DB（預設 tcbus）補上
                  目前先用本機儲存（Bot 照常運作，只是不會同步到其他機器）；
                  連上之後會再印一則提醒，重啟服務即可切換。
                """;
    }

    /// <summary>從連線字串取出主機名稱（不含帳密）。</summary>
    public static string? ExtractHost(string connectionString)
    {
        try
        {
            var afterScheme = connectionString[(connectionString.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var at = afterScheme.IndexOf('@');
            var hostPart = at >= 0 ? afterScheme[(at + 1)..] : afterScheme;
            var slash = hostPart.IndexOfAny(['/', '?']);

            return (slash >= 0 ? hostPart[..slash] : hostPart).Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// MongoDB 連不上時的背景重試（**只提醒，不切換**）。
///
/// 為什麼不自動切換到 MongoDB：中間用本機儲存寫進去的訂閱組不會自動搬過去，
/// 自動切換會讓使用者以為「資料都在雲端了」。所以這裡只做兩件事：
///   * 每 5 分鐘測一次
///   * 一旦連得上就印一則明確訊息，請使用者重啟（重啟後就會用 MongoDB）
/// </summary>
internal sealed class MongoRecheckLoop : IDisposable
{
    private readonly string _connectionString;
    private readonly string _database;
    private readonly CancellationTokenSource _cts = new();

    public MongoRecheckLoop(string connectionString, string database)
    {
        _connectionString = connectionString;
        _database = database;

        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (await CanConnectAsync(ct).ConfigureAwait(false))
                {
                    BotLog.Warn("[儲存] MongoDB 現在連得到了 → 重啟服務就會改用 MongoDB " +
                                "（期間在本機建立的訂閱組不會自動搬過去）");
                    return;   // 提醒一次就夠
                }
            }
        }
        catch (OperationCanceledException) { /* 正常結束 */ }
        catch (Exception) { /* 背景工作不該影響 Bot */ }
    }

    private async Task<bool> CanConnectAsync(CancellationToken ct)
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(_connectionString);
            settings.ConnectTimeout = TimeSpan.FromSeconds(5);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            settings.SocketTimeout = TimeSpan.FromSeconds(5);

            var client = new MongoClient(settings);
            var db = client.GetDatabase(_database);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token)
                    .ConfigureAwait(false);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 已取消 */ }
        _cts.Dispose();
    }
}
