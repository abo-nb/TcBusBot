namespace TcBusBot.Core.Chat;

/// <summary>
/// LLM 聊天需要「跨重啟保留」的極少量狀態（目前只有每週 token 用量）。
///
/// 為什麼獨立成一個介面：核心的聊天邏輯不該知道資料是存在 SQLite、MongoDB
/// 還是文字檔 —— 那些都由 <c>SavedGroupStore</c>（同一條後端鏈）負責。
/// 測試時傳 null 就是「只用記憶體」。
/// </summary>
public interface ILlmStateStore
{
    string? GetBlob(string key);

    void SetBlob(string key, string json);
}
