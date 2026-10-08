using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TcBusBot.Core.Chat.Digest;

/// <summary>模型回傳的結構（解析後的樣子，還沒被清理）。</summary>
public sealed record DigestDraft(
    string Summary,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<DigestItem> OpenItems,
    IReadOnlyList<DigestMention> Mentions);

/// <summary>
/// 每週摘要的提示詞與輸出解析。
///
/// 為什麼整段抽成純函式（沒有 Discord、沒有網路、沒有儲存）：
/// 這裡是整個功能最容易默默出錯的地方 ——
///   * 模型回的不是乾淨的 JSON（多一段說明、用 markdown 包起來、被截斷）
///   * 它 @ 了一個**不在頻道裡的人**（或自己編了一個 ID）
///   * 它把「上週還沒做完的事」忘了帶下去（那就等於每週都重新開始）
/// 這些都不會拋例外，只會產生一份看起來很正常的爛摘要。所以每一項都要能離線測。
/// </summary>
public static class DigestPrompt
{
    /// <summary>一次最多幾則訊息（超過就分批）由呼叫端決定；這裡只負責「一批」的文字。</summary>
    public const string SystemPrompt =
        """
        你是一個 Discord 頻道的「每週摘要整理者」。

        你的工作是把**這一週**的訊息整理成一份給頻道成員看的週報，並且維護一份「還在進行中」的清單。

        ## 摘要
        - 用繁體中文，語氣中性、簡短。
        - 只寫**訊息裡真的出現過**的事。不確定就不要寫，不要補腦、不要猜測動機。
        - 把同一件事的連續討論收成一件（不要一則一則列）。
        - 決議、結論、時間、地點、數字（路線號碼、金額、日期）要保留原樣。
        - 抱怨與閒聊只有在「形成結論」或「有人要處理」時才值得寫。

        ## 重點
        - 3～7 條，每條一句話，**具體**（「決定週六 10:00 在朝馬站集合」而不是「討論了集合時間」）。

        ## 還在進行中（跨週）
        - 輸入會給你「上一週還沒完成的事」。請逐條判斷：
          * 這一週已經完成／已經有人回答了 → **不要**放進輸出（等於完成）
          * 還在進行 → 保留（文字可以更新得更精確）
          * 這一週新出現、而且還沒結束的事 → 加進去
        - 每條要標「負責或相關的人」（用輸入清單裡的人）。
        - 沒有就回空陣列，不要硬湊。

        ## 跟誰有關（@ 誰）
        - 只挑「這一週的事真的跟他有關」的人（他要處理、他被指名、他在等回覆）。
        - **只能**從輸入給你的「參與者」清單裡挑，id 必須完全照抄，不可以自己編。
        - 不確定就不要放。最多 {0} 位。
        - 絕對不要 @everyone 或 @here。

        ## 輸出格式（**只輸出 JSON，不要任何說明文字、不要 markdown 圍欄**）
        {
          "summary": "一段 150～400 字的摘要",
          "highlights": ["重點一", "重點二"],
          "open_items": [ { "text": "還沒做完的事", "owner_id": "123456789012345678", "owner_name": "小明" } ],
          "mentions": [ { "id": "123456789012345678", "name": "小明", "reason": "一句話說明為什麼 @ 他" } ]
        }

        id 一律是字串（Discord 的 ID 很長，用數字會壞掉）。
        """;

    /// <summary>把系統提示裡的 {0} 換成實際的 @ 上限。</summary>
    public static string SystemPromptFor(int maxMentions)
        => SystemPrompt.Replace("{0}", Math.Max(0, maxMentions).ToString());

