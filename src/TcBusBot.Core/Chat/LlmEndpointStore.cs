using System.Text.Json;

namespace TcBusBot.Core.Chat;

/// <summary>
/// 一組「要接到哪個模型服務」的三個值（base URL、金鑰、模型名稱）。
///
/// 為什麼要一個型別而不是三個字串參數：這三個值**一定要一起看**
/// （換了服務卻忘了換模型名稱 → 404；換了模型卻還在打舊端點 → 401／404），
/// 而「退回環境變數的設定」就是回到**開機時那一組** —— 沒有存下來就退不回去。
/// </summary>
public readonly record struct LlmEndpointValues(string BaseUrl, string? ApiKey, string Model);

/// <summary>
/// 管理員在 Discord 上用 `/ai llm` 覆寫的模型連線設定（**只存有改的欄位**）。
///
/// `null` ＝ 「這個欄位沒有被改，照環境變數」——所以「只換金鑰」不會動到 base URL。
/// <see cref="ApiKey"/> 在**記憶體裡是明文**（要拿去加密、要拿去比對），
/// 但寫進資料庫時一律經過 <see cref="LlmSecretProtector"/>（見 `LlmEndpointStore.Save`）。
/// </summary>
public sealed record LlmEndpointOverride(
    string? BaseUrl = null,
    string? ApiKey = null,
    string? Model = null,
    ulong SetBy = 0,
    DateTimeOffset SetAt = default,
    string? Where = null)
{
    /// <summary>三個值都沒改（＝這筆紀錄沒有意義）。</summary>
    public bool IsEmpty
        => string.IsNullOrWhiteSpace(BaseUrl)
           && string.IsNullOrWhiteSpace(ApiKey)
           && string.IsNullOrWhiteSpace(Model);

    /// <summary>這筆紀錄改了哪些欄位（不含金鑰內容，只有「有改」）。</summary>
    public string DescribeChanges()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(BaseUrl)) parts.Add("base URL");
        if (!string.IsNullOrWhiteSpace(Model)) parts.Add("模型");
        if (!string.IsNullOrWhiteSpace(ApiKey)) parts.Add("金鑰");

        return parts.Count == 0 ? "（沒有改動）" : string.Join("、", parts);
    }
}

/// <summary>
/// 「整台 Bot 要用哪一組模型連線」的**唯一來源**：開機時是環境變數，管理員可以用
/// `/ai llm` 在 Discord 上覆寫，而且覆寫要**跨重啟**留著（存進資料庫）。
///
/// ── 為什麼要這個類別（而不是直接改 <see cref="LlmOptions"/>）────────────
/// 直覺做法是「收到指令就把 `cfg.Llm.ApiKey` 改掉」，但那樣有三個洞：
///   1. **退不回去**：開機時那一組值被蓋掉之後就沒有地方可以還原
///      （`/ai llm clear` 會變成「把金鑰清空 → AI 全掛」而不是「回到環境變數」）
///   2. **重啟就沒了**：Render 每次部署都是新行程 —— 那正是這個功能要解決的問題
///   3. **看不出來源**：`/ai status` 只印出「現在的金鑰」，沒人知道那是環境變數還是
///      有人偷偷在 Discord 改過（出問題時最需要知道的正是這個）
///
/// 所以：**開機值（<see cref="Baseline"/>）＋ 覆寫（<see cref="Override"/>) 分開存**，
/// 目前生效的值永遠是「覆寫優先、其餘沿用開機值」，而 `Clear()` 就是回到開機值。
/// </summary>
public sealed class LlmEndpointStore
{
    /// <summary>資料庫裡放這一筆的 key（跟訂閱、每週用量共用同一條後端鏈）。</summary>
    public const string BlobKey = "llm_endpoint_override";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly LlmOptions _options;
    private readonly ILlmStateStore? _store;
    private readonly string? _secret;
    private readonly object _gate = new();

    /// <summary>開機時（環境變數）的那一組值 —— `Clear()` 還原的目標。</summary>
    public LlmEndpointValues Baseline { get; }

    /// <summary>管理員在 Discord 上設定的覆寫（`null` ＝ 沒有，全部照環境變數）。金鑰是明文。</summary>
    public LlmEndpointOverride? Override { get; private set; }

    /// <summary>讀取舊紀錄時發現的問題（解不開的金鑰…）—— 啟動時要印出來，不能默默吃掉。</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>有沒有被 Discord 指令覆寫過。</summary>
    public bool HasOverride => Override is not null;

    /// <summary>這一筆覆寫能不能跨重啟（後端是記憶體模式時不能）。</summary>
    public bool Persistent => _store?.IsPersistent ?? false;

    /// <summary>資料庫裡那一筆金鑰是不是加密的。</summary>
    public bool Encrypted => Override?.ApiKey is not null && LlmSecretProtector.CanProtect(_secret);

