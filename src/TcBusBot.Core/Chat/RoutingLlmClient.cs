namespace TcBusBot.Core.Chat;

/// <summary>
/// 把「極短判斷」導到另一個模型（通常是便宜的小模型），其他一律走主模型。
///
/// ── 為什麼需要這一層（而不是在請求裡塞模型名稱）────────────────
/// 直覺做法是設定 <c>OpenAIPromptExecutionSettings.ModelId</c>，但
/// **Semantic Kernel 這個版本會忽略它** —— 實測（用一個故意不存在的模型名稱：
/// `definitely-not-a-real-model-xyz`）請求**照樣成功**，回來的 `model` 還是主模型。
/// 這跟 `ExtensionData` 被忽略是同一類問題（見 <c>ReasoningOffHandler</c> 的註解）：
/// **「設定了某個屬性」不等於「送出去了」**。
///
/// 所以「換模型」只能在**建立連線**時決定 —— 也就是建兩個客戶端，由這一層分流：
///
/// <code>
/// 主模型（聊天、工具呼叫、被 @ 的回答）  ← 全部預設走這裡
/// 判斷模型（是在跟我說話嗎／換話題了沒） ← request.JudgeCall = true 時走這裡
/// </code>
///
/// 沒有設定判斷模型時（<see cref="LlmOptions.HasSeparateJudgeModel"/> 為 false），
/// 這一層根本不會被建立（永遠只有主模型），所以「沒設定」＝零額外成本。
/// </summary>
public sealed class RoutingLlmClient : ILlmClient
{
    private readonly ILlmClient _main;
    private readonly ILlmClient? _judge;
    private readonly LlmDiagnostics? _diagnostics;

    public RoutingLlmClient(ILlmClient main, ILlmClient? judge, LlmDiagnostics? diagnostics = null)
    {
        _main = main;
        _judge = judge;
        _diagnostics = diagnostics;
    }

    /// <summary>有幾個模型可以用（1 = 只有主模型）。</summary>
    public int ModelCount => _judge is null ? 1 : 2;

    public bool IsConfigured => _main.IsConfigured;

    /// <summary>這次會用哪一個客戶端（測試與 log 用）。</summary>
    public ILlmClient For(LlmRequest request)
        => request.JudgeCall && _judge is not null ? _judge : _main;

    public string Describe()
        => _judge is null
            ? _main.Describe()
            : $"{_main.Describe()}｜短判斷走：{_judge.Describe()}";

    public Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        => CompleteAndRecordAsync(request, cancellationToken);

    /// <summary>
    /// 呼叫 ＋ 記一筆診斷（成功或失敗都記）。
    ///
    /// 為什麼記在這裡而不是各個偵測器：這一層是**所有**呼叫的必經之路
    /// （聊天、工具、短判斷都走它），所以在這裡記就一定記得到 ——
    /// 「LLM 到底有沒有接上」才回答得出來。
    /// </summary>
    private async Task<LlmReply> CompleteAndRecordAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;

        try
        {
            var reply = await For(request).CompleteAsync(request, cancellationToken);
            _diagnostics?.Record(request, reply, null, DateTimeOffset.UtcNow - started);
            return reply;
        }
        catch (Exception ex)
        {
            // ⚠️ 例外一定要原樣往上丟（呼叫端靠它決定「先不出聲」或回一句錯誤），
            //    這裡只多做一件事：把訊息留下來給 `/ai status` 看。
            _diagnostics?.Record(request, null, ex, DateTimeOffset.UtcNow - started);
            throw;
        }
    }

    /// <summary>
    /// 需要幾個模型就建幾個：<paramref name="judgeOptions"/> 為 null（或主設定沒有另外指定判斷模型）時
    /// 只建一個，回傳的 <see cref="ModelCount"/> 會是 1。
    ///
    /// ⚠️ 判斷「要不要建第二個」看的是 **<paramref name="mainOptions"/>**，不是
    /// <paramref name="judgeOptions"/>：後者是把 <c>Model</c> 換成判斷模型之後的複本，
    /// 那時它自己的 <see cref="LlmOptions.HasSeparateJudgeModel"/> 一定是 false
    /// （Model == JudgeModel），拿它來判斷會變成「永遠只有一個模型」——
    /// 這個錯真的發生過，而且只有**真的打一次 API** 才看得出來（設定看起來完全正常）。
    /// </summary>
    public static (RoutingLlmClient Client, string Message) Create(
        LlmOptions mainOptions, LlmOptions? judgeOptions, Func<LlmOptions, (ILlmClient?, string)> factory,
        LlmDiagnostics? diagnostics = null)
    {
        var (main, mainMessage) = factory(mainOptions);

        if (main is null) return (new RoutingLlmClient(DisabledLlmClient.Instance, null, diagnostics), mainMessage);

        if (judgeOptions is null || !mainOptions.HasSeparateJudgeModel)
            return (new RoutingLlmClient(main, null, diagnostics), mainMessage);

        var (judge, judgeMessage) = factory(judgeOptions);

        if (judge is null)
        {
            // 判斷模型建不起來**不可以**拖垮整個 AI 功能 —— 退回只有主模型
            return (new RoutingLlmClient(main, null, diagnostics),
                $"{mainMessage}\n⚠️  判斷用的模型（{judgeOptions.JudgeModel}）建立失敗 → 短判斷改用主模型：" +
                judgeMessage);
        }

        return (new RoutingLlmClient(main, judge, diagnostics),
            $"{mainMessage}\n✅ 短判斷（是在跟我說話嗎／換話題了沒）走另一個模型：{judge.Describe()}");
    }
}
