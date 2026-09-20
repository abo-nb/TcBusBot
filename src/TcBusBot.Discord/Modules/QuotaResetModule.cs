using Discord;
using Discord.Interactions;
using TcBusBot.Core.Chat;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// 「重置每週額度」按鈕的 custom_id（後面接「開啟這顆按鈕的人」的 ID）。
///
/// 為什麼要把「誰開的」寫進 custom_id：這顆按鈕是**全機**的（額度是整個 Bot 共用一份），
/// 所以不能讓任何人撿到就能按。寫進去之後，按的人必須**就是**當初開 `/ai status` 的那個人
/// —— 轉傳、截圖、或請別人代按都不會生效。
/// </summary>
public static class QuotaResetCid
{
    public const string Ask = "quota:reset:ask:";
    public const string Confirm = "quota:reset:confirm:";
    public const string Cancel = "quota:reset:cancel:";

    public static string AskButton(ulong userId) => Ask + userId;
    public static string ConfirmButton(ulong userId) => Confirm + userId;
    public static string CancelButton(ulong userId) => Cancel + userId;

    /// <summary>從 custom_id 取出「誰開的」；格式不對就回 null（**不猜**）。</summary>
    public static ulong? OpenedBy(string? customId, string prefix)
        => customId is not null
           && customId.StartsWith(prefix, StringComparison.Ordinal)
           && ulong.TryParse(customId[prefix.Length..], out var id)
           && id != 0
            ? id
            : null;
}

/// <summary>
/// 「誰可以重置額度」的判斷（刻意抽成純函式）。
///
/// 為什麼是純函式：這是**安全性**判斷，而安全性判斷最怕「只寫在文件上、程式碼裡沒擋」。
/// 抽出來之後 `--dryrun` 可以直接拿各種身分餵它，驗「不是管理員一定被擋」、
/// 「別人的按鈕一定被擋」，而不是只掃 IL 猜有沒有檢查。
/// </summary>
public static class QuotaResetPolicy
{
    /// <summary>主機端名單（`LLM_ADMIN_IDS`）＝自己人。額度是全機的，所以只認這份名單。</summary>
    public static bool IsAdmin(LlmOptions options, ulong userId) => options.AdminUserIds.Contains(userId);

    /// <summary>
    /// 回傳 null＝可以按；否則是不能按的原因（直接顯示給使用者）。
    ///
    /// 兩個條件都要成立（fail closed）：
    ///   1. 按的人是主機端名單上的人
    ///   2. 而且他就是**開啟這顆按鈕的人**
    ///
    /// ⚠️ 為什麼第 2 點不能省：按鈕本身不帶任何憑證，只有 custom_id 裡的 ID。
    ///    如果只看第 1 點，那「兩個管理員互相按對方的按鈕」就會成立 ——
    ///    聽起來無害，但這顆按鈕的第二段（確定重置）就變成「誰都能替誰按確認」，
    ///    而「要按兩次才生效」的設計本來就是要擋掉誤按與順手按。
    /// </summary>
    public static string? DenyReason(LlmOptions options, ulong openedBy, ulong presserId)
    {
        if (!IsAdmin(options, presserId))
        {
            return "只有主機端名單（`LLM_ADMIN_IDS`）上的人可以重置額度 ——\n" +
                   "這是**全機**的額度（所有伺服器共用同一份），所以不能由一般使用者重置。\n" +
                   "如果你是主機主人，把自己加進 `LLM_ADMIN_IDS` 再重啟即可。";
        }

        if (openedBy != presserId)
        {
            return "這顆按鈕是**別人**開啟的（`/ai status` 的訊息是私人的）。\n" +
                   "請你自己打一次 `/ai status`，用你自己那顆按鈕。";
        }

        return null;
    }
}

/// <summary>
/// 按鈕的產生。刻意跟處理函式放在一起 —— 這種「按了要有反應」的東西，
/// 兩邊分開放就會出現「做了按鈕但忘了寫處理函式」（按了完全沒反應，而且不會報錯）。
/// </summary>
public static class QuotaResetButtons
{
    /// <summary>`/ai status` 底部那一排（只有管理員看得到）。</summary>
    public static ComponentBuilder StatusRow(ulong userId)
        => new ComponentBuilder()
            .WithButton("♻️ 重置本週額度", QuotaResetCid.AskButton(userId), ButtonStyle.Secondary);

