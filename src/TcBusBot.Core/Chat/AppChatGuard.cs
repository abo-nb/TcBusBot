using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcBusBot.Core.Chat;

/// <summary>某個頻道的「跟其他 App 對話」狀態（會被持久化）。</summary>
public sealed record AppChatState(
    [property: JsonPropertyName("stopped")] bool Stopped,
    [property: JsonPropertyName("by")] ulong StoppedBy,
    [property: JsonPropertyName("at")] DateTimeOffset? StoppedAt,
    [property: JsonPropertyName("hour")] string HourKey,
    [property: JsonPropertyName("sent")] int SentThisHour,
    /// <summary>
    /// 這個頻道**由管理員啟用**了「跟其他 App 對話」嗎。
    ///
    /// 為什麼是「每個頻道各自啟用」而不是一個環境變數：
    /// 「要不要跟另一個機器人聊」是**那個頻道的事**（別的頻道可能只想看到通知、
    /// 不想被兩個機器人的對話洗版），而且啟用的人應該在那裡看得到成果。
    /// 環境變數只留「沒設定時的預設值」。
    /// </summary>
    [property: JsonPropertyName("enabled")] bool Enabled,
    /// <summary>只跟這一個 App 對話（0 ＝ 任何 App 都可以）。</summary>
    [property: JsonPropertyName("app")] ulong AppId,
    /// <summary>這個頻道自己的每小時上限（0 ＝ 用主機設定的預設值）。</summary>
    [property: JsonPropertyName("cap")] int HourCap,
    [property: JsonPropertyName("enabled_by")] ulong EnabledBy,
    [property: JsonPropertyName("enabled_at")] DateTimeOffset? EnabledAt)
{
    public static AppChatState Empty { get; } = new(false, 0, null, "", 0, false, 0, 0, 0, null);
}

/// <summary>
/// 「跟另一個 App 聊天」的煞車與開關（**持久化**）。
///
/// ── 為什麼需要這個東西 ───────────────────────────────────
/// 兩個都會回話的機器人放進同一個頻道，就會變成
/// 「A 回我 → 我回 A → A 又回我…」的無限迴圈，而每一輪都真的花錢。
/// 所以這條路**一定要有停損**：
///
///   * `/stop`（使用者指定的主要開關）：立刻停，**直到有真人叫他**才恢復
///   * 每小時上限（無人看管的保險）：就算沒有人記得按 `/stop`，
///     也不會在半夜把整週額度燒完
///
/// ⚠️ 狀態**一定要持久化**：正是「沒有人在看」的時候最需要它。
///    如果只放在記憶體，一次重啟（Render 會自己重啟）就會讓迴圈從頭開始。
/// </summary>
public sealed class AppChatGuard
{
    /// <summary>持久化用的鍵名（在共用的 blob 區裡，跟對話記憶分開）。</summary>
    public const string BlobKey = "app_chat_guard";

    /// <summary>最多記幾個頻道（超過就淘汰最舊的）；這是很小的狀態，不該無限長大。</summary>
    public const int MaxChannels = 200;

    private readonly object _gate = new();
    private readonly ILlmStateStore? _store;
    private readonly Dictionary<ulong, AppChatState> _states = [];

    public AppChatGuard(ILlmStateStore? store = null)
    {
        _store = store;

        try
        {
            var json = store?.GetBlob(BlobKey);

            if (!string.IsNullOrWhiteSpace(json))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, AppChatState>>(json!, Json);

                if (parsed is not null)
                {
                    foreach (var (key, value) in parsed)
                    {
                        if (ulong.TryParse(key, out var channelId)) _states[channelId] = value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[App 對話] 讀不到停止狀態（{ex.GetType().Name}）—— 先當成沒有停過");
            _states.Clear();
        }
    }

    /// <summary>這個頻道現在可以跟其他 App 對話嗎（回 null ＝ 可以，回字串 ＝ 原因）。</summary>
    public string? BlockReason(ulong channelId, int hourlyCap, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(channelId, out var state)) return null;

            if (state.Stopped)
                return $"已經用 `/stop` 停下來了（{state.StoppedAt?.ToLocalTime():MM-dd HH:mm}）——" +
                       "要有真人 @ 它才會恢復";

            if (hourlyCap <= 0) return null;

            var hour = HourKey(now);

            // 換一個小時 → 計數歸零（不必等人來按什麼）
            if (state.HourKey != hour) return null;

            return state.SentThisHour >= hourlyCap
                ? $"這個小時跟其他 App 講了 {state.SentThisHour} 句（上限 {hourlyCap}）——" +
                  "先停到下一小時（要調整設 LLM_APP_CHAT_PER_HOUR）"
                : null;
        }
    }

    /// <summary>`/stop`：停下來，直到有真人叫他。</summary>
    public AppChatState Stop(ulong channelId, ulong byUserId, DateTimeOffset now)
    {
        lock (_gate)
        {
            return Save(channelId, Get(channelId) with
            {
                Stopped = true,
                StoppedBy = byUserId,
                StoppedAt = now
            });
        }
    }

    /// <summary>
    /// `/chat enable`：**在這個頻道**啟用「跟其他 App 對話」（管理員指令）。
    ///
    /// <paramref name="appId"/> 給了就**只跟那一個 App** 對話 ——
    /// 這是最實用的一種設定：頻道裡可能有好幾個機器人（通知、簽到、音樂），
    /// 只想跟其中一個聊。其他 App 的訊息仍然「看得到但不回話」。
    /// </summary>
    public AppChatState Enable(
        ulong channelId, ulong byUserId, ulong appId, int hourCap, DateTimeOffset now)
    {
        lock (_gate)
        {
            return Save(channelId, Get(channelId) with
            {
                Enabled = true,
                AppId = appId,
                HourCap = Math.Max(0, hourCap),
                EnabledBy = byUserId,
                EnabledAt = now,
                // 啟用本身就是「我要聊」→ 順便解除先前的 /stop
                Stopped = false,
                StoppedBy = 0,
                StoppedAt = null
            });
        }
    }

