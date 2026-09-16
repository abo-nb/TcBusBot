namespace TcBusBot.Core.Chat;

/// <summary>
/// 「沒有啟用 AI 聊天」時用的空物件。
///
/// 為什麼需要它（**這是踩過的坑**）：Discord.Net 建立模組實例時，
/// 會挑「參數最多」的建構子，而且要求**每一個參數型別都解析得到**。
/// 如果 <see cref="ILlmClient"/> 在容器裡不存在，整個模組就建不起來 ——
/// 結果不是「AI 功能不能用」，而是**Bot 連啟動都失敗**（`/ai` 指令全部消失）。
///
/// 所以不論有沒有設定金鑰，容器裡一定要有 <see cref="ILlmClient"/>：
/// 沒設定時就是這一個，它會誠實說自己沒啟用，而且真的被呼叫到就丟例外。
/// （這個 bug 是 `--dryrun` 的指令樹檢查抓到的，不是上線後才發現。）
/// </summary>
public sealed class DisabledLlmClient : ILlmClient
{
    public static readonly DisabledLlmClient Instance = new();

    public bool IsConfigured => false;

    public string Describe() => "未啟用（沒有 LLM_API_KEY）";

    public Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        => throw new LlmException("AI 聊天未啟用（主機沒有設定 LLM_API_KEY）");
}