    /// <summary>確認那一排（紅色＝會改到全機設定）。</summary>
    public static ComponentBuilder ConfirmRow(ulong userId)
        => new ComponentBuilder()
            .WithButton("✅ 確定重置", QuotaResetCid.ConfirmButton(userId), ButtonStyle.Danger)
            .WithButton("🚫 取消", QuotaResetCid.CancelButton(userId), ButtonStyle.Secondary);
}

/// <summary>
/// `/ai status` 上「♻️ 重置本週額度」那顆按鈕（**只有管理員**看得到）。
///
/// 為什麼需要這個按鈕：每週額度是**硬的** —— 用完之後 Bot 會直接拒絕回話，
/// 一直到 UTC 週一 00:00 才會自己重置。但「額度用完」常常發生在**最需要它的時候**
/// （大家在問路），而以前唯一的解法是去改環境變數、重啟服務，
/// 或等好幾天 —— 那對「只是想讓它今天能回話」來說太重了。
///
/// 三個刻意的設計：
///   * **一定要按兩次**：第一次只顯示「目前用了多少、重置會發生什麼」，第二次才真的做。
///     額度是「錢」，不該一顆按鈕就擦掉。
///   * **按鈕只認開啟它的那個人**（見 <see cref="QuotaResetPolicy"/>），而且**按的當下**
///     再檢查一次是不是管理員 —— 不依賴「看不到就按不到」。
///   * **留下紀錄**：誰、什麼時候、把多少用量歸零，會進主人操作紀錄（`/ai audit`）。
///     不然「額度怎麼突然變多」會變成查不出來的事。
///
/// ⚠️ 重置只是**重新計算這一週的上限**，不會退錢、也不會改變你跟服務商的帳單。
/// </summary>
public sealed class QuotaResetModule : BusModuleBase
{
    private readonly WeeklyTokenBudget _budget;
    private readonly GuildPersonaStore _personas;
    private readonly LlmOptions _options;

    public QuotaResetModule(WeeklyTokenBudget budget, GuildPersonaStore personas, LlmOptions options)
    {
        _budget = budget;
        _personas = personas;
        _options = options;
    }

    [ComponentInteraction(QuotaResetCid.Ask + "*")]
    public Task AskAsync(string openedBy) => AskCoreAsync(openedBy);

    [ComponentInteraction(QuotaResetCid.Confirm + "*")]
    public Task ConfirmAsync(string openedBy) => ConfirmCoreAsync(openedBy);

    [ComponentInteraction(QuotaResetCid.Cancel + "*")]
    public Task CancelAsync(string openedBy) => CancelCoreAsync(openedBy);

    // ── 主流程（internal 是為了讓 --dryrun 能直接走一遍，不必連線）────

    internal async Task AskCoreAsync(string openedBy)
    {
        if (!Authorize(QuotaResetCid.Ask, openedBy, out var reason))
        {
            await RespondAsync(reason, ephemeral: true);
            return;
        }

        var usage = _budget.Usage;
        var limit = _budget.Limit;

        var spent = limit <= 0
            ? $"不限額度（這一週已用 {usage.TotalTokens:N0} tokens）"
            : $"{usage.TotalTokens:N0}／{limit:N0} tokens（剩 {Math.Max(0, limit - usage.TotalTokens):N0}）";

        await RespondAsync(
            embed: new EmbedBuilder()
                .WithColor(new Color(0xE6, 0x7E, 0x22))
                .WithTitle("♻️ 要重置這一週的 AI 額度嗎？")
                .WithDescription(
                    "這是**全機**的額度（所有伺服器共用同一份用量統計）。\n" +
                    "重置之後這一週會**從 0 開始算**，等於立刻又有完整額度可以用。")
                .AddField("目前這一週", spent, inline: false)
                .AddField("呼叫次數", $"{usage.Calls} 次（被額度擋下 {usage.Refusals} 次）", inline: true)
                .AddField("原本的重置時間", $"{WeeklyBudgetResetText()}", inline: true)
                .AddField("重置**不會**做的事",
                    "• 不會退錢，也不會改變你跟服務商的帳單\n" +
                    "• 不會影響公車功能（它本來就不受額度限制）\n" +
                    "• 不會清掉對話記憶（那是 `/ai forget` 的工作）",
                    inline: false)
                .WithFooter($"只有 <@{Context.User.Id}> 能按這兩顆按鈕｜會記進主人操作紀錄（/ai audit）")
                .Build(),
            components: QuotaResetButtons.ConfirmRow(Context.User.Id).Build(),
            ephemeral: true);
    }

