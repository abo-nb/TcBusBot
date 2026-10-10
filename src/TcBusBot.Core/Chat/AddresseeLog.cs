namespace TcBusBot.Core.Chat;

/// <summary>一次「這句是在跟誰說話」的判斷紀錄。</summary>
public sealed record AddresseeEntry(
    DateTimeOffset At,
    ulong GuildId,
    ulong ChannelId,
    ulong SpeakerId,
    string Speaker,
    string Preview,
    Addressee Verdict,
    /// <summary>這個結論是怎麼來的：`規則`（機械鐵證，不花錢）或 `模型`。</summary>
    string Source,
    string Reason)
{
    public string Label => Verdict switch
    {
        Addressee.Reply => "🗣 接話",
        Addressee.Skip => "👂 不出聲",
        _ => "⏹ 退出"
    };

    public string Describe()
        => $"{At.ToLocalTime():HH:mm:ss}　{Label}（{Source}）　{Speaker}：{Preview}";
}

/// <summary>
/// 「誰在對誰說話」的最近判斷紀錄（**只留最近幾筆，在記憶體裡**）。
///
/// 為什麼需要它：使用者問的是「**我想確認他能不能分清楚誰跟誰在說話**」——
/// 這種問題光看程式碼或 log 都很難回答，因為症狀是「它偶爾插一句話」，
/// 而且插話的當下沒有人知道它當時是怎麼想的。
/// 有了這個紀錄，`/ai status` 就能直接顯示：
///
///   21:03:11  👂 不出聲（規則）  阿美：@小華 你幾點到？
///   21:03:40  👂 不出聲（模型）  小華：我大概七點
///   21:04:02  🗣 接話（模型）    阿美：那 304 有到嗎
///
/// 一眼就看得出來「它是靠規則還是靠模型」、「同一句話它判成什麼」。
/// 只放記憶體是刻意的：這是除錯用的觀察資料，不需要跨重啟（也不需要永久保存）。
/// </summary>
public sealed class AddresseeLog
{
    public const int Capacity = 20;

    /// <summary>訊息預覽最多幾個字（避免一則長訊息把紀錄撐爆）。</summary>
    public const int PreviewLength = 60;

    private readonly object _gate = new();
    private readonly List<AddresseeEntry> _entries = [];

    public int Replies { get; private set; }
    public int Skips { get; private set; }
    public int Stops { get; private set; }

    public void Record(
        ulong guildId, ulong channelId, ChatTurn turn, Addressee verdict, string source, string reason,
        DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            switch (verdict)
            {
                case Addressee.Reply: Replies++; break;
                case Addressee.Skip: Skips++; break;
                default: Stops++; break;
            }

            _entries.Add(new AddresseeEntry(
                at ?? DateTimeOffset.UtcNow,
                guildId,
                channelId,
                turn.AuthorId,
                turn.PromptLabel ?? turn.AuthorName,
                Preview(turn.Content),
                verdict,
                source,
                reason));

            while (_entries.Count > Capacity) _entries.RemoveAt(0);
        }
    }

    /// <summary>最近幾筆（新的在前）。</summary>
    public IReadOnlyList<AddresseeEntry> Recent(int count = 5)
    {
        lock (_gate)
            return _entries.OrderByDescending(e => e.At).Take(Math.Max(1, count)).ToList();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Replies = Skips = Stops = 0;
        }
    }

    /// <summary>一行統計（`/ai status` 的抬頭）。</summary>
    public string Stats()
        => Replies + Skips + Stops == 0
            ? "還沒有判斷過任何訊息"
            : $"接話 {Replies} 次／不出聲 {Skips} 次／退出 {Stops} 次";

    /// <summary>最近幾筆的文字（`/ai status` 用；沒有紀錄時回一句說明）。</summary>
    public string Describe(int count = 3)
    {
        var recent = Recent(count);

        if (recent.Count == 0)
            return "還沒有判斷過（它只會在「剛剛回過話」的偷聽窗口裡判斷）";

        return string.Join("\n", recent.Select(e => e.Describe()));
    }

    private static string Preview(string? text)
    {
        var flat = (text ?? "").Replace('\n', ' ').Trim();

        return flat.Length <= PreviewLength ? flat : flat[..PreviewLength] + "…";
    }
}
