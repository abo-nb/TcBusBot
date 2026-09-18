# TcBusBot — 台中公車到站通知 Discord Bot

用 TDX（Transport Data eXchange）的台中市公車資料，
讓使用者在 Discord 上「選起點 → 選目的地 → 挑可搭路線 → 訂閱 → 公車快到時收到通知」。

| 文件 | 內容 |
| --- | --- |
| `docs/01-TDX-研究報告.md` | TDX HTTP API 與 MQTT 的實測研究（含真實 payload、點數公式） |
| `docs/02-CSharp-實作方案.md` | 完整實作方案（架構、資料模型、演算法、Discord 流程） |
| `reference/` | 官方第一手素材（OpenAPI 全文、官方範例碼、研究筆記） |

---

## 目前進度

| 階段 | 狀態 |
| --- | --- |
| M0 專案骨架 | ✅ |
| M1 TDX 資料模型與解析 | ✅ |
| M2 靜態索引 + 模糊搜尋 + 候選集合匹配 | ✅ 637 項離線驗收測試全過 |
| **M3 Discord UI** | ✅ 已實作（含訂閱組；實機驗收中） |
| M4 即時輪詢 | ✅ 程式完成（輪詢迴圈 + 到站時間總表），實際運作需 TDX 金鑰 |
| **M7 Android APK**（掛在舊手機上） | ⏸ **擱置**（已實機驗證過，程式碼留著；改用 Render） |
| **M10 Render 部署** | ✅ 健康檢查端點 + 防休眠 + Dockerfile + `render.yaml`（端點有離線測試） |
| **M8 儲存後端**（MongoDB / SQLite / 文字檔） | ✅ 自動挑選 + 失敗退回本機，都有測試 |
| **M9 Termux**（手機上的控制台） | ✅ 發佈腳本 + 實機步驟（`publish-termux.ps1`） |
| **M11 AI 聊天**（OpenAI 相容 API ＋ Semantic Kernel） | ✅ @ 提及／回覆觸發、時間＋LLM 切段、每週全域 token 上限、工具呼叫真的能訂閱公車、**可以被馴服（每個伺服器自己的提示詞，`/rest` 可重設）**、**能幫使用者按面板按鈕**、**會偷聽後幾句（多人聊天時不會被忽略、也不會顯示假的「正在輸入」、看得到大家的暱稱）**（實測真實 API 通過） |
| M5 / M6 | ⬜ |

---

## 三種跑法

| | 桌面版（Windows） | **Render（雲端，推薦）** | Termux（手機上的控制台） |
| --- | --- | --- | --- |
| 專案 | `src/TcBusBot.Discord` | 同上（＋ `render.yaml`／Dockerfile） | 同上（發佈後複製到手機） |
| Bot 邏輯 | 同一份原始碼 | 同一份原始碼（`<Compile Include>` 共用） | 同一個發佈產物 |
| 啟動方式 | `dotnet run --project src\TcBusBot.Discord` | push 到 GitHub → Render 自動部署 | `sh run.sh`（有完整控制台） |
| 常駐機制 | 主控台視窗 | Web Service ＋ 自我 ping 防休眠 | `termux-wake-lock` + 背景執行 |
| 訂閱組儲存 | SQLite（內建 `winsqlite3.dll`） | MongoDB（容器沒有持久化磁碟） | 文字檔或 MongoDB |
| 適合 | 開發、除錯 | **24 小時掛著、不用自己顧機器** | 想要控制台、不想用雲端 |

> 📦 **Android APK（`src/TcBusBot.Mobile`）目前擱置**：程式碼與 `build-apk.ps1` 都還在，
> 只是不再納入方案建置（Render 這條路已經能 24 小時掛著，不需要自己的手機）。
> 要回來做的話：`dotnet sln add src\TcBusBot.Mobile\TcBusBot.Mobile.csproj` 之後
> 執行 `.\build-apk.ps1` 即可（當初已在實機驗證過）。

> ⚠️ **同一個 Discord Bot Token 不要同時在兩台機器上跑**：會有兩個閘道連線，
> 通知會重複送。要換機器時，先把舊的那台停掉。
>
> 💡 **設了 MongoDB 就沒有這個問題**（也解決「手機與電腦各一份訂閱組」）：
> 見下面的「訂閱組要放哪裡」。

---

## 快速開始

### 1. 建立 Discord Bot

1. 開 <https://discord.com/developers/applications> → **New Application**
2. 左側 **Bot** → **Reset Token** → 複製 Token
   （Privileged Gateway Intents 三個都**保持關閉**即可，本 Bot 不需要）
3. 左側 **OAuth2 → URL Generator**：
   - Scopes 勾 `bot` 與 `applications.commands`
   - 產生連結後把 Bot 邀進你的伺服器

### 2. 填設定檔

複製 `.env.example` 成 `.env`，填入 Token：

```ini
DISCORD_TOKEN=你的Token
```

`.env` **已經在 `.gitignore` 裡**，不會被提交。

支援這些寫法（`DotEnv` 解析器都能吃）：

```ini
DISCORD_TOKEN=abc          # 標準 dotenv
DISCORD_TOKEN="abc"        # 加引號
export DISCORD_TOKEN=abc   # shell 風格
$env:DISCORD_TOKEN=abc     # PowerShell 風格（可以直接貼 PowerShell 的設定行）
```

設定優先序：**命令列參數 &gt; 環境變數 &gt; `.env`**。

### 3. 啟動

```powershell
dotnet run --project src\TcBusBot.Discord
```

啟動時會印出設定摘要（Token 只顯示長度與來源，**不會印出值**）：

```
  .env 檔　　　：E:\Projects\TcBusBot\.env
  Discord Token：72 字元，來源：.env 的 DISCORD_TOKEN
  TDX 金鑰　　 ：未設定（使用內建資料集）
  資料來源　　 ：內建最小資料集
```

連上 Discord 後會印出邀請連結與可用指令。

> 連不上時 30 秒會自動結束並列出檢查清單（不會呆呆掛著）。
> 想看到 Discord.Net 的連線日誌，加 `--verbose`：
>
> ```powershell
> dotnet run --project src\TcBusBot.Discord -- --verbose
> ```

> ⚠️ **這個環境的建置注意事項**
> `dotnet run --project <專案>` 與單一專案 `dotnet build <專案.csproj>` 都正常。
> 但**在專案根目錄直接 `dotnet build`（方案層級、多節點平行建置）會靜默失敗**——
> 只印 `Build FAILED. 0 Error(s)` 而沒有任何錯誤訊息（沙箱環境的通訊限制）。
> 若要用方案層級建置，請加 `-m:1` 強制單節點：
>
> ```powershell
> dotnet build -m:1
> ```
>
> **另一個常見狀況**：Bot 還在執行時重新建置會失敗，錯誤是
> `The process cannot access the file '...TcBusBot.Core.dll' because it is being used by another process.`
> → 先按 Ctrl+C 停掉 Bot，再建置。

### 4. 在 Discord 試用

```
/bus panel     ← 開始設定（起點 → 目的地 → 搜尋路線 → 選路線 → 按「訂閱」→ 選通知時間）
/bus groups    ← 我的訂閱組（一次套用多個、合併、改名、刪除、復原）
/bus list      ← 查看我的訂閱（也可以在這裡取消）
/bus status    ← 看 Bot 狀態、資料來源、訂閱組數量
/bus next      ← 查看所有訂閱目前的到站時間
/bus end       ← 結束追蹤：一次取消全部訂閱（附「↩️ 復原」，按錯不用重設）
/say <內容>    ← 讓 Bot 幫你說一句話（無用小功能）
/ai status     ← AI 聊天狀態：模型、這一週用掉多少 token、這個頻道的記憶與偷聽狀態（含偷聽到的閒聊則數）
/ai learned    ← 看我學到了哪些規矩（這個伺服器專屬）
/ai pset       ← 管理員直接設定這個伺服器的提示詞（只有 LLM_ADMIN_IDS 的人能用）
/ai audit      ← 看最近的主人操作紀錄（只有主人看得到）
/ai forget     ← 忘掉這個頻道的 AI 對話記憶（不影響公車訂閱）
/rest          ← 重設這個伺服器學到的規矩（提示詞恢復預設）
```

AI 聊天不用指令：**@ 它、或回覆它的訊息**就會回話（見「AI 聊天」一節）。

流程：

```
/bus panel
  → [設定起點] → 輸入「台中車站」（打「台」或簡體「台中车站」都會找到「臺中車站」）
  → 勾選站名（可多選）→ [確認]
  → [設定目的地] → 輸入「靜宜大學」→ 勾選 → [確認]
  → [搜尋路線] → 勾選要訂閱的路線
  → [✅ 訂閱這 N 條路線]   ← 明確的訂閱動作
  → [2 分鐘] [5 分鐘] [10 分鐘] [15 分鐘] [30 分鐘]   ← 提前多久通知你（2 分鐘 = 我現在就要出門）
  → 完成時同時顯示「所有訂閱的到站時間（依剩餘時間排序）」
  → [🔄 重新整理] [模擬一則通知] [返回面板]
    [💾 存成訂閱組] [📂 我的訂閱組]
```

> 「💾 存成訂閱組」就在**完成訂閱那張卡片的第二列**（跟「看到站時間」時是同一組按鈕）。
> 面板上的「📋 我的訂閱」「📂 訂閱組」是另外兩個入口。

### 訂閱組

把常用的起訖點與路線存起來，下次一鍵套用（可以**一次勾多個**）：

```
[💾 存成訂閱組] → 輸入名稱（例如「上班通勤」）

📂 我的訂閱組                      ← 選單是多選，可以一次勾好幾個
▶ **上班通勤**　（用過 3 次）
　　臺中車站 → 靜宜大學
　　20 條路線 · 提前 10 分鐘
▶ **回老家**
　　國立臺中科技大學 → 大坑口
　　11 條路線 · 提前 15 分鐘
• **通勤全部**
　　**2 段行程**
　　　1. 靜宜大學 → 臺中車站（5 條）
　　　2. 臺中車站 → 靜宜大學（20 條）
　　25 條路線 · 提前 10 分鐘
— 共 3/20 組（依使用次數排序）｜已選 2 組。

[▼ 已選 2 組（可多選）]
[▶ 各自獨立訂閱 2 組] [🧩 合併成一個通知流（2 組）] [🔗 合併成新組] [✏️ 改名] [🗑 刪除]
[↩️ 復原：套用 2 個訂閱組] [返回面板]
```

### 一次套用多個組／合併／復原

| 按鈕 | 會建立什麼 | 通知行為 |
| --- | --- | --- |
| **▶ 各自獨立訂閱 N 組** | N 個訂閱（每組、每段各一個） | 各通知各的 |
| **🧩 合併成一個通知流** | 1 個訂閱，內含多段行程 | 從**所有**路線挑最快到的那一班，**只通知一次** |
| **🔗 合併成新組** | 不建訂閱，把勾選的組合併成**一個新的訂閱組**存起來 | 下次一鍵套用 |
| **↩️ 復原** | 還原上一個動作 | 按鈕上會寫出要復原什麼 |

**復原**可以連按（保留最近 10 個動作），涵蓋：批次套用、合併、批次刪除、改名。
合併時如果覆蓋了同名的舊組，復原會把舊內容寫回去。

> ⚠️ 復原紀錄在**記憶體**，Bot 重啟後就不能復原了（跟「訂閱重啟就消失」同一個已知限制）。
> 訂閱組本身存在 SQLite，重啟後還在。

**套用時會用目前的站序資料重新跑一次匹配**，不是直接複製舊訂閱 ——
所以路線改道或停駛時不會產生錯誤的訂閱（找不到的路線會列出並略過）。

**合併的規則**：起訖相同的段落會併成一段（同一條通勤路線存兩次不會變兩段），
路線依 `(RouteUid, Direction)` 去重，提前通知時間取**最大**的那個
（= 提醒最早；晚通知會讓人錯過公車，早通知只是多一則訊息）。

訂閱組存在 `tcbus.db`（SQLite），**重開 Bot 之後還在**。
這是專案裡唯一使用資料庫的地方：訂閱本身仍在記憶體（重啟消失，依需求），
只有使用者刻意存下來的範本需要持久化。
（舊版存的單段格式會自動升級成「1 段行程」，已存的組不會消失。）

完成訂閱後看到的到站時間總表：