    internal async Task ConfirmCoreAsync(string openedBy)
    {
        if (!Authorize(QuotaResetCid.Confirm, openedBy, out var reason))
        {
            await RespondAsync(reason, ephemeral: true);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var before = _budget.Usage;

        _budget.Reset(now);

        // 這一筆一定要留：不然「額度怎麼突然變多」查不出來是誰做的
        _personas.AuditAdmin(
            Context.User.Id,
            "重置每週 AI 額度",
            $"把 {before.TotalTokens:N0} tokens／{before.Calls} 次呼叫歸零" +
            $"（原本的重置時間 {WeeklyTokenBudget.ResetAt(now).ToLocalTime():MM-dd HH:mm}，" +
            $"由 /ai status 的按鈕）");

        Console.WriteLine($"[額度] {Context.User.Id} 重置了每週 token 用量" +
                          $"（原本 {before.TotalTokens:N0} tokens／{before.Calls} 次）");

        var after = _budget.Usage;
        var limit = _budget.Limit;

        await UpdateAsync(
            embed: new EmbedBuilder()
                .WithColor(new Color(0x2E, 0x8B, 0x57))
                .WithTitle("✅ 已重置這一週的 AI 額度")
                .WithDescription(
                    limit <= 0
                        ? "額度統計已經歸零（這台主機沒有設定上限，所以本來就不會被擋）。"
                        : $"額度統計已經歸零 —— 現在又可以用滿 **{limit:N0}** tokens。")
                .AddField("重置前", $"{before.TotalTokens:N0} tokens／{before.Calls} 次呼叫", inline: true)
                .AddField("重置後", $"{after.TotalTokens:N0} tokens／{after.Calls} 次呼叫", inline: true)
                .AddField("下一次自動重置", $"{WeeklyTokenBudget.ResetAt(now).ToLocalTime():MM-dd HH:mm}", inline: true)
                .WithFooter($"已記進主人操作紀錄（/ai audit）｜{now.ToLocalTime():MM-dd HH:mm}")
                .Build(),
            components: new ComponentBuilder().Build());
    }

    internal async Task CancelCoreAsync(string openedBy)
    {
        if (!Authorize(QuotaResetCid.Cancel, openedBy, out var reason))
        {
            await RespondAsync(reason, ephemeral: true);
            return;
        }

        var usage = _budget.Usage;

        await UpdateAsync(
            embed: new EmbedBuilder()
                .WithColor(new Color(0x5A, 0x5A, 0x5A))
                .WithTitle("🚫 沒有重置")
                .WithDescription($"額度統計**沒有被動到**：目前這一週仍是 {usage.TotalTokens:N0} tokens／{usage.Calls} 次呼叫。")
                .Build(),
            components: new ComponentBuilder().Build());
    }

    /// <summary>
    /// 這一顆按鈕按得下去嗎？
    ///
    /// custom_id 用**Discord 送來的那一份**（不是把參數接回字串）：
    /// 那才是「使用者真的按到哪一顆按鈕」的證據。兩個東西都要對上：
    ///   * 前綴符合（這是我們發的按鈕）
    ///   * 尾巴的數字＝這次互動帶來的 wildcard（沒有被改過）
    ///
    /// 對不上就拒絕 —— 那代表按鈕是**別人造出來的**（或程式改壞了），不是我們發的。
    /// </summary>
    private bool Authorize(string prefix, string wildcard, out string reason)
    {
        var customId = (Context.Interaction as IComponentInteraction)?.Data.CustomId;
        var owner = QuotaResetCid.OpenedBy(customId, prefix);

        if (owner is null || customId != prefix + wildcard)
        {
            reason = "這顆按鈕的內容不完整（無法確認是誰開的），請重新用 `/ai status` 開啟。";
            return false;
        }

        reason = QuotaResetPolicy.DenyReason(_options, owner.Value, Context.User.Id) ?? "";
        return reason.Length == 0;
    }

    private string WeeklyBudgetResetText()
        => $"{WeeklyTokenBudget.ResetAt(DateTimeOffset.UtcNow).ToLocalTime():MM-dd HH:mm}（UTC 週一 00:00）";
}
