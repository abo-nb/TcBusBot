using Discord;
using Discord.Interactions;

namespace TcBusBot.Discord.Modules;

/// <summary>
/// 模組基底類別。
///
/// 為什麼需要它：Discord.Net 的 <see cref="InteractionModuleBase{T}"/> 有 RespondAsync /
/// DeferAsync / FollowupAsync，但**沒有** UpdateAsync ——
/// 更新「元件所在的訊息」必須走 <see cref="IComponentInteraction"/> 上的版本，
/// 而它的簽章是 Action&lt;MessageProperties&gt;。
/// 這裡包一層，讓子類別可以用一致的 (embed, components) 風格呼叫。
/// </summary>
public abstract class BusModuleBase : InteractionModuleBase<SocketInteractionContext>
{
    protected Task UpdateAsync(Embed? embed = null, MessageComponent? components = null, string? text = null)
        => SetMessageAsync(embeds: embed is null ? null : new[] { embed }, components, text);

    /// <summary>
    /// 更新「元件所在的訊息」，**已 defer 過或還沒都適用**。
    ///
    /// 差別很重要：一旦呼叫過 DeferAsync，互動就已經回應了，
    /// 這時只能走 ModifyOriginalResponseAsync；直接 UpdateAsync 會丟例外。
    /// </summary>
    protected Task SetMessageAsync(Embed[]? embeds, MessageComponent? components = null, string? text = null)
    {
        if (Context.Interaction.HasResponded)
            return ModifyOriginalResponseAsync(m =>
            {
                m.Content = text;
                m.Embeds = embeds;
                m.Components = components;
            });

        if (Context.Interaction is not IComponentInteraction component)
            throw new InvalidOperationException(
                $"此互動型別不支援更新訊息：{Context.Interaction.GetType().Name}");

        return component.UpdateAsync(m =>
        {
            m.Content = text;
            m.Embeds = embeds;
            m.Components = components;
        });
    }
}
