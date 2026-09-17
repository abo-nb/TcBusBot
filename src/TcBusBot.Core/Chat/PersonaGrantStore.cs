using System.Collections.Concurrent;

namespace TcBusBot.Core.Chat;

/// <summary>
/// 授權請求的種類 —— 決定「同意之後要不要順便複製設定」，以及怎麼複製。
/// 放在 Core（不是 Discord 那一層）：這是**資料**，不是按鈕的長相。
/// </summary>
public static class PersonaGrantModes
{
    /// <summary>把來源伺服器的設定複製過來，覆蓋目標現有的全部。</summary>
    public const string CopyReplace = "copy-replace";

    /// <summary>把來源伺服器的設定複製過來，追加（跳過重複的）。</summary>
    public const string CopyAppend = "copy-append";
}

/// <summary>
/// 一筆「有人在別的伺服器請求授權」的待辦。
///
/// 情境：小明在 B 伺服器想用 `/ai pset from:A` 把 A 伺服器的自訂提示詞搬過來，
/// 但那些內容是**A 伺服器的東西**（常常包含只有那裡才有的自訂表情名稱與內規）。
/// 所以 Bot 不會直接照做，而是在 **A 伺服器發一則通知**，讓 A 那邊的人按「同意」。
/// </summary>
public sealed record PendingGrant(
    string Id,
    ulong SourceGuildId,
    ulong TargetGuildId,
    ulong TargetChannelId,
    ulong RequesterId,
    string Mode,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    public bool Expired(DateTimeOffset now) => now >= ExpiresAt;
}

/// <summary>
/// 「這個人在這個伺服器可以用 `/ai pset`」的授權（由來源伺服器的人按按鈕同意而來）。
///
/// 為什麼要**按伺服器**分開：授權是「A 願意把設定給 B 的管理者」，
/// 不是「這個人變成萬用管理員」。他拿到的是 B 那一個伺服器的設定權。
/// </summary>
public sealed record PersonaAuthority(ulong GuildId, ulong UserId, DateTimeOffset GrantedAt, DateTimeOffset ExpiresAt)
{
    public bool Expired(DateTimeOffset now) => now >= ExpiresAt;
}

/// <summary>
/// 跨伺服器授權：**用按鈕同意**，不用叫人去改環境變數。
///
/// 為什麼是這個設計（而不是「把對方加進 LLM_ADMIN_IDS」）：
///   * `LLM_ADMIN_IDS` 是**主機端**的名單，加進去等於給他**全域**權限
///     （改所有伺服器都適用的規則）。只是要讓他自己那個伺服器能設定提示詞，
///     不該順便送他全域權限 —— 這是「權限給太大」的經典錯誤。
///   * 而且加名單要重啟服務；這個流程當場就能完成，也留下「誰在什麼時候同意了什麼」。
///
/// 有效性：請求 24 小時、授權 30 天（都寫在常數裡，`/ai pset` 會顯示到期時間）。
/// 全部只在記憶體 —— 重啟就沒了，重新請求一次即可（跟訂閱、對話記憶同一個取捨）。
/// </summary>
public sealed class PersonaGrantStore
{
    /// <summary>請求多久沒人回應就作廢。</summary>
    public static readonly TimeSpan RequestTtl = TimeSpan.FromHours(24);

    /// <summary>同意之後，那個人可以用多久。</summary>
    public static readonly TimeSpan AuthorityTtl = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<string, PendingGrant> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), PersonaAuthority> _authorities = new();

    /// <summary>建立一筆請求，回傳它的編號（按鈕的 custom_id 會帶著它）。</summary>
    public PendingGrant Request(
        ulong sourceGuildId, ulong targetGuildId, ulong targetChannelId, ulong requesterId,
        string mode, DateTimeOffset now)
    {
        var grant = new PendingGrant(
            Id: Guid.NewGuid().ToString("N")[..8],
            SourceGuildId: sourceGuildId,
            TargetGuildId: targetGuildId,
            TargetChannelId: targetChannelId,
            RequesterId: requesterId,
            Mode: mode,
            CreatedAt: now,
            ExpiresAt: now + RequestTtl);

        _pending[grant.Id] = grant;
        return grant;
    }

    /// <summary>看一筆請求還在不在（過期就順手清掉）。</summary>
    public PendingGrant? Peek(string? id, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        if (!_pending.TryGetValue(id!, out var grant)) return null;

        if (grant.Expired(now))
        {
            _pending.TryRemove(id!, out _);
            return null;
        }

        return grant;
    }

    /// <summary>把請求拿掉（同意或拒絕之後都呼叫，避免同一顆按鈕被按兩次）。</summary>
    public PendingGrant? Take(string? id, DateTimeOffset now)
    {
        var grant = Peek(id, now);
        if (grant is null) return null;

        _pending.TryRemove(grant.Id, out _);
        return grant;
    }

    /// <summary>記錄「這個人在這個伺服器被授權了」（由來源伺服器的人按下同意時呼叫）。</summary>
    public PersonaAuthority Authorize(ulong guildId, ulong userId, DateTimeOffset now)
    {
        var authority = new PersonaAuthority(guildId, userId, now, now + AuthorityTtl);
        _authorities[(guildId, userId)] = authority;
        return authority;
    }

    /// <summary>這個人在這個伺服器有授權嗎（過期就順手清掉）。</summary>
    public PersonaAuthority? PeekAuthority(ulong guildId, ulong userId, DateTimeOffset now)
    {
        if (!_authorities.TryGetValue((guildId, userId), out var authority)) return null;

        if (authority.Expired(now))
        {
            _authorities.TryRemove((guildId, userId), out _);
            return null;
        }

        return authority;
    }

    public int PendingCount => _pending.Count;
    public int AuthorityCount => _authorities.Count;

    /// <summary>清掉過期的請求與授權；回傳清了幾筆。</summary>
    public int Purge(DateTimeOffset now)
    {
        var requests = _pending.Where(kv => kv.Value.Expired(now)).Select(kv => kv.Key).ToList();
        var authorities = _authorities.Where(kv => kv.Value.Expired(now)).Select(kv => kv.Key).ToList();

        var removed = requests.Count(k => _pending.TryRemove(k, out _));
        removed += authorities.Count(k => _authorities.TryRemove(k, out _));

        return removed;
    }
}
