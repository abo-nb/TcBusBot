namespace TcBusBot.Core.Chat;

public enum ChatRole
{
    User,
    Assistant
}

/// <summary>
/// 對話裡的一則訊息。
///
/// 為什麼不用 SK 的 <c>ChatMessageContent</c> 當作儲存格式：
/// 這裡的每一則訊息都要記「誰說的、什麼時候說的、訊息 ID 是多少」——
/// 時間決定要不要接續上下文，訊息 ID 決定「被回覆的是哪一則」，
/// 這些都不是 LLM 需要的東西，而是**我們決定要餵什麼給 LLM** 需要的東西。
/// 所以自己一個型別，送出去前才轉成 SK 的訊息。
/// </summary>
public sealed record ChatTurn(
    ChatRole Role,
    string AuthorName,
    ulong AuthorId,
    ulong MessageId,
    string Content,
    DateTimeOffset At,
    ulong? ReplyToMessageId = null)
{
    public bool IsBot => Role == ChatRole.Assistant;

    /// <summary>這一則在提示詞裡要長什麼樣（多人頻道要分得出誰在說話）。</summary>
    public string ToPromptText()
        => IsBot ? Content : $"{AuthorName}：{Content}";

    public string Describe()
        => $"[{(IsBot ? "Bot" : AuthorName)} @ {At.ToLocalTime():HH:mm:ss}] {Preview(Content)}";

    private static string Preview(string text)
        => text.Length <= 40 ? text : text[..40] + "…";
}