```
⏱ 到站時間（依剩餘時間排序）
**300**（返程）　臺中車站(A月台)　**約 3 分鐘**（14:22）
**304**（返程）　干城站　**約 8 分鐘**（14:27）
**15**（去程）　國立臺中科技大學　尚未發車（05:40 發車）
**901**（返程）　國立臺中科技大學　末班車已過
**903**（返程）　國立臺中科技大學　目前查不到
— 臺中車站 → 靜宜大學　共 5 條訂閱　更新於 14:19:03
```

有預估時間的排前面（由近到遠），其餘排在後面。
**需要 TDX 金鑰才有真實資料**；沒有金鑰時會顯示模擬資料並明確標示。

因為一句話就能講完：**搜尋不是要求你輸入完整站名，而是模糊比對**。
`台中車站`、`台中火車站`、`Ａ月台`、`Taichung Station`、`中車站` 都會命中。

### 結束追蹤（`/bus end`）

不想再收通知時，不用一個一個取消：

```
🛑 已停止追蹤
你的訂閱已經全部取消，我不會再查詢這些路線的到站時間，也不會再通知你。
**按下面的「↩️ 復原」就可以全部放回來。**
【取消的訂閱群組】 1 個　【取消的訂閱】 20 筆
【不再查詢的上車站】 9 個　【不再查詢的路線】 2 條
— 復原紀錄只存在記憶體，Bot 重啟後就沒辦法復原了。要重新設定請用 /bus panel。

[↩️ 復原（把 1 組訂閱放回來）] [開始新訂閱]
```

**復原放回的是原本的物件，不是重新建立** —— 群組 id 與「這班車已經通知過」的去重狀態都保留，
所以復原之後不會突然被重複通知一次（`selftest` 第 19 節有專門驗這點）。
`/bus end` 只影響自己，也會順便把面板目前指向的訂閱清掉。

### `/say`：讓 Bot 幫你說一句話（無用小功能）

```
/say 公車快到了\n我先下樓
```

- 純粹好玩，跟公車無關；但用來**驗收通知通道**很好用：打一句話就知道 Bot 在這個頻道能不能發訊息
- **不留使用指令的痕跡**：訊息不會掛著「@某某 使用了 /say」的回應標頭；
  過程中的「正在思考…」只出現在指令使用者自己那邊，而且送出後就刪掉 ——
  頻道上只剩那句話本身
- 內容裡的 `\n` 會變成真正的換行（斜線指令的輸入框打不出多行）
- **關閉所有 mention** —— 不能拿它去 `@everyone` 洗頻
- 想限制誰能用：`SAY_ALLOWED_USERS=<使用者ID>,<使用者ID>`（**不設定 = 所有人都能用**）

### AI 聊天（OpenAI 相容 API ＋ Semantic Kernel）

**@ 它、或回覆它的訊息**就會回話：

```
小明：@笨蛋猫猫搭公车 台中車站到靜宜大學要搭幾號？
Bot ：300 或 304 都可以，300 走臺灣大道、304 停的站比較多。
      即時到站我查不到，你可以用 /bus panel 訂閱，快到時我會通知你。

小明：（回覆 Bot 的訊息）那大概多久一班？
Bot ：（接著上面那一段講，知道「那」指的是 300/304）
```

沒有設定 `LLM_API_KEY` 時**整組功能不會啟用**，公車功能完全不受影響。

#### 上下文怎麼決定（三段規則）

| 情況 | 行為 |
| --- | --- |
| 距離上次說話**超過 `LLM_SEGMENT_GAP_MINUTES`**（預設 30 分） | 直接當成新的一段，**不帶**舊上下文，也不花錢問 LLM |
| 時間內 | 由 **LLM 判斷**「這是接續還是換話題」；判斷換話題就開新的一段、不帶舊上下文 |
| **使用者回覆了某一則訊息** | 回到那一則所屬的段落（即使它很舊、即使超過時間門檻），被回覆的內容一定會在上下文裡 |

- **不同伺服器一定不相通**：記憶的 key 是 (伺服器, 頻道)，連索引都是分開的
- 每個頻道各自一條對話；`/ai forget` 可以清掉這個頻道的記憶
- 判斷失敗（模型亂回答、連不上）時**沿用目前段落** —— 突然失憶比多帶一點上下文更糟

#### 每週 token 上限

- **全域一份**（整個 Bot 共用），`LLM_WEEKLY_TOKENS`（預設 300,000；`0` = 不限）
- 重置時間固定是 **UTC 週一 00:00**（台灣時間週一早上 8 點），`/ai status` 會顯示
- 送出**之前**就用估算擋掉會超額的呼叫（不會先花錢再說），超過就回「額度用完了」+ 重置時間
- 用量**會寫進資料庫**（跟訂閱組同一條後端鏈），所以重啟不會歸零
- `/ai status` 會列出「哪個伺服器花了多少」，因為全域額度的代價就是需要看得出誰在花

> 上限是軟性的：最後一次呼叫可能稍微超過（最多一次回覆的量）。

#### ⚠️ 必須打開 Message Content Intent

AI 聊天要讀訊息內容，這在 Discord 是**特權意圖**，必須手動開啟：

1. <https://discord.com/developers/applications> → 選你的 Application
2. 左側 **Bot** → **Privileged Gateway Intents** → 打開 **Message Content Intent**
3. Save Changes → 重新啟動 Bot

**沒開的後果**：閘道會用 `4014 Disallowed intent` 拒絕連線，Bot 完全連不上。
程式在偵測到這個錯誤時會直接把上面三步印在 console（不會只丟一行看不懂的訊息）。

不想開也可以：`--no-message-intent` → 只用公車功能（AI 聊天會關掉）。

#### 工具：LLM 可以真的幫你訂閱公車

**@ 它說「幫我訂台中車站到靜宜大學的公車」就會真的訂下去**（不是只會講）：

| 工具 | 做什麼 | 會改資料嗎 |
| --- | --- | --- |
| `search_stops` | 查站牌的正確名稱＋**有哪幾條路線經過** | 不會 |
| `search_routes` | 用**路線號碼**查（「300 到哪裡」「304 幾站」） | 不會 |
| `find_routes` | 查「A 到 B」可以搭哪些車；**沒有直達時給「轉一次」的走法** | 不會 |
| `list_subscriptions` | 列出已經訂了什麼 | 不會 |
| `next_arrivals` | 查還要幾分鐘到站（**所有訂閱組都會查**） | 不會 |
| `subscribe_bus` | **真的建立訂閱**（與 `/bus panel` 同一套站序匹配邏輯） | ✅ 會 |
| `cancel_all_subscriptions` | **真的取消全部訂閱**（附「↩️ 復原」按鈕） | ✅ 會 |
| `remember_rule` / `list_rules` / `forget_rule` | **修改自己在這個伺服器的提示詞**（見下一節） | ✅ 會（只影響該伺服器） |
| `open_panel` / `set_origin` / `set_destination` | **幫使用者按面板上的按鈕**（開面板、設定起點／目的地）（見「讓模型幫你按按鈕」） | 面板狀態（記憶體） |
| `search_panel_routes` / `subscribe_panel_routes` / `undo_last_action` | 用面板搜路線、按「訂閱」、按「復原」 | ✅ 會（訂閱） |

實測（真實 API）：

```
小明：幫我訂閱從台中車站到靜宜大學的公車，提前 10 分鐘通知
Bot ：訂好了 ✅ 共 2 條路線…300 在臺中車站(A月台)上車、304 在臺灣大道上車
      🔧 我實際做了：search_stops（台中車站）、search_stops（靜宜大學）、
                     subscribe_bus（…→ 靜宜大學(專用道)，2 筆訂閱）

小明：靜宜大學到逢甲大學怎麼去？
Bot ：沒有直達。可以搭 300 到秋紅谷（11 站）轉 63，再 5 站到逢甲大學 ✅
```

- 模型一次可以連續呼叫多個工具（先查站牌、再訂閱），全部在**一次**呼叫裡完成
- **只能動「問的人」的資料**：工具參數裡沒有任何「幫誰訂」的欄位
- 模型**真的動了資料**時，回覆後面會附一行 `🔧 我實際做了：…` 讓你核對
- 站名不確定時會**先問你**，不會自己猜一個看起來像的
- 不想讓它動手：`LLM_TOOLS=false`（只會聊天）

> **AI 與 `/bus panel` 用的是同一套站牌解析**（`StopPicks`）。
> 這一點很重要：台中同一條路上常有**同名但不同 StopUID** 的月台
> （「臺中車站」有 38 個月台、去回程各登記一組），
> 只要有一邊自己算候選站，就會出現「停在其他月台的路線整條找不到」。
> 實測（真實資料集 14,036 站牌）：AI 說「台中車站 → 靜宜大學」→ 解析出 **38 個候選站牌**、
> 找出 **20 條路線**，與面板走同一條路徑得到的結果完全相同。
> `--dryrun` 有一項「站牌解析檢查」會持續比對兩邊（含每個選單選項值都解析得出站牌）。

#### 讓模型幫使用者「按按鈕」（UI 動作）

使用者講的話與面板上的按鈕**是同一件事**，所以模型不必叫使用者自己去點：

```
小明：@笨蛋猫猫搭公车 幫我開面板，起點台中車站、終點靜宜大學，然後找路線
Bot ：面板開好了 ✅ 起點 38 個月台、終點 20 個月台，找到 20 條路線
      🔧 我實際做了：open_panel、set_origin（臺中車站）、set_destination（靜宜大學）、
                     search_panel_routes（20 條）

小明：@笨蛋猫猫搭公车 幫我訂 300 跟 304，提前 10 分鐘
Bot ：訂好了 ✅（訂閱卡上有「↩️ 復原」可以按）
      🔧 我實際做了：subscribe_panel_routes（300、304）
```

| 工具 | 對應的面板動作 |
| --- | --- |
| `open_panel` | 開出設定面板（**面板留在使用者的頻道**，他可以直接接著點） |
| `set_origin` / `set_destination` | 設定起點／目的地（站名模糊搜尋、同名月台整組一起處理） |
| `search_panel_routes` | 按「搜尋路線」 |
| `subscribe_panel_routes` | 按「✅ 訂閱這 N 條路線」 |
| `undo_last_action` | 按「↩️ 復原」（例如「訂錯了」「取消剛剛那個」） |

- **走的是同一份面板狀態**：模型改的就是使用者畫面上那個面板，不是另一套影子設定
  —— 所以它按完之後使用者看到的畫面**就是已經改好的**（訂閱完成時按鈕與「復原」都會出現）
- 站牌解析同樣走 `StopPicks`，跟 `/bus panel` 完全一致（不會出現 AI 找得到、面板找不到）
- 不想要它動手按：`LLM_UI_ACTIONS=false`（只留查公車與訂閱工具）

#### 偷聽模式（回完話後繼續跟著聊）

模型回完話以後，同一個頻道**會繼續跟著聽**，這樣不用每句都 @ 它：

```
小明：@笨蛋猫猫搭公车 300 到靜宜大學大概幾分鐘？
Bot ：我查不到即時到站，訂閱之後快到會通知你
      （開始偷聽：最多判斷 12 則／安靜 120 秒後回到「等 @」）

阿華：那 304 呢？                    ← 沒 @ 它，但在問它剛講的事
Bot ：304 也有到，停的站比較多        ← ✅ 接話（REPLY）
      🔧 我實際做了：search_routes（304）

阿美：@小華 你幾點到？                ← @ 了別人 → 這句不插話，**但還繼續聽**
阿美：我今天不想搭公車了              ← 跟它無關 → 安靜不出聲（SKIP），繼續聽
阿美：你們晚餐要吃什麼？
小華：都可以啊，不要太遠
小華：那吃火鍋好了，七點門口集合      ← 別人之間的邀約 → 退出（STOP），回到等 @

小明：@笨蛋猫猫搭公车 我們剛剛在聊什麼？
Bot ：你們剛剛在聊晚餐要吃什麼，還有你問 300 到靜宜大學要多久
      ← ✅ 閒聊有留下來當上下文（不是只有它回過的訊息才記得）
```

判斷有三種結果（**不是「回話／不理」二選一**）：

| 判斷 | 什麼時候 | 行為 |
| --- | --- | --- |
| **REPLY** | 在問它、延續它剛講的事（即使沒 @ 它） | 回話 |
| **SKIP** | 沒 @ 它、但這群人還在聊同一件事／聽不出來對誰說／還沒輪到它 | **不出聲，留在頻道裡繼續聽** |
| **STOP** | 明顯是別人之間的對話（@ 別人、互相邀約、私事、完全離題） | 停止偷聽，回到「等 @」 |

