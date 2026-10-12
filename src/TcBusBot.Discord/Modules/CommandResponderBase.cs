using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// 讓「不是指令模組的指令實作」也能回話。
///
/// ── 為什麼需要這一層 ────────────────────────────────────────
/// <see cref="InteractionModuleBase{T}.RespondAsync"/>／<c>DeferAsync</c>／<c>FollowupAsync</c>
/// 都是 **protected** —— 只有模組自己（或它的衍生類別）叫得到。
///
/// 但專案裡有一個指令的實作**刻意不住在模組裡**：`/ai llm`（見
/// <see cref="LlmEndpointCommand"/>）。原因是 Discord.Net 的註冊方式是
/// 「每個 top-level 模組各自產生一個指令」，所以兩個類別都掛 `[Group("ai")]` 時，
/// 送給 Discord 的 payload 會有**兩個** `name: "ai"`，整份請求被拒絕
/// —— 使用者看到的就只有「找不到 /ai llm」（真實事故）。
///
/// 於是這個基底類別把三個 protected 方法開放成 **internal**（同一個組件才看得到，
/// 不是 public）：`ChatModule` 繼承它、把 `this` 傳給實作類別，
/// 實作類別就能用同一套方法回話，而 Discord 仍然只收到**一個** `/ai`。
/// </summary>
public abstract class CommandResponderBase : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>回一則新訊息（跟 <c>RespondAsync</c> 同義）。</summary>
    internal Task RespondRawAsync(
        string? text = null, Embed? embed = null, bool ephemeral = false,
        MessageComponent? components = null)
        => RespondAsync(text, embed: embed, components: components, ephemeral: ephemeral);

    /// <summary>先延後回應（打 API 會超過 Discord 的 3 秒限制時必須用）。</summary>
    internal Task DeferRawAsync(bool ephemeral = false) => DeferAsync(ephemeral);

    /// <summary>延後之後的後續訊息（跟 <c>FollowupAsync</c> 同義）。</summary>
    internal Task FollowupRawAsync(
        string? text = null, Embed? embed = null, bool ephemeral = false,
        MessageComponent? components = null)
        => FollowupAsync(text, embed: embed, components: components, ephemeral: ephemeral);
}
