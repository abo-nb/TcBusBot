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
                .Select(r => $"{r.DomainName.Value.TrimEnd('.')}:{r.Port}")
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

    /// <summary>連不上時印給使用者的檢查清單（照發生機率排序）。</summary>
    public static string Checklist(string connectionString)
    {
        var host = ExtractHost(connectionString) ?? "（解析不出主機）";

        return $"""
                  連線字串 ：{Mask(connectionString)}
                  SRV 檢查 ：{ProbeSrv(connectionString)}
                  主機     ：{host}
                  請依序檢查：
                    1. ★ Atlas → Network Access（IP 白名單）
                       雲端平台（Render／Heroku…）的對外 IP 是**動態**的，
                       免費方案幾乎只能允許 0.0.0.0/0。
                       → 判別方法：用你的電腦跑同一個連線字串
                         （dotnet run --project src\TcBusBot.Discord -- --env TcBusBot-1.env --data fixture）
                         電腦連得上、雲端連不上 = 就是白名單。
                    2. 使用者名稱／密碼是否正確
                       密碼若含 @ : / ? 等字元，必須 URL encode。
                    3. 叢集是否被暫停（Atlas 介面上顯示 Paused）
                       免費叢集閒置或手動暫停後要按 Resume，喚醒要幾十秒。
                    4. 連線字串是否漏了資料庫名稱
                       Atlas 的「Connect」按鈕給的字串常是
                       mongodb+srv://user:pass@xxx/?appName=…（沒有 /dbname），
                       本程式會用 TCBUS_MONGO_DB（預設 tcbus）補上，不用自己加。
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
