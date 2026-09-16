namespace TcBusBot.Core.Chat;

/// <summary>
/// 粗估 token 數。
///
/// 為什麼需要估：真實用量只有 API 回應裡才有（呼叫完才知道），
/// 但「這一次呼叫會不會超出每週上限」必須**送出去之前**就知道。
/// 所以估算只用來做兩件事：事前擋掉會超額的呼叫、以及 API 沒回報用量時的後備數字。
///
/// 估法（刻意簡單，誤差在這個用途可以接受）：
///   * CJK 字元 ≈ 1 token（BPE 對中文大約一字一 token）
///   * 其他字元 ≈ 4 個字 1 token
///   * 每則訊息再加一點固定開銷（角色、分隔符）
/// 實測對照：系統提示 47 tokens 的內容，這裡估出來也在同一個量級。
/// </summary>
public static class TokenEstimator
{
    /// <summary>每則訊息的固定開銷（role / 分隔符）。</summary>
    public const int PerMessageOverhead = 4;

    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var cjk = 0;
        var other = 0;

        foreach (var ch in text)
        {
            if (IsCjk(ch)) cjk++;
            else other++;
        }

        // 非 CJK 部分 4 個字約 1 token（無條件進位，寧可高估）
        return cjk + (other + 3) / 4;
    }

    /// <summary>估算一整串訊息（含每則的固定開銷）。</summary>
    public static int Estimate(IEnumerable<string?> parts)
    {
        var total = 0;
        foreach (var part in parts) total += Estimate(part) + PerMessageOverhead;
        return total;
    }

    /// <summary>估算一段對話（含作者名稱，因為提示詞裡真的會帶）。</summary>
    public static int Estimate(IEnumerable<ChatTurn> turns)
        => Estimate(turns.Select(t => t.ToPromptText()));

    public static bool IsCjk(char ch)
        => ch is >= '\u3000' and <= '\u303F'      // 中文標點
           or >= '\u3040' and <= '\u30FF'         // 日文假名
           or >= '\u3400' and <= '\u4DBF'         // 罕用漢字
           or >= '\u4E00' and <= '\u9FFF'         // 漢字
           or >= '\uF900' and <= '\uFAFF'         // 相容漢字
           or >= '\uFF00' and <= '\uFFEF'         // 全形
           or >= '\uD840' and <= '\uD87F';        // 增補漢字（surrogate 前半）
}

/// <summary>把歷史裁到「可以送出去」的大小。</summary>
public static class ContextBuilder
{
    public sealed record TrimResult(
        IReadOnlyList<ChatTurn> Turns,
        int DroppedByCount,
        int EstimatedTokens,
        bool KeptReplyTarget);

    /// <summary>
    /// 從**最新**往回留，留到數量或估算 token 上限為止。
    ///
    /// 被回覆的那一則一定留著：使用者回覆它就是在說「我要接著這個講」，
    /// 把它裁掉會讓 LLM 完全看不懂在講什麼。
    /// </summary>
    public static TrimResult Trim(
        IReadOnlyList<ChatTurn> context, LlmOptions options, ChatTurn? mustKeep = null)
    {
        var dropped = 0;
        var kept = new List<ChatTurn>();

        // 1) 數量上限
        var start = Math.Max(0, context.Count - Math.Max(1, options.MaxContextTurns));
        dropped += start;
        kept.AddRange(context.Skip(start));

        // 2) token 上限（從最舊的開始丟，但至少留一則）
        while (kept.Count > 1
               && TokenEstimator.Estimate(kept) > Math.Max(64, options.MaxContextTokens))
        {
            kept.RemoveAt(0);
            dropped++;
        }

        // 3) 被回覆的那一則一定要在
        var keptReplyTarget = false;

        if (mustKeep is { } target)
        {
            if (kept.Any(t => t.MessageId == target.MessageId))
            {
                keptReplyTarget = true;
            }
            else
            {
                kept.Insert(0, target);
                keptReplyTarget = true;
            }
        }

        return new TrimResult(kept, dropped, TokenEstimator.Estimate(kept), keptReplyTarget);
    }
}
