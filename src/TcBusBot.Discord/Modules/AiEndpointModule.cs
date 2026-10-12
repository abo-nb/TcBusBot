using Discord;
using Discord.Interactions;
using TcBusBot.Core.Chat;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// <c>/ai llm</c>：**整台 Bot** 要用哪一個模型服務（API 金鑰／base URL／模型名稱）。
///
/// ── 為什麼要在 Discord 上做這件事 ─────────────────────────
/// 金鑰是環境變數（`LLM_API_KEY`），所以以前「換金鑰」＝「登入 Render 後台改環境變數 ＋ 等重新部署」。
/// 偏偏金鑰過期或額度用完是最需要**馬上**處理的事（Bot 會直接安靜下來，所有頻道都一樣）。
/// 這個指令讓主人當場就能換，而且**跨重啟保留**（存進資料庫，見 <see cref="LlmEndpointStore"/>）。
///
/// ── 為什麼同一組指令不做成「模型可以呼叫的工具」────────────
/// 這是**憑證**：能改它的人可以把整台 Bot 的對話（包含別人頻道裡的內容）導到自己的端點。
/// 斜線指令是 Discord 直接送到 Bot 的互動，**模型看不到也碰不到** ——
/// 而工具是模型可以自己決定要不要呼叫的。所以這一條路刻意只有指令，沒有工具，
/// 也因此不需要 `LLM_ADMIN_KEY`（那把 key 是為了擋「模型被騙去打主人的指令」）。
/// </summary>
[Group("ai", "AI 聊天（OpenAI 相容 API）")]
public sealed class AiEndpointModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly LlmOptions _options;
    private readonly LlmEndpointStore _endpoints;
    private readonly ReloadableLlmClient _llm;
    private readonly WeeklyTokenBudget _budget;
    private readonly GuildPersonaStore _personas;

    /// <summary>⚠️ 所有參數都必填：Discord.Net 挑「參數最多的建構子」（見 DisabledLlmClient 的說明）。</summary>
    public AiEndpointModule(
        LlmOptions options,
        LlmEndpointStore endpoints,
        ReloadableLlmClient llm,
        WeeklyTokenBudget budget,
        GuildPersonaStore personas)
    {
        _options = options;
        _endpoints = endpoints;
        _llm = llm;
        _budget = budget;
        _personas = personas;
    }

    /// <summary>
    /// <c>/ai llm</c>：看目前設定，或換掉它。
    ///
    /// 參數名稱刻意是小寫（`baseurl`／`model`）：Discord.Net 直接把**參數名稱**當成
    /// 選項名稱，而 Discord 只接受小寫。`--dryrun` 的指令樹檢查會在離線時就抓到寫錯。
    /// </summary>
    [SlashCommand("llm", "看／設定整台 Bot 的模型連線（金鑰、base URL、模型名稱；只有主人）")]
    public async Task LlmAsync(
        [Summary("key", "新的 API 金鑰（只會顯示前後幾碼；留空＝不改）")] string? key = null,
        [Summary("baseurl", "新的 base URL（要含 /v1，例如 https://api.deepseek.com/v1；留空＝不改）")]
        string? baseurl = null,
        [Summary("model", "新的模型名稱（留空＝不改）")] string? model = null,
        [Summary("clear", "清掉 Discord 上的設定，回到主機環境變數那一組")] bool clear = false,
        [Summary("test", "設定完立刻打一次真實的測試呼叫（預設要；測不過會自動退回舊設定）")]
        bool test = true)
    {
        // ── 授權：只有主機端名單（LLM_ADMIN_IDS）上的人 ──────────
        //
        // ⚠️ 刻意**不**開放給「有管理伺服器權限的人」或「按鈕同意過的伺服器管理員」：
        //    那些授權是「可以設定那個伺服器的提示詞」，而這一組設定是**全機器**的 ——
        //    拿到它等於能決定所有伺服器（含別人的私頻）的對話要送到哪裡去。
        //    只有主機主人（能改環境變數的人）才該有這個權限。
        if (!_options.AdminUserIds.Contains(Context.User.Id))
        {
            await RespondAsync(
                "這個指令只有**主人**能用（`LLM_ADMIN_IDS` 名單）。\n" +
                "它改的是**整台 Bot**的模型連線（金鑰、端點、模型）——" +
                "等於能決定所有伺服器的對話要送到哪一個服務，所以刻意只給主機端名單上的人。\n" +
                (_options.AdminUserIds.Length == 0
                    ? "\n（目前主機沒有設定 `LLM_ADMIN_IDS`，所以沒有人能用這個指令。）"
                    : ""),
                ephemeral: true);
            return;
        }

        // ── 全部清掉 → 回到環境變數 ───────────────────────────
        if (clear)
        {
            var restored = _endpoints.Clear();
            var message = _llm.Reload();

            _personas.AuditAdmin(Context.User.Id, "清掉 Discord 的模型連線設定",
                $"回到 {restored.Model} @ {LlmEndpointStore.Host(restored.BaseUrl)}（金鑰 {LlmEndpointStore.Mask(restored.ApiKey)}）");

            await RespondAsync(embed: new EmbedBuilder()
                .WithColor(new Color(0x5A, 0x5A, 0x5A))
                .WithTitle("🧹 已回到環境變數的設定")
                .WithDescription(_endpoints.DescribeOverride())
                .AddField("現在生效", $"`{restored.Model} @ {LlmEndpointStore.Host(restored.BaseUrl)}`" +
                                     $"\n金鑰：{LlmEndpointStore.Mask(restored.ApiKey)}", inline: false)
                .AddField("連線狀態",
                    _llm.IsConfigured
                        ? $"已重建（第 {_llm.ReloadCount} 次）" +
                          (message.Contains('✅') ? " ✅" : $"：{Trim(message)}")
                        : "⚠️ 現在沒有可用的金鑰 → AI 功能是關閉的", inline: false)
                .WithFooter("要再設定：/ai llm key:… baseurl:… model:…")
                .Build(), ephemeral: true);
            return;
        }

        var hasChange = !string.IsNullOrWhiteSpace(key)
                        || !string.IsNullOrWhiteSpace(baseurl)
                        || !string.IsNullOrWhiteSpace(model);

        // ── 沒有帶任何改動 → 顯示目前設定 ─────────────────────
        if (!hasChange)
        {
            var now = _endpoints.Effective;

            await RespondAsync(embed: new EmbedBuilder()
                .WithColor(new Color(0x2B, 0x6C, 0xB0))
                .WithTitle("🔌 整台 Bot 的模型連線")
                .WithDescription(_endpoints.DescribeOverride())
                .AddField("現在生效",
                    $"模型：`{now.Model}`\n端點：`{LlmEndpointStore.Host(now.BaseUrl)}`\n" +
                    $"金鑰：{LlmEndpointStore.Mask(now.ApiKey)}", inline: false)
                .AddField("環境變數那一組（`clear` 會回到這裡）", _endpoints.DescribeBaseline(), inline: false)
                .AddField("連線", _llm.IsConfigured
                    ? $"已建立（重建 {_llm.ReloadCount} 次）｜{_llm.Describe()}"
                    : "⚠️ 沒有可用的金鑰 → AI 聊天、摘要都不會動", inline: false)
                .AddField("保存方式", _endpoints.Persistent
                    ? _endpoints.Encrypted
                        ? "跨重啟保留；資料庫裡的金鑰**已加密**（用 `LLM_ADMIN_KEY` 推導的密語）"
                        : "跨重啟保留；但資料庫裡的金鑰是**明文** —— 設 `LLM_ADMIN_KEY` 就會自動改成加密保存"
                    : "⚠️ 這一台的儲存後端是記憶體模式 → 重啟後會回到環境變數", inline: false)
                .WithFooter(_options.AdminUserIds.Contains(Context.User.Id)
                    ? "用法：/ai llm key:<金鑰> baseurl:<含 /v1> model:<名稱>｜clear:True 回到環境變數"
                    : "")
                .Build(), ephemeral: true);
            return;
        }

        // ── 驗證輸入（在改動任何東西之前）─────────────────────
        string? newKey = null, newUrl = null, newModel = null;

        if (!string.IsNullOrWhiteSpace(key))
        {
            newKey = key.Trim();

            if (newKey.Length < 8)
            {
                await RespondAsync("❌ 金鑰太短（看起來不像一組金鑰）。沒有動到任何設定。", ephemeral: true);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(baseurl))
        {
            var text = baseurl.Trim();

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                await RespondAsync(
                    $"❌ `{Trim(text)}` 不是有效的 http(s) 網址。沒有動到任何設定。\n" +
                    "（要像 `https://api.deepseek.com/v1` 這樣，**含 `/v1`**。）",
                    ephemeral: true);
                return;
            }

            newUrl = text.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(model)) newModel = model.Trim();

        // ── 套用 ＋ 重建連線 ──────────────────────────────────
        // 先回應（打 API 可能要好幾秒 → 不 defer 的話 Discord 會說「互動失敗」）
        await DeferAsync(ephemeral: true);

        var previous = _endpoints.Override;   // 供「金鑰被拒絕」時回退
        var now2 = DateTimeOffset.UtcNow;

        _endpoints.Apply(new LlmEndpointOverride(
            BaseUrl: newUrl,
            ApiKey: newKey,
            Model: newModel,
            SetBy: Context.User.Id,
            SetAt: now2,
            Where: Where()));

        var reloadMessage = _llm.Reload();

        // ── 立刻打一次真的，確認這組設定能用 ──────────────────
        //
        // 為什麼一定要測：金鑰打錯字是**最常見**的失敗，而它的症狀是「Bot 從此不理人」——
        // 沒有人會想到「是剛剛那個指令打錯了一個字」，只會覺得「它壞了」。
        LlmReply? probe = null;
        LlmException? failure = null;
        string? rawFailure = null;

        if (test && _llm.IsConfigured)
        {
            try
            {
                probe = await _llm.CompleteAsync(ProbeRequest(), CancellationToken.None);

                // 真的呼叫要算進每週額度（不然「換金鑰」會變成繞過額度的後門）
                _budget.Record(probe.InputTokens, probe.OutputTokens,
                    Context.Guild?.Id ?? 0, DateTimeOffset.UtcNow);
            }
            catch (LlmException ex) { failure = ex; }
            catch (Exception ex) { rawFailure = $"{ex.GetType().Name}: {ex.Message}"; }
        }

        // ── 金鑰被拒絕（401／403）→ 自動退回舊設定 ────────────
        //
        // 只有這一種失敗會自動退：它幾乎一定代表「新的設定是壞的」。
        // 網路不通、逾時、429 這些**不能退** —— 那會把一組正確的設定丟掉。
        if (failure?.LooksLikeBadSettings == true)
        {
            _endpoints.Restore(previous);
            _llm.Reload();

            _personas.AuditAdmin(Context.User.Id, "模型連線設定被回退（金鑰被拒絕）",
                $"{failure.Kind}｜已退回 {_endpoints.DescribeSource()}｜金鑰 {LlmEndpointStore.Mask(_endpoints.Effective.ApiKey)}");

            await FollowupAsync(embed: new EmbedBuilder()
                .WithColor(new Color(0xC0, 0x39, 0x2B))
                .WithTitle("❌ 新設定接不上 → 已自動退回舊設定")
                .WithDescription($"`{Trim(failure.Message)}`")
                .AddField("偵測到", $"金鑰被拒絕（`{failure.Kind}`）—— 這種失敗幾乎一定是設定打錯，" +
                                   "所以自動退回了；其他種類（網路、逾時）不會自動退。", inline: false)
                .AddField("現在生效", _endpoints.DescribeSource() +
                                     $"\n`{_endpoints.Effective.Model} @ {LlmEndpointStore.Host(_endpoints.Effective.BaseUrl)}`" +
                                     $"\n金鑰：{LlmEndpointStore.Mask(_endpoints.Effective.ApiKey)}", inline: false)
                .AddField("怎麼辦", CheckList(failure.Kind), inline: false)
                .WithFooter("確認金鑰無誤後可以再下一次指令")
                .Build(), ephemeral: true);
            return;
        }

        // ── 沒有可用的設定（例如 clear 之後環境變數也沒有金鑰）──
        if (!_llm.IsConfigured)
        {
            await FollowupAsync(
                "⚠️ 設定已寫入，但**現在沒有可用的金鑰**（環境變數也沒有）→ AI 聊天與每週摘要都會是關閉的。\n" +
                $"• 環境變數那一組：{_endpoints.DescribeBaseline()}\n" +
                "• 要用這一組跑起來，請補上 `key:`（或到主機後台設 `LLM_API_KEY`）。",
                ephemeral: true);
            return;
        }

        // ── 回報 ─────────────────────────────────────────────
        var applied = _endpoints.Override!;

        var httpWarning = _endpoints.Effective.BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "\n\n⚠️ **這是 `http://`（沒有加密）** —— 金鑰會以明文經過網路。" +
              "只有在自架、內網或本機（例如 `http://localhost:11434/v1`）時才適合。"
            : "";

        _personas.AuditAdmin(Context.User.Id, "更換模型連線設定",
            $"{applied.DescribeChanges()}｜現在：{_endpoints.Effective.Model} @ " +
            $"{LlmEndpointStore.Host(_endpoints.Effective.BaseUrl)}｜" +
            $"金鑰 {LlmEndpointStore.Mask(_endpoints.Effective.ApiKey)}" +
            (_endpoints.Encrypted ? "（加密保存）" : "（明文保存）"));

        // 測不過（但不是金鑰問題）→ 明確說「設定留著、但要自己決定退不退」
        if (failure is not null || rawFailure is not null)
        {
            await FollowupAsync(embed: new EmbedBuilder()
                .WithColor(new Color(0xE6, 0x7E, 0x22))
                .WithTitle("⚠️ 設定已套用，但測試呼叫失敗")
                .WithDescription($"`{Trim(failure?.Message ?? rawFailure ?? "")}`")
                .AddField("設定**留著**（沒有自動退回）",
                    "這一種失敗不一定是設定錯（可能是網路、逾時、服務商在忙）——" +
                    "自動退掉反而可能把一組正確的設定丟掉。\n" +
                    "要回到主機環境變數那一組：`/ai llm clear:True`" +
                    (previous is null ? "（現在沒有 Discord 覆寫 → 就是環境變數那一組）" : ""),
                    inline: false)
                .AddField("現在生效", _endpoints.DescribeOverride(), inline: false)
                .AddField("怎麼查", CheckList(failure?.Kind ?? LlmErrorKind.Unknown), inline: false)
                .WithFooter("確認可以用了之後，`/ai test` 會再打一次真的")
                .Build(), ephemeral: true);
            return;
        }

        var description = probe is null
            ? "✅ 設定已套用（這次沒有測試呼叫）。\n建議用 `/ai test` 打一次真的確認。"
            : $"✅ 設定已套用，而且**接得上**。它回你：{Truncate(probe.Text, 300)}";

        await FollowupAsync(embed: new EmbedBuilder()
            .WithColor(new Color(0x2E, 0x8B, 0x57))
            .WithTitle("🔌 模型連線已更新")
            .WithDescription(description + httpWarning)
            .AddField("改了什麼", DescribeChange(newUrl, newKey, newModel), inline: true)
            .AddField("來源", $"Discord 指令（<@{Context.User.Id}>）", inline: true)
            .AddField("保存", _endpoints.Persistent
                    ? _endpoints.Encrypted ? "跨重啟保留（金鑰加密）" : "跨重啟保留（金鑰**明文**）"
                    : "⚠️ 記憶體模式 → 重啟後會回到環境變數",
                inline: true)
            .AddField("現在生效",
                $"模型：`{_endpoints.Effective.Model}`" +
                (probe is null ? "" : $"（服務端回報：`{probe.Model}`）") +
                $"\n端點：`{LlmEndpointStore.Host(_endpoints.Effective.BaseUrl)}`" +
                $"\n金鑰：{LlmEndpointStore.Mask(_endpoints.Effective.ApiKey)}", inline: false)
            .AddField("用法",
                "• 只改一部分：沒給的欄位沿用原本的值\n" +
                "• 退回環境變數：`/ai llm clear:True`\n" +
                "• 再測一次：`/ai test`｜看狀態：`/ai status`", inline: false)
            .WithFooter(probe is null
                ? ""
                : $"這次測試用量 in {probe.InputTokens} / out {probe.OutputTokens} tokens（已算進每週額度）")
            .Build(), ephemeral: true);
    }

    /// <summary>
    /// 「這次改了哪些欄位」的一行說明（**金鑰只顯示遮罩**）。
    ///
    /// 為什麼不直接顯示 `applied.DescribeChanges()`：那只說「改了金鑰」，
    /// 沒有說「改成什麼」—— 換錯端點的時候，使用者需要看到的是值本身。
    /// </summary>
    private static string DescribeChange(string? url, string? key, string? model)
    {
        var parts = new List<string>();

        if (model is not null) parts.Add($"模型 → `{model}`");
        if (url is not null) parts.Add($"端點 → `{LlmEndpointStore.Host(url)}`");
        if (key is not null) parts.Add($"金鑰 → {LlmEndpointStore.Mask(key)}");

        return parts.Count == 0 ? "（沒有改動）" : string.Join("\n", parts);
    }

    /// <summary>測試用的最小請求（跟 `/ai test` 一樣：便宜、而且回什麼都不影響判斷）。</summary>
    private LlmRequest ProbeRequest() => new(
        SystemPrompt: "你是一個測試用的助理。只回答一個簡短的句子。",
        History: [],
        Incoming: new ChatTurn(
            ChatRole.User, Context.User.Username, Context.User.Id, 0UL,
            "ping：請只回一個字 OK", DateTimeOffset.UtcNow),
        MaxTokens: 32,
        Temperature: 0,
        Tag: "probe");

    private string Where()
        => (Context.Guild?.Name is { Length: > 0 } guild ? $"{guild} " : "") + $"#{Context.Channel.Name}";

    /// <summary>依失敗種類給對應的下一步（不然使用者只看到一串英文）。</summary>
    internal static string CheckList(LlmErrorKind kind)
        => kind switch
        {
            LlmErrorKind.Auth =>
                "🔑 **金鑰不對**：到服務商後台重新複製一組，注意有沒有多複製到空白。\n" +
                "也要確認這個金鑰是**這個端點**發的（DeepSeek 的金鑰不能用在 OpenAI 的端點上）。",
            LlmErrorKind.ModelNotFound =>
                "🏷 **模型名稱不對**：換了服務商通常連模型名稱也要換" +
                "（例如 `deepseek-flash` → `gpt-4o-mini`）。用 `/ai llm model:<名稱>` 改。",
            LlmErrorKind.Quota =>
                "💳 **服務商那邊的餘額不足**：設定本身是對的，去後台儲值就會好。",
            LlmErrorKind.RateLimited =>
                "🚦 **被限流**：設定是對的，等一下再試。",
            LlmErrorKind.Network =>
                "🌐 **連不上端點**：確認 base URL 打對、主機連得到那個網域" +
                "（自架服務要確認它對外開放、而且 Bot 所在的主機連得到）。",
            LlmErrorKind.Timeout =>
                "⏱ **等太久**：服務商太慢或網路不通；可以調高 `LLM_TIMEOUT_SECONDS`。",
            _ => "• 先用 `/ai status` 看「最近幾次呼叫」的完整錯誤\n" +
                 "• 確認 base URL 含 `/v1`、模型名稱與服務商一致\n" +
                 "• 不確定就 `/ai llm clear:True` 回到環境變數那一組"
        };

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>把可能很長（含換行）的說明壓成一行（放進 embed 欄位用）。</summary>
    private static string Trim(string text)
        => Truncate((text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim(), 300);
}
