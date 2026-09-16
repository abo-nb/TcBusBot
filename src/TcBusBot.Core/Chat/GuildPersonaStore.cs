using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TcBusBot.Core.Storage;

namespace TcBusBot.Core.Chat;

/// <summary>
/// **可以被「馴服」的提示詞**：每個伺服器各自一份、可以事後加上去的規則。
///
/// 使用者要的是「讓機器人修改自己的提示詞，但只限那個伺服器」——
/// 所以這裡刻意**不改** `LLM_SYSTEM_PROMPT`（那是主機端的設定，改了會影響所有伺服器），
/// 而是每個伺服器一份 overlay，在組系統提示時接在使用者的提示詞後面：
///
/// ```
/// [主機的 LLM_SYSTEM_PROMPT]          ← 人格、語言、範圍（只有主機端能改）
/// [工具的規則]                        ← 由程式附加（能力說明）
/// [這個伺服器學到的規則]              ← 這裡（AI 自己或使用者都可以加）
/// ```
///
/// 典型用途：
///   * 「講話再簡短一點」「不要用條列」「叫我大大的時候要尊敬一點」
///   * **自訂表情的意思**：「某個表情 是 委屈」（Discord 的自訂表情每個伺服器都不一樣，
///     所以這種知識只能存在該伺服器，而且只能由那個伺服器的人教它）
///
/// 邊界（避免被拿來當無限的記事本或塞爆提示詞）：
///   * 每個伺服器最多 <see cref="MaxLinesPerGuild"/> 條、每條 <see cref="MaxLineLength"/> 字
///   * 進提示詞的總長再截到 <see cref="MaxOverlayLength"/>
///   * 完全相同的內容不會重複加（AI 很容易講兩次同一件事）
///   * `/rest` 可以整個重設（回傳被清掉的內容，讓使用者可以複製回去）
/// </summary>
public sealed class GuildPersonaStore
{
    public const int MaxLinesPerGuild = 40;
    public const int MaxLineLength = 300;
    public const int MaxGuilds = 200;
    public const int MaxOverlayLength = 2000;

    /// <summary>持久化用的鍵名（與每週用量共用同一個 blob 區）。</summary>
    public const string BlobKey = "guild_personas";

    /// <summary>
    /// 「全域」規則的桶子（主人的指令）。
    ///
    /// 為什麼要跟伺服器分開：使用者要的是「**只限那個伺服器**」，
    /// 但主人（後台指定的 ID ＋ 特殊 key）應該能改**所有伺服器都適用**的規則。
    /// `/rest` 只清伺服器那一份，不會動到全域（不然主人在 A 伺服器打 `/rest`
    /// 就把全體規則清掉了）。
    /// </summary>
    public const string GlobalKey = "*";

    private readonly ILlmStateStore? _store;
    private readonly object _gate = new();

    private readonly Dictionary<string, List<string>> _lines = new(StringComparer.Ordinal);

    public GuildPersonaStore(ILlmStateStore? store = null)
    {
        _store = store;
        Load();
    }

    /// <summary>學到了幾條（全部伺服器加起來，不含全域）——`/ai status` 用。</summary>
    public int TotalLines
    {
        get { lock (_gate) return _lines.Where(kv => kv.Key != GlobalKey).Sum(kv => kv.Value.Count); }
    }

    public int GuildCount
    {
        get { lock (_gate) return _lines.Count(kv => kv.Key != GlobalKey); }
    }

    /// <summary>主人指定的全域規則（所有伺服器都適用）。</summary>
    public IReadOnlyList<string> GlobalLines
    {
        get
        {
            lock (_gate)
                return _lines.TryGetValue(GlobalKey, out var list) ? list.ToList() : [];
        }
    }

    public IReadOnlyList<string> Lines(ulong guildId)
    {
        lock (_gate)
            return _lines.TryGetValue(Key(guildId), out var list) ? list.ToList() : [];
    }

