namespace TcBusBot.Core.Chat;

/// <summary>授權檢查的結果。</summary>
public sealed record AdminCheck(
    bool IsAdmin,
    bool KeyPresent,
    bool KeyExpected,
    string CleanedContent)
{
    /// <summary>有帶 key 但不在名單裡（要記 log，因為可能是有人在亂試）。</summary>
    public bool UnauthorizedAttempt => KeyPresent && !IsAdmin;
}

/// <summary>
/// 「主人」授權：後台指定一組 Discord 使用者 ID，那個人在訊息裡帶上特殊 key
/// 就可以對 Bot 下達**必須接受**的命令。
///
/// 兩個條件都成立才算授權（fail closed）：
///   1. 發話者的 ID 在 <see cref="LlmOptions.AdminUserIds"/> 裡
///   2. 訊息裡出現 <see cref="LlmOptions.AdminKey"/>
/// 只要 <see cref="LlmOptions.AdminKey"/> 沒設定，整個機制就是關的 ——
/// 不會出現「忘了設 key 所以誰都能下令」這種事。
///
/// ⚠️ **key 一定會被移除**（不管有沒有授權成功）：
///    * 不從內容裡拿掉的話，key 會進到對話記憶、提示詞與 console log
///    * 尤其「有人拿別人的 key 亂打」時，更不該讓它留在歷史裡
/// </summary>
public static class AdminAuthorizer
{
    /// <summary>訊息裡的 key 被拿掉之後，若什麼都不剩就補這一句（讓模型知道「有東西被移除了」）。</summary>
    public const string KeyStrippedPlaceholder = "（已收到授權指令）";

    public static AdminCheck Check(ulong userId, string? content, LlmOptions options)
    {
        var text = content ?? "";
        var keyExpected = options.AdminEnabled;
        var key = options.AdminKey;

        if (!keyExpected || string.IsNullOrEmpty(key) || text.Length == 0)
            return new AdminCheck(false, false, keyExpected, text);

        var index = text.IndexOf(key, StringComparison.Ordinal);

        if (index < 0)
            return new AdminCheck(false, false, keyExpected, text);

        // 把 key 拿掉（全部出現的地方都拿掉），並把多出來的空白整理一下
        var cleaned = text.Replace(key, " ", StringComparison.Ordinal);

        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);

        cleaned = cleaned.Trim().TrimStart('：', ':', '，', ',').Trim();

        if (cleaned.Length == 0) cleaned = KeyStrippedPlaceholder;

        var isAdmin = Array.IndexOf(options.AdminUserIds, userId) >= 0;

        return new AdminCheck(isAdmin, true, keyExpected, cleaned);
    }
}

/// <summary>
/// 把訊息裡的 Discord mention（<c>&lt;@123&gt;</c>／<c>&lt;@!123&gt;</c>／<c>&lt;@&amp;123&gt;</c>）
/// 換成「看得懂的名字 ＋ ID」。
///
/// 為什麼要做：使用者打「@某人 幫我查」時，模型原本只看到 <c>&lt;@123456789&gt;</c> ——
/// 有 ID 但不知道那是誰；換成「@小明(123456789012345678)」之後，
/// 它就能分辨同名的人，也能把規則綁在特定人身上。
///
/// 抽成純函式（放 Core）是為了可以離線測試：mention 的格式有好幾種寫法，
/// 換錯會讓訊息內容整段壞掉。
/// </summary>
public static class MentionFormatter
{
    /// <summary>要拿來替換的對象：ID → 顯示字串（例如「小明」）。</summary>
    public static string Expand(
        string? content,
        IReadOnlyDictionary<ulong, string> knownUsers,
        bool includeIds)
    {
        var text = content ?? "";
        if (text.Length == 0 || knownUsers.Count == 0) return text;

        foreach (var (id, name) in knownUsers)
        {
            var label = includeIds ? $"@{name}({id})" : $"@{name}";

            text = text.Replace($"<@{id}>", label, StringComparison.Ordinal)
                       .Replace($"<@!{id}>", label, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// 把「組 prompt 用的標籤」做出來：暱稱（@帳號, ID）。
    ///
    /// 例：<c>小明(@wuxiaohan0922, 123456789012345678)</c>
    /// </summary>
    public static string Label(string? displayName, string? username, ulong userId, bool includeId)
    {
        var name = string.IsNullOrWhiteSpace(displayName)
            ? (string.IsNullOrWhiteSpace(username) ? $"使用者{userId}" : username!)
            : displayName!;

        if (!includeId) return name;

        var handle = string.IsNullOrWhiteSpace(username) || username == name ? null : $"@{username}";
        var id = userId.ToString();

        return handle is null ? $"{name}({id})" : $"{name}({handle}, {id})";
    }
}