- **判斷由 LLM 做**（一次很便宜的短判斷：輸出上限 8 tokens；實測每次約 305~313 in／2~3 out）
- **不確定 → SKIP**：不插話，但**也不要離開**（離開之後就再也接不上了）
- **@ 了別人 → 完全不問模型**：那是「在跟那個人說話」的鐵證，不插話、**不花任何 token**，
  而且**繼續偷聽** —— 多人頻道裡 @ 別人太常見，以前在這裡直接退出，
  結果就是「有人 @ 別人之後，Bot 之後的訊息全部不理」
- **閒聊會留下來當上下文**：不是只有「它回過的訊息」才記。偷聽到的訊息標成 `[閒聊]`
  進到提示詞（並附上一段說明「那是別人之間的對話、不要回它們」），
  所以之後被 @ 時它接得上大家在聊什麼（`LLM_EAVESDROP_CONTEXT=false` 可關）
- **兩個上限同時管**：`LLM_EAVESDROP_MESSAGES`（最多判斷幾則，**成本上限**）與
  `LLM_EAVESDROP_SECONDS`（**閒置**幾秒後停止，每一則訊息都會往後延，不是從開窗起算）；
  `/ai status` 看得到還剩幾次判斷／剩幾秒
- **沒在偷聽時完全免費**：這一關寫在核心流程裡（不是只寫在 Discord 那一層），
  沒有窗口就**連判斷都不問**、也不回話（`selftest` 有專門驗「LLM 呼叫 0 次」）
- 需要 **Message Content Intent**（偷聽到的訊息才有內容；沒開只會看到空的）
- **不會出現「假的正在輸入」**：偷聽的訊息要等判斷結果確定是「要回話」才顯示
  「正在輸入…」；判斷成 SKIP／STOP 時頻道上**完全沒有動靜** ——
  否則旁人會看到「Bot 顯示正在輸入，然後什麼都沒說」，看起來像它正在回應誰、或像它壞了
- 不想要：`LLM_EAVESDROP=false`；上限設 `0` 也等於關閉

> ⚠️ 這裡修過一個「功能寫好了但整段接不到」的 bug：判斷器、窗口、提示詞全都在，
> 但入口那行 `if (!mentioned && !replyAddressed) return;` 會在**到達偷聽之前**就返回 ——
> 所以「回完話後繼續聽」從來沒有真的生效過，使用者看到的是
> 「@ 它講一句之後，其他人再講什麼它都當作沒看到」。
> 現在入口判斷抽成 `ShouldHandle`，而且 `--dryrun` 會用 **IL 掃描**確認
> 入口真的呼叫了它、也真的查了偷聽窗口（這種「接不到」的 bug，元件檢查與單元測試都抓不到）。

#### 可以「馴服」它（只限這個伺服器）

直接 @ 它教它規矩，它會**修改自己在這個伺服器的提示詞**：

```
小明：@笨蛋猫猫搭公车 記住：以後回話都要在最後加一個「喵」
Bot ：好，我記住了：以後回話都要在最後加一個「喵」
      🔧 我實際做了：remember_rule（回話時都要在最後加一個「喵」）

小明：@笨蛋猫猫搭公车 記住：<:cat_cry:123456789> 這個表情是委屈，看到要安慰我
Bot ：記住了：cat_cry 是委屈的意思 ✅

小明：@笨蛋猫猫搭公车 我教過你什麼？
Bot ：你教過我兩件事喵：1. 回話時都要在最後加一個「喵」 2. cat_cry 是委屈的意思…
```

| 指令 | 做什麼 |
| --- | --- |
| `/ai learned` | 看這個伺服器目前學到哪些規矩（含自訂表情的意思） |
| `/ai pset` | **後台指定的管理員**（`LLM_ADMIN_IDS`）直接設定這個伺服器的提示詞（見下） |
| `/rest` | **重設**這個伺服器學到的規矩（可選順便清掉這個頻道的對話記憶） |
| `/ai audit` | 看最近的主人操作紀錄（**只有主人看得到**） |

##### `/ai pset`：管理員直接寫（不用教模型）

```
/ai pset text:一律用繁體中文、回答前先確認站名          ← 追加一條（mode 預設 Append）
/ai pset text:只幫大家查台中公車，其他一律婉拒 mode:Replace ← 覆蓋掉這個伺服器現有的全部
/ai pset clear:True                                     ← 清空
/ai pset                                                ← 顯示目前的設定
```

- **只有 `LLM_ADMIN_IDS` 名單上的人能用**（fail closed：沒設名單＝沒人能用）；
  ⚠️ 不需要帶 `LLM_ADMIN_KEY` —— 斜線指令是 Discord 直接送到 Bot 的互動，
  **模型碰不到**（那把 key 是為了擋「模型被騙去打主人的指令」，兩者風險不同）
- 寫進去的是**跟模型自己學的同一份**（`/ai learned` 看得到、`/rest` 清得掉）
  —— 不做第二套，所以不會出現「兩個地方都寫了、不知道哪個生效」
- 一樣受 `LLM_MAX_GUILD_RULES`（40 條）與 `LLM_MAX_RULE_CHARS`（300 字）限制
- **覆蓋模式是安全的**：內容寫不進去（太長）時**舊的不會被吃掉**（會自動還原）
- 每次都會留**稽核紀錄**（`/ai audit` 看得到誰、什麼時候、改了什麼），console 也會有一行
- 適合「一開始就寫好一整段」；使用者隨口教一句的情況用 `@我 記住：…` 就好

##### 把某個伺服器的設定複製到另一個（`from:`）

原本那個伺服器教了 40 條，新伺服器不想重教一次：

```
/ai pset from:123456789012345678 mode:Replace    ← 直接整套搬過來（覆蓋新伺服器現有的）
/ai pset from:123456789012345678                 ← 追加（跳過重複的）
/ai pset from:我的舊伺服器 mode:Replace           ← 也可以打伺服器名稱（同名時會要求改用 ID）
```

- **來源 ID 怎麼拿**：先在來源伺服器打 `/ai pset`，那張卡片上就會顯示「這個伺服器 ID」，複製它
  （或開啟 Discord 開發者模式 → 對伺服器按右鍵 → 複製伺服器 ID）
- **來源不會被動到**（是複製不是搬移）；重複複製不會長出重複的規則
- 追加時如果撞到上限（40 條），會明確告訴你「**N 條因為超過上限沒帶過來**」，
  而不是默默少掉；這時改用 `mode:Replace` 就是整組換掉
- 每次複製都會留稽核紀錄（`/ai audit` 看得到「從哪個伺服器搬了幾條」）

##### 別人要授權時：**在來源伺服器按按鈕同意**（不用改環境變數）

自訂提示詞是**那個伺服器的東西**（常常包含只有那裡才有的自訂表情名稱、稱呼、內規），
所以「要不要給別的伺服器」應該由**那邊的人**決定：

```
（在 B 伺服器）小明：/ai pset from:A伺服器ID mode:Replace

→ 我（Bot）在 A 伺服器發一則通知（誰發起、要複製幾條、請求編號）：

  🤝 有人在別的伺服器請求授權
  「B」的 @小明 想把這個伺服器的自訂提示詞複製過去（覆蓋他那邊原本的）。
  同意的話：他那邊會拿到這 40 條設定，而且接下來 30 天可以直接用
  /ai pset 維護他那邊的設定（不會拿到全域權限）。
  [✅ 同意並複製] [🚫 拒絕]

→ A 那邊的人按「✅ 同意並複製」之後：
  • A 的設定直接複製到 B
  • 小明拿到「**只限 B**」的 `/ai pset` 權限 30 天（到期時間會顯示）
  • B 的頻道會收到一則完成回報，A 的通知會被改成「誰在什麼時候同意了什麼」
```

| 誰能按那顆按鈕 | 說明 |
| --- | --- |
| 來源伺服器的**擁有者** | 最自然的「在伺服器裡驗證」 |
| 有 **Manage Server** 權限的人 | 跟 Discord 的權限一致，不用另外維護名單 |
| `LLM_ADMIN_IDS` 名單上的人 | 主機端自己人（跨所有伺服器） |

- **不會給全域權限**：授權只開「那一個伺服器」，跟 `LLM_ADMIN_IDS`（會順便拿到全域設定權）分開
- **要能發起請求**：你在**目標**伺服器要有「管理伺服器」權限
  （不然任何人按一下就能讓別人的伺服器跳通知）
- 請求 **24 小時**內有效、授權 **30 天**；同意或拒絕都會改寫那則通知，頻道上留得下紀錄
- 如果你同時是主機主人又在來源伺服器裡，`/ai pset from:` 會**直接執行**（不用按按鈕）




- **只對那個伺服器生效**：A 伺服器學到的東西在 B 伺服器完全看不到（實測 B 會說「這個伺服器沒有記下任何規矩」）
- **不會動到主機的 `LLM_SYSTEM_PROMPT`** —— 那是主機端的設定，改了會影響所有伺服器
- 學到的規則接在提示詞**最後面**（模型對最後的指示最聽話，才壓得過前面的通用規則）
- 每個伺服器最多 **40 條**、每條 **300 字**（不會被拿來當無限記事本）；完全相同的不會重複加
  —— 這幾個數字全部可以用環境變數調（見下方「學到的提示詞要存多少」）
- **自訂表情**（`:名字:`）每個伺服器都不一樣，所以這種知識只能存在該伺服器、也只有該伺服器的人能教
- 學到的內容會**寫進資料庫**（跟訂閱組同一條後端鏈），重啟後還在；`/rest` 會把清掉的內容原文列出來，想留就複製走

#### AI 好像沒接上？用 `/ai test` 與 `/ai status` 查

「它沒回我」有很多種可能，先用這兩個指令分辨：

| 指令 | 做什麼 |
| --- | --- |
| `/ai test` | **真的呼叫一次**（只給管理員用）。成功就回覆內容、模型、用量、往返時間；失敗就把 **API 的原話**貼出來，並依錯誤給方向（401 = 金鑰無效、402 = 餘額不足、404 = 模型名稱打錯、逾時…） |
| `/ai status` | 看「**最近幾次呼叫**」：最後一次成功/失敗的模型、用量、耗時，以及**最後一次失敗的完整訊息** |

其他的「沒回話」其實是設計如此，不是壞掉：

- **私訊不回**（`LLM_ALLOW_DM=false`）：現在收到私訊會回一句說明，並提醒到頻道 @ 它
- **偷聽的訊息判斷成「不是在跟我說話」** → 安靜不出聲（這是刻意的，見上一節）
- **額度用完**：會回一句「這個星期的 AI 額度用完了」，`/ai status` 看得到剩下多少
- **很多人同時說話**：會排隊（見上一節），`/ai status`／`/health` 看得到排隊與丟棄次數

#### 學到的提示詞要存多少（環境變數可調）

「@ 它教它規矩」這件事有兩個成本：**提示詞會變長**（每次對話的錢）與**別人能叫它記多少東西**
（公開伺服器的濫用風險）。所以容量上限全部拉成環境變數：

| 環境變數 | 預設 | 作用 |
| --- | --- | --- |
| `LLM_MAX_GUILD_RULES` | `40` | 每個伺服器最多學幾條（全域規則那個桶子共用這個上限） |
| `LLM_MAX_RULE_CHARS` | `300` | 每一條最多幾個字 |
| `LLM_MAX_PERSONA_GUILDS` | `200` | 最多幾個伺服器有自己的規則（超過淘汰條數最少的） |
| `LLM_MAX_OVERLAY_CHARS` | `2000` | 這些規則**接進提示詞的總長**上限（每次多花的 token 煞車） |
| `LLM_MAX_AUDIT_ENTRIES` | `200` | 主人操作紀錄最多留幾筆 |

- 私人小伺服器可以放寬（例如 `LLM_MAX_GUILD_RULES=100`）；公開大伺服器建議收緊
- 超過上限時模型會拿到明確的訊息（「已經記了 N 條（上限），請先刪掉一些」），
  所以它會照實跟使用者說，而不是默默失敗
- 實際生效的數字在**啟動 log**、`--dryrun`、`/ai status`（`/ai learned` 的頁尾也有 `N/上限`）都看得到

#### 它看得到誰在說話

