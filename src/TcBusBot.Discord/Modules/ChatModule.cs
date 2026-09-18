using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using TcBusBot.Core.Chat;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// <c>/ai</c>：AI 聊天的查詢與維護指令。
///
/// 聊天本身不用指令（@ 它、或回覆它的訊息就會回），這幾個指令是「看不到的東西要看得到」：
///   * <c>/ai status</c> — 用哪個模型、這一週花了多少 token、這個頻道的記憶有幾則
///   * <c>/ai forget</c> — 忘掉這個頻道的對話（換話題、或不想讓它記得時用）
///
/// ⚠️ 刻意**沒有**「開啟／關閉」指令：AI 有沒有開是**主機端**的設定（有沒有 LLM_API_KEY），
/// 在 Discord 上按一下就能改的東西會讓「到底有沒有開」變得不確定。
/// </summary>
[Group("ai", "AI 聊天（OpenAI 相容 API）")]
public sealed class ChatModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly LlmOptions _options;
    private readonly ConversationStore _conversations;
    private readonly WeeklyTokenBudget _budget;
    private readonly ILlmClient _llm;
    private readonly GuildPersonaStore _personas;
    private readonly PersonaGrantStore _grants;
    private readonly LlmDiagnostics _diagnostics;

    /// <summary>
    /// ⚠️ 所有參數都**必填**：Discord.Net 挑的是「參數最多的建構子」，
    /// 給預設值會讓它挑到解析不到的那個（見 <see cref="DisabledLlmClient"/> 的說明）。
    /// 沒有啟用時容器會注入 <see cref="DisabledLlmClient"/>。
    /// </summary>
    public ChatModule(
        LlmOptions options,
        ConversationStore conversations,
        WeeklyTokenBudget budget,
        ILlmClient llm,
        GuildPersonaStore personas,
        PersonaGrantStore grants,
        LlmDiagnostics diagnostics)
    {
        _options = options;
        _conversations = conversations;
        _budget = budget;
        _llm = llm;
        _personas = personas;
        _grants = grants;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// 這個人在這個伺服器可以設定提示詞嗎？
    ///
    /// 兩種來源：
    ///   1. 主機端名單 `LLM_ADMIN_IDS`（自己人，任何伺服器都可以）
    ///   2. **來源伺服器的人按按鈕同意**（見 <see cref="PersonaGrantStore"/>）——
    ///      授權只限「那一個」伺服器，不會順便給全域權限
    /// </summary>
    private bool CanSetPersona(ulong guildId, ulong userId)
        => _options.AdminUserIds.Contains(userId)
           || _grants.PeekAuthority(guildId, userId, DateTimeOffset.UtcNow) is not null;

    [SlashCommand("status", "看 AI 聊天的狀態：模型、這一週用掉多少 token、這個頻道的記憶")]
    public async Task StatusAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var guildId = Context.Guild?.Id ?? 0;
        var channelId = Context.Channel.Id;
        var snapshot = _conversations.Snapshot(guildId, channelId, now);
        var enabled = _llm.IsConfigured;

        var embed = new EmbedBuilder()
            .WithTitle("🤖 AI 聊天狀態")
            .WithColor(enabled ? new Color(0x2B, 0x6C, 0xB0) : new Color(0x5A, 0x5A, 0x5A))
            .AddField("啟用", enabled ? "✅ 已啟用" : "❌ 未啟用（主機沒有設定 LLM_API_KEY）", inline: false)
            .AddField("服務城市", _options.MultiCity
                    ? $"{_options.CityDisplay}（同時服務多個城市，即時到站按城市分批查）"
                    : $"{_options.CityDisplay}（模型會被明確告知只回答這個縣市）", inline: false)
            .AddField("模型", enabled ? $"{_options.Model} @ {_options.EndpointHost}" : "—", inline: false)
            .AddField("判斷用的模型",
                !enabled
                    ? "—"
                    : _options.HasSeparateJudgeModel
                        ? $"{_options.JudgeModel}（「是在對我說話嗎」「換話題了沒」這類短判斷）"
                        : $"同主模型（{_options.Model}）—— 要省錢可以設 `LLM_JUDGE_MODEL` 指向小的模型",
                inline: false)
            .AddField("工具（可以真的動手）",
                !enabled
                    ? "—"
                    : _options.ToolsEnabled
                        ? "✅ 已啟用：查站牌／查路線號碼／查路線／訂閱公車／列出訂閱／取消訂閱／到站時間／記住規矩"
                        : "❌ 已關閉（LLM_TOOLS=false）→ 只會聊天，不會動到你的訂閱",
                inline: false)
            .AddField("這個伺服器學到的規矩",
                _personas.Describe(guildId),
                inline: false)
            .AddField("每週額度",
                _budget.Limit <= 0
                    ? $"不限（已用 {_budget.Usage.TotalTokens:N0} tokens）"
                    : $"{_budget.Usage.TotalTokens:N0}／{_budget.Limit:N0} tokens" +
                      $"（剩 {Math.Max(0, _budget.Limit - _budget.Usage.TotalTokens):N0}）",
                inline: true)
            .AddField("重置時間",
                $"{WeeklyTokenBudget.ResetAt(now).ToLocalTime():MM-dd HH:mm}",
                inline: true)
            .AddField("本週呼叫",
                $"{_budget.Usage.Calls} 次（被額度擋下 {_budget.Usage.Refusals} 次）",
                inline: true)
            .AddField("這個頻道的記憶",
                snapshot is null
                    ? "（空的，還沒聊過）"
                    : $"{snapshot.SegmentCount} 段／目前 {snapshot.CurrentTurnCount} 則" +
                      (snapshot.AmbientTurnCount > 0 ? $"（含 {snapshot.AmbientTurnCount} 則偷聽到的閒聊）" : "") +
                      (snapshot.Idle is { } idle ? $"（最後一次 {idle.TotalMinutes:0} 分鐘前）" : ""),
                inline: false)
            .AddField("最近幾次呼叫（接不接得上就看這裡）",
                !enabled
                    ? "—（沒有設定金鑰）"
                    : _diagnostics.Describe() +
                      (Context.Guild is null || _options.AllowDm
                          ? ""
                          : "\nℹ️ 私訊不回（`LLM_ALLOW_DM=false`）—— 請在伺服器頻道 @ 我"),
                inline: false)
            .AddField("記憶容量（環境變數可調）",
                _options.DescribeMemory(),
                inline: false)
            .AddField("偷聽",
                !_options.Eavesdrop || _options.EavesdropMaxMessages <= 0 || _options.EavesdropSeconds <= 0
                    ? "❌ 已關閉（只回 @ 它或回覆它的訊息）"
                    : snapshot is { ListenRemaining: > 0 }
                        ? $"👂 正在偷聽：還能判斷 {snapshot.ListenRemaining} 則／" +
                          $"安靜 {snapshot.ListenTimeLeft?.TotalSeconds:0} 秒後停止"
                        : $"✅ 已啟用（回完話後開始聽：最多判斷 {_options.EavesdropMaxMessages} 則／" +
                          $"安靜 {_options.EavesdropSeconds} 秒後回到「等 @」）",
                inline: false)
            .WithFooter("用法：@ 我 或 回覆我的訊息就會回話｜/ai test 可以測 AI 通不通｜/ai forget 清記憶");

        // 誰在花額度（全域額度的代價就是需要看得出來誰在花）
        var top = _budget.Usage.ByGuild
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"• {DescribeKey(kv.Key)}：{kv.Value:N0} tokens");

        if (_budget.Usage.ByGuild.Count > 0)
            embed.AddField("用量來源", string.Join("\n", top), inline: false);

        await RespondAsync(embed: embed.Build(), ephemeral: true);
    }

    [SlashCommand("learned", "看我（這個伺服器）學到了哪些規矩與自訂表情的意思")]
    public async Task LearnedAsync()
    {
        var guildId = Context.Guild?.Id ?? 0;
        var lines = _personas.Lines(guildId);

        if (lines.Count == 0)
        {
            await RespondAsync(
                "這個伺服器還沒有學到任何規矩 —— 是預設的樣子。\n\n" +
                "可以這樣教它：\n" +
                "• `@我 講話再簡短一點`（它會用 remember_rule 記下來）\n" +
                "• `@我 <自訂表情> 是 委屈`（表情名稱每個伺服器不一樣，只有這裡學到的才有意義）\n\n" +
                "學到的內容**只對這個伺服器生效**，用 `/rest` 可以整個重設。",
                ephemeral: true);
            return;
        }

        var numbered = lines.Select((l, i) => $"`{i + 1}.` {Truncate(l, 200)}");
        var description = string.Join("\n", numbered);

        if (description.Length > 4000) description = description[..4000] + "…";

        await RespondAsync(
            embed: new EmbedBuilder()
                .WithColor(new Color(0x2B, 0x6C, 0xB0))
                .WithTitle("📚 這個伺服器學到的規矩")
                .WithDescription(description)
                .WithFooter($"共 {lines.Count}/{_personas.Limits.MaxLinesPerGuild} 條｜只對這個伺服器生效｜/rest 可以重設")
                .Build(),
            ephemeral: true);
    }

    /// <summary>
    /// `/ai pset from:`：把另一個伺服器的自訂提示詞整套帶過來。
    ///
    /// 為什麼需要「來源必須是我還在的伺服器、而且你也在裡面」：
    /// 自訂提示詞是**那個伺服器的東西**（常常包含只有那裡才有的自訂表情名稱、稱呼、
    /// 內規）。管理員是自己人，但「能不能把 A 伺服器的內容搬進 B」仍然要在意 ——
    /// 所以在兩個地方都擋：Bot 要在來源、**你也**要在來源。
    /// </summary>
    private async Task CopyFromAsync(ulong targetGuildId, string source, PersonaMode mode)
    {
        var (sourceId, error) = ResolveSourceGuild(source);

        if (sourceId is null)
        {
            await RespondAsync($"❌ {error}", ephemeral: true);
            return;
        }

        if (sourceId.Value == targetGuildId)
        {
            await RespondAsync("❌ 來源就是這個伺服器本身（不用複製）。", ephemeral: true);
            return;
        }

        var guild = Context.Client.GetGuild(sourceId.Value);

        if (guild is null)
        {
            await RespondAsync(
                $"❌ 我不在「{source}」那個伺服器裡，所以讀不到它的設定。\n" +
                "（如果那個伺服器已經不在了，可以先用 `/ai learned` 把內容複製下來，再用 " +
                "`/ai pset text:…` 一條一條寫進來。）",
                ephemeral: true);
            return;
        }

        // ── 授權：不只看名單，改成「來源伺服器的人按按鈕同意」──────
        //
        // 為什麼要這樣：那些內容是**來源伺服器的東西**（自訂表情名稱、稱呼、內規），
        // 能不能給出去應該由**那邊的人**決定。以前是「你也要在來源伺服器裡」——
        // 那對「幫忙代管兩個伺服器的人」很合理，但對「想把設定給朋友那個伺服器」就很怪：
        // 你不在那邊、也不想為了這件事把對方加進 LLM_ADMIN_IDS（那會給他全域權限）。
        var allowedDirectly = _options.AdminUserIds.Contains(Context.User.Id)
                              && guild.GetUser(Context.User.Id) is not null;

        if (!allowedDirectly)
        {
            await RequestGrantAsync(guild, targetGuildId, mode);
            return;
        }

        var sourceLines = _personas.Lines(sourceId.Value);

        if (sourceLines.Count == 0)
        {
            await RespondAsync($"ℹ️「{guild.Name}」沒有任何自訂提示詞（沒有東西可以複製）。", ephemeral: true);
            return;
        }

        var replace = mode == PersonaMode.Replace;
        var outcome = _personas.CopyFrom(targetGuildId, sourceId.Value, Context.User.Id, replace);

        var summary = replace
            ? $"✅ 已從「{guild.Name}」複製 **{outcome.Copied}** 條（覆蓋掉原本的 {outcome.Removed} 條）"
            : $"✅ 已從「{guild.Name}」複製 **{outcome.Copied}** 條";

        if (outcome.Skipped > 0) summary += $"，跳過 {outcome.Skipped} 條重複的";
        if (outcome.Dropped > 0) summary += $"，**{outcome.Dropped} 條因為超過上限沒帶過來**";
        if (!replace && outcome.Dropped > 0)
            summary += "（可以改用 `mode:Replace` 覆蓋）";

        await RespondAsync(
            $"{summary}\n目前共 {outcome.Lines.Count}/{_personas.Limits.MaxLinesPerGuild} 條：" +
            Truncate(string.Join("\n", outcome.Lines.Select(l => $"• {l}")), 3000),
            ephemeral: true);
    }

    /// <summary>
    /// 解析 `from:`：接受伺服器 ID 或**完整名稱**。
    ///
    /// 名稱重複時要求改用 ID —— 猜錯的代價是「把別的伺服器的設定寫進來」，
    /// 那種錯誤很難發現（兩個伺服器看起來都正常，但規矩是錯的）。
    /// </summary>
    private (ulong? Id, string? Error) ResolveSourceGuild(string raw)
    {
        var trimmed = raw.Trim();

        // 容忍貼上 <#123>／<@123> 之類的寫法（Discord 的 ID 常常是這樣複製的）
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());

        if (digits.Length > 0 && ulong.TryParse(digits, out var id) && id != 0)
            return (id, null);

        var matches = Context.Client.Guilds
            .Where(g => string.Equals(g.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1) return (matches[0].Id, null);

        if (matches.Count > 1)
            return (null, $"有 {matches.Count} 個伺服器叫「{trimmed}」，請改用伺服器 ID" +
                          "（開啟開發者模式 → 對伺服器按右鍵 → 複製伺服器 ID）。");

        return (null, $"找不到「{trimmed}」這個伺服器 —— 我只找得到我加入的伺服器，" +
                      "也可以直接給伺服器 ID。");
    }

    /// <summary>
    /// 在**來源伺服器**發一則「授權通知」，讓那邊的人按「同意」。
    ///
    /// 三個刻意的設計：
    ///   * 通知發在**來源**（不是請求者那邊）—— 要給出去的是那邊的東西，
    ///     同意的人當然要在那邊。這也是「在另一個伺服器傳授權通知」的意思。
    ///   * 請求者必須在**目標**伺服器有「管理伺服器」權限：不然任何路人都能
    ///     讓別人的伺服器跳通知（洗頻）。
    ///   * 同意之後**同時**給他「設定那個伺服器」的權限（30 天）—— 不然他下次
    ///     想改一個字又要再請一次，這個流程就變成麻煩而不是安全。
    /// </summary>
    private async Task RequestGrantAsync(SocketGuild sourceGuild, ulong targetGuildId, PersonaMode mode)
    {
        if (Context.Guild is not { } targetGuild)
        {
            await RespondAsync("❌ 這個指令要在伺服器裡使用。", ephemeral: true);
            return;
        }

        if (!((Context.User as SocketGuildUser)?.GuildPermissions.ManageGuild ?? false))
        {
            await RespondAsync(
                "❌ 這個功能要「**管理伺服器**」權限的人才能發起（它會讓另一個伺服器跳通知）。\n" +
                "請找你們的管理員，或請對方直接在他的伺服器用 `/ai pset`。",
                ephemeral: true);
            return;
        }

        var channel = PickNoticeChannel(sourceGuild);

        if (channel is null)
        {
            await RespondAsync(
                $"❌ 我在「{sourceGuild.Name}」沒有可以發通知的頻道（缺少「傳送訊息」權限）。\n" +
                "請在那邊給我一個可以發言的頻道，或請那邊的人自己用 `/ai pset` 匯出。",
                ephemeral: true);
            return;
        }

        var request = _grants.Request(
            sourceGuildId: sourceGuild.Id,
            targetGuildId: targetGuildId,
            targetChannelId: Context.Channel.Id,
            requesterId: Context.User.Id,
            mode: mode == PersonaMode.Replace ? PersonaGrantModes.CopyReplace : PersonaGrantModes.CopyAppend,
            now: DateTimeOffset.UtcNow);

        var notice = new EmbedBuilder()
            .WithColor(new Color(0xE6, 0x7E, 0x22))
            .WithTitle("🤝 有人在別的伺服器請求授權")
            .WithDescription(
                $"**{targetGuild.Name}** 的 <@{Context.User.Id}> 想把**這個伺服器**的自訂提示詞複製過去" +
                $"（{(mode == PersonaMode.Replace ? "覆蓋他那邊原本的" : "追加，跳過重複的")}）。\n\n" +
                $"同意的話：他那邊會拿到這 **{_personas.Lines(sourceGuild.Id).Count}** 條設定，" +
                $"而且接下來 {PersonaGrantStore.AuthorityTtl.TotalDays:0} 天可以直接用 `/ai pset` 維護**他那邊**的設定" +
                "（不會拿到全域權限）。\n\n" +
                $"-# 請求編號 `{request.Id}`｜{PersonaGrantStore.RequestTtl.TotalHours:0} 小時內有效｜" +
                "只有這個伺服器的管理員（或有 `Manage Server` 權限的人）能按")
            .Build();

        var buttons = new ComponentBuilder()
            .WithButton("✅ 同意並複製", PersonaGrantCid.AllowButton(request.Id), ButtonStyle.Success)
            .WithButton("🚫 拒絕", PersonaGrantCid.DenyButton(request.Id), ButtonStyle.Danger)
            .Build();

        try
        {
            await ((IMessageChannel)channel).SendMessageAsync(embed: notice, components: buttons);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[授權] 通知發送失敗：{ex.Message}");
            await RespondAsync($"❌ 通知發不出去（{ex.Message}）。請確認我在「{sourceGuild.Name}」有發言權限。",
                ephemeral: true);
            return;
        }

        Console.WriteLine($"[授權] {Context.User.Id} 請求把 {sourceGuild.Id} 的提示詞複製到 {targetGuildId}" +
                          $"（{request.Id}）");

        await RespondAsync(
            $"📨 已把**授權請求**送到「{sourceGuild.Name}」的 <#{channel.Id}>（請求編號 `{request.Id}`）。\n" +
            "那邊的管理員會看到一則通知，按「✅ 同意並複製」之後：\n" +
            $"• 設定會直接複製到這裡（{(mode == PersonaMode.Replace ? "覆蓋現有的" : "追加")}）\n" +
            "• 你之後可以直接用 `/ai pset` 維護這裡的設定（30 天）\n" +
            "• 完成時我會在這個頻道回報",
            ephemeral: true);

        Console.WriteLine($"[授權] 通知已發到 {sourceGuild.Name} 的 #{channel.Name}（{channel.Id}）");
    }

    /// <summary>找一個「我在來源伺服器可以發言」的頻道（預設頻道優先，其次照順序找）。</summary>
    private static SocketTextChannel? PickNoticeChannel(SocketGuild guild)
    {
        if (guild.SystemChannel is { } system && CanPost(system)) return system;

        return guild.TextChannels
            .OrderBy(c => c.Position)
            .FirstOrDefault(CanPost);
    }

    private static bool CanPost(SocketTextChannel channel)
        => channel.Guild.CurrentUser.GetPermissions(channel).SendMessages;

    /// <summary>沒有授權時要講的話（順便告訴他「怎麼拿到授權」）。</summary>
    private string NotAuthorizedMessage()
    {
        var listConfigured = _options.AdminUserIds.Length > 0;

        return "這個指令需要授權。有兩種方式：\n" +
               "• **主機端名單**：把自己加進 `LLM_ADMIN_IDS`（那會同時給全域權限，" +
               "通常只有主機主人需要）\n" +
               "• **請來源伺服器同意**（推薦）：用 `/ai pset from:<伺服器ID>` —— " +
               "我會在**那個伺服器**發一則通知，那邊的管理員按「同意」之後，" +
               "你就能維護**這裡**的設定\n" +
               (listConfigured ? "" : "\n（目前主機端沒有設定 `LLM_ADMIN_IDS`，所以名單那條路是空的）");
    }

    /// <summary>
    /// `/ai test`：**用一次真的呼叫**確認 LLM 管線通不通。
    ///
    /// 為什麼需要這個指令：使用者說「LLM 好像沒接上」時，最常見的診斷困難是
    /// 「看不出來是哪一種壞掉」—— 金鑰無效、餘額不足、模型名稱打錯、逾時、
    /// 還是根本沒被叫到？這個指令直接打一次，成功就回覆內容與用量，
    /// 失敗就把**API 的原話**貼出來（例如 `401 Unauthorized` 就是金鑰問題）。
    ///
    /// 會花掉一點額度（一次短呼叫，幾十個 token），所以只開放給
    /// 管理員／已授權的人。
    /// </summary>
    [SlashCommand("test", "用一次真的呼叫確認 AI 有沒有接上（會花掉一點額度）")]
    public async Task TestAsync(
        [Summary("text", "要對它說的話（留空＝最簡單的 ping）")] string? text = null)
    {
        var guildId = Context.Guild?.Id ?? 0;

        if (!CanSetPersona(guildId, Context.User.Id))
        {
            await RespondAsync(NotAuthorizedMessage(), ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var probe = text?.Trim() is { Length: > 0 } t ? t : "ping：請只回一個字 OK";

        var request = new LlmRequest(
            SystemPrompt: "你是一個測試用的助理。只回答一個簡短的句子。",
            History: [],
            Incoming: new ChatTurn(
                ChatRole.User, Context.User.Username, Context.User.Id, 0UL,
                probe, DateTimeOffset.UtcNow),
            MaxTokens: 64,
            Temperature: 0,
            Tag: "probe");

        var started = DateTimeOffset.UtcNow;
        LlmReply? reply = null;
        string? error = null;

        try
        {
            reply = await _llm.CompleteAsync(request, CancellationToken.None);

            // 真的呼叫要算進每週額度（不然「測試」會變成繞過額度的後門）
            _budget.Record(reply.InputTokens, reply.OutputTokens, guildId, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            error = ex is LlmException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
        }

        var elapsed = DateTimeOffset.UtcNow - started;

        if (reply is not null)
        {
            await FollowupAsync(embed: new EmbedBuilder()
                .WithColor(new Color(0x2E, 0x8B, 0x57))
                .WithTitle("✅ AI 接得上")
                .WithDescription($"它回你：{Truncate(reply.Text, 500)}")
                .AddField("模型", reply.Model, inline: true)
                .AddField("用量", $"in {reply.InputTokens} / out {reply.OutputTokens}", inline: true)
                .AddField("往返時間", $"{elapsed.TotalSeconds:0.0} 秒", inline: true)
                .AddField("這一週剩下",
                    _budget.Limit <= 0
                        ? "不限"
                        : $"{Math.Max(0, _budget.Limit - _budget.Usage.TotalTokens):N0} tokens",
                    inline: false)
                .WithFooter("這次呼叫也會算進每週額度")
                .Build(), ephemeral: true);

            return;
        }

        await FollowupAsync(embed: new EmbedBuilder()
            .WithColor(new Color(0xC0, 0x39, 0x2B))
            .WithTitle("❌ AI 呼叫失敗")
            .WithDescription($"`{Truncate(error ?? "（沒有錯誤訊息）", 800)}`")
            .AddField("設定", $"{_options.Model} @ {_options.EndpointHost}" +
                             (_options.HasSeparateJudgeModel ? $"（判斷用：{_options.JudgeModel}）" : ""),
                inline: false)
            .AddField("怎麼查", CheckList(error), inline: false)
            .WithFooter($"{elapsed.TotalSeconds:0.0} 秒後失敗")
            .Build(), ephemeral: true);
    }

    /// <summary>依錯誤訊息給對應的檢查方向（不然使用者只看到一串英文）。</summary>
    private static string CheckList(string? error)
    {
        var text = error ?? "";

        if (text.Contains("401", StringComparison.Ordinal) || text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return "🔑 **金鑰無效**：`LLM_API_KEY` 打錯或被撤銷了（到服務供應商的後台重新產生一組）。";

        if (text.Contains("402", StringComparison.Ordinal) || text.Contains("Insufficient", StringComparison.OrdinalIgnoreCase)
            || text.Contains("balance", StringComparison.OrdinalIgnoreCase))
            return "💳 **餘額不足**：到服務供應商的後台儲值。";

        if (text.Contains("404", StringComparison.Ordinal) || text.Contains("model", StringComparison.OrdinalIgnoreCase)
            && text.Contains("not", StringComparison.OrdinalIgnoreCase))
            return "🏷 **模型名稱不對**：檢查 `LLM_MODEL`／`LLM_JUDGE_MODEL`（錯誤訊息裡通常會列出可用的名稱）。";

        if (text.Contains("timeout", StringComparison.OrdinalIgnoreCase) || text.Contains("逾時", StringComparison.Ordinal))
            return "⏱ **逾時**：服務商太慢或網路不通；可以調高 `LLM_TIMEOUT_SECONDS`。";

        if (text.Contains("429", StringComparison.Ordinal) || text.Contains("rate", StringComparison.OrdinalIgnoreCase))
            return "🚦 **被限流**：等幾分鐘再試，或降低呼叫頻率。";

        return "• 先看「設定」那一行的模型與端點對不對\n" +
               "• 再到服務供應商後台確認金鑰與餘額\n" +
               "• 需要更多線索：`/ai status` 看最近幾次呼叫（含失敗訊息）";
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>`/ai pset` 的兩種模式。</summary>
    public enum PersonaMode
    {
        /// <summary>追加一條（跟模型自己學到的規則放在一起）。</summary>
        Append,

        /// <summary>覆蓋掉這個伺服器現有的全部自訂提示詞。</summary>
        Replace
    }

    /// <summary>
    /// `/ai pset`：**後台指定的管理員**（`LLM_ADMIN_IDS`）直接設定這個伺服器的自訂提示詞。
    ///
    /// 為什麼需要這個指令：模型自己學（`remember_rule`）很適合「使用者隨口教一句」，
    /// 但管理員想要的常常是**一開始就寫好、而且是精確的一整段**（例如「一律用繁體中文、
    /// 不要用條列、每次回答前先確認站名」）。用嘴巴講給模型聽會有不確定性，
    /// 也會佔用對話額度；這個指令就是「直接寫進去」。
    ///
    /// 寫的東西跟學到的是**同一份 overlay**（`GuildPersonaStore`）：
    ///   * `/ai learned` 看得到、`/rest` 清得掉（單一來源，不做第二套）
    ///   * 一樣受 `LLM_MAX_GUILD_RULES`／`LLM_MAX_RULE_CHARS` 的上限約束
    ///   * 每次都會記進稽核紀錄（`/ai audit`），因為這是「有人改了設定」
    ///
    /// ⚠️ 為什麼只認 `LLM_ADMIN_IDS` 而**不需要** `LLM_ADMIN_KEY`：
    ///    那把 key 是為了擋「模型被騙去打主人的指令」（訊息是模型讀得到的東西）。
    ///    斜線指令是 Discord 直接送到 Bot 的互動，**模型碰不到**，
    ///    而且少了 key 就不會出現在對話紀錄裡。真的被騙的風險在 `OwnerTools`（全域設定）那邊。
    /// </summary>
    [SlashCommand("pset", "設定這個伺服器的自訂提示詞（管理員，或經來源伺服器按鈕同意）")]
    public async Task PersonaSetAsync(
        [Summary("text", "要設定的內容（一句重點；留空＝只顯示目前的設定）")] string? text = null,
        [Summary("mode", "Append＝追加；Replace＝覆蓋掉這個伺服器現有的全部")] PersonaMode mode = PersonaMode.Append,
        [Summary("clear", "清空這個伺服器的自訂提示詞")] bool clear = false,
        [Summary("from", "複製來源：另一個伺服器的 ID 或名稱（把那邊的設定整套帶過來）")] string? from = null)
    {
        var guildId = Context.Guild?.Id ?? 0;

        if (guildId == 0)
        {
            await RespondAsync("這個指令要在伺服器裡使用（自訂提示詞是「每個伺服器一份」的）。", ephemeral: true);
            return;
        }

        // ── 授權：主機端名單，或「來源伺服器按按鈕同意」───────
        if (!CanSetPersona(guildId, Context.User.Id))
        {
            await RespondAsync(NotAuthorizedMessage(), ephemeral: true);
            return;
        }

        // ── 從另一個伺服器複製 ────────────────────────────
        if (!string.IsNullOrWhiteSpace(from))
        {
            await CopyFromAsync(guildId, from!, mode);
            return;
        }

        // ── 清空 ─────────────────────────────────────────
        if (clear)
        {
            var removed = _personas.Reset(guildId);

            if (removed.Count == 0)
            {
                await RespondAsync("這個伺服器本來就沒有自訂提示詞（不用清）。", ephemeral: true);
                return;
            }

            _personas.AuditAdmin(Context.User.Id, "清空這個伺服器的提示詞", $"{removed.Count} 條");

            await RespondAsync(
                $"🧹 已清空 {removed.Count} 條自訂提示詞（回到預設的樣子）。\n" +
                "下面是清掉的原文（想留就複製走）：\n" +
                Truncate(string.Join("\n", removed.Select(l => $"• {l}")), 3500),
                ephemeral: true);
            return;
        }

        // ── 沒給內容 → 顯示目前設定 ────────────────────────
        var current = _personas.Lines(guildId);

        if (string.IsNullOrWhiteSpace(text))
        {
            var body = current.Count == 0
                ? "（目前沒有自訂提示詞）"
                : string.Join("\n", current.Select((l, i) => $"`{i + 1}.` {Truncate(l, 300)}"));

            await RespondAsync(
                embed: new EmbedBuilder()
                    .WithColor(new Color(0x2B, 0x6C, 0xB0))
                    .WithTitle("🛠 這個伺服器的自訂提示詞")
                    .WithDescription(Truncate(body, 3500))
                    .AddField("這個伺服器 ID（複製到別的伺服器時會用到）", $"`{guildId}`", inline: false)
                    .WithFooter($"共 {current.Count}/{_personas.Limits.MaxLinesPerGuild} 條｜" +
                                "用法：/ai pset text:<內容>｜/ai pset from:<伺服器ID> mode:Replace｜/ai pset clear:True")
                    .Build(),
                ephemeral: true);
            return;
        }

        // ── 設定（追加或覆蓋）─────────────────────────────
        var outcome = _personas.SetRules(guildId, Context.User.Id, text, replace: mode == PersonaMode.Replace);

        var message = outcome.Result switch
        {
            GuildPersonaStore.LearnResult.Added =>
                (mode == PersonaMode.Replace
                    ? $"✅ 已**覆蓋**這個伺服器的自訂提示詞（清掉 {outcome.Removed} 條舊的）。"
                    : "✅ 已**追加**一條自訂提示詞。") +
                $"\n目前共 {outcome.Lines.Count}/{_personas.Limits.MaxLinesPerGuild} 條：" +
                Truncate(string.Join("\n", outcome.Lines.Select(l => $"• {l}")), 3000),

            GuildPersonaStore.LearnResult.Duplicate =>
                "ℹ️ 這條已經在裡面了（沒有重複加）。目前內容：\n" +
                Truncate(string.Join("\n", outcome.Lines.Select(l => $"• {l}")), 3000),

            GuildPersonaStore.LearnResult.TooLong =>
                $"❌ 太長了（上限 {_personas.Limits.MaxLineLength} 字）。" +
                (mode == PersonaMode.Replace ? "舊的設定**沒有**被動到。" : "請縮短成一句重點。"),

            GuildPersonaStore.LearnResult.TooMany =>
                $"❌ 這個伺服器已經有 {_personas.Limits.MaxLinesPerGuild} 條（上限）了。" +
                (mode == PersonaMode.Replace ? "舊的設定**沒有**被動到。" : "可以先 `mode:Replace` 覆蓋，或清理一些。"),

            _ => "❌ 沒有內容可以設定。"
        };

        await RespondAsync(message, ephemeral: true);
    }

    [SlashCommand("audit", "看最近的主人操作紀錄（誰改了全域設定；只有主人看得到）")]
    public async Task AuditAsync()
    {
        // 唯讀、不洩漏 key，所以**不需要**在訊息裡帶 key ——
        // 主人在任何頻道都能查（但仍然只有名單上的人看得到）。
        var isAdmin = _options.AdminUserIds.Contains(Context.User.Id);

        if (!isAdmin)
        {
            await RespondAsync(
                "這個指令只有主人能看（`LLM_ADMIN_IDS` 名單上的人）。\n" +
                "它會揭露誰對**全域設定**做了什麼，屬於管理資訊。",
                ephemeral: true);
            return;
        }

        var entries = _personas.AuditLog(10);

        if (entries.Count == 0)
        {
            await RespondAsync("目前沒有任何主人操作紀錄（沒有人改過全域設定）。", ephemeral: true);
            return;
        }

        var lines = string.Join("\n", entries.Select(e => $"`{e.At.ToLocalTime():MM-dd HH:mm}` {e.Describe()}"));
        if (lines.Length > 4000) lines = lines[..4000] + "…";

        await RespondAsync(
            embed: new EmbedBuilder()
                .WithColor(new Color(0x5A, 0x5A, 0x5A))
                .WithTitle("📝 主人操作紀錄")
                .WithDescription(lines)
                .WithFooter($"最近 {entries.Count} 筆｜每次動到全域設定都會留下紀錄（含被拒絕的嘗試）")
                .Build(),
            ephemeral: true);
    }

    [SlashCommand("forget", "忘掉這個頻道的 AI 對話記憶（不影響公車訂閱）")]
    public async Task ForgetAsync()
    {
        var guildId = Context.Guild?.Id ?? 0;
        var removed = _conversations.Reset(guildId, Context.Channel.Id);

        await RespondAsync(
            removed
                ? "🧹 已經忘掉這個頻道的對話記憶。下一句會當成全新的對話。"
                : "這個頻道本來就沒有記憶（沒聊過，或被清過了）。",
            ephemeral: true);
    }

    private static string DescribeKey(string key)
        => key == "dm" ? "私訊" : $"伺服器 {key}";
}