    public LlmEndpointStore(LlmOptions options, ILlmStateStore? store = null, string? secret = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store;

        // 加密用的密語預設就是「主人 key」：它是唯一「只有主人知道、而且不在資料庫裡」的東西
        _secret = secret ?? options.AdminKey;

        Baseline = new LlmEndpointValues(options.BaseUrl, options.ApiKey, options.Model);

        Load();
    }

    /// <summary>目前**生效**的三個值（給顯示與比對用）。</summary>
    public LlmEndpointValues Effective
        => new(_options.BaseUrl, _options.ApiKey, _options.Model);

    /// <summary>
    /// 開機時把資料庫裡的覆寫套回 <see cref="LlmOptions"/>。
    ///
    /// ⚠️ 任何讀不出來的情況都是「**整筆放棄、回到環境變數**」而不是「套一半」：
    ///    套一半會產生一組沒人設定過的組合（新模型 ＋ 舊端點），
    ///    那種錯誤的症狀是 404，而使用者完全查不出原因。
    /// </summary>
    private void Load()
    {
        if (_store is null) return;

        string? json;

        try { json = _store.GetBlob(BlobKey); }
        catch (Exception ex)
        {
            LoadWarning = $"讀不到資料庫裡的模型設定（{ex.GetType().Name}）→ 全部改用環境變數";
            return;
        }

        if (string.IsNullOrWhiteSpace(json)) return;

        LlmEndpointOverride? parsed;

        try { parsed = JsonSerializer.Deserialize<LlmEndpointOverride>(json, Json); }
        catch (Exception ex)
        {
            LoadWarning = $"資料庫裡的模型設定不是有效的內容（{ex.GetType().Name}）→ 全部改用環境變數";
            return;
        }

        if (parsed is null || parsed.IsEmpty) return;

        var apiKey = parsed.ApiKey;

        if (apiKey is not null)
        {
            var plain = LlmSecretProtector.Unprotect(apiKey, _secret);

            if (plain is null)
            {
                LoadWarning = "資料庫裡的模型金鑰解不開（通常是 `LLM_ADMIN_KEY` 換過了）" +
                              "→ 全部改用環境變數，請重新用 `/ai llm` 設定";
                return;
            }

            apiKey = plain;
        }

        Override = parsed with { ApiKey = apiKey };
        ApplyToOptions(Override);
    }

    /// <summary>
    /// 套用一次改動（只給有值／有改的欄位；其餘沿用目前的值）並寫進資料庫。
    ///
    /// 回傳值＝套用後的覆寫紀錄（給指令顯示「現在改了哪些」用）。
    /// </summary>
    public LlmEndpointOverride Apply(LlmEndpointOverride change)
    {
        lock (_gate)
        {
            var existing = Override;

            // 合併：這次沒給的欄位沿用「上一次的覆寫」（不是沿用開機值）——
            // 這樣「先改模型、再改金鑰」兩次指令的結果跟「一次改兩個」完全一樣
            var merged = new LlmEndpointOverride(
                BaseUrl: string.IsNullOrWhiteSpace(change.BaseUrl) ? existing?.BaseUrl : change.BaseUrl.TrimEnd('/'),
                ApiKey: string.IsNullOrWhiteSpace(change.ApiKey) ? existing?.ApiKey : change.ApiKey!.Trim(),
                Model: string.IsNullOrWhiteSpace(change.Model) ? existing?.Model : change.Model!.Trim(),
                SetBy: change.SetBy,
                SetAt: change.SetAt,
                Where: change.Where);

            Override = merged;
            Save(merged);
            ApplyToOptions(merged);

            return merged;
        }
    }

    /// <summary>清掉 Discord 的覆寫，回到**開機時**（環境變數）的設定。</summary>
    public LlmEndpointValues Clear()
    {
        Restore(null);
        return Effective;
    }

    /// <summary>
    /// 把覆寫**整筆換成**這一筆（不合併）。
    ///
    /// 為什麼需要它（跟 <see cref="Apply"/> 的差別很關鍵）：`/ai llm` 套用失敗要
    /// **回退**時，如果拿舊的那筆去 <c>Apply</c>，合併規則會讓「這次剛設的新 base URL」
    /// 留在原地（因為舊那筆沒有改 base URL）—— 結果回退變成「一半新的、一半舊的」，
    /// 那是最糟的狀態（沒人設定過的組合）。回退一定要是「整筆還原」。
    /// </summary>
    public void Restore(LlmEndpointOverride? value)
    {
        lock (_gate)
        {
            LoadWarning = null;
            Override = value;

            if (value is null)
            {
                // 用空字串而不是刪除：ILlmStateStore 只有 Get/Set 兩個操作
                // （空 ＝ 沒有覆寫，讀取端一開始就把它當成「沒有」）
                try { _store?.SetBlob(BlobKey, ""); }
                catch (Exception ex) { LoadWarning = $"清不掉資料庫裡的設定（{ex.GetType().Name}）"; }
            }
            else
            {
                Save(value);
            }

            ApplyToOptions(value);
        }
    }