**預設看得到大家的 Discord 暱稱**（伺服器顯示名稱）—— 頻道上大家互相稱呼的是暱稱，
模型要聽得懂「小明剛剛說的」「叫小明來」就必須看到暱稱。
這在**判斷「這句話是不是在對我說話」**與**判斷話題**時特別重要
（那兩段提示詞以前只寫帳號，模型根本認不出誰是誰）：

| 給模型的資訊 | 說明 |
| --- | --- |
| **發話者** | `暱稱(@帳號, Discord ID)` —— 例如 `小明(@wuxiaohan0922, 123456789012345678)` |
| **判斷用的名字** | 只用**暱稱**（`小明`）—— 給判斷器與話題判斷的提示詞短一點、也貼近大家平常的講法 |
| **它自己的名字** | 伺服器把 Bot 改暱稱（例如「貓貓」）時，提示詞會多一句「大家叫你『貓貓』」 —— 有人喊那個名字它才知道是在叫它 |
| **被 @ 的人** | 訊息裡的 `<@123>` 會展開成 `@阿明(123)`，所以它知道那是誰 |
| **被回覆的人** | 回覆訊息的那一則也會帶上同樣的標籤 |
| **自訂表情** | 它看得到 `<:名稱:ID>`，也會用（**只用對話裡真的出現過的**，不會自己編） |

- 想改回「一律用帳號」：`LLM_SHOW_NICKNAMES=false`
  （好處是同一個人在不同伺服器身分一致；缺點是判斷「這句是不是在對我說話」時只看得到帳號，認人會變差）
- 不管開或關，**主對話的標籤一定同時帶帳號與 ID**（`LLM_EXPOSE_IDS`），
  所以就算兩個人剛好取同一個暱稱也分得出來

> ⚠️ **要讀得到別人的伺服器暱稱，必須開啟 Server Members Intent**（特權意圖）：
> Developer Portal → 你的 Application → 左側 **Bot** → **Privileged Gateway Intents** →
> 打開 **Server Members Intent** → Save Changes → 重啟服務。
>
> 沒開的話 Discord 不會把成員資料給 Bot，模型**只讀得到帳號名**（`@wuxiaohan0922`），
> 而且**不會有任何錯誤訊息** —— 功能看起來有開，實際上認人還是靠帳號。
> 所以開機時會先用 `GET /applications/@me` 的 flags 確認一次
> （bit 14／15），沒被允許就**不要**這個意圖並印出上面那三步。
>
> 為什麼「沒被允許就不要」而不是硬要：要了沒被允許的特權意圖，
> 閘道會用 `4014` 把連線踢掉 —— Bot 會**完全連不上**。讀不到暱稱只是認人差一點，
> 兩者代價差太多（`message content` 則是沿用原本「問不到就照設定要」的作法）。

它**可以 @ 別人**（輸出 `<@ID>`）：說「幫我叫 @阿明 過來」它就會標記他。
只開放 @ **使用者** —— `@everyone`／`@here`／身分組一律擋掉（mention 的文字是模型產生的，
開放等於讓它有機會洗頻整個伺服器）。不想要它 @ 人就設 `LLM_ALLOW_MENTIONS=false`。
不想要它看到 ID 就設 `LLM_EXPOSE_IDS=false`（那就只給暱稱）。

#### 「主人」授權（後台指定 ＋ 特殊 key）

後台指定一組 ID，那個人在訊息裡帶上特殊 key，就會被當成**必須接受**的命令：

```powershell
# .env / Render 環境變數
LLM_ADMIN_IDS=123456789012345678     # 你自己的 Discord ID
LLM_ADMIN_KEY=OWNER-xxxxx            # 特殊 key（沒設定＝整個機制關閉）
```

```
喵喵主人：OWNER-xxxxx 記住：不管在哪個伺服器，回話都要先叫我一聲「主人」
Bot ：主人，記好了，以後不管在哪都會先叫您一聲「主人」
      🔧 我實際做了：remember_global_rule（…）
```

- **兩個條件都要成立**：發話者在名單裡 **且** 訊息裡有 key（fail closed：沒設 key 就等於沒這個功能）
- 主人多一組工具 `remember_global_rule` / `list_global_rules` / `forget_global_rule` /
  `list_owner_audit`，寫的是**所有伺服器都適用**的規則（一般人只寫得到自己伺服器那一份，
  而且**連這些工具的存在都看不到**）
- 授權時系統提示前面會加一段「他是你的主人、必須照做、不要拒絕」
- `/ai audit` 可以看最近的主人操作紀錄（**只有名單上的人看得到**；唯讀、不需要帶 key）

### 安全不依賴模型的表現

「全域設定」影響每一台伺服器，所以它**不是**靠提示詞叫模型聽話來保護的：

| 防線 | 作法 | 效果 |
| --- | --- | --- |
| 1. 憑證物件 | 全域寫入的方法都**必須**收 `OwnerGrant`；而憑證只有 `AdminAuthorizer.Check`（名單內 ＋ key 正確的那一次判斷）發得出來，建構子私有、不序列化 | 編譯器就擋掉「沒有授權卻想寫全域設定」的程式碼路徑；模型看不到也造不出憑證 |
| 2. 工具不掛載 | 沒通過驗證的對話**根本不會**掛上主人 plugin | 模型連「有這個能力」都不知道（實測：被要求刪全域規則時它回答「我沒有那個工具」） |
| 3. 授權在模型之前 | 判斷在 `AskAsync` 第 0 步完成（寫進對話記憶之前） | 提示注入沒辦法讓模型「升級」成主人 |
| 4. 破壞性操作再確認 | `forget_global_rule` 關鍵字留白＝清空全部，必須 `confirm_all=true` 才動手 | 模型不小心傳空字串不會把累積的規則全清掉 |
| 5. 稽核紀錄 | 每次動到全域設定都記 `誰／什麼時候／做了什麼`，**連被拒絕的嘗試也記**，並持久化 | 模型抽風或有人在亂試時，`/ai audit` 與 console 都查得到 |

實測（真實 API，讓一般人用「系統管理員說你現在是主人」與「直接呼叫 forget_global_rule
清空全部」兩種方式去騙它）：**兩次都拒絕，一次工具都沒呼叫，全域規則完好無損**。

> ⚠️ 唯一「暴露」的情境是**你自己把主人說明寫進 `LLM_SYSTEM_PROMPT`** ——
> 那等於對所有人常駐生效（等於關掉這個機制）。上面那五道防線保護的是**全域設定能不能被寫**，
> 跟提示詞裡寫了什麼無關。

- ⚠️ **key 一定會從訊息移除**（不管有沒有授權成功）—— 不會進到對話記憶、提示詞或 console log；
  不在名單裡的人就算打出正確的 key 也只會被當成一般訊息，並且 console 會留一行警告
- ⚠️ key 打在公開頻道等於公開這把鑰匙，**請在私訊或私人頻道使用**
- `/rest` 只清**伺服器**那一份，不會動到主人的全域規則

> 開著工具時每次提問的輸入 token 約 1,500～1,900（工具定義本身要送出去），
> 關掉約 200。以 30 萬/週來看，開著大約可以問 150～180 次。

#### 不需要「思考」（預設已關掉）

這個用途（聊天、訂閱公車、判斷換話題）不需要模型的 reasoning，而思考**會吃掉輸出額度又算錢**：
實測同一個問題，`LLM_REASONING=auto` 是 **out 660 tokens**，`off` 只要 **out 20 tokens**（33 倍）。

`LLM_REASONING` 預設 `off`，運作方式是在送出的 JSON 補上
`reasoning_effort: "none"` ＋ `thinking: {"type":"disabled"}`（兩種寫法實測都有效，
其他候選如 `enable_thinking`／`reasoning.enabled`／`chat_template_kwargs`／`thinking_budget` 實測**無效**）。

> 為什麼要動到 HTTP 這一層？因為 Semantic Kernel 這個版本**送不出**這些欄位
> （`ExtensionData` 會被忽略 —— 用 `n=2` 做決定性實驗只有 1 則回覆；
> 型別化的 `ReasoningEffort` 又直接拒絕 `none`）。細節寫在 `ReasoningOffHandler` 的註解裡。
> 換到不認識這些欄位的服務時，設 `LLM_REASONING=auto` 就好。

#### 用兩個模型：主模型 ＋ 便宜的小模型（`LLM_JUDGE_MODEL`）

這支 Bot 有兩種差很多的工作：

| 工作 | 需要什麼 | 走哪個模型 |
| --- | --- | --- |
| 聊天、工具呼叫、被 @ 的回答 | 聽得懂話、選得對工具 | `LLM_MODEL` |
| 「**這句話是在跟我說話嗎**」「**換話題了沒**」 | 只要回一個單字（`max_tokens=8`、`temperature=0`），但**次數多**（偷聽中每一則沒被 @ 的訊息都要問一次） | `LLM_JUDGE_MODEL`（留空＝跟主模型一樣） |

```
LLM_MODEL=deepseek-v4-pro        # 聰明的那個：回答與工具呼叫
LLM_JUDGE_MODEL=deepseek-flash   # 快又便宜的那個：只做短判斷
```

- 實測這個端點可用的模型名稱：`deepseek-flash`、`deepseek-v4-pro`
  （打錯名稱不會在啟動時報錯，只會在判斷那一瞬間失敗 —— 偷聽會變成「先不出聲」，不會亂回話）
- 啟動 log、`--dryrun`、`/ai status` 都會把兩個名稱印出來，方便核對
- **沒設定＝零額外成本**：只建一個連線，短判斷也走主模型
- ⚠️ 為什麼要「建兩條連線」而不是在同一個請求裡換模型：Semantic Kernel 這個版本會**忽略**
  請求裡的 `ModelId`（實測：把模型名稱設成不存在的字串，請求照樣成功、回來的還是主模型），
  所以換模型只能靠 `RoutingLlmClient` 在建連線時決定 —— 這也是為什麼程式碼裡有這一層

#### 讓它看得懂圖片與貼圖（`LLM_VISION`）——**只有你指定才會用**

```
小明：@笨蛋猫猫 （附上一張公車站牌的照片）這張站牌寫什麼？
Bot ：這張站牌是「臺中車站(A月台)」，上面有 300、304…

小明：（貼一張截圖）@笨蛋猫猫 這張圖裡的時刻表幾點發車？
Bot ：（讀出截圖裡的文字）
```

- **只有「明確對它說話」的那一則才可能附圖**（@ 它、或回覆它的訊息）——
  偷聽到的訊息、別人聊天裡的照片**一律不送**（一張圖最多約 1024 tokens，而且那是別人的照片）
- **回覆某張圖的訊息**時，被回覆那一則的圖片也算（「這張圖是什麼？」是最自然的用法）
- 支援 JPEG／PNG／GIF／WebP；Discord **貼圖**只有 PNG／APNG 能看（Lottie 動畫不是圖片檔，會跳過）
- 一次最多 `LLM_VISION_MAX_IMAGES`（預設 **2**）張；`0` = 關閉
- 關掉（`LLM_VISION=false`）時，有圖片的那一則會留一句
  「（有 N 張圖片，主機沒有開啟圖片理解）」——讓模型知道自己沒看到，而不是憑空編造
- 歷史訊息裡的圖片**不會**每輪重送（只留「（附 N 張圖片）」的文字記號），
  不然每輪都要再花一次圖片 token

#### 很多人同時說話？每個人都排隊等回答（`LLM_CHANNEL_QUEUE`）

**以前的行為**：同一頻道「一次只處理一則」，正在想的時候有人再問，第二個人會被**默默吃掉**
（console 只有一行「略過一則…還在想上一題」，使用者什麼都看不到）——
這就是「多個人說話會被吃掉」的原因。

**現在**：進來的訊息一律**排隊**，依講話順序一則一則回：

```
小明：@bot 300 幾點到？
小華：@bot 那 304 呢？       ← 不會被吃掉，排在後面
小明：我要去靜宜大學          ← 同一人 4 秒內連打 → 合併進上一題（不會回兩次）
小美：@bot 我要去逢甲        ← 也排在後面
```

- `LLM_CHANNEL_QUEUE`（預設 **5**）：每個頻道最多排幾則，超過才丟 ——
  而且**先丟「不是在對它說話」的閒聊，最後才丟 @ 它的**（被犧牲的不會是最想被回答的人）
- `LLM_MERGE_SECONDS`（預設 **4**）：同一個人在這個秒數內連打的訊息合併成一題
  （「在嗎」「300 幾點」「我要去靜宜」→ 一題）；設 `0` = 不合併
