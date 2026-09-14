using System.Text.RegularExpressions;

namespace TcBusBot.Core.Configuration;

/// <summary>
/// 極簡的 .env 載入器（零外部套件）。
///
/// 支援這幾種寫法，因為使用者常常直接把 PowerShell 的設定行貼進來：
/// <code>
///   DISCORD_TOKEN=abc            # 標準 dotenv
///   "DISCORD_TOKEN=abc"          # 加引號
///   export DISCORD_TOKEN=abc     # shell 風格
///   $env:DISCORD_TOKEN=abc       # PowerShell 風格（本專案實際使用的格式）
///   TCBUS_MONGO=${MONGO_URI}     # 引用「同檔案的鍵」或「系統環境變數」
/// </code>
///
/// **三種設定來源可以混用**（優先序：命令列 &gt; 系統環境變數 &gt; .env 檔），
/// 而且 .env 裡的值可以引用環境變數 —— 所以「金鑰放系統環境變數、
/// 其他設定放 .env」這種常見做法是行得通的。
///
/// 仍然不做行內註解處理（`KEY=value # 註解` 的 `#` 之後會留著）——
/// 值有可能是 Token，寧可保留原樣也不要誤刪字元。
/// </summary>
public static class DotEnv
{
    private static readonly string[] SearchNames = [".env"];

    /// <summary>`${NAME}` 形式的變數引用。</summary>
    private static readonly Regex VariableRef =
        new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// 找 .env 檔。
    ///
    /// <paramref name="explicitPath"/> 可以是：
    ///   * **檔案**（任何檔名都可以，不限定叫 .env）
    ///   * **資料夾** → 找那個資料夾裡的 .env
    ///   * 含引號的路徑（PowerShell 貼上時常帶引號）
    ///
    /// 沒有指定時，從「目前工作目錄」與「執行檔目錄」各往上找 <paramref name="maxLevels"/> 層。
    /// </summary>
    public static string? FindFile(string? explicitPath = null, int maxLevels = 8)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            // PowerShell 使用者常常連引號一起貼進來（'C:\path\.env' 或 "C:\path\.env"）
            var p = explicitPath.Trim().Trim('"', '\'');

            if (Directory.Exists(p))
            {
                foreach (var name in SearchNames)
                {
                    var inside = Path.Combine(p, name);
                    if (File.Exists(inside)) return Path.GetFullPath(inside);
                }
            }

            return File.Exists(p) ? Path.GetFullPath(p) : null;
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = start;
            for (var i = 0; i < maxLevels && !string.IsNullOrEmpty(dir); i++)
            {
                foreach (var name in SearchNames)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                dir = Path.GetDirectoryName(dir);
            }
        }

        return null;
    }

    public static Dictionary<string, string> Load(string? explicitPath = null)
        => LoadFromFile(FindFile(explicitPath));

    public static Dictionary<string, string> LoadFromFile(string? path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path)) return map;

        foreach (var raw in File.ReadAllLines(path))
        {
            var (key, value) = ParseLine(raw);
            if (key is not null && value is not null) map[key] = value;
        }

        ExpandValues(map);
        return map;
    }

    /// <summary>
    /// 展開值裡的 <c>${NAME}</c>：
    /// **先找同一個檔案裡的其他鍵，再找系統環境變數**；兩邊都沒有就保留原樣
    /// （保留原樣才看得出來是哪個變數沒設，而不是變成空字串後查不出原因）。
    /// </summary>
    private static void ExpandValues(Dictionary<string, string> map)
    {
        if (map.Count == 0) return;

        var keys = map.Keys.ToList();

        // 最多三輪：支援 A=${B}、B=${C} 這種串接，同時保證一定會停下來
        for (var pass = 0; pass < 3; pass++)
        {
            var changed = false;

            foreach (var key in keys)
            {
                var current = map[key];
                if (!current.Contains("${", StringComparison.Ordinal)) continue;

                var expanded = VariableRef.Replace(current, match =>
                {
                    var name = match.Groups[1].Value;

                    // 同檔案的其他鍵優先（但不要自己引用自己）
                    if (map.TryGetValue(name, out var local)
                        && !string.Equals(local, current, StringComparison.Ordinal))
                        return local;

                    return Environment.GetEnvironmentVariable(name) ?? match.Value;
                });

                if (!string.Equals(expanded, current, StringComparison.Ordinal))
                {
                    map[key] = expanded;
                    changed = true;
                }
            }

            if (!changed) break;
        }
    }

    /// <summary>
    /// 把載入的鍵值**也寫進行程的環境變數**，這樣行程裡任何地方
    /// （其他程式庫、你自己的診斷程式碼、子行程）都能用
    /// <c>Environment.GetEnvironmentVariable("DISCORD_TOKEN")</c> 讀到。
    ///
    /// <paramref name="overwrite"/> 預設 false：**已經存在的系統環境變數不會被 .env 覆蓋** ——
    /// 這樣才與「命令列 &gt; 系統環境變數 &gt; .env」的優先序一致。
    ///
    /// 回傳實際寫進去的鍵數。
    /// </summary>
    public static int ApplyToEnvironment(IReadOnlyDictionary<string, string> map, bool overwrite = false)
    {
        var applied = 0;

        foreach (var (key, value) in map)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null) continue;

            if (!overwrite && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) continue;

            // ⚠️ 只影響「這個行程」，不會改到系統設定；子行程會繼承。
            Environment.SetEnvironmentVariable(key, value);
            applied++;
        }

        return applied;
    }

    /// <summary>解析單行；註解與空行回傳 (null, null)。</summary>
    public static (string? Key, string? Value) ParseLine(string raw)
    {
        var line = raw.Trim().TrimStart('\uFEFF');
        if (line.Length == 0 || line.StartsWith('#')) return (null, null);

        // shell 風格
        if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            line = line["export ".Length..].TrimStart();

        // PowerShell 風格：$env:NAME=value
        if (line.StartsWith("$env:", StringComparison.OrdinalIgnoreCase))
            line = line["$env:".Length..].TrimStart();

        var eq = line.IndexOf('=');
        if (eq <= 0) return (null, null);

        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();

        // 去除一組對稱的引號
        if (value.Length >= 2)
        {
            if ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
        }

        if (key.Length == 0) return (null, null);
        return (key, value);
    }
}