    /// <summary>
    /// 一批訊息的「使用者訊息」。
    ///
    /// <paramref name="previousOpenItems"/> 只在**最後一次彙整**時給 ——
    /// 分批抽取時給它沒有意義（那時還沒看到全部內容）。
    /// </summary>
    public static string BuildUserPrompt(
        string channelName,
        TimeSpan zoneOffset,
        IReadOnlyList<DigestMessage> messages,
        IReadOnlyList<DigestItem> previousOpenItems,
        IReadOnlyList<(ulong Id, string Name)> participants,
        bool isPartial,
        int partialIndex = 1,
        int partialCount = 1)
    {
        var body = new StringBuilder();

        if (isPartial)
        {
            body.AppendLine($"以下是「{channelName}」這一週訊息的**第 {partialIndex}/{partialCount} 批**。");
            body.AppendLine("這一輪只要「客觀記錄」：把值得留下的事實、決議、未完事項、跟誰有關寫下來。");
            body.AppendLine("不要下結論、不要寫成正式週報 —— 後面還會有一輪把所有批次彙整起來。");
        }
        else
        {
            body.AppendLine($"以下是「{channelName}」這一週的訊息（或分批抽取的結果）。請整理成週報。");
        }

        body.AppendLine();

        if (participants.Count > 0)
        {
            body.AppendLine("【參與者】（@ 人只能從這裡挑，id 要照抄）");
            foreach (var (id, name) in participants) body.AppendLine($"- {name} = {id}");
            body.AppendLine();
        }

        if (previousOpenItems.Count > 0)
        {
            body.AppendLine("【上一週還沒完成的事】（完成的就不要放進輸出）");
            foreach (var item in previousOpenItems)
                body.AppendLine($"- {item.Text}" +
                                (item.OwnerId == 0 ? "" : $"（相關：{item.OwnerName} = {item.OwnerId}）"));
            body.AppendLine();
        }

        body.AppendLine(isPartial ? "【訊息】" : "【內容】");

        foreach (var message in messages) body.AppendLine(message.ToPromptLine(zoneOffset));

        return body.ToString();
    }

    /// <summary>分批抽取的結果要當成「訊息」餵進最後一輪 —— 用一樣的形狀，模型才不會搞混。</summary>
    public static DigestMessage AsPartialTurn(int index, string text, DateTimeOffset at)
        => new(
            Id: $"part-{index}",
            AuthorId: 0,
            AuthorName: "分批整理",
            PromptLabel: $"分批整理 {index}",
            At: at,
            Text: text);

    /// <summary>
    /// 解析模型回傳的 JSON。
    ///
    /// 容錯（都是實際會遇到的情況）：
    ///   * 被 markdown 圍欄包起來（```json … ```）
    ///   * 前後多了一段說明文字 → 取第一個 `{` 到最後一個 `}`
    ///   * 欄位名稱用 camelCase／大小寫不同
    ///   * 完全不是 JSON → 把整段文字當成摘要（**寧可有摘要也不要整份失敗**）
    /// </summary>
    public static DigestDraft Parse(string? raw, int maxMentions)
    {
        var text = (raw ?? "").Trim();

        if (text.Length == 0) return new DigestDraft("（模型沒有回覆內容）", [], [], []);

        var json = ExtractJson(text);

        if (json is null)
            return new DigestDraft(Clean(text), [], [], []);

        try
        {
            var node = JsonNode.Parse(json);

            if (node is not JsonObject obj)
                return new DigestDraft(Clean(text), [], [], []);

            var summary = ReadString(obj, "summary") ?? Clean(text);
            var highlights = ReadStringArray(obj, "highlights");
            var openItems = ReadItems(obj, "open_items");
            var mentions = ReadMentions(obj, "mentions", maxMentions);

            return new DigestDraft(summary, highlights, openItems, mentions);
        }
        catch (Exception)
        {
            // 解析不了就退回「整段當摘要」（比什麼都沒有好）
            return new DigestDraft(Clean(text), [], [], []);
        }
    }

    /// <summary>
    /// 把模型 @ 的人**核對回參與者清單**。
    ///
    /// ⚠️ 為什麼一定要核對：模型很會「照著格式編出一個 ID」。真的拿去 mention，
    ///    輕則 @ 到不存在的人（顯示成亂碼），重則 @ 到**別的伺服器**的人
    ///    （Discord 允許跨伺服器 mention，對方會收到通知）。
    ///    所以只留「這一週真的在這個頻道出現過」的 ID，其餘一律丟掉。
    /// </summary>
    public static IReadOnlyList<DigestMention> SanitizeMentions(
        IEnumerable<DigestMention> mentions,
        IReadOnlyList<(ulong Id, string Name)> participants,
        int maxMentions,
        ulong botId = 0)
    {
        if (maxMentions <= 0) return [];

        var allowed = participants.ToDictionary(p => p.Id, p => p.Name);
        var kept = new List<DigestMention>();
        var seen = new HashSet<ulong>();

        foreach (var mention in mentions)
        {
            if (mention.UserId == 0 || mention.UserId == botId) continue;
            if (!allowed.TryGetValue(mention.UserId, out var name)) continue;
            if (!seen.Add(mention.UserId)) continue;

            kept.Add(mention with { Name = string.IsNullOrWhiteSpace(name) ? mention.Name : name });

            if (kept.Count >= maxMentions) break;
        }

        return kept;
    }