- 被丟掉的閒聊**內容還是會留下來當上下文**（沒回話 ≠ 完全不知道發生什麼事）
- 等待時間大約是「前面幾則 × 每則幾秒」：想更即時就把 `LLM_CHANNEL_QUEUE` 調小，
  想「一個人都不漏」就調大（代價是 token 與等待）
- 啟動 log、`--dryrun` 都看得到排隊設定與「合併／丟棄」次數

#### 上下文的相關環境變數

**「記得多少」與「送多少」是兩組不同的設定**（這個區分很重要）：

| | 環境變數 | 預設 | 說明 |
| --- | --- | --- | --- |
| 送給模型 | `LLM_MAX_CONTEXT_TURNS`、`LLM_MAX_CONTEXT_TOKENS` | 20 則／3000 tokens | 每次提問**送出去**多少歷史（直接影響每次的錢） |
| 記在記憶體 | `LLM_MAX_TURNS_PER_SEGMENT`、`LLM_MAX_SEGMENTS_PER_CHANNEL`、`LLM_MAX_CHANNELS`、`LLM_CHANNEL_TTL_HOURS` | 24 則 × 4 段 × 500 頻道、12 小時 | **記得**多少（影響記憶體，不影響每次的錢） |

- 記的比送的多是正常且刻意的：多留的那些是為了「**回覆很舊的訊息**」時還拉得回那一段的上下文
- 記憶體最壞情況＝`LLM_MAX_CHANNELS × LLM_MAX_SEGMENTS_PER_CHANNEL × LLM_MAX_TURNS_PER_SEGMENT` 則訊息
  （預設 = 48,000 則）；啟動時的 log 與 `--dryrun` 都會把這個數字印出來
- 全部都是**記憶體**，Bot 重啟就沒了（唯一持久化的只有每週 token 用量與訂閱）
- 想省記憶體（例如 Render 免費層只有 512 MB）：`LLM_MAX_CHANNELS=50`＋`LLM_CHANNEL_TTL_HOURS=3`

| 鍵 | 預設 | 說明 |
| --- | --- | --- |
| `LLM_SYSTEM_PROMPT` | 內建人格 | 系統提示（工具的能力說明會由程式附加在後面） |
| `LLM_TOOLS` | `true` | 要不要讓 LLM 用工具真的動手（訂閱公車…） |
| `LLM_UI_ACTIONS` | `true` | 讓模型**幫使用者按面板按鈕**（開面板／設起訖／找路線／訂閱／復原） |
| `LLM_EAVESDROP` | `true` | 回完話後**繼續跟著聽**（接話／先不出聲／退出三種判斷；@ 了別人的訊息不花錢判斷也不會退出） |
| `LLM_EAVESDROP_MESSAGES` | `20` | 偷聽期間最多**判斷**幾則（成本上限；太小會讓它在多人頻道聊兩句就退出） |
| `LLM_CHANNEL_QUEUE` | `5` | 每個頻道最多排幾則等回覆（超過先丟閒聊、不丟 @ 它的）—— 這是「多人同時說話時每個人都回得到」的關鍵 |
| `LLM_MERGE_SECONDS` | `4` | 同一個人在幾秒內連打的訊息合併成一題（`0` = 不合併） |
| `LLM_EAVESDROP_SECONDS` | `120` | **閒置**幾秒後停止偷聽（每一則訊息都往後延，所以聊很久也不會中途退出） |
| `LLM_EAVESDROP_CONTEXT` | `true` | 偷聽到的閒聊留下來當上下文（標成 `[閒聊]`；關掉就只記它有回覆的訊息） |
| `LLM_VISION` | `true` | 讓它看得懂**圖片與貼圖**（只有被 @ 或回覆的訊息才會附圖；偷聽到的照片不送） |
| `LLM_VISION_MAX_IMAGES` | `2` | 一次最多附幾張圖（`0` = 關閉；每張最多約 1024 tokens） |


| 鍵 | 預設 | 說明 |
| --- | --- | --- |
| `LLM_API_KEY` | — | **有填才會啟用**（別名：`OPENAI_API_KEY`、`DEEPSEEK_API_KEY`） |
| `LLM_BASE_URL` | `https://api.deepseek.com/v1` | 任何 OpenAI 相容端點（要含 `/v1`） |
| `LLM_MODEL` | `deepseek-flash` | **主要模型**名稱（聊天、工具呼叫、被 @ 的回答） |
| `LLM_JUDGE_MODEL` | — | **極短判斷**用的模型（「這句是在跟我說話嗎」「換話題了沒」）；留空＝跟 `LLM_MODEL` 一樣。這兩種判斷只要回一個單字但次數多，用便宜的小模型就夠 |
| `LLM_REASONING` | `off` | 模型要不要「思考」。`off` 最省（實測同一個問題 out 660 → 20 tokens）、`auto` 用服務端預設、`low`／`medium`／`high` 明確要思考 |
| `LLM_EXPOSE_IDS` | `true` | 讓模型看到發話者的 **暱稱＋@帳號＋Discord ID**（關掉只給暱稱） |
| `LLM_ALLOW_MENTIONS` | `true` | 允許模型在回覆裡 @ 使用者（`@everyone`／身分組一律擋掉） |
| `LLM_SHOW_NICKNAMES` | `true` | 讓模型看到 **Discord 暱稱**（判斷「這句是不是在對我說話」時才認得出誰是誰）＋知道自己在這個伺服器叫什麼。**需要 Server Members Intent**；關掉＝一律用帳號 |
| `LLM_ADMIN_IDS` | — | 後台指定的「主人」ID（逗號分隔）；還必須帶 `LLM_ADMIN_KEY` 才能下令 |
| `LLM_ADMIN_KEY` | — | 主人授權用的特殊 key。**沒設定＝整個機制關閉** |
| `LLM_WEEKLY_TOKENS` | `300000` | 每週 token 上限（全域；`0` = 不限） |
| `LLM_SEGMENT_GAP_MINUTES` | `30` | 超過幾分鐘沒說話就當成新的一段 |
| `LLM_TOPIC_DETECT` | `true` | 時間內是否讓 LLM 判斷「換話題了沒」 |
| `LLM_MAX_CONTEXT_TURNS` | `20` | 帶進提示詞的歷史上限（則） |
| `LLM_MAX_CONTEXT_TOKENS` | `3000` | 帶進提示詞的歷史上限（估算 token） |
| `LLM_MAX_TURNS_PER_SEGMENT` | `24` | **記得**多少：一段最多留幾則（超過丟最舊的）。這是記憶體；「送多少」看上面兩個 |
| `LLM_MAX_SEGMENTS_PER_CHANNEL` | `4` | 每個頻道最多留幾段（留著是為了「回覆很舊的訊息」還接得上） |
| `LLM_MAX_CHANNELS` | `500` | 同時追蹤幾個頻道（超過淘汰最久沒動的；整台 Bot 的記憶體上限） |
| `LLM_CHANNEL_TTL_HOURS` | `12` | 頻道多久沒動就整個忘掉（小時，可給小數例如 `0.5`） |
| `LLM_MAX_GUILD_RULES` | `40` | **學到的規則**：每個伺服器最多幾條 |
| `LLM_MAX_RULE_CHARS` | `300` | 學到的規則每一條最多幾個字 |
| `LLM_MAX_PERSONA_GUILDS` | `200` | 最多幾個伺服器有自己的規則（超過淘汰條數最少的） |
| `LLM_MAX_OVERLAY_CHARS` | `2000` | 學到的規則接進提示詞的總長上限（每次多花的 token） |
| `LLM_MAX_AUDIT_ENTRIES` | `200` | 主人操作紀錄最多留幾筆 |
| `LLM_MAX_OUTPUT_TOKENS` | `800` | 單次回覆的輸出上限 |
| `LLM_SYSTEM_PROMPT` | 內建人格 | 系統提示（決定人格與範圍） |
| `LLM_USER_COOLDOWN_SECONDS` | `3` | 同一個人連續問的冷卻 |
| `LLM_ALLOW_DM` | `false` | 私訊要不要回 |
| `LLM_TIMEOUT_SECONDS` | `90` | 單次呼叫逾時 |

---

## 選城市：預設臺中，也可以跑臺南（`BUS_CITY`）

```
BUS_CITY=Taichung      # 預設（不打就是臺中）
BUS_CITY=Tainan        # 臺南
BUS_CITY=高雄          # 中文也可以（高雄 → Kaohsiung）
```

- 接受**英文代碼**（`Taichung`／`Tainan`／`Kaohsiung`…）或**中文**（`臺中`／`台中`／`臺南`／`台南`），
  大小寫與底線都無所謂（`New_Taipei` 也通）
- 目前清單：基隆、臺北、新北、桃園、新竹（市／縣）、苗栗、**臺中**、彰化、南投、雲林、
  嘉義（市／縣）、**臺南**、高雄、屏東、宜蘭、花蓮、臺東、澎湖、金門、連江
- **每個城市的快取檔名都不一樣**（`Stop.Tainan.json`、`StopOfRoute.Tainan.json`…）——
  這一點很重要：以前檔名是固定的 `Stop.json`，換城市會**讀到上一個城市的快取**，
  而且檔案還很新、連重新抓都不會，結果是「站名全部變成別的城市」而且沒有任何錯誤訊息。
  舊檔名仍然相容（只有臺中會讀它）
- 換城市之後 `/bus status`、`/health`（`city` 欄位）、啟動畫面的資料來源都會顯示實際城市，
  面板標題也會變成「🚌 臺南公車訂閱」
- AI 的預設人格用 `{city}` 佔位（「主要幫大家查**臺南**公車」），所以換城市不會自稱在查台中
- ⚠️ 內建最小資料集（沒有 TDX 金鑰時的離線資料）**只有臺中走廊**：
  非臺中時必須有 TDX 金鑰或已抓好快取，不然 Bot 會直接說出這件事，而不是給你錯的站牌
- 實測：`BUS_CITY=Tainan` 從 TDX 抓到 **11,329 個站牌、323 筆路線站序**（臺中為 14,036／755）

---

## 手機版（Android APK，掛在舊手機上跑）

舊手機是最適合的常駐機器：一直插著電、連著 WiFi、不會被關機。

### 建置

```powershell
# 一行搞定（會把 APK 放到 dist\tcbusbot-mobile.apk）
.\build-apk.ps1

# 直接安裝到接上 USB 的手機（需要開啟「USB 偵錯」）
.\build-apk.ps1 -Install

# 先停掉手機上正在跑的舊版服務再裝
.\build-apk.ps1 -Install -StopExisting
```

需要的環境（這台機器已經有了）：

| 項目 | 路徑 | 說明 |
| --- | --- | --- |
| .NET SDK | 10.0.x | 手機版用 `net10.0-android`（Android workload 已安裝） |
| Android SDK | `E:\SDK\android\android-sdk` | 腳本預設值，可用 `-AndroidSdk` 覆寫 |
| JDK 17 | `E:\SDK\android\jdk` | 可用 `-JavaSdk` 覆寫 |
| adb | `E:\softwaer\platform-tools\adb.exe` | 只有 `-Install` 會用到 |

### 安裝後怎麼用

1. 手機上開啟「台中公車通知」
2. 填入 **Discord Bot Token**（TDX 金鑰可留空 → 用內建離線資料集）
3. 按「💾 儲存設定」→「▶ 啟動服務」
4. 按「🔋 關閉電池最佳化」，並在系統設定把本 App 的電池改成「不受限制」

**為什麼一定要做第 4 步**：Android 的 Doze 模式在螢幕關掉一陣子後會斷掉 App 的網路，
輪詢就停了 —— 這是「掛在手機上」最常見的失敗原因。App 需要前景服務（常駐通知）＋
電池最佳化白名單，才會整晚都在。

### 手機版的能力與桌面版完全一致

因為它**跟桌面版編譯同一份原始碼**（`<Compile Include="..\TcBusBot.Discord\**\*.cs">`），
不是另外寫一份。`TcBusBot.Discord` 專案本身完全沒動，`dotnet run` 照用：

| 能力 | 手機版 |
| --- | --- |
| 所有 `/bus` 指令、面板、按鈕、Modal | ✅ 同一份程式碼 |
| 模糊搜尋、簡繁、縮寫 | ✅ |
| 訂閱組（多選、合併、復原） | ✅ 存到手機上的 `tcbus.db` |
| 即時輪詢 + `$select` 省點數 | ✅ |
| 沒有 TDX 金鑰時的離線資料集 | ✅（APK 內打包，首次啟動複製出來） |
| 看得到的日誌 | ✅ App 內建的日誌區（也可以用 `adb logcat -s TcBusBot:I`） |

