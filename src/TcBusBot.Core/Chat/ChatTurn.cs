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
    ulong? ReplyToMessageId = null,
    /// <summary>
    /// 進提示詞時要用的「名字標籤」（例如「小明(@handle, 123456789012345678)」）。
    ///
    /// 為什麼不直接由這裡組：要不要帶 ID 是**設定**（<see cref="LlmOptions.ExposeUserIds"/>），
    /// 而這個型別刻意不知道設定 —— Discord 那一層組好之後放進來。
    /// 沒給就用 <see cref="AuthorName"/>。
    /// </summary>
    string? PromptLabel = null,
    /// <summary>
    /// 「偷聽到的閒聊」：不是對 Bot 說的訊息，但被留下來當上下文。
    ///
    /// 為什麼要區分：這些句子進到提示詞裡會長得跟「對 Bot 說的話」一模一樣
    /// （「你要不要一起去？」看起來就像在問 Bot），所以標成 `[閒聊]`，
    /// 讓模型知道那是別人之間的對話、只是背景。
    /// </summary>
    bool Ambient = false)
{
    public bool IsBot => Role == ChatRole.Assistant;

    /// <summary>這一則在提示詞裡要長什麼樣（多人頻道要分得出誰在說話）。</summary>
    public string ToPromptText()
        => IsBot ? Content
         : Ambient ? $"[閒聊] {PromptLabel ?? AuthorName}：{Content}"
         : $"{PromptLabel ?? AuthorName}：{Content}";

    public string Describe()
        => $"[{(IsBot ? "Bot" : AuthorName)} @ {At.ToLocalTime():HH:mm:ss}] {Preview(Content)}";

    private static string Preview(string text)
        => text.Length <= 40 ? text : text[..40] + "…";
}
