using TcBusBot.Core.Configuration;
using TcBusBot.Core.Storage;

namespace TcBusBot.Cli;

/// <summary>
/// 診斷 MongoDB：連線、延遲、內容，以及（選擇性）完整的寫入測試。
///
/// 為什麼需要這個指令：MongoDB 的問題幾乎都在「別人的機器上」
/// （雲端平台、另一台電腦），而那邊不一定有 shell 可以敲指令。
/// 這個指令讓同一個連線字串可以在任何地方被驗一次，並且**輸出跟 Bot 完全一樣的判斷邏輯**
/// （用的是同一套 <see cref="MongoSavedGroupRepository"/> 與 <see cref="MongoDiagnostics"/>）。
///
///   tcbus mongo --env TcBusBot-1.env              # 唯讀檢查
///   tcbus mongo --env TcBusBot-1.env --write-test # 另外做一次寫入／讀回／改名／刪除
///   tcbus mongo "mongodb+srv://…" --db TCBUS_SELFTEST
/// </summary>
public static class MongoCheck
{
    public static int Run(string[] args)
    {
        var envPath = TakeOption(ref args, "--env");
        var dbOverride = TakeOption(ref args, "--db");
        var writeTest = args.Contains("--write-test");

        // 連線字串：命令列直接給，或從 .env 檔讀（--env 可以指向檔案或資料夾）
        var connectionString = args.FirstOrDefault(a => a.StartsWith("mongodb", StringComparison.OrdinalIgnoreCase));

        var file = DotEnv.Load(envPath);

        connectionString ??= SettingResolver.Resolve(
            [], "--mongo", file, ["TCBUS_MONGO", "TCBUS_MONGO_URI", "MONGO_URI"]).Value;

        var database = dbOverride
                       ?? SettingResolver.Resolve([], "--mongo-db", file, ["TCBUS_MONGO_DB"]).Value
                       ?? MongoSavedGroupRepository.DefaultDatabase;

        Console.WriteLine("═══ MongoDB 連線檢查 ══════════════════════════════════════");
        if (!string.IsNullOrWhiteSpace(envPath))
            Console.WriteLine($"  .env 檔　 ：{DotEnv.FindFile(envPath) ?? $"（找不到 {envPath}）"}");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("❌ 沒有連線字串。用法：");
            Console.Error.WriteLine("     tcbus mongo --env TcBusBot-1.env");
            Console.Error.WriteLine("     tcbus mongo \"mongodb+srv://user:pass@host/?appName=x\" --db tcbus");
            return 1;
        }

        Console.WriteLine($"  連線字串　：{MongoDiagnostics.Mask(connectionString)}");
        Console.WriteLine($"  資料庫　　：{database}");
        Console.WriteLine($"  寫入測試　：{(writeTest ? "會做（用測試資料，做完刪掉）" : "不做（唯讀）")}");
        Console.WriteLine();

        // ── 連線前的網路診斷（連不上時最有用的部分）──
        Console.WriteLine("▶ 網路診斷");
        Console.WriteLine($"  SRV 檢查：{MongoDiagnostics.ProbeSrv(connectionString)}");
        Console.WriteLine($"  網路探測：{MongoDiagnostics.ProbeNetwork(connectionString)}");
        Console.WriteLine();

        // ── 真的連線 ──
        Console.WriteLine("▶ 連線");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        MongoSavedGroupRepository repo;
        try
        {
            repo = new MongoSavedGroupRepository(connectionString, database);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 連線失敗（{stopwatch.ElapsedMilliseconds} ms）");
            Console.WriteLine();
            Console.WriteLine(MongoDiagnostics.Checklist(connectionString));
            return 2;
        }

        stopwatch.Stop();
        Console.WriteLine($"  ✅ 連線成功（{stopwatch.ElapsedMilliseconds} ms）");
        Console.WriteLine($"  {repo.Describe()}");
        Console.WriteLine();

        // 極端測試帳號，避免碰到真實使用者的資料
        const ulong probeUser = 999999999999999999UL;
        const string probeName = "__tcbus_probe__";

        try
        {
            // ── 唯讀內容 ──
            Console.WriteLine("▶ 目前內容");
            var probeCount = repo.CountByUser(probeUser);
            Console.WriteLine($"  這個資料庫可以讀取（測試帳號現有 {probeCount} 筆）");

            if (!writeTest)
            {
                Console.WriteLine();
                Console.WriteLine("（要驗證寫入請加 --write-test）");
                return 0;
            }

            // ── 完整 CRUD ──
            Console.WriteLine();
            Console.WriteLine("▶ 寫入測試（模擬「存成訂閱組 → 讀回 → 改名 → 刪除」）");

            var payload = SavedGroupPayloadFactory.FromSubscriptions(
                new SavedTarget("臺中車站", new[] { "TXG12251", "TXG11020" }),
                new SavedTarget("靜宜大學", new[] { "TXG13567" }),
                new[] { ("TXG300", 1, "300"), ("TXG304", 1, "304") },
                notifyMinutes: 10);

            var (saved, savedMessage) = repo.Save(probeUser, probeName, payload);
            Console.WriteLine($"  1. 新增　　　：{saved}{(savedMessage is null ? "" : $"（{savedMessage}）")}");

            if (saved is not SaveGroupResult.Created and not SaveGroupResult.Updated)
            {
                Console.Error.WriteLine("  ❌ 寫入失敗 → 檢查 Atlas 的使用者權限（需要 readWrite）");
                return 3;
            }

            var stored = repo.GetByName(probeUser, probeName);
            Console.WriteLine($"  2. 讀回摘要　：{(stored is null ? "❌ 找不到" : $"id={stored.Id}、{stored.RouteCount} 條路線、提前 {stored.NotifyMinutes} 分")}");

            var restored = stored is null ? null : repo.GetPayload(stored.Id, probeUser);
            Console.WriteLine($"  3. 讀回內容　：{(restored is null ? "❌ 還原失敗" : $"{restored.LegCount} 段行程、{restored.RouteCount} 條路線、起點 {restored.Origin.DisplayName}")}");

            var renamed = stored is not null && repo.Rename(stored.Id, probeUser, probeName + "_renamed");
            Console.WriteLine($"  4. 改名　　　：{(renamed ? "✅" : "❌")}");

            if (stored is not null) repo.Touch(stored.Id, probeUser);
            var touched = stored is null ? null : repo.GetByName(probeUser, probeName + "_renamed");
            Console.WriteLine($"  5. 使用次數　：{touched?.UseCount ?? -1}（應為 1）");

            var deleted = stored is not null && repo.Delete(stored.Id, probeUser);
            var gone = repo.GetByName(probeUser, probeName + "_renamed") is null;
            Console.WriteLine($"  6. 刪除　　　：{(deleted && gone ? "✅ 已清除測試資料" : "❌")}");

            var ok = saved is SaveGroupResult.Created or SaveGroupResult.Updated
                     && stored is not null && restored is not null
                     && renamed && touched?.UseCount == 1 && deleted && gone;

            Console.WriteLine();
            Console.WriteLine(ok
                ? "✅ MongoDB 後端完全正常（新增／讀取／還原／改名／計數／刪除）"
                : "❌ 有一項沒過，請看上面的步驟");

            return ok ? 0 : 3;
        }
        finally
        {
            repo.Dispose();
        }
    }

    private static string? TakeOption(ref string[] args, string name)
    {
        var list = args.ToList();
        var index = list.IndexOf(name);

        if (index < 0 || index + 1 >= list.Count) return null;

        var value = list[index + 1];
        list.RemoveRange(index, 2);
        args = list.ToArray();
        return value;
    }
}