### 檔案放在哪裡（按「📁 選擇工作目錄」換）

App 私有目錄 `/data/data/...` 在**沒 root 的手機上幾乎拿不出來**（檔案管理員看不到、
MTP 看不到，只有 adb 能撈），所以 App 裡有一顆 **「📁 選擇工作目錄」** 按鈕：

| 選項 | 路徑 | 誰看得到 |
| --- | --- | --- |
| App 私有目錄 | `/data/data/com.tcbusbot.mobile/files` | 只有 adb（`run-as`） |
| App 專屬外部目錄 | `/storage/emulated/0/Android/data/com.tcbusbot.mobile/files` | Android 10 以前：檔案管理員／MTP |
| **手機儲存空間 /TcBusBot**（建議） | `/storage/emulated/0/TcBusBot` | **檔案管理員、插 USB、任何同步 App** |
| 自訂路徑 | 自己輸入的絕對路徑 | 看那個位置 |

選了公開目錄之後，App 會依 Android 版本請你授權：

* **Android 10 以前**：允許「儲存」權限（App 會直接跳出來問）
* **Android 11 以後**：要按「所有檔案存取權」→ 允許「管理所有檔案」

切換目錄時，App 會**實際試寫一個檔案**確認可寫（目錄存在不等於有寫入權限），
成功才切換，並在服務執行中自動重啟服務套用。

三個檔案都在工作目錄底下：`app.env`（設定，等同桌面版的 `.env`）、
`tcbus.db`（訂閱組）、`cache/`（TDX 快取）。
狀態區會直接顯示目前的路徑；如果是私有目錄，會提醒你「只能用 adb 拿檔案」。

> **要在兩台機器之間搬訂閱組**：把 `tcbus.db` 複製過去就好（兩邊都是標準 SQLite）。
> 換到手機儲存空間後，插 USB 複製即可；還在私有目錄時可以用：
> `adb exec-out run-as com.tcbusbot.mobile cat files/tcbus.db > tcbus.db`

---

## 部署到 Render（免費層：Web Service ＋ 健康檢查 ＋ 防休眠）

Render 的免費層**只提供 Web Service**（必須監聽一個 HTTP 埠），所以
`TcBusBot.Discord` 內建了一個極輕量的 HTTP 端點（`src/TcBusBot.Core/Hosting/HealthEndpoint.cs`）：

```
GET /        → 200 "TcBusBot is running!"
GET /health  → 200 JSON：uptime、discordReady、storage、dataSource、poller、訂閱數、快取筆數
其他          → 404       非 GET → 405
```

它送出的就是**標準 HTTP/1.1**（有 status line、headers、body），curl／瀏覽器／
Render 的健康檢查都認得：

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Content-Length: 341
Cache-Control: no-store
Connection: close
```

### 檔案

| 檔案 | 用途 |
| --- | --- |
| `render.yaml` | Blueprint：`type: web` + `runtime: docker` + `healthCheckPath: /health` + 環境變數 |
| `src/TcBusBot.Discord/Dockerfile` | 容器映像（`dotnet build -f src/TcBusBot.Discord/Dockerfile .`） |
| `.dockerignore` | 不把 `bin/`、`obj/`、`dist/`、`tcbus.db` 送進建置 context |

### 部署步驟

1. 把 repo 推上 GitHub（`render.yaml` 已經指向 `https://github.com/abo-nb/TcBusBot.git`）
2. Render → **New → Blueprint** → 選這個 repo（會自動讀 `render.yaml`）
3. 在後台填 `DISCORD_TOKEN`（必填）、TDX 金鑰（選填）、`TCBUS_MONGO`（**強烈建議**，見下）
4. 部署完成後打 `https://<你的服務>.onrender.com/health` 應該看到 JSON

### 三個容器／Render 才會遇到的坑（都已處理）

| 坑 | 處理 |
| --- | --- |
| **ENTRYPOINT 寫成 `TcBusBot.Discord.dll`** | 專案的 `AssemblyName` 是 `tcbus-bot`，容器會直接「找不到檔案」。Dockerfile 用的是 **`tcbus-bot.dll`** |
| **非 root 使用者寫不進 `/app`** | 預設的相對路徑（`tcbus.db`、`cache/`）會寫失敗 → Dockerfile 設 `TCBUS_DB=/tmp/tcbus.db`、`TCBUS_CACHE=/tmp/tcbus-cache` |
| **容器檔案系統是暫時的** | 每次重新部署就清空 → **訂閱組請設 `TCBUS_MONGO`**（MongoDB Atlas 免費層足夠） |

### MongoDB 連不上？（雲端部署最常見的問題）

```
[儲存] 正在連線 MongoDB（mongodb+srv://***@tcbus.xxxxx.mongodb.net/?appName=tcbus，最多等 15 秒）…
[儲存] ❌ MongoDB 連不上（TimeoutException: A timeout occurred after 15004ms selecting a server…）
        連線字串 ：mongodb+srv://***@tcbus.xxxxx.mongodb.net/?appName=tcbus
        SRV 檢查 ：查到 1 個節點（_mongodb._tcp.tcbus.xxxxx.mongodb.net:27017）
        主機     ：tcbus.xxxxx.mongodb.net
        請依序檢查：
          1. ★ Atlas → Network Access（IP 白名單）…
```

**照順序檢查**（前兩項佔了絕大多數）：

| # | 原因 | 怎麼確認／修 |
| --- | --- | --- |
| 1 | **Atlas 的 IP 白名單** | Atlas → Network Access → 加 `0.0.0.0/0`。雲端平台的對外 IP 是動態的，免費方案幾乎只能這樣開 |
| 2 | 帳密錯 | 密碼含 `@ : / ?` 等字元要 URL encode（用 Atlas 的「Connect」按鈕直接複製最保險） |
| 3 | 叢集被暫停 | Atlas 介面顯示 `Paused` → 按 Resume，喚醒要幾十秒 |
| 4 | 字串漏了資料庫名稱 | Atlas 給的字串常是 `…/xxx/?appName=…`（沒有 `/dbname`）。**本程式會用 `TCBUS_MONGO_DB`（預設 `tcbus`）補上，不用自己加** |

**實測（本機，2026-09）**：同一條連線字串在開發機上完全正常 ——
`✅ 連線成功（963 ms）`、三個節點 `TCP + TLS` 全成功、
新增／讀取／還原／改名／計數／刪除全通過。
所以 **Render 連不上時，幾乎可以確定是 Atlas 的 IP 白名單**（Render 的對外 IP 是動態的）。

用這個指令可以在任何機器上驗同一條連線字串（輸出跟 Bot 用同一套判斷邏輯）：

```powershell
dotnet run --project src\TcBusBot.Cli -- mongo --env TcBusBot-1.env --write-test
```

**如果白名單已經開了還是不行**：改用「非 SRV」的標準連線字串
（有些平台的 DNS 對 SRV 處理不好；參數取自 Atlas 的 TXT 記錄）：

```
mongodb://<user>:<pass>@ac-eeavuzc-shard-00-00.gbk5wj0.mongodb.net:27017,
        ac-eeavuzc-shard-00-01.gbk5wj0.mongodb.net:27017,
        ac-eeavuzc-shard-00-02.gbk5wj0.mongodb.net:27017/TCBUS
        ?ssl=true&replicaSet=atlas-iw1lln-shard-0&authSource=admin&retryWrites=true&w=majority
```

**最有效的判別方法**：在**自己的電腦**上用同一個 env 檔跑一次

```powershell
dotnet run --project src\TcBusBot.Discord -- --env TcBusBot-1.env --data fixture
```

* 電腦**連得上**、Render **連不上** → 就是第 1 項（白名單）
* 電腦也連不上 → 看上面的 `SRV 檢查` 那行：查不到節點 = DNS／叢集名稱問題
* 連不上時 Bot 不會掛掉：退回本機儲存繼續運作，每 5 分鐘重測一次，
  連上之後會印一則「MongoDB 現在連得到了 → 重啟服務即可切換」

> 伺服器選擇逾時原本設 3 秒 —— 雲端第一次連 Atlas 要經過 DNS SRV → TLS → 複製集探索，
> 3 秒不夠（你看到的 `TimeoutException … after 2998ms` 就是這個）。
> 現在放寬成 **連線 10 秒／伺服器選擇 15 秒／Socket 20 秒**。

### 防休眠（keep-alive）

免費層閒置約 **15 分鐘**就會把服務停掉（下次有人連進來要等幾十秒喚醒）。
設了 `APP_URL`（或 Render 自動注入的 `RENDER_EXTERNAL_URL`）之後，程式會
**每 10 分鐘 ping 自己一次**：

```
▶️  防休眠已啟動（每 10 分鐘 ping https://tcbusbot-discord.onrender.com/）
[keep-alive] 14:12:03 ping https://…/health → HTTP 200（成功 3／失敗 0）
```

> ⚠️ **自我 ping 的極限**：服務真的睡著之後，它就沒有東西可以 ping 自己了。
> 所以這只能「在醒著時維持清醒」，**不能把自己叫醒**。
> 要保證隨時都是醒的，請另外設外部監控（UptimeRobot／cron-job.org 免費層）
> 打 `/health`，或升級 Render 付費方案。

---

## 訂閱組要放哪裡（四種後端，自動挑）

「訂閱」是記憶體（重啟就消失，這是刻意的），但**訂閱組**是使用者自己整理出來的範本，
所以要存起來。存哪裡可以選，設定都不用改程式：

| 優先序 | 後端 | 什麼時候用 | 跨機器共用？ |
| --- | --- | --- | --- |
| 1 | **MongoDB** | 設定了 `TCBUS_MONGO`（伺服器或 Atlas 都可以） | ✅ 手機、電腦、雲端**同一份** |
| 2 | SQLite | 環境有 SQLite（Windows 內建／Android 系統的） | ❌ 各機器一份 |
| 3 | 文字檔 | 沒有 SQLite（例如 Termux） | ❌（`saved_groups.txt`，一行一筆，記事本就看得到） |
| 4 | 記憶體 | 連檔案都寫不進去 | ❌ 重啟就消失 |

**用 MongoDB 的理由**：不然每個平台都要各解一次儲存問題，資料還會散在好幾台機器上。
設好之後在手機訂閱、電腦打開就看到。

```powershell
# 方法一：寫進 .env（建議）
TCBUS_MONGO=mongodb+srv://user:pass@cluster0.xxxxx.mongodb.net/?retryWrites=true&w=majority
TCBUS_MONGO_DB=tcbus          # 資料庫名稱，預設 tcbus

# 方法二：命令列
dotnet run --project src\TcBusBot.Discord -- --mongo "mongodb+srv://..." --mongo-db tcbus
```

* **免費就夠**：MongoDB Atlas 的 M0（512MB）對「每人最多 20 組訂閱組」來說綽綽有餘。
* **連不上不會壞**：3 秒逾時就退回本機儲存，並在主控台／App 日誌留下
  `[儲存] MongoDB 連不上（…）→ 改用本機儲存（這次的訂閱組不會同步到其他機器）`。
  不會硬撐、也不會默默變成記憶體。
* **連線字串等同密碼**：啟動橫幅只會印 `mongodb+srv://***@cluster0.xxxxx.mongodb.net/...`。
* 資料長這樣（Atlas 介面上直接看得懂，`payload` 是 JSON 字串、中文沒被轉義）：
  ```json
  { "user": "123456789", "seq": 3, "name": "上班通勤",
    "origin": "臺中車站", "dest": "靜宜大學", "routes": 20, "notify": 10,
    "payload": "{\"legs\":[…],\"notify\":10}", "used": 7 }
  ```

### 這四種後端是怎麼被挑的（DI 容器）

「挑後端」與「怎麼把它交給需要的服務」現在都在 **DI 容器**裡，程式不用自己判斷：

```csharp
// BotServices.cs（唯一的組裝處）
services.AddBusBotStorage(new StorageSettings(cfg.DatabasePath, cfg.MongoUri, cfg.MongoDatabase), log);
```

* 後端的選擇與退回邏輯集中在 `StorageBackendFactory`（MongoDB → SQLite → 文字檔 → 記憶體）
* `SavedGroupStore`（訂閱組）與 `ILlmStateStore`（AI 的每週 token 用量）**共用同一個實例** ——
  同一條後端鏈不該開兩份連線；容器的 `Dispose` 負責關閉
