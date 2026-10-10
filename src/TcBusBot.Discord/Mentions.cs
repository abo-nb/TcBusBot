using Discord;

namespace TcBusBot.Discord;

/// <summary>
/// 「這一則訊息可以 @ 誰」的**唯一產生處**。
///
/// ── 為什麼要有這個檔案（真的在線上炸過）──────────────────
/// `AllowedMentions` 有兩套寫法，而它們**互斥**：
///
///   * 新式：`AllowedTypes = AllowedMentionTypes.Users`（送 `parse: ["users"]`）
///   * 舊式：`UserIds = [...]`（送 `parse: []` ＋ `users: [...]`）
///
/// 兩個一起設會直接丟：
/// <code>
/// ArgumentException: The Users flag is mutually exclusive with the list of User Ids. (Parameter 'allowedMentions')
/// </code>
/// 而那個例外是在**送出訊息時**才丟的 —— 發生在背景的佇列／排程裡，
/// 使用者只會看到「它什麼都沒說」，log 裡只有一行「處理一則時發生非預期錯誤」。
///
/// 官方文件的原文（Discord.Net 3.18）：
/// 「The <c>Users</c> flag is mutually exclusive with the <c>UserIds</c> property…
///  If <c>null</c>, only the ids specified in <c>UserIds</c> and <c>RoleIds</c> will be mentioned.」
///
/// 所以規則很簡單：**要限定名單就用 <see cref="OnlyUsers"/>（不要碰 AllowedTypes）；
/// 要「所有被 @ 的人都通知」才用 <see cref="UsersAllowed"/>。**
/// 集中在這裡之後，就不會再有第二個人（包括未來的我）手寫出那個壞組合。
/// </summary>
public static class Mentions
{
    /// <summary>
    /// 只有名單上的**這幾個使用者**會被 @（`@everyone`／`@here`／身分組一律不通）。
    ///
    /// ⚠️ 刻意**不設** <see cref="AllowedMentions.AllowedTypes"/> ——
    ///    設了就會跟上一個參數衝突（見類別說明）。
    /// </summary>
    public static AllowedMentions OnlyUsers(IEnumerable<ulong> userIds, bool mentionRepliedUser = false)
    {
        var ids = userIds.Distinct().ToList();

        return ids.Count == 0
            ? Silent(mentionRepliedUser)
            : new AllowedMentions
            {
                UserIds = ids,
                MentionRepliedUser = mentionRepliedUser
            };
    }

    /// <summary>一個 ping 都不發（連「回覆通知」也可以關掉）。</summary>
    public static AllowedMentions Silent(bool mentionRepliedUser = false)
        => new()
        {
            AllowedTypes = AllowedMentionTypes.None,
            MentionRepliedUser = mentionRepliedUser
        };

    /// <summary>**所有**被 @ 到的使用者都會收到通知（不限制名單；一般聊天回覆用）。</summary>
    public static AllowedMentions UsersAllowed()
        => new() { AllowedTypes = AllowedMentionTypes.Users };
}
