namespace TcBusBot.Core.Chat;

/// <summary>一次呼叫抓到的「思考內容」（模型在回答之前想的那一段）。</summary>
public sealed record ReasoningEntry(
    DateTimeOffset At,
    string Tag,
    string Model,
    string Text,
    int ReasoningTokens,
    TimeSpan Elapsed,
    int AnswerChars)
{
    /// <summary>有抓到思考原文嗎（有些模型／設定只回 token 數，不回原文）。</summary>
    public bool HasText => Text.Length > 0;

    public string Describe()
        => $"{At.ToLocalTime():MM-dd HH:mm:ss}　{Tag}｜{Model}｜想了 {Elapsed.TotalSeconds:0.0} 秒" +
           (ReasoningTokens > 0 ? $"／{ReasoningTokens:N0} 思考 tokens" : "") +
           $"｜回覆 {AnswerChars} 字";
}

/// <summary>
/// 「他在想什麼」的暫存區（**只留最近幾筆，在記憶體裡**）。
///
/// ── 為什麼要抓 HTTP 回應而不是讀 SK 的欄位 ───────────────────
/// 跟「送不出去」是同一個問題的鏡像：Semantic Kernel 這個版本**不保證**會把
/// 非標準欄位（`reasoning_content`）映射到它自己的型別上 —— 而那是「有沒有東西可看」
/// 的唯一來源。所以我們在**送出的 JSON 補欄位**（<see cref="ReasoningHandler"/>）
/// 的同一層，順手把**回應的 JSON** 也讀一遍，把思考原文留下來。
///
/// 這裡刻意只做「最近 20 筆、每筆最多 4000 字」：思考內容是**暫時的觀察資料**，
/// 不是設定也不是紀錄 —— 不該無限長大，也不需要跨重啟保留
/// （要長期保留的是摘要本身，見 <c>DigestStore</c>）。
///
/// ⚠️ 「哪一筆思考屬於哪一次呼叫」用 <see cref="AsyncLocal{T}"/> 綁定：
///    HTTP handler 不知道這次呼叫是聊天還是摘要，但它跑在**同一個非同步脈絡**裡，
///    所以 <see cref="Begin"/> 設好的位置它讀得到 —— 多個頻道同時聊天也不會互相蓋掉。
/// </summary>
public sealed class ReasoningCapture
{
    /// <summary>最多留幾筆。</summary>
    public const int Capacity = 20;

    /// <summary>每筆最多留幾個字（思考可能很長，但看的人是為了知道「它怎麼想」）。</summary>
    public const int MaxTextLength = 4000;

    private static readonly AsyncLocal<Slot?> Ambient = new();

    private readonly object _gate = new();
    private readonly List<ReasoningEntry> _entries = [];

    /// <summary>累計抓到幾次思考（含只有 token 數、沒有原文的）。</summary>
    public int TotalSeen { get; private set; }

    /// <summary>累計有幾次呼叫完全沒看到思考（模型沒想，或服務商不回）。</summary>
    public int TotalWithoutThinking { get; private set; }

    /// <summary>
    /// 開始一次呼叫：把「這一輪的思考」綁進目前這個非同步脈絡。
    ///
    /// 一定要用 <c>using</c> 包起來 —— 例外路徑也要把環境還原，
    /// 不然後面別的呼叫會把思考寫到別人的格子裡。
    ///
    /// 回傳的 <see cref="Slot"/> 就是「這一輪的思考」本身，呼叫端可以在呼叫結束後
    /// <see cref="Slot.Snapshot"/> 出來（例如塞進 <c>LlmReply</c> 給上層用）。
    /// </summary>
    public static Slot Begin()
    {
        var previous = Ambient.Value;
        var slot = new Slot();

        Ambient.Value = slot;

        return slot.Attach(previous);
    }

    /// <summary>HTTP 層讀到思考內容時呼叫（由 <see cref="ReasoningHandler"/>）。</summary>
    public static void Add(string? text, int reasoningTokens)
    {
        var slot = Ambient.Value;

        if (slot is null) return;

        lock (slot.Gate)
        {
            if (!string.IsNullOrWhiteSpace(text))
                slot.Parts.Add(text.Trim());

            if (reasoningTokens > 0) slot.Tokens += reasoningTokens;
        }
    }