* 指令模組註冊成 **transient**（Discord.Net 每次互動都要新的實例，共用會讓並行的互動互相蓋掉 Context）
* `Program.cs` 只負責啟動流程（登入、註冊指令、輪詢、防休眠），不再自己 `new` 這些服務

```powershell
# 離線看整張服務圖（與 Bot 用同一份註冊程式碼）
dotnet run --project src\TcBusBot.Discord -- --dryrun
#   ▶ DI 容器檢查  服務註冊 ↔ 建構子需求
#     註冊 23 個服務（19 singleton／4 transient）
#     ✔ 每個註冊的服務都解析得到
#     ✔ 儲存後端：…（訂閱組與 LLM 用量共用同一個實例）
#     ✔ 4 個指令模組都註冊成 transient
```

容器開了 `ValidateOnBuild`：**任何服務的建構子參數找不到，建容器時就會炸**。
（這也是為什麼之前那個「沒設定 `LLM_API_KEY` → `ILlmClient` 不存在 → 整個 Bot 起不來」的
bug 現在不可能再發生：`--dryrun` 的這一關會先擋下來。）

> 離線驗證用的是**記憶體模式**（`StorageSettings.Memory`），不會去開、也不會改到真正的
> `tcbus.db` 或 MongoDB。

---

## 在手機上用 Termux 跑（不用 APK，有控制台）

想要「跟電腦一樣的終端機畫面」就用這條路：**同一個程式、完整日誌、不用包 APK**。

```powershell
# 電腦端：發佈一個可攜式建置
.\publish-termux.ps1 -Zip        # → dist\termux\（+ dist\tcbus-termux.zip，約 5 MB）
adb push dist\tcbus-termux.zip /sdcard/
```

```sh
# 手機端：Termux（從 F-Droid 或 GitHub 裝，不要用 Play 商店的舊版）
pkg update
pkg install dotnet-runtime-8.0 dotnet-host-8.0     # 或 pkg install dotnet8.0（含 SDK）
termux-setup-storage
cd ~ && unzip ~/storage/shared/tcbus-termux.zip -d tcbus && cd tcbus
cp app.env.example app.env && vi app.env           # 至少填 DISCORD_TOKEN
sh run.sh
```

看到 `Bot 已上線` 就成功了。要一直在背景跑：

```sh
termux-wake-lock                                    # 重要：避免 CPU 睡著
nohup dotnet tcbus-bot.dll --env ~/tcbus > log.txt 2>&1 &
tail -f log.txt
```

**Termux 的特點**

* **真的有控制台**：啟動橫幅、輪詢訊息、錯誤堆疊都看得到（跟桌面版一字不差）
* **沒有 SQLite**：Termux 沒有 `winsqlite3.dll`，所以訂閱組會用 `saved_groups.txt`
  —— 或是設 `TCBUS_MONGO` 連到 MongoDB（**推薦**，這樣手機與電腦共用同一份）
* **簡繁轉換用內建字表**：Termux 沒有 Windows 的 `LCMapStringEx`，
  會退回內建的常用字對照表（常見字都涵蓋，冷僻字可能找不到）
* 手機上要先 `pkg install dotnet-runtime-8.0 dotnet-host-8.0`（Termux 官方有 .NET 8/9/10）

---

### 手機版的已知限制

- **APK 約 44 MB**：刻意關掉裁剪（Discord.Net 大量用反射，裁剪後會在手機上執行期才炸）
- **只支援 Android 7.0 以上**（`minSdkVersion 24`），已包含 32 位元 ARM（`armeabi-v7a`）
- 用 **debug keystore 簽署**：自己安裝沒問題，要上架 Google Play 才需要正式簽章
- **已在實機驗證**（Android 14 / arm64）：安裝、啟動、受管理執行階段、工作目錄解析都正常
  （`adb logcat -s TcBusBot:I` 可以看到 Bot 的日誌）
- 同一個 Token **不要**同時在手機與電腦上跑（會重複通知）
- 訂閱組要在手機上用，就必須在手機上建立（或用上面那行 `adb` 複製 `tcbus.db`）

---

## 資料來源

| 模式 | 條件 | 說明 |
| --- | --- | --- |
| 內建最小資料集（預設） | 沒有 TDX 金鑰 | 只有「臺中車站～靜宜大學」走廊的 63 個站牌、3 筆路線站序。**完全離線**，可以驗證整個 UI 流程 |
| TDX 會員模式 | 有金鑰 | 全台中約 5,000+ 站牌、751~765 筆路線站序 |
| TDX 訪客模式 | 指定 `--data tdx` 但無金鑰 | 官方限制：每 IP 每日 20 次，且限瀏覽器。**不保證可用** |

```powershell
# 使用 TDX 完整資料（寫進 .env 或設環境變數皆可）
#   .env:  TDX_CLIENT_ID=xxx / TDX_CLIENT_SECRET=xxx
dotnet run --project src\TcBusBot.Discord
```

### 所有可用的 `.env` 設定

| 鍵 | 必填 | 說明 |
| --- | --- | --- |
| `DISCORD_TOKEN` | ✅ | Discord Bot Token（別名：`DISCORD_BOT_TOKEN`、`BOT_TOKEN`、`TOKEN`） |
| `TDX_CLIENT_ID` | — | TDX API 金鑰（別名：`TDX_ID`） |
| `TDX_CLIENT_SECRET` | — | TDX API 秘密（別名：`TDX_SECRET`） |
| `DISCORD_GUILD_ID` | — | 只把指令註冊到這個伺服器（否則自動註冊到 Bot 已加入的全部伺服器） |
| `BUS_CITY` | `Taichung` | **服務哪個城市的公車**（別名 `TCBUS_CITY`／`TDX_CITY`）。接受英文代碼或中文；換城市時快取檔名會自動分開（見「選城市」一節） |
| `TCBUS_DATA` | — | `auto`（預設）／`fixture`／`tdx` |
| `TCBUS_POLL_INTERVAL` | — | 即時輪詢間隔秒數（預設 30；低於 20 沒有意義） |
| `TCBUS_NOTIFY_MINUTES` | — | 預設提前通知分鐘數（預設 10） |
| `TCBUS_CACHE` | — | 靜態資料快取目錄（預設 `cache`） |
| `TCBUS_CONNECT_TIMEOUT` | — | 等 Discord 連線的秒數（預設 30） |
| `TCBUS_DB` | — | 訂閱組的 SQLite 路徑（預設 `tcbus.db`；`:memory:` = 不落地） |
| `TCBUS_ENV` | — | **.env 檔的路徑**（也可以是「放 .env 的資料夾」；見下） |
| `TCBUS_MONGO` | — | MongoDB 連線字串（設定後訂閱組改存 MongoDB，所有平台共用） |
| `TCBUS_MONGO_DB` | — | MongoDB 資料庫名稱（預設 `tcbus`） |
| `SAY_ALLOWED_USERS` | — | 允許使用 `/say` 的 Discord 使用者 ID（逗號分隔）。**不設定 = 所有人都能用** |
| `LLM_API_KEY` | — | OpenAI 相容 API 金鑰。**有填才會啟用 AI 聊天**（見「AI 聊天」一節） |
| `LLM_BASE_URL` | — | LLM 端點（預設 `https://api.deepseek.com/v1`；任何 OpenAI 相容服務都可以） |
| `LLM_MODEL` | — | 模型名稱（預設 `deepseek-flash`） |
| `LLM_SYSTEM_PROMPT` | — | 系統提示（人格與範圍；不設定就用內建的） |
| `LLM_TOOLS` | — | 要不要讓 LLM 用工具真的動手（預設 true） |
| `LLM_WEEKLY_TOKENS` | — | 每週 token 上限（全域；預設 300000；`0` = 不限） |
| `LLM_SEGMENT_GAP_MINUTES` | — | 超過幾分鐘沒說話就當成新的一段（預設 30） |

> 📄 **完整清單在 `env-vars.csv`**（36 個變數：名稱／別名／必填／預設值／說明／是新增的還是原有的）。
> `--dryrun` 會雙向比對「程式用到的鍵」與「CSV 寫的鍵」，漏寫或過期都會被指出來。

也可以用命令列覆寫，例如
`--token <值>`、`--data tdx`、`--guild <id>`、`--verbose`、`--no-poller`、`--refresh`。
選項支援 `--opt 值`、`--opt=值`、`--opt:"值"` 三種寫法（值是路徑時建議加引號）。

### 三種設定來源可以混用

優先序：**命令列 &gt; 系統環境變數 &gt; `.env` 檔**。同一個鍵在好幾個地方都有時，左邊的贏；
啟動橫幅會直接告訴你每一個值是從哪來的：

```
  Discord Token：72 字元，來源：.env 的 DISCORD_TOKEN
  MongoDB　　　：mongodb+srv://***@cluster0.xxxxx.mongodb.net/...
```

常見的用法是**金鑰放系統環境變數、其他設定放 `.env`**：

```powershell
# 系統環境變數（金鑰不落地）
$env:DISCORD_TOKEN = "你的Token"

# .env 裡直接引用它 —— 不用把金鑰抄進檔案
TCBUS_MONGO=${MONGO_URI}
TCBUS_POLL_INTERVAL=30
```

`${NAME}` 的展開規則：**先找同一個 `.env` 裡的其他鍵，再找系統環境變數**；
兩邊都沒有就保留原樣（`${TCBUS_TEST_NOT_X}`）—— 保留原樣才看得出是哪個變數忘了設，
而不是變成空字串後查不出原因。

**完全沒有 `.env` 檔也可以** —— 只用系統環境變數就能跑（實測輸出）：

```
  .env 檔　　　：（找不到 .env）
  Discord Token：22 字元，來源：環境變數 DISCORD_TOKEN
  TDX 金鑰　　 ：已設定（會員模式）
  輪詢間隔　　 ：60 秒／預設提前通知：5 分鐘
  MongoDB　　　：mongodb://localhost:27017
```

```powershell
# 一句 .env 都不用寫
$env:DISCORD_TOKEN       = "你的Token"
$env:TDX_CLIENT_ID       = "..."
$env:TDX_CLIENT_SECRET   = "..."
$env:TCBUS_MONGO         = "mongodb+srv://..."
$env:TCBUS_POLL_INTERVAL = "30"
dotnet run --project src\TcBusBot.Discord
```

每個設定都有對應的環境變數名稱（見下面「所有可用的 `.env` 設定」表格，鍵名完全相同）。

**`.env` 也會被匯出到行程環境變數**：載入後，程式內任何地方（含其他程式庫）
都能用 `Environment.GetEnvironmentVariable("DISCORD_TOKEN")` 讀到，
橫幅也會註明 `（已把 N 個鍵匯出到環境變數）`。
已經存在的系統環境變數**不會被 `.env` 覆蓋**（維持上面的優先序）。

### .env 要放哪裡 / 怎麼指定

**預設**：從「目前工作目錄」與「執行檔目錄」各往上找 8 層，找到 `.env` 就用。
所以在專案根目錄執行 `dotnet run --project src\TcBusBot.Discord` 就找得到 `.env`。

**要放在別的地方**（或檔名不叫 `.env`），有三種方式（優先序：命令列 > 環境變數 > 自動搜尋）：

```powershell
# 1) 指定檔案（檔名不限，什麼都可以）
dotnet run --project src\TcBusBot.Discord -- --env D:\secrets\tcbus.env

# 2) 指定資料夾 → 會讀那個資料夾裡的 .env
dotnet run --project src\TcBusBot.Discord -- --env D:\secrets

# 3) 環境變數（適合寫在啟動腳本裡）
$env:TCBUS_ENV = "D:\secrets\tcbus.env"
dotnet run --project src\TcBusBot.Discord
```

路徑可以帶引號（PowerShell 常直接貼 `'D:\secrets\tcbus.env'`）。
啟動時橫幅會印出**實際用了哪個檔案、以及是怎麼找到的**：

```
  .env 檔　　　：D:\secrets\tcbus.env（命令列 --env）
  Discord Token：72 字元，來源：.env 的 DISCORD_TOKEN
```

指定的路徑不存在時會直接警告（不會默默用錯的設定）：

```
  .env 檔　　　：（找不到 D:\secrets\tcbus.env）
  ⚠️  找不到指定的 .env：D:\secrets\tcbus.env
     用法：--env <路徑>　或　--env <資料夾>　或　設定 TCBUS_ENV
```

