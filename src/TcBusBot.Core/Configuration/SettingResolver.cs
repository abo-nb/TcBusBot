namespace TcBusBot.Core.Configuration;

/// <summary>
/// 單一設定的解析：**命令列 &gt; 系統環境變數 &gt; `.env` 檔**。
///
/// 為什麼抽到 Core：這段優先序是「設定從哪裡來」的核心規則，
/// 而且很容易不小心改壞（例如先查 .env 再查環境變數，行為就整個反過來）。
/// 放在 Core 就能在 `tcbus selftest` 裡直接驗，不需要啟動 Bot。
///
/// **找不到 `.env` 檔時完全不會有問題**：`DotEnv.LoadFromFile(null)` 回傳空的字典，
/// 解析流程照樣往下走（先看命令列、再看環境變數），所以
/// 「只用系統環境變數設定」是支援的。
/// </summary>
public static class SettingResolver
{
    /// <summary>
    /// 這次啟動**問過**的所有環境變數名稱（含別名）。
    ///
    /// 為什麼要記這個：`env-vars.csv` 是給人看的環境變數清單，
    /// 但「加了新變數卻忘了更新文件」是遲早會發生的事。
    /// 有了這份清單，`--dryrun` 就能自動比對「程式用到的」與「文件寫的」，
    /// 少了或多了都會被指出來（見 DryRun.AuditEnvCsv）。
    /// </summary>
    public static IReadOnlyCollection<string> SeenKeys
    {
        get { lock (Seen) return Seen.ToArray(); }
    }

    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);

    /// <summary>
    /// 解析一個設定值，並回報它是從哪裡來的（啟動橫幅會印出來，方便查「為什麼沒生效」）。
    ///
    /// <paramref name="keys"/> 是別名清單，依序檢查（例：`DISCORD_TOKEN`、`DISCORD_BOT_TOKEN`…）。
    /// 空字串與只有空白的值都會被跳過 —— 不然「設成空字串」會被當成有效設定。
    /// </summary>
    public static (string? Value, string Source) Resolve(
        string[] args, string cliName, IReadOnlyDictionary<string, string> file, params string[] keys)
    {
        lock (Seen) foreach (var key in keys) Seen.Add(key);

        // 1) 命令列最優先
        var cli = TakeOption(args, cliName);
        if (!string.IsNullOrWhiteSpace(cli)) return (cli.Trim(), $"命令列 {cliName}");

        // 2) 系統環境變數（.env 不存在時，這裡就是唯一的來源）
        foreach (var key in keys)
        {
            var v = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(v)) return (v.Trim(), $"環境變數 {key}");
        }

        // 3) .env 檔（沒有檔案時 file 是空的字典，這裡自然全部落空）
        foreach (var key in keys)
        {
            if (file.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return (v.Trim(), $".env 的 {key}");
        }

        return (null, "（未設定）");
    }

    /// <summary>
    /// 取命令列選項的值。支援三種寫法：
    /// <code>
    ///   --env C:\secrets\.env      ← 空白分隔
    ///   --env=C:\secrets\.env      ← 等號
    ///   --env:"C:\secrets\.env"    ← 等號加引號（PowerShell 常見）
    /// </code>
    /// </summary>
    public static string? TakeOption(string[] args, string name)
    {
        var eqForm = name + "=";
        var colonForm = name + ":";

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];

            foreach (var prefix in new[] { eqForm, colonForm })
            {
                if (args[i].StartsWith(prefix, StringComparison.Ordinal) && args[i].Length > prefix.Length)
                    return args[i][prefix.Length..].Trim('"', '\'');
            }
        }

        return null;
    }
}
