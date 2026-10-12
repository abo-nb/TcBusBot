namespace TcBusBot.Core.Chat;

/// <summary>
/// 「把一組 <see cref="LlmOptions"/> 變成一條真的連線」的**唯一地方**。
///
/// 為什麼要從 Discord 那一層搬到 Core：會建立連線的時機不只一個了 ——
///   1. 開機（環境變數／資料庫裡的覆寫）
///   2. 管理員在 Discord 上用 `/ai llm` 換金鑰或端點（見 <see cref="ReloadableLlmClient"/>）
///
/// 兩條路徑如果各寫一份，就會出現「開機能用、換完金鑰之後少建了判斷模型那條連線」
/// 這種只在特定順序下才會出現的差異。所以只有這一份。
///
/// ⚠️ **不丟例外**：設定錯、連不上都只回一句說明，讓公車功能照常運作
///    （AI 是額外功能，不該拖垮主要功能）。
/// </summary>
public static class LlmClientFactory
{
    /// <summary>
    /// 建立（或重建）LLM 客戶端。回傳的 <c>Client</c> 永遠不是 null ——
    /// 沒設定時是 <see cref="DisabledLlmClient"/>（容器裡一定要有這個服務，
    /// 不然 Discord.Net 會連模組都建不出來，整個 Bot 起不來）。
    /// </summary>
    public static (ILlmClient Client, string Message) Create(
        LlmOptions options, LlmDiagnostics? diagnostics = null, ReasoningCapture? capture = null)
    {
        if (!options.IsConfigured)
            return (DisabledLlmClient.Instance, "⏸️  AI 聊天未啟用（沒有設定 LLM_API_KEY）");

        // 判斷用的模型：只換 Model（其餘設定照舊，包含思考關閉與逾時）
        var judgeOptions = options.HasSeparateJudgeModel ? options.Clone() : null;

        if (judgeOptions is not null) judgeOptions.Model = options.JudgeModel!;

        // 每週摘要那條連線：換思考強度（預設 high）與可選的模型。
        // 為什麼要另建一條：思考是在**建立連線**時決定的（ReasoningHandler 掛在 HttpClient 上），
        // 不能在單一請求裡切換 —— 理由跟「換模型」完全一樣（見 RoutingLlmClient 的說明）。
        var digestOptions = options.Clone();
        digestOptions.Reasoning = options.Digest.Reasoning;

        if (!string.IsNullOrWhiteSpace(options.Digest.Model)) digestOptions.Model = options.Digest.Model!;

        // 設定跟主連線一模一樣時就不必多建一條（設 `LLM_DIGEST_REASONING` 跟 `LLM_REASONING` 相同）
        var needsDigestConnection = options.Digest.Enabled && options.Digest.NeedsOwnConnection(options);

        var (client, message) = RoutingLlmClient.Create(options, judgeOptions, opts =>
        {
            var (created, createdMessage) = SemanticKernelLlmClient.Create(opts, capture);
            return ((ILlmClient?)created, createdMessage);
        }, diagnostics, needsDigestConnection ? digestOptions : null);

        var note = options.Digest.Enabled && !needsDigestConnection
            ? $"\nℹ️  每週摘要與聊天共用同一條連線（思考設定相同：{options.Digest.Reasoning}）"
            : "";

        return (client, message + note);
    }
}