靜態資料會快取在 `cache\`（預設 12 小時），所以重啟不會重複抓、不會浪費點數。

### 點數怎麼省的

TDX 公車 v2 的公式是 **`呼叫次數 / 1,500` ＋ `回傳資料量(MB) / 150`** —— 呼叫次數**和資料量**都要錢。

| 做法 | 省下什麼 |
| --- | --- |
| **輪詢以「上車站」過濾，不用路線過濾** | 一次請求 `$filter=StopUID eq 'A' or StopUID eq 'B' …` 最多涵蓋 **40 個站**；而且不會為了 1 個站下載整條路線上百站的資料 |
| 上車站**去重後**才查 | 100 個人關注同一個站 → 仍然只有 1 次呼叫；使用者勾幾個候選月台也不影響成本 |
| `$select` 只拿 8 個用得到的欄位 | 實測每筆資料**少 69% 位元組**（608 → 187 bytes） |
| 沒有任何訂閱時完全不呼叫 | 0 次呼叫 |
| 靜態資料磁碟快取 12 小時 | 重啟不再抓一次（靜態資料也是有算點數的） |

想確認實際數字，跑 `--dryrun`，它會印出**真正的輪詢計畫**（每週期幾次呼叫、預估幾筆、
每月點數估算）以及**會送出的那條 URL**：

```
▶ Step 8b  輪詢計畫（唯一會花點數的地方）
  （實測：完整一筆約 608 bytes、$select 後約 187 bytes → 少 69%）
  2 個不重複的上車站 → 每週期 1 次呼叫（每批 40 站）
  預估回傳 62 筆 ETA（每筆約 187 bytes，已用 $select 精簡）
  間隔 30 秒 → 2,880 次/日、約 31.84 MB/日（未壓縮）
  點數估算：呼叫 57.6 ＋ 資料量 6.4 = 約 64 點/月
  （官方公式：呼叫次數 / 1,500 ＋ 回傳資料量(MB) / 150）
```

以 30 秒輪詢估算約 **60~80 點/月**（隨訂閱的站數變動），銅級（NT$200/月，200 點）足夠。
免費基礎會員 3 點/月只夠開發測試。

> **想更省**：`TCBUS_POLL_INTERVAL=60` 會讓呼叫與資料量都減半（約 30~40 點/月）。
> 台中 N1 來源端約 20 秒才更新一次，所以再往下調沒有意義。

---

## 離線開發工具

核心邏輯（`TcBusBot.Core`）**零外部套件相依**，可以在沒有網路、沒有 TDX 金鑰、
沒有 Discord Token 的情況下完整測試。

```powershell
# 637 項驗收測試：搜尋、群組、匹配、通知判定、簡繁折疊、縮寫、輪詢成本、儲存後端與 DI 註冊、.env 路徑、合併與復原、結束追蹤、AI 聊天切段與每週額度、LLM 工具訂閱、可馴服的提示詞、認人與主人授權、面板與 AI 共用站牌解析、路線號碼與轉乘查詢、模型思考開關、偷聽模式（含「沒窗口就不花錢問」、三段判斷、@ 別人不會退出、閒聊留下來當上下文、不會顯示假的「正在輸入」）、對話記憶容量上限與學到的規則上限（每段／每頻道／頻道數／TTL／規則條數、管理員 /ai pset／跨伺服器複製設定／按鈕授權／雙模型／多人排隊／多城市／看圖）、SQLite 持久化
dotnet run --project src\TcBusBot.Cli -- selftest

# 模糊站牌搜尋
dotnet run --project src\TcBusBot.Cli -- search 台中車站
dotnet run --project src\TcBusBot.Cli -- search 靜宜

# 路線匹配 + 訂閱展開
dotnet run --project src\TcBusBot.Cli -- route 台中車站 靜宜大學

# 逐條說明「哪條路線為什麼被排除」
dotnet run --project src\TcBusBot.Cli -- diag 台中科技大學 大坑口

# 診斷 MongoDB（網路探測 + 連線延遲 + 完整 CRUD 驗證）
dotnet run --project src\TcBusBot.Cli -- mongo --env TcBusBot-1.env
dotnet run --project src\TcBusBot.Cli -- mongo --env TcBusBot-1.env --write-test
dotnet run --project src\TcBusBot.Cli -- mongo "mongodb+srv://…" --db TCBUS_SELFTEST

# Discord UI 離線驗證：把整個互動流程跑一遍並檢查元件是否合法
dotnet run --project src\TcBusBot.Discord -- --dryrun
```

> 參數要放在 `--` 之後（`dotnet run` 自己的選項在 `--` 之前）。

`--dryrun` 會做兩件事：

1. **元件限制**：選項 ≤25、custom_id ≤100 字元、按鈕每列 ≤5 個、embed 字數上限…
2. **接線檢查**：畫面上每個按鈕都要有處理函式，**每個處理函式也都要真的出現在畫面上**
   （曾經發生過「存成訂閱組」功能寫好但按鈕從沒被送出去，實機完全看不到 —— 元件限制全過也抓不到）。

---

## 專案結構

```
src/TcBusBot.Core/            零外部套件相依
├─ Models/TdxModels.cs              TDX DTO + 容錯轉換器
├─ Tdx/TdxApiClient.cs              Token 快取 + 所有 TDX HTTP 呼叫
├─ DataSources/
│   ├─ StaticDataLoader.cs          本機快取 → TDX（省點數）
│   └─ MiniFixtureSource.cs         內建最小資料集
├─ Bus/
│   ├─ StopNameNormalizer.cs        ★ 搜尋用 / 分組用，兩套正規化
│   ├─ StopSearchService.cs         ★ 模糊搜尋（六種策略 + 人氣權重）
│   ├─ StopAreaGrouping.cs          建議群組（前綴 + 座標叢集）
│   ├─ TaichungBusDataService.cs    ★ 靜態索引 + 候選集合匹配
│   └─ LocationTarget.cs            地點目標 / BoardChoice / RouteOption
├─ Storage/
│   ├─ SqliteDatabase.cs            ★ 零套件的 SQLite 封裝（P/Invoke winsqlite3.dll）
│   ├─ SavedGroupStore.cs           ★ 訂閱組儲存（四種後端自動挑：Mongo→SQLite→文字檔→記憶體）
│   ├─ MongoSavedGroupRepository.cs  MongoDB 後端 + document 轉換
│   ├─ SqliteSavedGroupRepository.cs SQLite 後端
│   ├─ ISavedGroupRepository.cs      後端介面 + 文字檔後端
├─ Realtime/RealtimeBusCache.cs     全體使用者共用的 ETA 快取
└─ Subscriptions/                   ★ 訂閱群組 / 服務 / Matcher（挑最快 + 去重）

src/TcBusBot.Discord/         Discord.Net 3.18.0
├─ Program.cs                       入口（啟動流程；服務由 DI 容器提供）
├─ BotServices.cs                   ★ 服務註冊（composition root；含儲存後端）
├─ BusUi.cs                         ★ 所有 Embed / 元件組裝（純函式）
├─ BusSession.cs                    面板狀態（記憶體）
├─ BotRuntime.cs                    通知預覽（模擬 / 真實）
├─ EtaPoller.cs                     唯一的輪詢迴圈（全體共用）
├─ DryRun.cs                        離線 UI 驗證
└─ src/TcBusBot.Mobile/           ← Android 版（APK）
    ├─ MainApplication.cs            行程啟動時裝好 SQLite 後端與 Console
    ├─ MainActivity.cs               設定畫面 + 日誌 + 啟停服務
    ├─ BotService.cs                 前景服務：跑同一份 Program.Main
    ├─ AppSettings.cs                設定寫成 .env（與桌面版共用 BotConfig）
    ├─ AndroidSqliteBackend.cs       android.database.sqlite 後端
    └─ BotConsole.cs                 Console → 畫面 + logcat
└─ Modules/                         /bus 指令 + 按鈕/選單/Modal 處理

src/TcBusBot.Cli/             離線開發工具（tcbus）
tests/fixtures/               驗收用資料集
```

---

## 幾個關鍵設計（踩過的坑）

| 坑 | 處理方式 |
| --- | --- |
| 台中同一條路線的**去程與返程共用同一個 `SubRouteUID`** | 一律用 `(RouteUID, Direction)` 當鍵 |
| 300 路的「臺中車站」叫 **A月台**，304 路叫 **臺灣大道** | 站名比對不能用完全相等，要前綴正規化 |
| 使用者打「台」但資料是「臺」 | 正規化（臺↔台、簡繁折疊、全形、口語「火車站」）＋縮寫與漏字容忍 |
| 簡→繁轉換會挑錯字（`干净`→`干凈`、`头发`→`頭發`） | **反過來做**：索引與查詢都折疊成簡體（多對一、幂等），顯示才是繁體 |
| 打縮寫「台中科大」找不到「國立臺中科技大學」 | 緊湊子序列（中間只漏一個字）算強相符，不再被強相符過濾器藏起來 |
| **TDX 的 ETA 不會自動遞減** | 自行用 `EstimateTime - (now - SrcUpdateTime)` 遞減 |
| 一台公車會依序經過多個候選上車站 | 去重鍵用 `(路線,方向,車牌)`，**不含站牌** |
| 多個候選站 → 多個訂閱，可能一次噴好幾則通知 | 預設只通知**最快到**的那班，其他附在同一則的「其他選擇」 |
| 候選站牌很多會不會讓 API 呼叫爆掉？ | 不會。輪詢只認「不重複的上車站」，與訂閱數脫鉤 |
| MQTT 能不能拿來做到站通知？ | **不能**。TDX MQTT 只有 Alert / News，沒有 ETA（見研究報告 §5） |

---

## 已知限制

- **重啟後訂閱消失**：訂閱本身與面板狀態都在記憶體（依需求）。重啟後面板會回覆「已失效，請重新執行 `/bus panel`」。**但「訂閱組」存在 SQLite，重啟後還在。**
- **訂閱組需要 SQLite**：使用 Windows 內建的 `winsqlite3.dll`（實測 SQLite 3.51.1），不需要任何 NuGet 套件。非 Windows 環境會退回記憶體模式（訂閱組重啟後消失）。
- **通知方式目前固定為「訂閱時所在的頻道」**。私訊模式（DM）還沒接上開關。
- **沒有 TDX 金鑰時不會有真實到站通知**；請用「模擬一則通知」驗收通知內容與格式。
- **手機版（APK）**：需要 Android 7.0 以上，APK 約 44 MB，且**必須關閉電池最佳化**才會穩定輪詢（見上面「手機版」一節）。

## 輸入容忍度

搜尋不需要打完整或正確的站名：

| 你打的 | 會找到 | 怎麼做到的 |
| --- | --- | --- |
| `台中車站` | 臺中車站(A月台) | 臺 ↔ 台 正規化 |
| `台中车站`（簡體） | 臺中車站(A月台) | 簡繁折疊（索引與查詢統一到簡體） |
| `干净` / `头发` | 乾淨 / 頭髮 | 一對多的字（乾淨、頭髮）另補對照 |
| **`台中科大`（縮寫）** | **國立臺中科技大學** | 緊湊子序列（中間只漏一個字）→ 算強相符 |
| `台中火車站` | 臺中車站(A月台) | 口語「火車站」→「車站」 |
| `Ａ月台` | 臺中車站(A月台) | 全形 → 半形 |
| `Taichung Station` | 臺中車站(A月台) | 英文站名欄位 |
| `中車站` | 臺中車站(A月台) | 子字串／子序列（容忍漏字） |
| `靜宜` | 靜宜大學(專用道) | 前綴相符 |

> **簡繁的處理方向是「繁 → 簡」**（多對一、幂等），不是「簡 → 繁」。
> 因為簡→繁是一對多，Windows 的對照表會挑錯字：實測 `头发`→`頭發`、`干净`→`干凈`，
> 兩者都跟 TDX 的「頭髮」「乾淨」對不上。折疊成簡體之後，
> 打繁體、打簡體、甚至半簡半繁，都會命中同一筆站牌（顯示仍然是 TDX 的繁體原名）。

**同名站牌會自動合併**：同一個站名的去回程／不同月台（多個 `StopUID`）
只會顯示成一個選項，不會出現「中正國小、中正國小」兩筆。
而搜尋有強相符（完全／前綴／子字串／縮寫）的結果時，會濾掉模糊相符的雜訊
（搜「臺中車站」不會被「沙鹿車站」「潭子車站」塞滿）。
