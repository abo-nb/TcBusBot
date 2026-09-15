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
| M2 靜態索引 + 模糊搜尋 + 候選集合匹配 | ✅ 244 項離線驗收測試全過 |
| **M3 Discord UI** | ✅ 已實作（含訂閱組；實機驗收中） |
| M4 即時輪詢 | ✅ 程式完成（輪詢迴圈 + 到站時間總表），實際運作需 TDX 金鑰 |
| **M7 Android APK**（掛在舊手機上） | ⏸ **擱置**（已實機驗證過，程式碼留著；改用 Render） |
| **M10 Render 部署** | ✅ 健康檢查端點 + 防休眠 + Dockerfile + `render.yaml`（端點有離線測試） |
| **M8 儲存後端**（MongoDB / SQLite / 文字檔） | ✅ 自動挑選 + 失敗退回本機，都有測試 |
| **M9 Termux**（手機上的控制台） | ✅ 發佈腳本 + 實機步驟（`publish-termux.ps1`） |
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
```

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
| `TCBUS_DATA` | — | `auto`（預設）／`fixture`／`tdx` |
| `TCBUS_POLL_INTERVAL` | — | 即時輪詢間隔秒數（預設 30；低於 20 沒有意義） |
| `TCBUS_NOTIFY_MINUTES` | — | 預設提前通知分鐘數（預設 10） |
| `TCBUS_CACHE` | — | 靜態資料快取目錄（預設 `cache`） |
| `TCBUS_CONNECT_TIMEOUT` | — | 等 Discord 連線的秒數（預設 30） |
| `TCBUS_DB` | — | 訂閱組的 SQLite 路徑（預設 `tcbus.db`；`:memory:` = 不落地） |
| `TCBUS_ENV` | — | **.env 檔的路徑**（也可以是「放 .env 的資料夾」；見下） |
| `TCBUS_MONGO` | — | MongoDB 連線字串（設定後訂閱組改存 MongoDB，所有平台共用） |
| `TCBUS_MONGO_DB` | — | MongoDB 資料庫名稱（預設 `tcbus`） |

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
# 244 項驗收測試：搜尋、群組、匹配、通知判定、簡繁折疊、縮寫、輪詢成本、儲存後端、.env 路徑、合併與復原、SQLite 持久化
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
├─ Program.cs                       入口（手工組裝，不用 DI 容器）
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
