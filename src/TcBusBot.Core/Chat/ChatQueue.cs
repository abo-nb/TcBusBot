using System.Collections.Concurrent;

namespace TcBusBot.Core.Chat;

/// <summary>排隊的結果。</summary>
public enum ChatQueueOutcome
{
    /// <summary>排在後面等。</summary>
    Added,

    /// <summary>跟上一則合併成一題（同一個人短時間內連打好幾句）。</summary>
    Merged,

    /// <summary>排不進去（佇列滿了），最後被丟掉。</summary>
    Dropped
}

/// <summary>排隊中的一則訊息。</summary>
public sealed record ChatQueueItem<T>(
    ulong AuthorId,
    DateTimeOffset At,
    /// <summary>這一則有沒有明確在對 Bot 說話（@ 它、回覆它）。</summary>
    bool Addressed,
    T Payload,
    /// <summary>總共合併了幾則（1 = 沒有合併）。</summary>
    int Merged = 1);

/// <summary>
/// 每個頻道的待處理佇列。
///
/// ── 為什麼需要它 ────────────────────────────────────────
/// 原本的作法是「同一頻道一次只處理一則，忙的時候直接丟掉」。那在多人頻道會出事：
/// 兩個人幾乎同時 @ 它，只有第一個會被回答，第二個被**默默吃掉**
/// （console 只留下一行「略過一則…還在想上一題」，使用者當然看不到任何東西）。
/// 使用者要的是「盡量每個人都回」，所以改成排隊，並且：
///
///   * **同一個人短時間內連打幾句 → 合併成一題**（「在嗎」「300 幾點」「我要去靜宜」），
///     不然會被當成三題、回三次（既慢又貴）
///   * **佇列滿了先丟「不是在對它說話」的那些**（閒聊），最後才丟「明確 @ 它」的
///     —— 順序反過來的話，被犧牲的永遠是最想被回答的那個人
///
/// 這一類放在 Core（不是 Discord 那一層）是為了能離線測試：這裡的每一個判斷
/// 都會直接影響「使用者有沒有被回答」。
/// </summary>
public sealed class ChatQueue<T>
{
    private readonly List<ChatQueueItem<T>> _items = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);

    private readonly Func<T, T, T>? _merge;
    private readonly int _maxDepth;
    private readonly TimeSpan _mergeWindow;

    /// <param name="maxDepth">最多排幾則（超過就丟，見型別說明）。至少要 1。</param>
    /// <param name="mergeWindow">同一個人在這個時間內連打的訊息會合併（<c>Zero</c> = 不合併）。</param>
    /// <param name="merge">怎麼把兩則合成一則（不合併時可以不給）。</param>
    public ChatQueue(int maxDepth, TimeSpan mergeWindow, Func<T, T, T>? merge = null)
    {
        _maxDepth = Math.Max(1, maxDepth);
        _mergeWindow = mergeWindow;
        _merge = merge;
    }

    /// <summary>目前排隊中幾則（不含正在處理的那一則）。</summary>
    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public ChatQueueOutcome Enqueue(ChatQueueItem<T> item)
    {
        var outcome = ChatQueueOutcome.Added;
        var wake = false;

        lock (_gate)
        {
            if (_merge is not null && _mergeWindow > TimeSpan.Zero && _items.Count > 0)
            {
                var last = _items[^1];

                // 同一個人、合併窗內、而且「有沒有在對 Bot 說話」一致才合併 ——
                // 把「@ 它的問題」跟「旁邊的閒聊」混成一題會讓回答對象錯亂。
                if (last.AuthorId == item.AuthorId
                    && last.Addressed == item.Addressed
                    && item.At - last.At <= _mergeWindow)
                {
                    _items[^1] = last with
                    {
                        Payload = _merge(last.Payload, item.Payload),
                        At = item.At,
                        Merged = last.Merged + 1
                    };

                    return ChatQueueOutcome.Merged;
                }
            }

            if (_items.Count >= _maxDepth)
            {
                // 先丟「不是在對它說話」的那一則（閒聊）；都滿了才丟最早的
                var victim = _items.FindIndex(i => !i.Addressed);
                if (victim < 0) victim = 0;

                _items.RemoveAt(victim);
                outcome = ChatQueueOutcome.Dropped;
            }

            wake = _items.Count == 0;
            _items.Add(item);
        }

        // 只有「本來是空的」才需要叫醒工人（其他情況工人不是正在忙、就是已經被叫醒過）
        if (wake) _signal.Release();

        return outcome;
    }

    /// <summary>等下一則（<paramref name="timeout"/> 內沒東西就回 false，讓工人可以收工）。</summary>
    public bool WaitForWork(TimeSpan timeout) => _signal.Wait(timeout);

    public bool TryDequeue(out ChatQueueItem<T> item)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                item = null!;
                return false;
            }

            item = _items[0];
            _items.RemoveAt(0);
            return true;
        }
    }

    /// <summary>看第一則（不拿掉）——工人判斷「要不要收工」時用。</summary>
    public bool HasWork
    {
        get { lock (_gate) return _items.Count > 0; }
    }

    /// <summary>放掉內部的號誌（關機時用）。</summary>
    public void Dispose() => _signal.Dispose();
}

/// <summary>每一則訊息的來源（log 用；避免把整包 Discord 物件帶進 Core）。</summary>
public sealed record ChatQueueLog(string Channel, string Author, string Preview);
