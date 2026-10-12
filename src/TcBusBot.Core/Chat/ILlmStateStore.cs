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

    /// <summary>
    /// 這個後端寫進去的東西**重啟後還在不在**（記憶體後端 ＝ false）。
    ///
    /// 為什麼介面要有這個：管理員在 Discord 上換的金鑰能不能留著，
    /// 完全取決於這台主機選到哪一種儲存（MongoDB／SQLite／文字檔／記憶體）——
    /// 而「以為存起來了、其實重啟就消失」是這個功能最糟的失敗方式
    /// （症狀是「換完金鑰過幾天又開始 401」，而且完全查不出原因）。
    /// 預設 true（大部分實作都是真的寫進去），記憶體後端自己覆寫成 false。
    /// </summary>
    bool IsPersistent => true;
}
