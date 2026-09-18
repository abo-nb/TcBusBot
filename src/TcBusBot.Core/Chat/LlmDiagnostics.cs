using System.Collections.Concurrent;

namespace TcBusBot.Core.Chat;

/// <summary>一次呼叫的紀錄（成功或失敗）。</summary>
public sealed record LlmCallRecord(
    DateTimeOffset At,
    string? Tag,
    string Model,
    bool Ok,
    int InputTokens,
    int OutputTokens,
    TimeSpan Elapsed,
    string? Error)
{
    public string Describe()
        => Ok
            ? $"{At.ToLocalTime():MM-dd HH:mm:ss}　{Model}｜in {InputTokens} / out {OutputTokens}" +
              $"｜{Elapsed.TotalSeconds:0.0}s" + (Tag is null ? "" : $"｜{Tag}")
            : $"{At.ToLocalTime():MM-dd HH:mm:ss}　❌ 失敗" + (Tag is null ? "" : $"（{Tag}）") + $"：{Error}";
}

/// <summary>
/// 「LLM 到底有沒有接上」的可觀測性。
///
/// ── 為什麼需要它 ────────────────────────────────────────
/// 使用者回報「LLM 似乎沒有接上」時，最難的地方是**看不出來發生什麼事**：
///   * Discord 上只看到「它沒回我」
///   * console 的 log 在別台機器（Render）上，還要登進去翻
///   * `/ai status` 只寫「已回幾則、失敗幾次」，沒有「最後一次失敗的訊息是什麼」
/// 於是把「最近幾次呼叫的結果」留下來，讓 `/ai status` 直接講出來：
/// 是金鑰無效（401）、餘額不足（402）、逾時、還是根本沒被叫到。
///
/// 只留最近 <see cref="MaxEntries"/> 筆（記憶體、重啟就清空）—— 這是診斷用的，
/// 不是稽核紀錄（稽核請看 <see cref="GuildPersonaStore"/> 的主人操作紀錄）。
/// </summary>
public sealed class LlmDiagnostics
{
    public const int MaxEntries = 20;

    private readonly ConcurrentQueue<LlmCallRecord> _records = new();

    /// <summary>記一筆（成功或失敗）。</summary>
    public void Record(LlmRequest request, LlmReply? reply, Exception? error, TimeSpan elapsed)
    {
        var record = error is null && reply is not null
            ? new LlmCallRecord(DateTimeOffset.UtcNow, request.Tag, reply.Model, true,
                reply.InputTokens, reply.OutputTokens, elapsed, null)
            : new LlmCallRecord(DateTimeOffset.UtcNow, request.Tag, "（失敗）", false,
                request.EstimatedInputTokens, 0, elapsed, Shorten(error?.Message));

        _records.Enqueue(record);

        while (_records.Count > MaxEntries) _records.TryDequeue(out _);
    }

    /// <summary>最近幾筆（新的在前）。</summary>
    public IReadOnlyList<LlmCallRecord> Recent(int limit = 5)
        => _records.Reverse().Take(Math.Max(1, limit)).ToList();

    /// <summary>最後一次呼叫（不管成功或失敗）。</summary>
    public LlmCallRecord? Last => _records.LastOrDefault();

    /// <summary>最後一次**失敗**（沒有失敗過就是 null）。</summary>
    public LlmCallRecord? LastFailure => _records.LastOrDefault(r => !r.Ok);

    public int Total => _records.Count;

    public int FailureCount => _records.Count(r => !r.Ok);

    /// <summary>
    /// 給 `/ai status` 用的一行說明 —— 沒有紀錄時也要講得清楚（「還沒有人問過它」）。
    /// </summary>
    public string Describe()
    {
        if (_records.IsEmpty) return "還沒有任何呼叫紀錄（還沒有人問過它，或剛重啟）";

        var last = Last!;
        var text = $"最後一次：{last.Describe()}";

        if (!last.Ok)
        {
            // 最後一次就失敗時，上面那一行已經把錯誤講完了 —— 不要讓使用者以為「還有別的失敗」
            text += "\n（上面那一筆就是失敗的內容 —— 「接不上」的線索就在那裡）";
        }
        else if (LastFailure is { } failure)
        {
            text += $"\n最後一次失敗：{failure.Describe()}";
        }

        if (FailureCount > 0 && _records.Count > 1)
            text += $"\n（最近 {_records.Count} 筆裡有 {FailureCount} 筆失敗）";

        return text;
    }

    private static string? Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;

        var flat = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }
}
