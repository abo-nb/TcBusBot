using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TcBusBot.Core.Storage;

namespace TcBusBot.Core.Chat;

/// <summary>
/// 「學到的提示詞」的容量上限。**全部都可以用環境變數調**。
///
/// 為什麼要可調：這些上限直接決定「提示詞會變多長」（＝每一次對話的錢）
/// 與「儲存會長多大」，而不同伺服器的需求差很多 ——
/// 三兩好友的私人伺服器可能想多記一點，公開的大伺服器則要嚴格限制
/// （不然任何人都能叫 Bot 記 40 條，把它變成別人的記事本）。
///
/// 對應的環境變數：
///   * <see cref="MaxLinesPerGuild"/>　`LLM_MAX_GUILD_RULES`（預設 40）
///   * <see cref="MaxLineLength"/>　　 `LLM_MAX_RULE_CHARS`（預設 300）
///   * <see cref="MaxGuilds"/>　　　　 `LLM_MAX_PERSONA_GUILDS`（預設 200）
///   * <see cref="MaxOverlayLength"/>　`LLM_MAX_OVERLAY_CHARS`（預設 2000）
///   * <see cref="MaxAuditEntries"/>　 `LLM_MAX_AUDIT_ENTRIES`（預設 200）
/// </summary>
public sealed record PersonaLimits
{
    /// <summary>沒設定時用的預設值。</summary>
    public static readonly PersonaLimits Default = new();

    /// <summary>每個伺服器最多幾條（全域規則那個桶子也共用這個上限）。</summary>
    public int MaxLinesPerGuild { get; init; } = 40;

    /// <summary>每一條最多幾個字。</summary>
    public int MaxLineLength { get; init; } = 300;

    /// <summary>最多幾個伺服器可以有自己的規則（超過淘汰「條數最少」的那個）。</summary>
    public int MaxGuilds { get; init; } = 200;

    /// <summary>接進提示詞的總長上限（字元）。這是「每次對話要多花多少 token」的煞車。</summary>
    public int MaxOverlayLength { get; init; } = 2000;

    /// <summary>主人操作紀錄最多留幾筆。</summary>
    public int MaxAuditEntries { get; init; } = 200;

    /// <summary>給 log／`--dryrun`／`/ai status` 看的一行摘要。</summary>
    public string Describe()
        => $"每伺服器最多 {MaxLinesPerGuild} 條 × {MaxLineLength} 字、" +
           $"最多 {MaxGuilds} 個伺服器有規則、接進提示詞最多 {MaxOverlayLength} 字、" +
           $"主人紀錄 {MaxAuditEntries} 筆";
}

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
/// 邊界（避免被拿來當無限的記事本或塞爆提示詞）——全部都是環境變數可調，見 <see cref="PersonaLimits"/>：
///   * 每個伺服器最多 `LLM_MAX_GUILD_RULES` 條、每條 `LLM_MAX_RULE_CHARS` 字
///   * 進提示詞的總長再截到 `LLM_MAX_OVERLAY_CHARS`
///   * 完全相同的內容不會重複加（AI 很容易講兩次同一件事）
///   * `/rest` 可以整個重設（回傳被清掉的內容，讓使用者可以複製回去）
/// </summary>
public sealed class GuildPersonaStore
{
    private readonly ILlmStateStore? _store;
    private readonly object _gate = new();

    private readonly Dictionary<string, List<string>> _lines = new(StringComparer.Ordinal);

    public GuildPersonaStore(ILlmStateStore? store = null, PersonaLimits? limits = null)
    {
        _store = store;
        Limits = limits ?? PersonaLimits.Default;
        Load();
    }

    /// <summary>這個實例實際套用的容量上限（`/ai status`、工具訊息都拿這裡的數字）。</summary>
    public PersonaLimits Limits { get; }

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
                if (sb.Length + line.Length > Limits.MaxOverlayLength) break;
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
                if (sb.Length + line.Length > Limits.MaxOverlayLength) break;
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
    /// 加一條**全域**規則（所有伺服器都適用）。
    ///
    /// ⚠️ **必須傳入 <see cref="OwnerGrant"/>** —— 這是刻意的設計：
    /// 全域設定影響每一台伺服器，所以把「有沒有授權」變成**編譯器檢查得到的參數**，
    /// 而不是靠提示詞叫模型「要聽主人的話」。就算模型抽風、被提示注入，
    /// 或是未來有人改壞了工具掛載的邏輯，沒有憑證就是寫不進去。
    /// </summary>
    public LearnResult LearnGlobal(OwnerGrant grant, string? text)
    {
        var result = LearnInto(GlobalKey, text, evict: false);

        if (result == LearnResult.Added)
            Audit(grant, "新增全域規則", Clean(text));

        return result;
    }

