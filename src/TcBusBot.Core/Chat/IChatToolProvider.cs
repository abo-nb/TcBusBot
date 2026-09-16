using Microsoft.SemanticKernel;

namespace TcBusBot.Core.Chat;

/// <summary>這一次互動的上下文 —— 工具要知道「誰在問、在哪裡問」才能動手。</summary>
public sealed record ChatToolContext(
    ulong GuildId,
    ulong ChannelId,
    ulong UserId,
    string UserName)
{
    /// <summary>私訊（沒有伺服器）。</summary>
    public bool IsDirectMessage => GuildId == 0;

    public string Describe()
        => $"{(IsDirectMessage ? "私訊" : $"伺服器 {GuildId}")}／頻道 {ChannelId}／{UserName}（{UserId}）";
}

/// <summary>
/// 這一次提問中，模型實際呼叫了哪些工具。
///
/// 為什麼要記：使用者看到的是模型的「心得」，但**模型可能講得比做得多**。
/// 有了這份紀錄，Bot 才能誠實地在旁邊附上「🔧 我實際做了：訂閱 300/304…」，
/// log 也才看得出「這則回覆是不是真的有動到資料」。
/// </summary>
public sealed class ToolCallLog
{
    private readonly List<string> _calls = new();
    private int _changedState;

    public IReadOnlyList<string> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    /// <summary>工具有沒有動到使用者的資料（建立／取消訂閱…）。</summary>
    public bool ChangedState => Volatile.Read(ref _changedState) != 0;

    public void Record(string tool, string detail, bool changedState = false)
    {
        lock (_calls) _calls.Add(detail.Length == 0 ? tool : $"{tool}（{detail}）");

        if (changedState) Interlocked.Exchange(ref _changedState, 1);
    }
}

/// <summary>
/// 每次提問要帶哪些工具給模型。
///
/// 為什麼是「每次提問現做」而不是註冊在 kernel 上：工具的實作綁著
/// **當下的使用者與頻道**（訂閱是幫「這個人」訂的），共用同一份會出事。
/// </summary>
public interface IChatToolProvider
{
    IReadOnlyList<KernelPlugin> CreateFor(ChatToolContext context, ToolCallLog log);

    /// <summary>給 log 與 `/ai status` 看的說明（例如「公車工具 5 個」）。</summary>
    string Describe();
}

/// <summary>沒有任何工具（`LLM_TOOLS=false` 或還沒接上）。</summary>
public sealed class NoChatTools : IChatToolProvider
{
    public static readonly NoChatTools Instance = new();

    public IReadOnlyList<KernelPlugin> CreateFor(ChatToolContext context, ToolCallLog log) => [];

    public string Describe() => "未啟用";
}
