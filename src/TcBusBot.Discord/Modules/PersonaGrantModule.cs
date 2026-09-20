using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using TcBusBot.Core.Chat;

namespace TcBusBot.Discord.Modules;

/// <summary>授權通知上那兩顆按鈕的 custom_id 前綴（後面接請求編號）。</summary>
public static class PersonaGrantCid
{
    public const string Allow = "pset:grant:allow:";
    public const string Deny = "pset:grant:deny:";

    public static string AllowButton(string requestId) => Allow + requestId;
    public static string DenyButton(string requestId) => Deny + requestId;
}

/// <summary>
/// 授權通知上那兩顆按鈕的產生。
///
/// 為什麼要抽出來（而不是留在 <c>/ai pset</c> 的流程裡）：`--dryrun` 的接線檢查
/// 是「畫面上出現的每個 custom_id 都要有處理函式、每個處理函式都要有按鈕」，
/// 而這則通知是發到**別的伺服器**、不會在本機的驗證畫面上出現 ——
/// 抽成純函式之後，驗證程式就能把它建出來對照，不必靠「例外名單」放行。
/// </summary>
public static class PersonaGrantButtons
{
    public static MessageComponent Notice(string requestId)
        => new ComponentBuilder()
            .WithButton("✅ 同意並複製", PersonaGrantCid.AllowButton(requestId), ButtonStyle.Success)
            .WithButton("🚫 拒絕", PersonaGrantCid.DenyButton(requestId), ButtonStyle.Danger)
            .Build();
}

/// <summary>
/// `/ai pset from:` 的**授權按鈕**：在來源伺服器按下同意或拒絕。
///
/// 為什麼要一顆按鈕而不是叫對方改環境變數：
///   * `LLM_ADMIN_IDS` 是主機端名單，加人 = 給**全域**權限（改所有伺服器都適用的規則）。
///     只是要讓他自己那個伺服器能設定提示詞，不該順便送他全域權限。
///   * 改名單要重啟服務、還要主機主人動手；按鈕當場就能完成，
///     而且**誰在什麼時候同意了什麼**會留在頻道上（那則通知會被改成結果）。
///
/// 誰能按：這個伺服器的**擁有者**、有「管理伺服器」權限的人，或主機端名單上的人。
/// 這是「在伺服器裡驗證」—— 用 Discord 自己的權限，而不是主機端的名單。
/// </summary>
public sealed class PersonaGrantModule : BusModuleBase
{
    private readonly PersonaGrantStore _grants;
    private readonly GuildPersonaStore _personas;
    private readonly LlmOptions _options;

    public PersonaGrantModule(PersonaGrantStore grants, GuildPersonaStore personas, LlmOptions options)
    {
        _grants = grants;
        _personas = personas;
        _options = options;
    }

    [ComponentInteraction(PersonaGrantCid.Allow + "*")]
    public Task AllowAsync(string requestId) => HandleAsync(requestId, approve: true);

    [ComponentInteraction(PersonaGrantCid.Deny + "*")]
    public Task DenyAsync(string requestId) => HandleAsync(requestId, approve: false);

    /// <summary>
    /// 這個人有資格按下這顆按鈕嗎？
    ///
    /// 抽成 public static 是為了能離線驗證（`--dryrun` 會用 IL 掃描確認
    /// 這裡真的檢查了「伺服器擁有者／Manage Server／主機名單」三件事）。
    /// </summary>
    public static bool IsApprover(LlmOptions options, SocketGuildUser? user, SocketGuild guild)
    {
        if (user is null) return false;

        // 1) 主機端名單（自己人）
        if (options.AdminUserIds.Contains(user.Id)) return true;

        // 2) 這個伺服器的擁有者
        if (guild.OwnerId == user.Id) return true;

        // 3) 有「管理伺服器」權限的人
        return user.GuildPermissions.ManageGuild;
    }