    /// <summary>覆寫 → <see cref="LlmOptions"/>（沒有覆寫時＝還原開機值）。</summary>
    private void ApplyToOptions(LlmEndpointOverride? source)
    {
        _options.BaseUrl = string.IsNullOrWhiteSpace(source?.BaseUrl) ? Baseline.BaseUrl : source!.BaseUrl!;
        _options.Model = string.IsNullOrWhiteSpace(source?.Model) ? Baseline.Model : source!.Model!;
        _options.ApiKey = source?.ApiKey is { Length: > 0 } key ? key : Baseline.ApiKey;
    }

    private void Save(LlmEndpointOverride value)
    {
        if (_store is null) return;

        // ⚠️ 金鑰**只有**在這裡被寫出去，而且一定經過 Protect（有密語就是 AES-GCM）
        var stored = value with { ApiKey = value.ApiKey is null ? null : LlmSecretProtector.Protect(value.ApiKey, _secret) };

        try { _store.SetBlob(BlobKey, JsonSerializer.Serialize(stored, Json)); }
        catch (Exception ex) { LoadWarning = $"寫不進資料庫（{ex.GetType().Name}）→ 這次設定重啟後會消失"; }
    }

    /// <summary>一行摘要（`/ai status`、啟動訊息）——**絕對不含金鑰原文**。</summary>
    public string Describe()
    {
        var now = Effective;

        var key = now.ApiKey is null
            ? "金鑰：未設定"
            : $"金鑰：{Mask(now.ApiKey)}";

        return $"{now.Model} @ {Host(now.BaseUrl)}｜{key}｜來源：{DescribeSource()}";
    }

    /// <summary>來源說明（誰在什麼時候改的；沒有覆寫時就是環境變數）。</summary>
    public string DescribeSource()
        => Override is null
            ? "環境變數"
            : $"Discord 指令（{Override.DescribeChanges()}" +
              (Override.SetBy != 0 ? $"，<@{Override.SetBy}>" : "") +
              (Override.SetAt == default ? "" : $"，{Override.SetAt.ToLocalTime():MM-dd HH:mm}") + "）";

    /// <summary>覆寫的細節（給 `/ai llm` 顯示：誰、哪裡、有沒有加密、會不會留著）。</summary>
    public string DescribeOverride()
    {
        if (Override is null)
            return $"目前完全照**環境變數**：`{Baseline.Model} @ {Host(Baseline.BaseUrl)}`" +
                   $"（金鑰 {Mask(Baseline.ApiKey)}）";

        var lines = new List<string>
        {
            $"目前**由 Discord 指令覆寫**中：`{Effective.Model} @ {Host(Effective.BaseUrl)}`",
            $"• 改了：{Override.DescribeChanges()}"
        };

        if (Override.SetBy != 0)
            lines.Add($"• 誰設的：<@{Override.SetBy}>" +
                      (Override.SetAt == default ? "" : $"（{Override.SetAt.ToLocalTime():yyyy-MM-dd HH:mm}）"));

        if (!string.IsNullOrWhiteSpace(Override.Where)) lines.Add($"• 在哪裡設的：{Override.Where}");

        lines.Add(Persistent
            ? Encrypted
                ? "• 保存：跨重啟保留，資料庫裡的金鑰**已加密**（用 `LLM_ADMIN_KEY` 推導）"
                : "• 保存：跨重啟保留，但資料庫裡的金鑰是**明文**" +
                  "（設 `LLM_ADMIN_KEY` 就會自動改成加密保存）"
            : "• 保存：⚠️ 這一台的儲存後端是記憶體模式 → **重啟後會回到環境變數**");

        return string.Join("\n", lines);
    }

    /// <summary>開機時那一組值（`clear` 之後會用到的東西）。</summary>
    public string DescribeBaseline()
        => $"`{Baseline.Model} @ {Host(Baseline.BaseUrl)}`（金鑰 {Mask(Baseline.ApiKey)}）";

    /// <summary>只露前後幾碼（跟 <see cref="LlmOptions.MaskedKey"/> 同一種格式）。</summary>
    public static string Mask(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? "（未設定）"
            : key!.Length <= 8
                ? "****"
                : $"{key[..4]}…{key[^4..]}（{key.Length} 字元）";

    /// <summary>只印 host（自架服務的內網位址不需要大聲講出來）。</summary>
    public static string Host(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return "（未設定）";

        try { return new Uri(baseUrl).Authority; }
        catch (Exception) { return baseUrl; }
    }
}