    /// <summary>
    /// 要接在系統提示後面的內容（沒有學到東西時回傳空字串）。
    ///
    /// 順序：**全域（主人）→ 伺服器**。主人的規則放前面，伺服器自己的規則緊接著，
    /// 兩者都在「工具說明」之後（對最後的指示最聽話）。
    /// </summary>
    public string Overlay(ulong guildId)
    {
        var global = GlobalLines;
        var local = Lines(guildId);

        if (global.Count == 0 && local.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();

        if (global.Count > 0)
        {
            sb.AppendLine("── 全域規則（由 Bot 的主人指定，所有伺服器都適用）──");

            foreach (var line in global)
            {
                if (sb.Length + line.Length > MaxOverlayLength) break;
                sb.AppendLine($"• {line}");
            }

            if (local.Count > 0) sb.AppendLine();
        }

        if (local.Count > 0)
        {
            sb.AppendLine("── 這個伺服器後天學到的規則（由伺服器成員或你自己加上去的）──");
            sb.AppendLine("（這些是**這個伺服器專屬**的偏好與知識。 Discord 的自訂表情名稱只有在這裡才有意義。）");

            foreach (var line in local)
            {
                if (sb.Length + line.Length > MaxOverlayLength) break;
                sb.AppendLine($"• {line}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public enum LearnResult
    {
        Added,
        Duplicate,
        TooLong,
        TooMany,
        Empty
    }

    /// <summary>加一條規則（AI 自己學到的、或使用者教的都走這裡）。</summary>
    public LearnResult Learn(ulong guildId, string? text)
        => LearnInto(Key(guildId), text, evict: true);

    /// <summary>
    /// 加一條**全域**規則（只有通過主人授權的請求能用）。
    /// 不會被 <see cref="Reset"/> 清掉 —— 那是伺服器層級的重設。
    /// </summary>
    public LearnResult LearnGlobal(string? text) => LearnInto(GlobalKey, text, evict: false);

    private LearnResult LearnInto(string key, string? text, bool evict)
    {
        var clean = Clean(text);
        if (clean.Length == 0) return LearnResult.Empty;
        if (clean.Length > MaxLineLength) return LearnResult.TooLong;

        lock (_gate)
        {
            if (!_lines.TryGetValue(key, out var list))
            {
                // 太多伺服器時淘汰「最少條」的那個（通常是很少用的）
                if (evict && _lines.Count(kv => kv.Key != GlobalKey) >= MaxGuilds)
                {
                    var victim = _lines.Where(kv => kv.Key != GlobalKey)
                                       .OrderBy(kv => kv.Value.Count)
                                       .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                                       .First().Key;

                    _lines.Remove(victim);
                }

                _lines[key] = list = new List<string>();
            }

            if (list.Any(l => string.Equals(l, clean, StringComparison.Ordinal))) return LearnResult.Duplicate;
            if (list.Count >= MaxLinesPerGuild) return LearnResult.TooMany;

            list.Add(clean);
        }

        Save();
        return LearnResult.Added;
    }

    /// <summary>依關鍵字刪掉符合的規則（找不到就回傳 0）。</summary>
    public int Forget(ulong guildId, string? match) => ForgetIn(Key(guildId), match);

    /// <summary>依關鍵字刪掉全域規則（主人限定）。</summary>
    public int ForgetGlobal(string? match) => ForgetIn(GlobalKey, match);

    private int ForgetIn(string key, string? match)
    {
        var needle = (match ?? "").Trim();

        lock (_gate)
        {
            if (!_lines.TryGetValue(key, out var list)) return 0;

            var removed = needle.Length == 0
                ? list.Count
                : list.RemoveAll(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase));

            if (removed > 0) SaveLocked();
            return removed;
        }
    }

    /// <summary>
    /// 清掉這個伺服器學到的全部內容，回傳被清掉的內容（讓使用者可以複製回去）。
    ///
    /// ⚠️ **不會動到全域規則** —— 那是主人的，不該因為某個伺服器打了 `/rest` 就消失。
    /// </summary>
    public IReadOnlyList<string> Reset(ulong guildId)
    {
        List<string> removed;

        lock (_gate)
        {
            if (!_lines.TryGetValue(Key(guildId), out var list)) return [];
            removed = list.ToList();
            _lines.Remove(Key(guildId));
        }

        Save();
        return removed;
    }

    /// <summary>給 `/ai status`／log 看的一行說明。</summary>
    public string Describe(ulong guildId)
    {
        var count = Lines(guildId).Count;
        var global = GlobalLines.Count;

        var local = count == 0
            ? "還沒學到東西（可以教它，例如「講話再簡短一點」或「<表情> 是 什麼意思」）"
            : $"已學到 {count}/{MaxLinesPerGuild} 條（這個伺服器專用）";

        return global == 0 ? local : $"{local}｜另有 {global} 條全域規則（主人指定）";
    }

    // ─────────────────────────────────────────────────────
    //  持久化（與每週用量同一條後端鏈）
    // ─────────────────────────────────────────────────────

    public string Serialize()
    {
        lock (_gate) return JsonSerializer.Serialize(_lines, Json);
    }

    private void Load()
    {
        if (_store is null) return;

        try
        {
            var json = _store.GetBlob(BlobKey);
            if (string.IsNullOrWhiteSpace(json)) return;

            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json, Json);
            if (loaded is null) return;

            lock (_gate)
            {
                _lines.Clear();
                foreach (var (key, value) in loaded)
                    if (value is { Count: > 0 }) _lines[key] = value.Take(MaxLinesPerGuild).ToList();
            }        }
        catch (Exception ex)
        {
            BotLog.Warn($"[人格] 讀不到學到的規則（{ex.GetType().Name}），這次從空的開始");
        }
    }

    private void Save()
    {
        lock (_gate) SaveLocked();
    }

    private void SaveLocked()
    {
        if (_store is null) return;

        try
        {
            _store.SetBlob(BlobKey, JsonSerializer.Serialize(_lines, Json));
        }
        catch (Exception ex)
        {
            // 寫不進去只影響「記住這件事」，不該讓對話失敗
            BotLog.Warn($"[人格] 學到的規則寫入失敗（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>0 = 私訊（沒有伺服器），自成一個桶子。</summary>
    private static string Key(ulong guildId) => guildId.ToString();

    /// <summary>清掉換行與多餘空白（提示詞是一行一條，塞換行會讓結構亂掉）。</summary>
    public static string Clean(string? text)
    {
        var clean = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

        while (clean.Contains("  ", StringComparison.Ordinal))
            clean = clean.Replace("  ", " ", StringComparison.Ordinal);

        // 去掉開頭的條列符號（AI 常常會自己加上「-」或「•」）
        return clean.TrimStart('-', '•', '*', '·', ' ').Trim();
    }
}
