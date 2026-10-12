namespace TcBusBot.Core.Chat;

/// <summary>
/// 一個「永遠存在、裡面可以被換掉」的 LLM 客戶端。
///
/// ── 為什麼不能只是「重新 new 一個」────────────────────────
/// 連線是在**建立物件**時決定的（模型名稱、思考強度、逾時都綁在 HttpClient 上，
/// 見 <see cref="RoutingLlmClient"/> 的說明）。所以「換金鑰／換端點／換模型」
/// 一定要重建客戶端 —— 但**用它的那些物件**（聊天服務、摘要服務、指令模組）
/// 都是在開機時就拿到 <see cref="ILlmClient"/> 的單例，重啟前不會再解析一次。
///
/// 這個類別就是那個「轉接頭」：外面看到的是同一顆物件（所以不必重啟、不必改 DI），
/// 裡面那一顆在 <see cref="Reload"/> 時整批換新。
///
/// ⚠️ 三件一定要做對的事：
///   1. **換的當下不能丟掉還在用的舊連線** —— 舊的延後 <see cref="GraceDelay"/> 秒才處置，
///      否則「換設定的同時有人在問問題」會讓那一則對話莫名失敗
///   2. 換的動作要**看得出來**（<see cref="ReloadCount"/>／<see cref="LastReloadMessage"/>）——
///      不然「設定換了嗎」只能靠猜
///   3. 沒有設定時裡面是 <see cref="DisabledLlmClient"/>，<see cref="IsConfigured"/> 要跟著變 ——
///      這樣「開機沒有金鑰、後來在 Discord 補上」也能真的生效
/// </summary>
public sealed class ReloadableLlmClient : ILlmClient, IDisposable
{
    private readonly LlmOptions _options;
    private readonly Func<LlmOptions, (ILlmClient Client, string Message)> _factory;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private ILlmClient _current;

    /// <summary>換掉之後要做的事（參數是這次的說明訊息）——例如「順便啟動每週摘要排程器」。</summary>
    public event Action<string>? Reloaded;

    /// <summary>舊連線延後多久才處置（讓進行中的請求先跑完）。</summary>
    public TimeSpan GraceDelay { get; init; } = TimeSpan.FromSeconds(30);

    public ReloadableLlmClient(
        LlmOptions options,
        LlmDiagnostics? diagnostics = null,
        ReasoningCapture? capture = null,
        Func<LlmOptions, (ILlmClient Client, string Message)>? factory = null,
        Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;

        // 測試可以塞假的 factory（不必真的建 Semantic Kernel／HttpClient）
        _factory = factory ?? (opts => LlmClientFactory.Create(opts, diagnostics, capture));

        _current = _factory(_options).Client;
    }

    /// <summary>目前生效的那一個客戶端（測試與診斷用）。</summary>
    public ILlmClient Current => Volatile.Read(ref _current);

    /// <summary>重建過幾次（`/ai status` 與 `/health` 看得到 —— 「有沒有換成功」的證據）。</summary>
    public int ReloadCount { get; private set; }

    /// <summary>最後一次重建的說明（成功或失敗的原話）。</summary>
    public string LastReloadMessage { get; private set; } = "";

    public bool IsConfigured => Current.IsConfigured;

    public string Describe() => Current.Describe();

    public Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        => Current.CompleteAsync(request, cancellationToken);

    /// <summary>
    /// 依**目前的** <see cref="LlmOptions"/> 重建連線，回傳這次的說明訊息。
    ///
    /// 回傳值**不代表成功** —— 跟 <see cref="LlmClientFactory.Create"/> 一樣，
    /// 建不起來時回的是「沒啟用」的訊息與 <see cref="DisabledLlmClient"/>。
    /// 呼叫端（`/ai llm`）要靠接著打一次真的測試才知道設定對不對。
    /// </summary>
    public string Reload()
    {
        ILlmClient? old;

        lock (_gate)
        {
            var (created, message) = _factory(_options);

            old = _current;
            _current = created;        // 先換：這一刻之後的新請求都走新連線
            ReloadCount++;
            LastReloadMessage = message;

            _log?.Invoke(message);

            // 進行中的請求拿到的還是舊實例，所以要等它跑完才處置
            Retire(old, created);
        }

        Reloaded?.Invoke(LastReloadMessage);

        return LastReloadMessage;
    }

    /// <summary>舊連線的處置（同一顆就不動；延後 <see cref="GraceDelay"/>）。</summary>
    private void Retire(ILlmClient old, ILlmClient created)
    {
        if (ReferenceEquals(old, created) || old is not IDisposable disposable
            || ReferenceEquals(old, DisabledLlmClient.Instance))
            return;

        if (GraceDelay <= TimeSpan.Zero)
        {
            disposable.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(GraceDelay).ConfigureAwait(false);
                disposable.Dispose();
            }
            catch (Exception)
            {
                // 處置失敗不該影響任何事（最差情況就是那個 HttpClient 等到行程結束才被回收）
            }
        });
    }

    public void Dispose()
    {
        ILlmClient current;

        lock (_gate)
        {
            current = _current;
            _current = DisabledLlmClient.Instance;
        }

        if (current is IDisposable disposable && !ReferenceEquals(current, DisabledLlmClient.Instance))
            disposable.Dispose();
    }
}