    /// <summary>`/chat disable`：停用（回到「看得到但不回話」）。設定本身留著，方便再啟用。</summary>
    public AppChatState Disable(ulong channelId)
    {
        lock (_gate) return Save(channelId, Get(channelId) with { Enabled = false });
    }

    /// <summary>
    /// 這個頻道現在要用哪一種「App 訊息模式」。
    ///
    /// * 管理員在這個頻道啟用過 → <see cref="AppMessageMode.Chat"/>
    ///   （指定了 AppId 時，其他 App 仍然只記上下文）
    /// * 沒有 → 用主機的預設值（`LLM_APP_MESSAGES`，預設是「看得到但不回話」）
    /// </summary>
    public AppMessageMode ModeFor(ulong channelId, ulong authorId, AppMessageMode fallback)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(channelId, out var state) || !state.Enabled) return fallback;

            // 只跟指定的一個 App 聊：其他 App 走預設（通常是「看得到但不回話」）
            if (state.AppId != 0 && state.AppId != authorId) return fallback;

            return AppMessageMode.Chat;
        }
    }

    /// <summary>這個頻道的每小時上限（有自己設定就用它，否則用主機預設）。</summary>
    public int HourCapFor(ulong channelId, int fallback)
    {
        lock (_gate)
            return _states.TryGetValue(channelId, out var state) && state.HourCap > 0
                ? state.HourCap
                : fallback;
    }

    /// <summary>這個頻道的設定（`/chat status` 用）。</summary>
    public AppChatState Config(ulong channelId)
    {
        lock (_gate) return Get(channelId);
    }

    /// <summary>目前有哪些頻道啟用了（診斷用）。</summary>
    public IReadOnlyList<ulong> EnabledChannels()
    {
        lock (_gate) return _states.Where(kv => kv.Value.Enabled).Select(kv => kv.Key).ToList();
    }

    /// <summary>`/stop all:True`：全部頻道都停。</summary>
    public int StopAll(ulong byUserId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var stopped = 0;

            // 已經有狀態的頻道 ＋ 目前正在對話的頻道（由呼叫端先 Stop 過）都要停
            foreach (var channelId in _states.Keys.ToList())
            {
                Stop(channelId, byUserId, now);
                stopped++;
            }

            return stopped;
        }
    }

    /// <summary>有真人找他 → 恢復（回傳是不是真的有停過，讓呼叫端決定要不要講一句）。</summary>
    public bool Resume(ulong channelId)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(channelId, out var state) || !state.Stopped) return false;

            Save(channelId, state with { Stopped = false, StoppedBy = 0, StoppedAt = null });
            return true;
        }
    }

    /// <summary>
    /// 記一句「回給其他 App 的話」（回傳這一個小時累計幾句）。
    ///
    /// 為什麼要在**回話前**問、回話後記：計數要對得上「真的送出去了幾句」——
    /// 先記再送的話，送失敗也會被算進去。
    /// </summary>
    public int RecordAppReply(ulong channelId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var hour = HourKey(now);
            var state = Get(channelId);

            var sent = state.HourKey == hour ? state.SentThisHour + 1 : 1;

            Save(channelId, state with { HourKey = hour, SentThisHour = sent });

            return sent;
        }
    }

    /// <summary>目前這個小時已經回了幾句（診斷用）。</summary>
    public int SentThisHour(ulong channelId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(channelId, out var state)) return 0;

            return state.HourKey == HourKey(now) ? state.SentThisHour : 0;
        }
    }

    /// <summary>被 `/stop` 停下來的頻道（`/ai status` 與啟動訊息用）。</summary>
    public IReadOnlyList<ulong> Stopped()
    {
        lock (_gate) return _states.Where(kv => kv.Value.Stopped).Select(kv => kv.Key).ToList();
    }

    public string Describe()
    {
        lock (_gate)
        {
            var enabled = _states.Values.Count(s => s.Enabled);
            var stopped = _states.Values.Count(s => s.Stopped);

            if (enabled == 0 && stopped == 0) return "沒有頻道啟用「跟其他 App 對話」";

            return $"{enabled} 個頻道啟用「跟其他 App 對話」" +
                   (stopped > 0 ? $"｜{stopped} 個被 /stop 停下來（等真人找它）" : "");
        }
    }

    private static string HourKey(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return $"{utc:yyyy-MM-dd HH}";
    }

    private AppChatState Get(ulong channelId)
        => _states.TryGetValue(channelId, out var state) ? state : AppChatState.Empty;

    private AppChatState Save(ulong channelId, AppChatState state)
    {
        _states[channelId] = state;

        // 狀態很小，但不該無限長大：超過上限就淘汰「最久沒動」的那些
        if (_states.Count > MaxChannels)
        {
            foreach (var key in _states
                         .OrderBy(kv => kv.Value.StoppedAt ?? DateTimeOffset.MinValue)
                         .Take(_states.Count - MaxChannels)
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _states.Remove(key);
            }
        }

        Persist();
        return state;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private void Persist()
    {
        if (_store is null) return;

        try
        {
            _store.SetBlob(BlobKey, JsonSerializer.Serialize(
                _states.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), Json));
        }
        catch (Exception ex)
        {
            // 寫不進去**不可以**讓對話壞掉；但這代表重啟後會忘記 /stop —— 要留痕跡
            Console.WriteLine($"[App 對話] 停止狀態寫入失敗（{ex.GetType().Name}）：{ex.Message}");
        }
    }
}
