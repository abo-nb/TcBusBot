# Discord.Net 與 Discord 互動元件限制 — 技術調查報告

調查日期：2026-09-12（本機時間 2026-09-12 00:42 +08:00）
用途：TcBusBot（C#/.NET Discord bot，10,000+ 站牌的「起站/迄站」挑選 → 路線清單）
所有事實均附官方來源連結；無法從一手來源確認者標記 **未確認**。

---

## 1. Discord.Net 版本、目標框架、維護狀態

| 項目 | 結果 | 來源 |
|---|---|---|
| NuGet 最新穩定版 | **`Discord.Net` 3.20.1** | [nuget.org/packages/Discord.Net](https://www.nuget.org/packages/Discord.Net) |
| 發佈日期 | **2026-06-07**（NuGet「Last updated 6/7/2026」；GitHub release `3.20.1` `published_at=2026-06-07T15:30:31Z`） | [GitHub release 3.20.1](https://github.com/discord-net/Discord.Net/releases/tag/3.20.1)、[GitHub Releases API](https://api.github.com/repos/discord-net/Discord.Net/releases?per_page=5) |
| 前一版 | 3.20.0 = 2026-06-06；3.19.1 = 2026-03-12；3.19.0 = 2026-03-03；3.18.0 = 2025-07-19 | [NuGet 版本清單](https://www.nuget.org/packages/Discord.Net#versions-body-tab) |
| 是否有 v4 | **沒有**。NuGet flat-container 索引最後一筆就是 `3.20.1`，**從未**發佈任何 4.x（含 prerelease）；GitHub issue #2756「[v4] Rest Client & model updates」已 **CLOSED**（2023-12-04，`state_reason: completed`，標籤 `version: major`，計畫 `Discord.Net.V4.Core`/`Discord.Net.V4.Rest`、全面 nullable、改用 System.Text.Json、`IRestApiProvider`/`RequestQueue` 重構）。v4 只以**分支**形式存在：`4.0`、`5.0`、`v4/message-builder`、`v4/state-cache-providers`；nightly 只在 BaGet（<https://baget.discordnet.dev/>）與 GitHub Packages（4.x nightly 是否存在 → **未確認**） | [flatcontainer index](https://api.nuget.org/v3-flatcontainer/discord.net/index.json)、[issue #2756](https://github.com/discord-net/Discord.Net/issues/2756)、[branches API](https://api.github.com/repos/discord-net/Discord.Net/branches?per_page=100) |
| 目標框架（3.20.1） | **net8.0 / net9.0 / net10.0** 三個相依群組（無 netstandard / net461） | [NuGet Frameworks 分頁](https://www.nuget.org/packages/Discord.Net#supportedframeworks-body-tab) |
| .NET 8 / 9 / 10 | **全部可用**。net8.0、net9.0 自 3.17.0 起（PR #3032/#3033）；net10.0 自 3.19.0-beta.1 起（PR #3200） | [CHANGELOG](https://docs.discordnet.dev/CHANGELOG.html) |
| 舊框架 | 3.19.0-beta.1 起「Removes unsupported SDK targets」，官方說明「DNet drops legacy SDK versions and switches to modern .NET targets (.NET8+)」→ 3.19+ **只能** .NET 8 以上。（3.18.0 之前的完整 TFM 清單 **未確認**） | [3.19.0-beta.1 release notes](https://github.com/discord-net/Discord.Net/releases/tag/3.19.0-beta.1) |
| 是否仍積極維護 | **是**。`dev` 分支最後 commit 2026-09-09（PR #3286 file-type filters），其後 2026-09-05、2026-08-21、2026-08-11 皆有合併 | [GitHub commits API](https://api.github.com/repos/discord-net/Discord.Net/commits?per_page=5) |
| 授權 / 版本策略 | MIT；SemVer（MAJOR.MINOR.PATCH），3.x 仍為現行主線 | [NuGet](https://www.nuget.org/packages/Discord.Net)、[README](https://github.com/discord-net/Discord.Net) |

**對本專案的結論**：用 `3.20.1`，`TargetFramework=net8.0`（或 net9.0/net10.0）。因為 3.19+ 已無 netstandard2.0 資產，**不要**再以 .NET Framework / net6 為目標。

---

## 2. 替代方案比較（一段話 + 精確版本）

2026-09 的 .NET Discord 生態：

| 函式庫 | 最新穩定版 | 發佈日 | net8.0 | net9.0 | net10.0 | 最後 commit |
|---|---|---|---|---|---|---|
| **Discord.Net** | **3.20.1** | 2026-06-07 | ✔ 直接 | ✔ 直接 | ✔ 直接 | 2026-09-09 |
| NetCord | **無穩定版**（1.0.0-beta.20） | 2026-09-10（beta） | ✘ | ✘ | ✔ 僅此 | 2026-09-11 |
| DSharpPlus | 4.5.3 | 2026-08-24 | 經 netstandard2.0 | 經 netstandard2.0 | 經 netstandard2.0 | 2026-09-10 |
| DisCatSharp | 10.7.0 | 2026-03-20 | ✔ | ✔ | ✔（+net11） | 未確認 |

**Discord.Net 3.20.1**（2026-06-07，net8/9/10 **直接**目標框架，維護活躍，**無 v4**）仍是唯一「成熟 3.x 穩定線」。**NetCord**（repo 是 **`NetCordDev/NetCord`**；`netcord/NetCord` 為 404）仍為 prerelease，最新 `1.0.0-beta.20`（2026-09-10），且**只出 net10.0**，單一 NuGet owner（bus factor）——新穎但不穩定且逼你上 .NET 10。**DSharpPlus** 最新穩定 `4.5.3`（2026-08-24，套件本體只出 netstandard2.0），v5 仍只有 nightly（`5.0.0-nightly-02605`，2026-09-10，net10.0 only），官方文件要求新專案用 v5 nightly 並明言「no support will be provided for v4.5.X」；⚠️ NuGet 上名為 `DSharpPlus 5.0.0` 的套件已被標為 deprecated（critical bugs）且 unlisted（2023-05-20、net7.0），**不是** v5 正式版。**DisCatSharp** 10.7.0（2026-03-20，net8/9/10）是 DSharpPlus 系衍生的穩定替代品（其 DSharpPlus 血緣關係 **未確認**）。

因此，除非你要賭 nightly API 或綁定 .NET 10，**Discord.Net 3.20.1 是本專案最務實的選擇**。以下全部聚焦 Discord.Net。

來源：[NuGet Discord.Net](https://www.nuget.org/packages/Discord.Net)、[NuGet NetCord](https://www.nuget.org/packages/NetCord)、[NetCordDev/NetCord](https://github.com/NetCordDev/NetCord)、[DSharpPlus v4.5.3 release](https://github.com/DSharpPlus/DSharpPlus/releases/tag/v4.5.3)、[DSharpPlus 5.0.0（deprecated）](https://www.nuget.org/packages/DSharpPlus/5.0.0)、[DSharpPlus docs preamble](https://raw.githubusercontent.com/DSharpPlus/DSharpPlus/master/docs/articles/preamble.md)、[NuGet DisCatSharp](https://www.nuget.org/packages/DisCatSharp)、[Aiko-IT-Systems/DisCatSharp](https://github.com/Aiko-IT-Systems/DisCatSharp)

---

## 3. Slash command AUTOCOMPLETE

### 3.1 Discord.Net API（兩條路）

**(a) Autocomplete Command（最簡單、適合 10,000+ 站牌）**
- 選項加 `[Autocomplete]`；另寫一個**無參數**方法標記 `[AutocompleteCommand("parameterName", "commandName")]`：
```csharp
[SlashCommand("route", "查詢公車路線")]
public async Task Route([Summary("from"), Autocomplete] string from) { ... }

[AutocompleteCommand("from", "route")]
public async Task AutocompleteFrom()
{
    var input = (Context.Interaction as SocketAutocompleteInteraction)!.Data.Current.Value.ToString();
    var results = _stopService.Search(input, 25)
        .Select(s => new AutocompleteResult($"{s.Name} ({s.Id})", s.Id));
    await (Context.Interaction as SocketAutocompleteInteraction)!.RespondAsync(results);
}
```
官方文件明註：「max - 25 suggestions at a time」。
來源：[Interaction Service Intro — Autocomplete Commands](https://docs.discordnet.dev/guides/int_framework/intro.html)

**(b) AutocompleteHandler（可重用的 singleton handler）**
```csharp
[SlashCommand("route", "查詢公車路線")]
public async Task Route([Summary("from"), Autocomplete(typeof(StopAutocompleteHandler))] string from)
    => await RespondAsync($"您選擇：{from}");

public class StopAutocompleteHandler : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter, IServiceProvider services)
    {
        var input = autocompleteInteraction.Data.Current.Value.ToString()!;
        var results = _stopService.Search(input, 25)
            .Select(s => new AutocompleteResult(s.Name, s.Id));   // 名稱, 值
        return Task.FromResult(AutocompletionResult.FromSuccess(results)); // 建議 ≤25
    }
}
```
- 型別：`Discord.Interactions.AutocompleteHandler`（abstract，實作 `IAutocompleteHandler`），方法簽章
  `public abstract Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext, IAutocompleteInteraction, IParameterInfo, IServiceProvider)`。
- 結果型別：**`AutocompletionResult`**（struct；`FromSuccess(...)`、`FromError()`），單筆建議為 **`AutocompleteResult(name, value)`**。
- 回傳 `FromSuccess()`（無參數）＝顯示 "No options match your search."；`FromError()` ＝不回覆（客戶端顯示載入失敗），且會傳給 `AutocompleteHandlerExecuted` 事件。
- Handler 是**啟動時建立的 singleton**（constructor/property injection 只解析一次；要 per-request 相依性請用 `GenerateSuggestionsAsync` 的 `IServiceProvider` 參數）。
- `AutocompleteHandlerExecuted` 事件可做 post-execution 邏輯。
- 另有泛型 `AutocompleteAttribute<T>`（3.15.1「Generic autocomplete」PR #2935）。
來源：[Command Autocompletion 指南](https://docs.discordnet.dev/guides/int_framework/autocompletion.html)、[AutocompleteHandler API](https://docs.discordnet.dev/api/Discord.Interactions.AutocompleteHandler.html)、[Discord.Interactions 命名空間](https://docs.discordnet.dev/api/Discord.Interactions.html)

**(c) 註冊指令的 builder（不是處理 autocomplete 的地方）**
- `Discord.SlashCommandOptionBuilder` 有 `public bool IsAutocomplete { get; set; }` 與 `WithAutocomplete(bool)`；常數 `MaxChoiceCount = 25`、`ChoiceNameMaxLength = 100`。其 `AddOption(...)` 也有 `bool isAutocomplete = false` 參數。
- 使用者提到的「`SlashCommandOptionBuilder` + `RequestOptions`」是誤解：`RequestOptions` 是 REST 請求選項（audit log reason、retry、ratelimit callback 等），與 autocomplete 無關；`SlashCommandBuilder`/`SlashCommandOptionBuilder` 只用來**註冊**指令。
來源：[SlashCommandOptionBuilder API](https://docs.discordnet.dev/api/Discord.SlashCommandOptionBuilder.html)

### 3.2 Discord 官方硬限制（autocomplete）

| 限制 | 數值 | 來源 |
|---|---|---|
| autocomplete 回覆的 choices 上限 | **25** | [Interaction Callback Data → Autocomplete](https://docs.discord.com/developers/interactions/receiving-and-responding#interaction-response-object-autocomplete)：「autocomplete choices (max of 25 choices)」 |
| choice `name` | **1–100 字元** | [Application Command Option Choice Structure](https://docs.discord.com/developers/interactions/application-commands#application-command-object-application-command-option-choice-structure) |
| choice `value`（字串時） | **最多 100 字元** | 同上 |
| 初始回應截止 | **3000 ms（3 秒）**；超過則 token 失效 | [Receiving and Responding §Interaction Callback](https://docs.discord.com/developers/interactions/receiving-and-responding#interaction-callback)：「Interaction `tokens` are valid for **15 minutes** … you **must send an initial response within 3 seconds** of receiving the event. If the 3 second deadline is exceeded, the token will be invalidated.」 |
| autocomplete 專屬截止時間 | 官方**沒有**另外寫 autocomplete 專用的 ms 值 → 只能套用上面的 3 秒通則（**未確認**有更嚴格的獨立門檻） | [Autocomplete 章節](https://docs.discord.com/developers/interactions/application-commands#autocomplete) 全文無任何秒數 |
| 觸發頻率 | 官方僅說「dynamically return option suggestions to a user **as they type**」；被輸入的選項會帶 `focused: true`，其他已填選項一併附上（不含 `focused`），且**可能是部分資料**（client-side 驗證） | 同上 |
| 每次按鍵都觸發？最少字元數？ | 官方未載明 → **未確認**（社群常說 focus 後每次輸入都送，屬觀察值） | — |
| `autocomplete: true` 不可與 `choices` 並存 | 官方明文 | [Option Structure](https://docs.discord.com/developers/interactions/application-commands#application-command-object-application-command-option-structure) |

**Discord.Net 端的對應限制（一手原始碼）**
- 送 autocomplete 結果時強制檢查 25 筆：`InteractionHelper.SendAutocompleteResultAsync` 內 `Preconditions.AtMost(result.Count(), 25, nameof(result), "A maximum of 25 choices are allowed!")`。
- 3.19.0-beta.1 修正「`AutocompleteResult.Value` 沒有長度限制」（PR #3206），對應官方 100 字元 value 上限（**實際強制值未逐一核對 → 未確認**）。
來源：[InteractionHelper.cs @3.20.1](https://raw.githubusercontent.com/discord-net/Discord.Net/3.20.1/src/Discord.Net.Rest/Entities/Interactions/InteractionHelper.cs)、[CHANGELOG](https://docs.discordnet.dev/CHANGELOG.html)

> 對 10,000+ 站牌的實務建議：autocomplete 天生就是「打字 → 回傳 ≤25 筆」，把站名/代碼做前綴索引（或 SQLite FTS/前綴索引），並在 `AutocompleteResult.Value` 內放**穩定 ID**（≤100 字元），選項名稱放「站名 + 站牌代碼」；不要試圖把 10,000 站塞進 select menu（見 §4 的 25 筆硬上限）。

---

## 4. 元件硬限制（含官方引用）

### 4.1 String Select（component type 3）
| 欄位 | 限制 | 來源 |
|---|---|---|
| `options` | **最多 25 個** | [String Select Structure](https://docs.discord.com/developers/components/reference#string-select-string-select-structure) |
| `custom_id` | **1–100 字元** | 同上（另見 [Custom ID](https://docs.discord.com/developers/components/reference#anatomy-of-a-component-custom-id)：「a string of 1 to 100 characters」） |
| `placeholder` | **最多 150 字元** | 同上 |
| `min_values` | 預設 **1**；min **0**，max **25** | 同上 |
| `max_values` | 預設 **1**；max **25** | 同上 |
| option `label` | **最多 100 字元** | [Select Option Structure](https://docs.discord.com/developers/components/reference#string-select-select-option-structure) |
| option `value` | **最多 100 字元** | 同上 |
| option `description` | **最多 100 字元** | 同上 |
| `required` | 僅 modal 用（預設 true，訊息中忽略） | 同上 |
| `min_values` 補充 | 若 `required` 省略或為 true，`min_values` 必須省略或 ≥1 | 同上 |

- **多選（multi-select）：支援。** 單選＝`max_values` 省略或 1；多選＝設 `min_values`（0–25）與 `max_values`（1–25）。官方：「Select menus support single-select and multi-select behavior」。
- ⚠️ 舊文件（2021–2022 版）寫 `label` max 25、`description` max 50、`placeholder` max 100 — **已過時**，勿再引用（見 [archived official Message_Components.md](https://github.com/discord/discord-api-docs/blob/1d09e3da359757db749f7e52e58330b8ffdcb802/docs/interactions/Message_Components.md)）。
- Discord.Net 自己的文件亦覆述：options 1–25、placeholder ≤150、label/value/description ≤100。來源：[Create Components V2 guide](https://docs.discordnet.dev/guides/components_v2/advanced.html)

### 4.2 Buttons 與 Action Row
| 項目 | 限制 | 來源 |
|---|---|---|
| 一個 Action Row 的按鈕數 | **最多 5 個** | [Action Row](https://docs.discord.com/developers/components/reference#action-row)：「Up to 5 contextually grouped buttons」 |
| 一個 Action Row 的 select | **只能 1 個**（select 與 button 不可混在同一 row） | 同上 |
| 按鈕 `label` | **最多 80 字元** | [Button Structure](https://docs.discord.com/developers/components/reference#button-button-structure) |
| 按鈕 `custom_id` | **1–100 字元** | 同上 |
| Link 按鈕 `url` | **最多 512 字元** | 同上 |
| 每訊息 Action Row 數（legacy 元件） | 現行 reference 未再列硬數字；Discord 官方舊版文件明寫「**You can have up to 5 Action Rows per message**」；Discord.Net 指南也以「>5 action rows」描述邊界情況 | [archived official docs](https://github.com/discord/discord-api-docs/blob/1d09e3da359757db749f7e52e58330b8ffdcb802/docs/interactions/Message_Components.md)、[Discord.Net V2 Advanced](https://docs.discordnet.dev/guides/components_v2/advanced.html) |
| 每訊息元件總數（Components V2） | **最多 40 個元件** | [Component Reference 開頭](https://docs.discord.com/developers/components/reference)：「Messages allow up to 40 total components」 |
| Components V2 的 `content`/`embeds` | **失效**（必須改用 Text Display / Container） | 同上 |

### 4.3 Embed 限制（官方）
| 欄位 | 限制 |
|---|---|
| `title` | 256 字元 |
| `description` | 4096 字元 |
| `fields` | 最多 **25** 個 |
| `field.name` | 256 字元 |
| `field.value` | 1024 字元 |
| `footer.text` | 2048 字元 |
| `author.name` | 256 字元 |
| **所有 embed 合計**（title+description+field.name+field.value+footer.text+author.name） | **≤ 6000 字元** |
| 每訊息 embed 數 | **最多 10 個**（webhook/訊息建立：「array of up to 10 embed objects」；interaction response：「Supports up to 10 embeds」；Discord.Net 常數 `DiscordConfig.MaxEmbedsPerMessage = 10`） |
| 訊息 `content` | 2000 字元（`DiscordConfig.MaxMessageSize = 2000`） |
| 去重 | embed 以 URL 去重，重複 URL 只顯示第一個 |
來源：[Embed Limits](https://docs.discord.com/developers/resources/message#embed-object-embed-limits)、[Execute Webhook JSON params](https://docs.discord.com/developers/resources/webhook#execute-webhook-jsonform-params)、[Interaction Callback Data → Messages](https://docs.discord.com/developers/interactions/receiving-and-responding#interaction-response-object-messages)、[DiscordConfig API](https://docs.discordnet.dev/api/Discord.DiscordConfig.html)

> ⚠️ 實務：若用 Embed 塞 10,000 站牌清單，6000 字元總量限制會先炸；路線清單建議分頁或改用 Components V2 的 Text Display（單一元件上限 4000 字元，見下）。「所有元件文字合計 ≤4000 字元」僅見於第三方 mirror（userdoccers），**未確認** 為官方條文。

---

## 5. 元件互動的處理（Discord.Net）

### 5.1 接收：事件與型別
- `DiscordSocketClient`／`BaseSocketClient` 的事件（官方文件列舉可用來執行互動指令的事件）：
  `InteractionCreated`（所有互動）、`ButtonExecuted`、`SelectMenuExecuted`、`ModalExecuted`、`AutocompleteExecuted`、`UserCommandExecuted`、`MessageCommandExecuted`。
  來源：[Interaction Service Intro — Executing Commands](https://docs.discordnet.dev/guides/int_framework/intro.html)
- 低階型別：**`Discord.WebSocket.SocketMessageComponent`**（`: SocketInteraction, IComponentInteraction, IDiscordInteraction`），屬性：
  - `SocketMessageComponentData Data` → `CustomId`（string）、`Type`（`ComponentType`）、`Values`（`IReadOnlyCollection<string>`，select menu 用）、`Value`、`BoolValue`、`Users/Channels/Roles/Members`（resolved）。
  - `SocketUserMessage Message`（被點擊的訊息）。
  - `SocketInteraction` 基底：`Id`、`Token`、`User`、`Channel`、`GuildId`、`IsDMInteraction`、`CreatedAt` 等。
  來源：[SocketMessageComponent API](https://docs.discordnet.dev/api/Discord.WebSocket.SocketMessageComponent.html)、[SocketMessageComponentData 原始碼](https://raw.githubusercontent.com/discord-net/Discord.Net/3.20.1/src/Discord.Net.WebSocket/Entities/Interaction/MessageComponents/SocketMessageComponentData.cs)
- 若用互動框架（`Discord.Interactions`）：**只有 `[ComponentInteraction(...)]`**（`ComponentInteractionAttribute`）＋ `ComponentCommandInfo`。**不存在** `ButtonExecuted`/`SelectMenuExecuted` 的**屬性**版本（`[ButtonInteraction]`/`[SelectMenuInteraction]` 在 `Discord.Interactions` 命名空間清單中**不存在**）。
  - 支援 wildcard：`*`（lazy match，捕獲值按順序傳入方法參數）、`**`、`?`、長度量詞、`TreatAsRegex`（3.10.0）。
  - select menu：值以 `string[]` 傳入，且**必須是最後一個參數**；也可用 `IUser[]`/`IChannel[]`/`IRole[]`/`IMentionable[]` 取得 resolved 實體。
  - `[Group]` 會把群組名當 custom_id 前綴，可用 `[ComponentInteraction("id", true)]`（`ignoreGroupNames`）關閉。
  來源：[命名空間清單](https://docs.discordnet.dev/api/Discord.Interactions.html)、[Intro 指南](https://docs.discordnet.dev/guides/int_framework/intro.html)

### 5.2 custom_id 如何呈現
- 原始 payload：`interaction.data.custom_id`（官方 [Message Component Data Structure](https://docs.discord.com/developers/interactions/receiving-and-responding#interaction-object-message-component-data-structure)：`custom_id`、`component_type`、`values`（select 一定有）、`resolved`）。
- Discord.Net：`component.Data.CustomId`；select menu 的選取值在 `component.Data.Values`。

### 5.3 回覆 API 與時序規則
| Discord.Net 方法 | 送出的 callback type | 說明 |
|---|---|---|
| `RespondAsync(...)` | **4 CHANNEL_MESSAGE_WITH_SOURCE** | 新增一則回應訊息（可由 `ephemeral: true` 變成只有使用者可見） |
| `UpdateAsync(Action<MessageProperties>)` | **7 UPDATE_MESSAGE** | 直接就地編輯觸發元件的訊息（元件互動專用） |
| `DeferAsync(bool ephemeral = false)` | **6 DEFERRED_UPDATE_MESSAGE** | 元件互動的「稍後更新原訊息」ACK（使用者不會看到 loading） |
| `DeferLoadingAsync(bool ephemeral = false)` | **5 DEFERRED_CHANNEL_MESSAGE_WITH_SOURCE** | 「thinking…」載入狀態，之後要送新的回應訊息 |
| `FollowupAsync(...)` / `FollowupWithFilesAsync(...)` | webhook followup（POST `/webhooks/{app}/{token}`） | token 有效期 15 分鐘 |
| `ModifyOriginalResponseAsync(...)` / `DeleteOriginalResponseAsync()` | PATCH／DELETE `@original` | 修改/刪除初始回應 |
來源：type 6/7 由原始碼確認（`DeferAsync` → `InteractionResponseType.DeferredUpdateMessage`；`UpdateAsync` → `UpdateMessage`；`DeferLoadingAsync` → `DeferredChannelMessageWithSource`）：[SocketMessageComponent.cs @3.20.1](https://raw.githubusercontent.com/discord-net/Discord.Net/3.20.1/src/Discord.Net.WebSocket/Entities/Interaction/MessageComponents/SocketMessageComponent.cs)；callback type 值：[Interaction Callback Type](https://docs.discord.com/developers/interactions/receiving-and-responding#interaction-response-object-interaction-callback-type)

**時序規則（官方）**
- **必須在收到互動後 3 秒內送出初始回應**（`RespondAsync` / `UpdateAsync` / `DeferAsync` / `DeferLoadingAsync` 皆算）；超過則 **token 失效**。
- **interaction token 有效期 15 分鐘**，可用來送 followup／編輯原回應。
- 一次互動**只能回應一次**：Discord.Net 會以 `HasResponded` 擋下第二次並丟 `InvalidOperationException("Cannot respond, update, or defer the same interaction twice")`；要延長處理就 `DeferAsync()` ＋ `ModifyOriginalResponseAsync()`。
- Discord.Net 會**在本地先檢查 3 秒**：`InteractionHelper.ResponseTimeLimit = 3`（秒）、`ResponseAndFollowupLimit = 15`（分），由 `DiscordConfig.ResponseInternalTimeCheck`（**預設 true**）控制；`UseInteractionSnowflakeDate`（預設 true）決定用 snowflake 時間還是接收時間計時。開發環境若遇時鐘誤差可設 `ResponseInternalTimeCheck = false`。
來源：[Receiving and Responding §Interaction Callback](https://docs.discord.com/developers/interactions/receiving-and-responding#interaction-callback)、[Followup Messages](https://docs.discord.com/developers/interactions/receiving-and-responding#followup-messages)、[InteractionHelper.cs](https://raw.githubusercontent.com/discord-net/Discord.Net/3.20.1/src/Discord.Net.Rest/Entities/Interactions/InteractionHelper.cs)、[DiscordConfig.cs](https://raw.githubusercontent.com/discord-net/Discord.Net/3.20.1/src/Discord.Net.Core/DiscordConfig.cs)
- Discord.Net 自己的 CV2 指南寫法：「A component should receive an initial response within a **3 second** timeframe. After this it can continue receiving responses for up to **15 minutes**」。來源：[Interact with Components V2](https://docs.discordnet.dev/guides/components_v2/interaction.html)
- 官方 additional quota：**user-install 且未安裝於該伺服器的 app，每個互動最多 5 則 followup**。來源：[Create Followup Message](https://docs.discord.com/developers/interactions/receiving-and-responding#create-followup-message)

**典型寫法（先 defer 再更新，避免 3 秒逾時）**
```csharp
_client.ButtonExecuted += async (SocketMessageComponent c) =>
{
    await c.DeferAsync();                       // type 6，立即 ACK
    var routes = await _routeService.QueryAsync(ParseState(c.Data.CustomId));
    await c.ModifyOriginalResponseAsync(m => { m.Content = string.Join('\n', routes); });
};
```

---

## 6. 重啟後的持久化元件 / 元件狀態

### 6.1 Discord.Net 3.x 有沒有「component registry / persistent view」？
**沒有。** Discord.Net 3.x 沒有對應 discord.py `View` 或 discord.js collector／「component handler registry」的概念，`Discord.Interactions` 命名空間也沒有任何 view/registry 型別（見[命名空間清單](https://docs.discordnet.dev/api/Discord.Interactions.html)：只有 `ComponentInteractionAttribute`、`ComponentCommandInfo`、`InteractionService`、`InteractionUtility`）。官方 CV2 指南示範的正是**手動解析 custom_id**：
```csharp
private async Task ClientOnInteractionCreatedAsync(SocketInteraction arg)
{
    switch (arg)
    {
        case SocketMessageComponent component:
            switch (component.Data.CustomId) { /* 解析 customId */ }
            break;
        case SocketModal modal: /* ... */ break;
    }
}
```
並提到可用 `message.Components.FindComponentById<T>(id)`（以訊息內遞增的整數 id 找元件，可選泛型型別）或 custom_id。來源：[Interact with Components V2](https://docs.discordnet.dev/guides/components_v2/interaction.html)、[Create Components V2](https://docs.discordnet.dev/guides/components_v2/advanced.html)

### 6.2 custom_id 解析 vs 「Components V2 / IMessageComponent 註冊」
- **custom_id 解析**：把狀態編碼進 `custom_id`（≤100 字元）或存到資料庫，互動時即時查。這是**唯一**官方建議且可跨重啟的做法。
- **`InteractionService` 的 `[ComponentInteraction("pattern")]`**：模組是 **transient**（官方 DI FAQ：「modules … are spawned whenever a request is received, and are killed from memory when the execution finishes … you cannot store persistent data inside a module」），而 component 指令是靠 **custom_id 樣式在執行時比對**（並非在記憶體中註冊特定訊息實例）。因此**只要重啟後重新 `AddModulesAsync` 並把 `InteractionCreated` 接回 `InteractionService.ExecuteCommandAsync`，舊訊息上的按鈕／選單仍然能被正確路由**——真正會在重啟後消失的是你自己存於記憶體的挑選狀態，必須放進 singleton service（或更好：SQLite/Redis）。
來源：[DI FAQ](https://docs.discordnet.dev/faq/basics/dependency-injection.html)、[Intro 指南](https://docs.discordnet.dev/guides/int_framework/intro.html)
- **Components V2（flag 32768）不是註冊機制**，它只是訊息結構/呈現方式的變更，與「重啟後恢復狀態」無關。`IMessageComponent` 只是元件模型介面，不是 handler 註冊表。
- `InteractionUtility.WaitForInteractionAsync(...)` 只是「等一則互動」的工具，**不是**持久化 view（且在 `RunMode.Sync` 下會阻塞 gateway thread）。

### 6.3 Discord.Net 3.x 是否支援 Components V2（flag 32768）？
**支援。** 具體如下：
- 3.18.0-beta.1 加入 **Components V2**（PR #3065）；3.18.0 stable 有「[CV2] add children component counts to IComponentContainer」、「Docs/components v2」等。來源：[CHANGELOG](https://docs.discordnet.dev/CHANGELOG.html)
- 列舉名稱是 **`Discord.MessageFlags.ComponentsV2 = 32768`**（**不是** `MessageFlags.IsComponentsV2`）。來源：[MessageFlags API](https://docs.discordnet.dev/api/Discord.MessageFlags.html)
- 建構方式：`new ComponentBuilderV2().WithTextDisplay(...).WithMediaGallery([...]).WithActionRow([...])`；非 CV2 元件用 `ComponentBuilder`。（`ContainerBuilder`、`SectionBuilder`、`TextDisplayBuilder`、`ThumbnailBuilder`、`SeparatorBuilder`、`MediaGalleryBuilder` 等亦存在，並在 3.19.0 加入可選 `id` 建構子。）來源：[CV2 Intro](https://docs.discordnet.dev/guides/components_v2/intro.html)、[CHANGELOG 3.18–3.19](https://docs.discordnet.dev/CHANGELOG.html)
- 輔助方法：`ComponentCount()`（3.18.0-beta.1「update component limits + add `ComponentCount()` extension」#3107）、`FindComponentById<T>()`。
- **flag 會自動補上**：原始碼中若元件陣列含非 ActionRow 元件，就自動 OR 進 flag：
  `if (components?.Components?.Any(x => x.Type != ComponentType.ActionRow) ?? false) flags |= MessageFlags.ComponentsV2;`
  但官方 guide 提醒少數情況（例如 >5 action rows）可能沒自動設定，需手動：
```csharp
MessageFlags? flags = component.Message.Flags ?? MessageFlags.None;
flags |= MessageFlags.ComponentsV2;
await component.UpdateAsync(m => { m.Flags = flags; m.Components = cv2builder.Build(); });
```
來源：[SocketMessageComponent.cs](https://raw.githubusercontent.com/discord-net/Discord.Net/3.20.1/src/Discord.Net.WebSocket/Entities/Interaction/MessageComponents/SocketMessageComponent.cs)、[V3.18 breaking changes](https://docs.discordnet.dev/guides/breakings/v3.18.html)
- 官方限制：CV2 訊息不能用 `content`/`embeds`（flag 一旦設定不可移除），最多 40 個元件；`content` 欄位就算帶了也會被忽略/報錯。來源：[Component Reference 開頭](https://docs.discord.com/developers/components/reference)、[Using Message Components](https://docs.discord.com/developers/components/using-message-components)

### 6.4 建議架構（本專案）
1. **狀態不進記憶體**：`userSelections[userId] = { fromStopId, toStopId, createdAt }` 存 SQLite/Redis（附 TTL）。
2. **custom_id 只放必要輕量鍵**：如 `sel:from:{stopId}` / `sel:to:{stopId}` / `route:{fromId}:{toId}:{page}`，控制在 100 字元內。
3. 選單流程全用 **ephemeral**（`RespondAsync(..., ephemeral: true)` / `DeferAsync(true)`），避免公開訊息累積與被他人誤點。
4. 互動處理用 `ComponentInteractionAttribute` 樣式（重啟後自動可用），或 `ButtonExecuted`/`SelectMenuExecuted` 事件 + 解析 custom_id；避免依賴「這則訊息是我幾分鐘前建立」的記憶體假設。
5. 精確查詢結果放快取（key = from+to），並在回應前 `DeferAsync()`，把可能超過 3 秒的 DB/外部 API 查詢放到 defer 之後。

---

## 7. 單一 gateway client 的狀態壽命與 GatewayIntents

### 7.1 `GatewayIntents.None` 夠不夠？
- **技術上夠用**：官方 intent 清單開頭明寫「Any events **not** listed means it's not associated with an intent and **will always be sent** to your app.」，而 `INTERACTION_CREATE` 不在任何 intent 之下 → slash command 與元件互動**不需要任何 intent**，也不需要 `MESSAGE_CONTENT`（`MESSAGE_CONTENT` 只影響 message 的 `content`/`embeds`/`attachments`/`components`/`poll` 欄位）。來源：[Gateway §List of Intents](https://docs.discord.com/developers/events/gateway#gateway-intents)、[Message Content Intent](https://docs.discord.com/developers/events/gateway#message-content-intent)
- **實務上建議至少加 `GatewayIntents.Guilds`（1<<0）**：`GUILD_CREATE`/`GUILD_UPDATE`/`CHANNEL_CREATE`/`CHANNEL_UPDATE`/… 都屬 GUILDS；沒有它，socket client 的 guild/channel 快取不會被填充（`client.GetGuild(id)` 可能拿不到；REST 與 interaction payload 自帶的 `guild_id`/`channel_id` 不受影響）。
- Discord.Net 的預設值是 `GatewayIntents.AllUnprivileged`（不是 None）：`public GatewayIntents GatewayIntents { get; set; } = GatewayIntents.AllUnprivileged;`。來源：[DiscordSocketConfig.cs](https://raw.githubusercontent.com/discord-net/Discord.Net/3.20.1/src/Discord.Net.WebSocket/DiscordSocketConfig.cs)、[GatewayIntents API](https://docs.discordnet.dev/api/Discord.GatewayIntents.html)
- 若你要用 gateway 看到「自己送出的 ephemeral 訊息」，官方警告 ephemeral 訊息綁在 **`DIRECT_MESSAGES`** intent 上（訊息物件不含 `guild_id`/`member`）。來源：[Gateway Events §Messages](https://docs.discord.com/developers/events/gateway-events#messages)
- `GatewayIntents.None = 0` 在 Discord.Net 的定義為「This intent includes no events」。來源：[GatewayIntents API](https://docs.discordnet.dev/api/Discord.GatewayIntents.html)

### 7.2 記憶體狀態可以放多久？
- **沒有官方時間上限**：Discord 不托管你的狀態；單一 gateway client 的 in-memory 狀態壽命＝**行程存活時間**。真正的殺手是重啟/部署、崩潰、多實例（同一 token 起兩個 client 會互搶 session）。
- 官方 Tracking State 建議：「only storing data in memory that are *needed*」。來源：[Gateway §Tracking State](https://docs.discord.com/developers/events/gateway#tracking-state)
- Discord.Net 預設幾乎不佔記憶體：`MessageCacheSize = 0`、`AuditLogCacheSize = 0`、`AlwaysDownloadUsers = false`、`LargeThreshold = 250`、`HandlerTimeout = 3000`（事件處理逾時警告）、`ConnectionTimeout = 30000`、`MaxWaitBetweenGuildAvailablesBeforeReady = 10000`。來源：[DiscordSocketConfig.cs](https://raw.githubusercontent.com/discord-net/Discord.Net/3.20.1/src/Discord.Net.WebSocket/DiscordSocketConfig.cs)
- 相關硬限制（會影響長時間運作）：
  - **1000 次 IDENTIFY / 24 小時**（全域、跨 shard；達上限會終止所有 session **並重設 bot token**）。來源：[Gateway §Identifying](https://docs.discord.com/developers/events/gateway#identifying)
  - session start limit（`total=1000`、`remaining`、`reset_after`、`max_concurrency`）。來源：[Session Start Limit Object](https://docs.discord.com/developers/events/gateway#session-start-limit-object)
  - **每個 shard 最多 2500 guilds**，2500+ 必須 sharding。來源：[Gateway §Sharding](https://docs.discord.com/developers/events/gateway#sharding)
  - 每個連線每 60 秒最多送 **120** 個 gateway event。來源：[Gateway §Rate Limiting](https://docs.discord.com/developers/events/gateway#rate-limiting)
  - interaction token 15 分鐘；**元件本身（訊息上的按鈕/選單）只要訊息還在就能再被點**，官方未記載元件到期時間 → 「永久有效」屬 **未確認**（官方只規範 token）。

---

## 8. 速率限制（含 DM）

| 項目 | 數值 / 規則 | 來源 |
|---|---|---|
| **Global rate limit** | **每個 bot 每秒 50 次請求**（未帶 authorization 則依 IP）；與 per-route 限制獨立 | [Global Rate Limit](https://docs.discord.com/developers/topics/rate-limits#global-rate-limit) |
| Interaction endpoints | **不**受 bot global rate limit 限制（`POST /interactions/{id}/{token}/callback` 等） | 同上；亦見 [Receiving and Responding §Endpoints](https://docs.discord.com/developers/interactions/receiving-and-responding#endpoints) |
| Per-route / bucket | 依 route + HTTP method，並以 `X-RateLimit-Bucket` 共用 bucket；top-level resource 目前限 `channel_id`、`guild_id`、`webhook_id`（因此**不同 channel 各自計算**） | [Rate Limits 開頭](https://docs.discord.com/developers/topics/rate-limits) |
| Headers | `X-RateLimit-Limit` / `-Remaining` / `-Reset` / `-Reset-After` / `-Bucket`；429 時另帶 `X-RateLimit-Global`、`X-RateLimit-Scope`（`user` / `global` / `shared`） | [Header Format](https://docs.discord.com/developers/topics/rate-limits#header-format) |
| 429 處理 | 依 `Retry-After` header 或 `retry_after` 欄位重試；官方明言**不要 hardcode** 限制，要用回應標頭 | [Exceeding A Rate Limit](https://docs.discord.com/developers/topics/rate-limits#exceeding-a-rate-limit) |
| Invalid request / Cloudflare ban | **每 10 分鐘 10,000 次無效請求**（401/403/429）；`X-RateLimit-Scope: shared` 的 429 不計入 | [Invalid Request Limit](https://docs.discord.com/developers/topics/rate-limits#invalid-request-limit-aka-cloudflare-bans) |
| **DM 專屬限制** | 官方**沒有**數字化的 DM 速率限制，但有**明文警告**：`POST /users/@me/channels`（Create DM）「**You should not use this endpoint to DM everyone in a server about something. DMs should generally be initiated by a user action. If you open a significant amount of DMs too quickly, your bot may be rate limited or blocked from opening new ones.**」→ 大量主動 DM 有被限制/封鎖開 DM 的風險 | [Create DM](https://docs.discord.com/developers/resources/user)、[Rate Limits](https://docs.discord.com/developers/topics/rate-limits) |
| Group DM | **同一個端點限制「10 active group DMs」** | [Create Group DM](https://docs.discord.com/developers/resources/user) |
| DM 是否比 guild channel 更嚴 | 官方**未**如此記載；DM 只是另一種 channel，`/channels/{channel.id}/messages` 以 `channel_id` 為 top-level resource → **每個 DM channel 各自 bucket**，但仍共用全域 50 rps | [Rate Limits](https://docs.discord.com/developers/topics/rate-limits) |
| slowmode | `rate_limit_per_user`（0–21600 秒）**對 bot 無效**（「bots, as well as users with the permission BYPASS_SLOWMODE, are unaffected」），且 Modify Channel 表格不含 DM | [Channel Resource](https://docs.discord.com/developers/resources/channel) |
| 「每 channel 5 訊息/5 秒」等社群數字 | **未確認**：官方 Rate Limits 頁**完全沒有** per-channel 訊息速率數字，Execute Webhook 頁也沒有；唯一官方要求是「讀 header、不要 hardcode」 | [Rate Limits](https://docs.discord.com/developers/topics/rate-limits)、[Execute Webhook](https://docs.discord.com/developers/resources/webhook) |
| Cloudflare ban 適用範圍 | 限制綁在 **IP** 上 → 同一台主機的多個 shard/行程共用這個額度 | [Invalid Request Limit](https://docs.discord.com/developers/topics/rate-limits#invalid-request-limit-aka-cloudflare-bans) |
| Discord.Net 支援 | `DiscordConfig.DefaultRatelimitCallback`（`Func<IRateLimitInfo, Task>`）可全域掛 hook 記錄限流；`UseSystemClock`（預設 true）決定用 `X-RateLimit-Reset` 或 `Reset-After`；`RetryMode`（`DefaultRetryMode = AlwaysRetry`） | [DiscordConfig API](https://docs.discordnet.dev/api/Discord.DiscordConfig.html)、[DiscordConfig.cs](https://raw.githubusercontent.com/discord-net/Discord.Net/3.20.1/src/Discord.Net.Core/DiscordConfig.cs) |

**對「一次通知很多使用者」的實務含意**
1. 官方對「大量主動開 DM」有**明文警告**（見上表）：DMs 應由使用者行為發起；短時間開大量 DM 可能被限流或**被封鎖開新 DM**——這比 50 rps 的數字更值得當設計約束。
2. 主動 DM 通知**不是** interaction response，會計入 **50 rps 全域**限制（interaction 端點的豁免不適用）。
3. 每個使用者一個 DM bucket，但全域 50 rps 是共同上限 → 必須**自己排隊／節流**（例如以 40 rps 為目標、併發限制、指數退避），並尊重 `retry_after`。
4. 大量通知時**最危險的不是 429 本身，而是 401/403/429 累積成 10,000/10 分鐘的 Cloudflare ban（且綁 IP）**：對已關閉 DM 的使用者（403）或已失效 token（401）務必記下並跳過，不要重試。
5. 若只是「在頻道公告」，用一則訊息（或 webhook）遠比逐一 DM 便宜；互動式查詢本身建議以 **ephemeral** 回應，避免額外訊息流量。

---

## 9. 未確認清單（不可當事實使用）

1. **autocomplete 的觸發頻率細節**（是否每次按鍵、最少字元數）— 官方未載明。
2. **autocomplete 是否有獨立的更短截止時間** — 官方 autocomplete 章節無秒數，只能沿用通則 3 秒。
3. **3.18.0 及更早版本的完整 TFM 清單**（只知道 3.19+ 為 net8/9/10；3.17.0 加入 net8/net9）。
4. **Discord.Net 對 `AutocompleteResult.Value` 實際強制的字元數**（只知道 3.19.0-beta.1 修了「沒有長度限制」，推測為 100）。
5. **legacy 訊息「5 個 Action Row / 每訊息」的現行官方條文位置** — 現行 reference 的 Legacy 章節因文件過長未能完整抓取；此數字來自 Discord 官方**舊版**文件（discord-api-docs 1d09e3d 版）與 Discord.Net 指南的旁證。
6. **CV2「所有元件文字合計 ≤4000 字元」** — 僅見於第三方 mirror（docs.discord.food），未在官方頁面確認。
7. **元件（訊息上的按鈕/選單）「永久有效、無到期」** — 官方只規範 interaction token 的 15 分鐘，未對元件本身寫 TTL。
8. **DM 專屬速率限制、per-channel 5/5s、bulk DM 限制** — 官方文件未載明。
9. **DSharpPlus「Labs」fork／org** — `DSharpPlus-Labs` 的 GitHub org/user 皆 404，找不到一手來源。
10. **Discord.Net v4 nightly 是否存在於 BaGet/GitHub Packages**（套件本身確定不存在；nightly feed 未查詢）。v4 分支名稱 `4.0`/`5.0`/`v4/message-builder`/`v4/state-cache-providers` 已由 GitHub branches API 查核。
11. **DSharpPlus 2023/2024 維護團隊動盪的官方公告** — 找不到一手聲明；僅能確認 2026 年仍在活躍開發。
12. **Channels Resource 的「Create Message」章節**（該頁約 84 KB，抓取工具在四個鏡像都固定丟掉同一段中間內容）→ 若該章節另有速率說明，屬 **未確認**；本報告第 8 節結論建立在官方 Rate Limits 頁（已完整取得）。

---

## 10. 主要來源

- NuGet：<https://www.nuget.org/packages/Discord.Net>
- GitHub Releases / commits：<https://github.com/discord-net/Discord.Net/releases>、<https://api.github.com/repos/discord-net/Discord.Net/commits?per_page=5>
- Discord.Net 文件：<https://docs.discordnet.dev/>（Changelog、Interaction Service Intro、Autocompletion、Components V2 Intro/Advanced/Interaction、V3.18 breaking、DI FAQ、API 參考）
- Discord.Net 原始碼（tag 3.20.1）：`SocketMessageComponent.cs`、`SocketMessageComponentData.cs`、`InteractionHelper.cs`、`DiscordSocketConfig.cs`、`DiscordConfig.cs`、`GatewayIntents.cs`
- Discord 官方文件（2026-09 版，docs.discord.com）：Component Reference、Using Message Components、Components Overview、Interactions/Receiving and Responding、Interactions/Application Commands、Resources/Message、Resources/Webhook、Topics/Rate Limits、Events/Gateway、Events/Gateway Events、Change Log
- Discord 官方舊版文件（存檔）：<https://github.com/discord/discord-api-docs/blob/1d09e3da359757db749f7e52e58330b8ffdcb802/docs/interactions/Message_Components.md>
