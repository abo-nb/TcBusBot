using TcBusBot.Core.Storage;

namespace TcBusBot.Core.Subscriptions;

/// <summary>
/// 一次「可以復原的動作」。
///
/// 為什麼要做這個：合併、批次套用、批次刪除都是**一次影響很多東西**的操作，
/// 點錯一下就要全部重來。所以每個動作都先把自己「怎麼還原」記下來，
/// 使用者按一下「↩️ 復原」就回到上一步。
///
/// 復原分成兩個世界：
///   * **訂閱**（記憶體）→ 把剛才建立的群組移除
///   * **訂閱組**（SQLite）→ 刪掉新建的／還原被覆蓋的／把刪掉的重存回去／改回舊名字
///
/// ⚠️ 復原紀錄放在記憶體（跟著使用者的面板 session），**Bot 重啟後就不能復原了** ——
/// 這與「訂閱本身重啟就消失」是同一個已知限制。
/// </summary>
public abstract record UndoEntry(string Description)
{
    /// <summary>執行復原，回傳「實際做了什麼」給使用者看。</summary>
    public abstract string Undo(SubscriptionService subs, SavedGroupStore store, ulong userId);
}

/// <summary>復原「批次套用訂閱組」→ 把剛才建立的訂閱群組全部移除。</summary>
public sealed record UndoAppliedSubscriptions(
    string Description,
    IReadOnlyList<string> CreatedGroupIds,
    string? PreviousSessionGroupId) : UndoEntry(Description)
{
    public override string Undo(SubscriptionService subs, SavedGroupStore store, ulong userId)
    {
        var removed = CreatedGroupIds.Count(subs.RemoveGroup);

        return removed == 0
            ? "剛才套用的訂閱已經不在了（可能已經被取消）。"
            : $"已取消剛才套用的 {removed} 個訂閱群組（連同其中的訂閱）。";
    }
}

/// <summary>
/// 復原「合併成新組」→ 刪掉合併出來的新組；
/// 如果合併時**覆蓋了同名**的舊組，就把舊內容還原回去。
/// </summary>
public sealed record UndoMergedSavedGroup(
    string Description,
    long NewGroupId,
    string Name,
    SavedGroupPayload? Overwritten) : UndoEntry(Description)
{
    public override string Undo(SubscriptionService subs, SavedGroupStore store, ulong userId)
    {
        if (Overwritten is { } previous)
        {
            // 同名覆蓋 → 把合併前的內容寫回去（id 不變，因為 Save 是同名更新）
            var (result, _) = store.Save(userId, Name, previous);
            return result is SaveGroupResult.Created or SaveGroupResult.Updated
                ? $"已把「{Name}」還原成合併前的內容。"
                : $"還原「{Name}」失敗（{result}）。";
        }

        return store.Delete(NewGroupId, userId)
            ? $"已刪除剛才合併出來的「{Name}」。"
            : $"剛才合併出來的「{Name}」已經不在了。";
    }
}

/// <summary>復原「刪除訂閱組」→ 把刪掉的內容重新存回去。</summary>
public sealed record UndoDeletedSavedGroups(
    string Description,
    IReadOnlyList<UndoDeletedSavedGroups.Snapshot> Deleted) : UndoEntry(Description)
{
    /// <summary>刪除當下先把內容抄下來，才有東西可以還原。</summary>
    public sealed record Snapshot(string Name, SavedGroupPayload Payload);

    public override string Undo(SubscriptionService subs, SavedGroupStore store, ulong userId)
    {
        var restored = 0;
        var failed = new List<string>();

        foreach (var s in Deleted)
        {
            var (result, message) = store.Save(userId, s.Name, s.Payload);
            if (result is SaveGroupResult.Created or SaveGroupResult.Updated) restored++;
            else failed.Add($"{s.Name}（{message ?? result.ToString()}）");
        }

        if (restored == 0)
            return $"還原失敗：{string.Join("、", failed)}";

        return failed.Count == 0
            ? $"已還原 {restored} 個訂閱組（內容與刪除前相同）。"
            : $"已還原 {restored} 個訂閱組，但 {string.Join("、", failed)} 失敗。";
    }
}

/// <summary>復原「改名」→ 改回原來的名字。</summary>
public sealed record UndoRenamedSavedGroup(
    string Description,
    long GroupId,
    string OldName) : UndoEntry(Description)
{
    public override string Undo(SubscriptionService subs, SavedGroupStore store, ulong userId)
        => store.Rename(GroupId, userId, OldName)
            ? $"已把名稱改回「{OldName}」。"
            : $"改回「{OldName}」失敗（可能已經有同名的組）。";
}

/// <summary>
/// 每個面板 session 一份的復原堆疊（後進先出）。
///
/// 刻意保留不只一層：使用者常常是「套用 → 發現多套一個 → 再套用」，
/// 能連按兩次復原比只有一層實用。
/// </summary>
public sealed class UndoStack
{
    public const int MaxDepth = 10;

    private readonly List<UndoEntry> _entries = new();
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public void Push(UndoEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);

            // 只留最近的幾筆，避免長時間掛機後無限成長
            while (_entries.Count > MaxDepth) _entries.RemoveAt(0);
        }
    }

    public UndoEntry? Peek()
    {
        lock (_gate) return _entries.Count == 0 ? null : _entries[^1];
    }

    /// <summary>取出上一筆並執行復原。</summary>
    public string Undo(SubscriptionService subs, SavedGroupStore store, ulong userId)
    {
        UndoEntry? entry;
        lock (_gate)
        {
            if (_entries.Count == 0) return "沒有可以復原的動作了。";
            entry = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
        }

        return entry.Undo(subs, store, userId);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }
}