    /// <summary>跨週清單也要核對 owner（一樣不能相信模型給的 ID）。</summary>
    public static IReadOnlyList<DigestItem> SanitizeItems(
        IEnumerable<DigestItem> items,
        IReadOnlyList<(ulong Id, string Name)> participants,
        int maxItems = 12)
    {
        var allowed = participants.ToDictionary(p => p.Id, p => p.Name);
        var kept = new List<DigestItem>();

        foreach (var item in items)
        {
            var text = Clean(item.Text);

            if (text.Length == 0) continue;

            var ownerId = 0UL;
            var ownerName = "";

            if (item.OwnerId != 0 && allowed.TryGetValue(item.OwnerId, out var name))
            {
                ownerId = item.OwnerId;
                ownerName = name;
            }

            kept.Add(new DigestItem(text, ownerId, ownerName));

            if (kept.Count >= maxItems) break;
        }

        return kept;
    }

    // ── 內部 ─────────────────────────────────────────────

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    /// <summary>字串清理：拿掉多餘空白與 markdown 殘留（模型常常包 ```）。</summary>
    private static string Clean(string? text)
        => (text ?? "")
            .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "", StringComparison.Ordinal)
            .Trim();

    private static string? ReadString(JsonObject obj, string name)
    {
        foreach (var key in Keys(obj, name))
        {
            if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                return Clean(text);
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject obj, string name)
    {
        foreach (var key in Keys(obj, name))
        {
            if (obj[key] is not JsonArray array) continue;

            return array
                .Select(node => node is JsonValue v && v.TryGetValue<string>(out var text) ? Clean(text) : "")
                .Where(text => text.Length > 0)
                .Take(12)
                .ToList();
        }

        return [];
    }

    private static IReadOnlyList<DigestItem> ReadItems(JsonObject obj, string name)
    {
        foreach (var key in Keys(obj, name))
        {
            if (obj[key] is not JsonArray array) continue;

            var items = new List<DigestItem>();

            foreach (var node in array)
            {
                if (node is not JsonObject item) continue;

                var text = ReadString(item, "text") ?? ReadString(item, "item") ?? "";
                if (text.Length == 0) continue;

                items.Add(new DigestItem(
                    text,
                    ReadId(item, "owner_id", "owner", "member_id"),
                    ReadString(item, "owner_name") ?? ReadString(item, "name") ?? ""));
            }

            return items;
        }

        return [];
    }

    private static IReadOnlyList<DigestMention> ReadMentions(JsonObject obj, string name, int maxMentions)
    {
        foreach (var key in Keys(obj, name))
        {
            if (obj[key] is not JsonArray array) continue;

            var mentions = new List<DigestMention>();

            foreach (var node in array)
            {
                // 允許模型偷懶寫成 ["123"] 或 [{"id":…}] 兩種
                if (node is JsonValue simple && simple.TryGetValue<string>(out var rawId))
                {
                    if (ulong.TryParse(rawId.Trim(), out var simpleId))
                        mentions.Add(new DigestMention(simpleId, "", ""));

                    continue;
                }

                if (node is not JsonObject item) continue;

                var id = ReadId(item, "id", "user_id", "user");

                if (id != 0)
                    mentions.Add(new DigestMention(
                        id,
                        ReadString(item, "name") ?? ReadString(item, "user_name") ?? "",
                        ReadString(item, "reason") ?? ""));
            }

            return mentions.Take(Math.Max(1, maxMentions * 3)).ToList();
        }

        return [];
    }

    /// <summary>欄位名稱大小寫／底線／連字號都通融（模型常常自己換寫法）。</summary>
    private static IEnumerable<string> Keys(JsonObject obj, string name)
    {
        var normalized = name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();

        foreach (var key in obj.Select(kv => kv.Key))
        {
            if (key.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() == normalized)
                yield return key;
        }
    }

    /// <summary>ID 一律當字串讀（模型可能回字串或數字）；讀不出來回 0。</summary>
    private static ulong ReadId(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var key in Keys(obj, name))
            {
                if (obj[key] is not JsonValue value) continue;

                if (value.TryGetValue<string>(out var text) && ulong.TryParse(text.Trim(), out var parsed))
                    return parsed;

                if (value.TryGetValue<long>(out var number) && number > 0)
                    return (ulong)number;
            }
        }

        return 0;
    }
}