    private async Task HandleAsync(string requestId, bool approve)
    {
        var now = DateTimeOffset.UtcNow;

        if (Context.Guild is not { } guild)
        {
            await RespondAsync("這個按鈕只能在伺服器裡使用。", ephemeral: true);
            return;
        }

        if (!IsApprover(_options, Context.User as SocketGuildUser, guild))
        {
            await RespondAsync(
                "只有這個伺服器的**擁有者**、有「管理伺服器」權限的人，或主機端名單上的人可以按。",
                ephemeral: true);
            return;
        }

        var request = _grants.Peek(requestId, now);

        if (request is null)
        {
            await SetMessageAsync(
                embeds: [ClosedEmbed("⌛ 這個請求已經失效", "可能已過期（24 小時），或已經被處理過了。")],
                components: new ComponentBuilder().Build());

            return;
        }

        // 拿掉請求（同一顆按鈕不能被按第二次 —— 尤其「同意」是會改資料的）
        _grants.Take(requestId, now);

        var sourceGuild = Context.Client.GetGuild(request.SourceGuildId);
        var sourceName = sourceGuild?.Name ?? $"伺服器 {request.SourceGuildId}";

        var targetGuild = Context.Client.GetGuild(request.TargetGuildId);
        var targetName = targetGuild?.Name ?? $"伺服器 {request.TargetGuildId}";

        if (!approve)
        {
            await SetMessageAsync(
                embeds: [ClosedEmbed($"🚫 {Context.User.Username} 拒絕了這個請求",
                    $"沒有把「{sourceName}」的任何設定給出去。")],
                components: new ComponentBuilder().Build());

            await TellTargetAsync(request,
                $"🚫 「{sourceName}」拒絕了這次的授權請求 —— 沒有複製任何設定。\n" +
                "（如果覺得是誤會，可以請那邊的管理員直接跟你確認，或請他用 `/ai pset` 匯出。）");

            Console.WriteLine($"[授權] {Context.User.Id} 拒絕了 {request.Id}（{request.SourceGuildId} → {request.TargetGuildId}）");
            return;
        }

        // ── 同意 ────────────────────────────────────────
        var authority = _grants.Authorize(request.TargetGuildId, request.RequesterId, now);

        var copied = 0;
        var dropped = 0;
        var skipped = 0;

        if (request.Mode is PersonaGrantModes.CopyReplace or PersonaGrantModes.CopyAppend)
        {
            var outcome = _personas.CopyFrom(
                request.TargetGuildId, request.SourceGuildId, request.RequesterId,
                replace: request.Mode == PersonaGrantModes.CopyReplace);

            copied = outcome.Copied;
            dropped = outcome.Dropped;
            skipped = outcome.Skipped;
        }

        var detail = copied == 0
            ? "（那邊本來就有這些規則了，所以沒有新增）"
            : $"已複製 **{copied}** 條設定到「{targetName}」";

        if (skipped > 0) detail += $"，跳過 {skipped} 條重複的";
        if (dropped > 0) detail += $"，**{dropped} 條因為超過上限沒帶過去**";

        await SetMessageAsync(
            embeds: [ClosedEmbed($"✅ {Context.User.Username} 同意了這個請求", detail)],
            components: new ComponentBuilder().Build());

        var grantedTo = $"{PersonaGrantStore.AuthorityTtl.TotalDays:0}";

        await TellTargetAsync(request,
            $"✅ 「{sourceName}」同意了授權請求！\n" +
            $"• {detail}\n" +
            $"• <@{request.RequesterId}> 接下來 {grantedTo} 天可以直接用 `/ai pset` 維護這個伺服器的設定" +
            $"（到期：{authority.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}）\n" +
            "• 只限**這個伺服器**，不會拿到其他伺服器的權限");

        Console.WriteLine($"[授權] {Context.User.Id} 同意 {request.Id}：" +
                          $"{request.SourceGuildId} → {request.TargetGuildId}，複製 {copied} 條");
    }

    /// <summary>回報到「發起請求的那個頻道」（沒有權限就只寫 console，不要讓整個流程失敗）。</summary>
    private async Task TellTargetAsync(PendingGrant request, string text)
    {
        try
        {
            if (Context.Client.GetChannel(request.TargetChannelId) is IMessageChannel channel)
                await channel.SendMessageAsync(text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[授權] 回報到目標頻道失敗（{ex.Message}）—— 結果以來源頻道的通知為準");
        }
    }

    private static Embed ClosedEmbed(string title, string description)
        => new EmbedBuilder()
            .WithColor(new Color(0x2B, 0x6C, 0xB0))
            .WithTitle(title)
            .WithDescription(description)
            .WithFooter($"處理時間 {DateTimeOffset.UtcNow.ToLocalTime():MM-dd HH:mm}")
            .Build();
}