    /// <summary>
    /// 刪掉符合關鍵字的全域規則（主人限定）。
    ///
    /// ⚠️ 關鍵字留白＝清掉**全部**全域規則，這種破壞性動作用
    /// <paramref name="confirmAll"/> 再擋一層 —— 模型只要不小心傳了空字串，
    /// 就會把主人累積的規則全部清掉。
    /// </summary>
    public int ForgetGlobal(OwnerGrant grant, string? match, bool confirmAll = false)
    {
        var needle = (match ?? "").Trim();

        if (needle.Length == 0 && !confirmAll)
        {
            Audit(grant, "⚠️ 拒絕清空全部全域規則（沒有確認）", "");
            return 0;
        }

        var removed = ForgetIn(GlobalKey, needle);

        if (removed > 0)
            Audit(grant, needle.Length == 0 ? "清空全部全域規則" : $"刪除全域規則（關鍵字：{needle}）", $"{removed} 條");

        return removed;
    }

    private LearnResult LearnInto(string key, string? text, bool evict)
    {
        var clean = Clean(text);
        if (clean.Length == 0) return LearnResult.Empty;
        if (clean.Length > Limits.MaxLineLength) return LearnResult.TooLong;

        lock (_gate)
        {
            if (!_lines.TryGetValue(key, out var list))
            {
                // 太多伺服器時淘汰「最少條」的那個（通常是很少用的）
                if (evict && _lines.Count(kv => kv.Key != GlobalKey) >= Limits.MaxGuilds)
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
            if (list.Count >= Limits.MaxLinesPerGuild) return LearnResult.TooMany;

            list.Add(clean);
        }

        Save();
        return LearnResult.Added;
    }

    /// <summary>依關鍵字刪掉符合的規則（找不到就回傳 0）。</summary>
    public int Forget(ulong guildId, string? match) => ForgetIn(Key(guildId), match);

    private int ForgetIn(string key, string? match)
    {
        var needle = (match ?? "").Trim();

        lock (_gate)
        {
            if (!_lines.TryGetValue(key, out var list)) return 0;

            int removed;

            if (needle.Length == 0)
            {
                // ⚠️ 這裡以前只回傳 `list.Count` 卻**沒有真的清掉**：
                //    呼叫端（模型）會以為刪了 N 條，實際上規則還在。
                //    「留白＝刪掉全部」必須真的刪。
                removed = list.Count;
                list.Clear();
            }
            else
            {
                removed = list.RemoveAll(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }

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

    /// <summary>
    /// **把另一個伺服器的自訂提示詞複製過來**（`/ai pset from:`）的結果。
    ///
    /// 為什麼不沿用 <see cref="PersonaSetResult"/>：複製是一對多的操作，
    /// 「帶過來幾條／跳過幾條重複／因為上限少帶幾條」都必須講清楚 ——
    /// 不然使用者只看到「成功」，卻不知道少了什麼。
    /// </summary>
    public sealed record PersonaCopyResult(
        int Copied, int Skipped, int Dropped, int Removed, IReadOnlyList<string> Lines)
    {
        public bool Ok => Copied > 0;
    }

    /// <summary>
    /// 把 <paramref name="sourceGuildId"/> 的自訂提示詞複製到 <paramref name="targetGuildId"/>。
    ///
    /// <paramref name="replace"/> 為 true 時**先備份目標的舊內容再整個換掉**
    /// （目標是空的時候行為等同於複製全部）。
    ///
    /// 兩種模式的差別（都刻意保留來源的原文，不重新清理）：
    ///   * 覆蓋：直接把來源那一批寫進去（來源的內容本來就通過驗證了，
    ///     重新清理只會讓「當初允許、現在不允許」的規則莫名其妙消失）
    ///   * 追加：一條一條走 <see cref="Learn"/>（去重、驗長度、算上限），
    ///     撞到上限就停，並把「沒帶過來幾條」回報給使用者
    /// </summary>
    public PersonaCopyResult CopyFrom(ulong targetGuildId, ulong sourceGuildId, ulong userId, bool replace)
    {
        if (targetGuildId == sourceGuildId) return new PersonaCopyResult(0, 0, 0, 0, Lines(targetGuildId));

        var source = Lines(sourceGuildId);
        if (source.Count == 0) return new PersonaCopyResult(0, 0, 0, 0, Lines(targetGuildId));

        if (replace)
        {
            var backup = Reset(targetGuildId);
            var taken = source.Take(Limits.MaxLinesPerGuild).ToList();

            PutBack(targetGuildId, taken);
            AuditAdmin(userId,
                $"從伺服器 {sourceGuildId} 複製提示詞（覆蓋，共 {taken.Count} 條）",
                $"{taken.Count} 條");

            return new PersonaCopyResult(
                taken.Count, 0, source.Count - taken.Count, backup.Count, Lines(targetGuildId));
        }

        var copied = 0;
        var skipped = 0;
        var dropped = 0;

        foreach (var line in source)
        {
            var result = Learn(targetGuildId, line);

            switch (result)
            {
                case LearnResult.Added: copied++; break;
                case LearnResult.Duplicate: skipped++; break;
                default: dropped++; break;
            }

            // 撞到上限之後就不用再試了（後面的每一條都會失敗）
            if (result == LearnResult.TooMany)
            {
                dropped += source.Count - copied - skipped - dropped;
                break;
            }
        }

        if (copied > 0)
            AuditAdmin(userId,
                $"從伺服器 {sourceGuildId} 複製提示詞（追加 {copied} 條）",
                $"{copied} 條");

        return new PersonaCopyResult(copied, skipped, dropped, 0, Lines(targetGuildId));
    }

    /// <summary>
    /// **管理員用指令設定這個伺服器的自訂提示詞**（`/ai pset`）的結果。
    /// <see cref="Lines"/> 是設定完之後的完整內容（給指令直接顯示，不用再查一次）。
    /// </summary>
    public sealed record PersonaSetResult(
        LearnResult Result, int Removed, IReadOnlyList<string> Lines)
    {
        public bool Ok => Result == LearnResult.Added;
    }

    /// <summary>
    /// 管理員用指令設定這個伺服器的自訂提示詞：<paramref name="replace"/> 為 true 時
    /// 先清掉現有的全部再寫入，否則只是追加一條。
    ///
    /// 為什麼放在 store（而不是寫在指令模組裡）：這條路徑**一定會動到使用者的東西**，
    /// 所以「覆蓋時先清掉什麼、上限怎麼算、稽核要記什麼」都必須能離線測試。
    /// 授權（誰是管理員）留在呼叫端 —— 那是互動層的事（只有那裡拿得到 Discord 使用者 ID）。
    /// </summary>
    public PersonaSetResult SetRules(ulong guildId, ulong userId, string? text, bool replace)
    {
        var clean = Clean(text);
        if (clean.Length == 0) return new PersonaSetResult(LearnResult.Empty, 0, Lines(guildId));

        if (!replace)
        {
            var appended = Learn(guildId, clean);

            if (appended == LearnResult.Added)
                AuditAdmin(userId, "追加這個伺服器的提示詞", clean);

            return new PersonaSetResult(appended, 0, Lines(guildId));
        }

        // ⚠️ 覆蓋模式的順序很重要：清掉舊的 → 寫新的 → **寫不進去就把舊的還原**。
        //    如果只顧著清，一個失敗的指令（內容太長）就會讓使用者的設定憑空消失。
        var backup = Reset(guildId);
        var result = Learn(guildId, clean);

        if (result != LearnResult.Added)
        {
            PutBack(guildId, backup);
            return new PersonaSetResult(result, 0, Lines(guildId));
        }

        AuditAdmin(userId, "設定這個伺服器的提示詞（覆蓋全部）", clean);

        return new PersonaSetResult(result, backup.Count, Lines(guildId));
    }

    /// <summary>把剛才備份起來的內容原封不動放回去（還原用；不做清理與去重）。</summary>
    private void PutBack(ulong guildId, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;

        lock (_gate)
            _lines[Key(guildId)] = lines.Take(Limits.MaxLinesPerGuild).ToList();

        Save();
    }

    /// <summary>給 `/ai status`／log 看的一行說明。</summary>
    public string Describe(ulong guildId)
    {
        var count = Lines(guildId).Count;
        var global = GlobalLines.Count;

        var local = count == 0
            ? "還沒學到東西（可以教它，例如「講話再簡短一點」或「<表情> 是 什麼意思」）"
            : $"已學到 {count}/{Limits.MaxLinesPerGuild} 條（這個伺服器專用）";

        var auditNote = _audit.Count == 0 ? "" : $"｜主人操作紀錄 {_audit.Count} 筆";

        return (global == 0 ? local : $"{local}｜另有 {global} 條全域規則（主人指定）") + auditNote;
    }

    // ─────────────────────────────────────────────────────
    //  主人操作紀錄（audit log）
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 每一次「動到全域設定」的紀錄。
    ///
    /// 為什麼要記：全域規則影響每一台伺服器，而它是由**模型呼叫工具**寫進去的 ——
    /// 模型抽風（亂叫工具）時，光看回覆看不出發生什麼事。
    /// 有了這份紀錄，誰、什麼時候、改了什麼都查得到，
    /// 而且它**會被持久化**（重啟後還在），不是只有 console 上的幾行。
    /// </summary>
    public sealed record AuditEntry(
        [property: JsonPropertyName("at")] DateTimeOffset At,
        [property: JsonPropertyName("user")] ulong UserId,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("text")] string Text)
    {
        public string Describe()
            => $"{At.ToLocalTime():MM-dd HH:mm}　主人 {UserId}　{Action}" +
               (Text.Length == 0 ? "" : $"：{Text}");
    }

    private readonly List<AuditEntry> _audit = new();

    public IReadOnlyList<AuditEntry> AuditLog(int limit = 10)
    {
        lock (_gate) return _audit.TakeLast(Math.Max(1, limit)).Reverse().ToList();
    }

    /// <summary>
    /// 記一筆「有人用指令改了設定」的紀錄。
    ///
    /// 跟主人操作記在**同一份稽核**裡（`/ai audit` 看得到）：使用者要的是
    /// 「誰、什麼時候、改了什麼」查得到，至於是打字下令還是打指令，不是重點。
    /// </summary>
    public void AuditAdmin(ulong userId, string action, string text) => Audit(userId, action, text);

    /// <summary>記一筆主人操作（同時寫進 console，讓主機端也看得到）。</summary>
    private void Audit(OwnerGrant grant, string action, string text) => Audit(grant.UserId, action, text);

    private void Audit(ulong userId, string action, string text)
    {
        var entry = new AuditEntry(DateTimeOffset.UtcNow, userId, action, Truncate(text, 200));

        lock (_gate)
        {
            _audit.Add(entry);
            while (_audit.Count > Limits.MaxAuditEntries) _audit.RemoveAt(0);
            SaveLocked();
        }

        BotLog.Warn($"[設定] {entry.Describe()}");
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    // ─────────────────────────────────────────────────────
    //  持久化（與每週用量同一條後端鏈）
    // ─────────────────────────────────────────────────────

    public string Serialize()
    {
        lock (_gate) return JsonSerializer.Serialize(Snapshot(), Json);
    }

    /// <summary>要存下來的內容（規則 ＋ 主人操作紀錄）。</summary>
    private Persisted Snapshot()
        => new(_lines, _audit.TakeLast(Limits.MaxAuditEntries).ToList());

    /// <summary>存檔格式（舊版的「純字典」也讀得進來）。</summary>
    private sealed record Persisted(
        [property: JsonPropertyName("lines")] Dictionary<string, List<string>> Lines,
        [property: JsonPropertyName("audit")] List<AuditEntry> Audit);

    private void Load()
    {
        if (_store is null) return;

        try
        {
            var json = _store.GetBlob(BlobKey);
            if (string.IsNullOrWhiteSpace(json)) return;

            // 先試新格式（含 audit），再試舊格式（只有 lines 的純字典）
            Persisted? current = null;
            Dictionary<string, List<string>>? legacy = null;

            try { current = JsonSerializer.Deserialize<Persisted>(json, Json); }
            catch (JsonException) { /* 往下試舊格式 */ }

            if (current?.Lines is null && current?.Audit is null)
            {
                try { legacy = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json, Json); }
                catch (JsonException) { /* 兩種都不是 → 當成空的 */ }
            }

            lock (_gate)
            {
                _lines.Clear();
                _audit.Clear();

                foreach (var (key, value) in current?.Lines ?? legacy ?? [])
                    if (value is { Count: > 0 }) _lines[key] = value.Take(Limits.MaxLinesPerGuild).ToList();

                if (current?.Audit is { Count: > 0 })
                    _audit.AddRange(current.Audit.TakeLast(Limits.MaxAuditEntries));
            }
        }
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
            _store.SetBlob(BlobKey, JsonSerializer.Serialize(Snapshot(), Json));
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