    /// <summary>
    /// 呼叫結束：把這一輪的思考記下來。
    ///
    /// 沒有思考也**要記一筆「沒有」**嗎？不要 —— 那會讓清單被一般回覆塞滿
    /// （預設聊天是不思考的），反而看不到真正想看的那幾筆摘要。
    /// 所以只在「有原文或 token 數」時才記。
    /// </summary>
    public void Commit(string tag, string model, TimeSpan elapsed, int answerChars, DateTimeOffset? at = null)
    {
        var slot = Ambient.Value;

        if (slot is null) return;

        var (text, tokens) = slot.Take();

        lock (_gate)
        {
            if (text.Length == 0 && tokens <= 0)
            {
                TotalWithoutThinking++;
                return;
            }

            TotalSeen++;

            if (text.Length > MaxTextLength) text = text[..MaxTextLength] + "…";

            _entries.Add(new ReasoningEntry(
                at ?? DateTimeOffset.UtcNow, tag, model, text, tokens, elapsed, answerChars));

            while (_entries.Count > Capacity) _entries.RemoveAt(0);
        }
    }

    /// <summary>最近幾筆（新的在前）。</summary>
    public IReadOnlyList<ReasoningEntry> Recent(int count = 1)
    {
        lock (_gate)
            return _entries
                .OrderByDescending(e => e.At)
                .Take(Math.Max(1, count))
                .ToList();
    }

    public ReasoningEntry? Latest
    {
        get { lock (_gate) return _entries.Count == 0 ? null : _entries[^1]; }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    /// <summary>一行說明（`/ai status` 用）。</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
                return TotalWithoutThinking > 0
                    ? $"還沒有抓到思考內容（最近 {TotalWithoutThinking} 次呼叫都沒有思考 —— " +
                      "預設聊天是不思考的，只有每週摘要會思考）"
                    : "還沒有任何呼叫紀錄";

            var latest = _entries[^1];

            return $"最近一次思考：{latest.Tag}（{latest.Elapsed.TotalSeconds:0.0} 秒" +
                   (latest.ReasoningTokens > 0 ? $"／{latest.ReasoningTokens:N0} 思考 tokens" : "") +
                   (latest.HasText ? "，有原文" : "，只有 token 數") +
                   $"）｜已抓到 {TotalSeen} 次｜用 `/ai think` 看內容";
        }
    }

    /// <summary>
    /// 這一輪的思考格子：HTTP handler 把內容寫進來、呼叫端在結束時讀出去。
    ///
    /// <see cref="Dispose"/> 會把「目前脈絡」還原成呼叫前的樣子（支援巢狀呼叫）。
    /// </summary>
    public sealed class Slot : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<string> _parts = [];
        private Slot? _previous;
        private int _tokens;
        private bool _detached;

        internal Slot Attach(Slot? previous)
        {
            _previous = previous;
            return this;
        }

        internal object Gate => _gate;

        internal List<string> Parts => _parts;

        internal int Tokens
        {
            get => _tokens;
            set => _tokens = value;
        }

        /// <summary>目前抓到的思考（原文 ＋ token 數）。**不會清空**（清空是 Dispose 的事）。</summary>
        public (string Text, int Tokens) Snapshot()
        {
            lock (_gate) return (string.Join("\n", _parts).Trim(), _tokens);
        }

        /// <summary>把格子清乾淨（給 <see cref="Commit"/> 用：同一輪不重複記）。</summary>
        internal (string Text, int Tokens) Take()
        {
            lock (_gate)
            {
                var text = string.Join("\n", _parts).Trim();
                var tokens = _tokens;

                _parts.Clear();
                _tokens = 0;

                return (text, tokens);
            }
        }

        public void Dispose()
        {
            if (_detached) return;

            _detached = true;

            // 只有在「我還是目前脈絡」時才還原 —— 巢狀／並行情境下不可以蓋掉別人的格子
            if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = _previous;
        }
    }
}
