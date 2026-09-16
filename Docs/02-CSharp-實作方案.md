# 台中市公車 Discord Bot：C# 實作方案

> 搭配 `docs/01-TDX-研究報告.md` 閱讀。本方案的所有 API 行為與資料結構，都以研究報告中
> **[實測]** 的結果為依據；關鍵設計決策會標明「為什麼這樣做」。

> ### 📌 需求變更紀錄
>
> **v14（目前）**：**部署到 Render**（Web Service ＋ 健康檢查 ＋ 防休眠）。
> - `TcBusBot.Discord` 內建極輕量 HTTP 端點（`GET /` 與 `GET /health`），
>   在**連 Discord 之前**就開埠（Render 探測很快，太慢會被判定部署失敗）
> - 用 **`TcpListener` 自己回 HTTP/1.1**，不是 `Sdk.Web`／`HttpListener`：
>   前者會讓 Termux 版「裝了但起不來」，後者在 Windows 需要 URL ACL
>   （變成「Render 上可以、本機跑不起來」）→ 兩者都無法在本機驗證（§19.2）
> - **防休眠**：每 10 分鐘 ping 自己（`APP_URL`／`RENDER_EXTERNAL_URL`），
>   並在啟動時誠實說明「睡著之後就叫不醒自己」（§19.4）
> - `Dockerfile`（修掉 `ENTRYPOINT` 的組件名、非 root 寫入路徑、離線資料集）＋
>   `render.yaml`（`type: web`／`runtime: docker`／`healthCheckPath: /health`）（§19.5）
> - Android APK 專案**暫時擱置**（程式碼留著，已從方案建置移除）
>
> **v13**：**設定來源三合一**（命令列／系統環境變數／`.env`）。
> - `.env` 的值可以用 `${VAR}` 引用**同檔案的其他鍵**或**系統環境變數**
>   （兩邊都沒有就保留原樣，看得出是哪個沒設）→「金鑰放系統環境變數、其他放 .env」可行（§3.1）
> - 載入 `.env` 之後會**匯出到行程環境變數**，所以任何地方都能用
>   `Environment.GetEnvironmentVariable("DISCORD_TOKEN")` 讀到；
>   既有的系統環境變數不會被覆蓋
> - 啟動橫幅會說明**每個值來自哪裡**（`.env 的 X`／`環境變數 X`／`命令列 --x`），
>   以及匯出了幾個鍵
>
> **v12**：**儲存後端四選一**（MongoDB → SQLite → 文字檔 → 記憶體）＋ **Termux 跑法**。
> - 儲存抽成 `ISavedGroupRepository`：SQLite／文字檔／MongoDB 三個實作，行為完全一致（§8.3）
> - **MongoDB 排第一**：設了 `TCBUS_MONGO` 就所有平台共用同一份訂閱組
>   （連不上時**不硬撐**：3 秒逾時 → 退回本機 + 明確警告）
> - **文字檔後備**（`saved_groups.txt`，一行一筆、中文不轉義）：沒有 SQLite 的環境
>   （Termux、精簡系統）訂閱組仍然留得住 —— 這正是 §18 Termux 跑法能成立的原因
> - **Termux 跑法**：`publish-termux.ps1` 發佈可攜式建置（13 MB，zip 後 5 MB），
>   手機上 `pkg install dotnet-runtime-8.0` 之後就有**完整控制台**（§18）
>
> **v11**：**Android APK**（掛在舊手機上跑）。
> - 新增 `TcBusBot.Mobile`（`net10.0-android`）**直接參考 `TcBusBot.Discord`** ——
>   不複製任何 Bot 邏輯，手機版與桌面版的能力天生一致（§17.1）
> - Android **不能**用 `winsqlite3.dll` 那一套（`libsqlite3.so` 從 API 24 起是私有的），
>   因此把 SQLite 抽成 `ISqliteBackend`，Android 走系統的 `android.database.sqlite`（§17.2）
> - 前景服務 + 電池最佳化白名單，Android 才不會把掛整晚的 Bot 殺掉（§17.3）
> - 桌面版只做了三處「加法」：`Program.RequestStop()`、`OutputEncoding` 包 try/catch、
>   SQLite 後端介面 —— 行為完全不變（181 項驗收與乾跑都照舊通過）
> - 實測產物：`dist/tcbusbot-mobile.apk` 約 44 MB，`minSdkVersion 24`（Android 7.0），
>   含 32 位元 ARM，已簽署可直接安裝（§17.5）
> - ⚠️ **兩個實機才會發現的坑**（都靠 `adb` 實測抓出來）：
>   自訂 `[Application]` 子類別的 stub 沒有 `TypeManager.Activate` → `UnsatisfiedLinkError`（§17.4b）；
>   以及 `SelfContained=false` 會讓整個 Mono 執行階段起不來（§17.1）
> - **可選擇工作目錄**：App 私有目錄在沒 root 的手機上拿不出來，
>   所以內建「📁 選擇工作目錄」——預設放到 `/storage/emulated/0/TcBusBot`，
>   插 USB 就看得到 `app.env` 與 `tcbus.db`（§17.6）
>
> **v10**：**一次訂閱多個組** ＋ **合併** ＋ **復原**。
> - 「📂 我的訂閱組」的選單改成**多選**，可以一次勾多個組
> - 三個動作分開：**▶ 各自獨立訂閱**（各通知各的）、**🧩 合併成一個通知流**
>   （所有行程放一個訂閱群組 → 只通知最快的那一班）、**🔗 合併成新組**
>   （把多個組合併成**一個多段行程的訂閱組**存起來）（§8.2）
> - **↩️ 復原**：批次套用／合併／批次刪除／改名都能一鍵還原，深度 10 層（§8.2）
> - 訂閱組的 payload 從「單段」變成「多段」`legs`，**舊格式自動升級**，已存的組不會消失
> - 通知的「起訖」改用**訂閱自己那一段的行程**（合併後才不會顯示錯的行程）
> - 乾跑抓到並修掉一個真的 bug：訂閱被取消後，「路線」欄位可能是空的 → Discord 拒絕空欄位值 → 整則訊息建不出來
>
> **v9**：**提前 2 分鐘**的通知選項 ＋ **輪詢成本可驗證**。
> - 提前通知時間改為 **2 / 5 / 10 / 15 / 30 分鐘**（Discord 一列最多 5 個按鈕，這已是上限）
> - `$select` 只要求真正會用到的 8 個欄位：**實測每筆資料少 69% 位元組**
>   （608 → 187 bytes），直接反映在點數上（§9.2）
> - `--dryrun` 新增 **Step 8b 輪詢計畫**：印出每週期幾次呼叫、預估幾筆資料、
>   每月點數估算，以及**實際會送出的那條 URL**（可以自己拿去驗證）（§9.4）
> - 新增 `tcbus selftest` 第 15 節，擋住「不小心又要求用不到的欄位」
> - 文件補上「為什麼用 StopUID 過濾而不是用路線過濾」的完整比較（§9.1）
> - **`.env` 路徑可以指定了**：`--env <檔案或資料夾>`、`TCBUS_ENV` 環境變數、
>   帶引號的路徑、`--opt=值` 寫法；指定卻找不到會直接警告（§3.1）
>
> **v8**：**真正的簡繁轉換** ＋ **縮寫搜尋**。
> - 打「台中科大」找不到「國立臺中科技大學」的原因有兩個，兩個都修好了：
>   1. 縮寫（中間漏字）以前只算**弱相符**，被「只顯示強相符」的過濾器藏起來
>      → 新增 **§5.4 Step 2 的「縮寫」策略**（緊湊子序列 → 強相符）
>   2. 簡繁轉換方向錯了：簡→繁是**一對多**，Windows 的表會挑錯字
>      （實測 `头发`→`頭發`、`干净`→`干凈`，都跟資料對不上）
>      → 改成**一律折疊成簡體**（多對一、幂等），見 **§5.4 Step 1.5**
> - 搜尋的鍵改為簡體，**顯示仍然是 TDX 的原始繁體**；既有的路線匹配結果不變
>   （實測 `臺中車站→靜宜大學` 仍是 20 條、`國立臺中科技大學→大坑口` 仍是 11 條）
>
> **v7**：新增**訂閱組**（可重複使用的訂閱範本）＋ **SQLite 持久化**。
> - 「💾 存成訂閱組」把目前的起訖點與路線存成具名範本；「📂 我的訂閱組」可一鍵套用／改名／刪除（§8.1）
> - 按鈕位置：**訂閱完成／看到站時間的那張卡片的第二列**（`BusUi.EtaComponents`）
> - **套用時用目前的站序資料重新匹配**，不是複製舊訂閱 → 路線改道時不會產生錯誤訂閱
> - 儲存層：**零 NuGet 相依的 SQLite 封裝**（P/Invoke Windows 內建的 `winsqlite3.dll`，實測 3.51.1）
> - 這是專案裡唯一使用資料庫的地方 —— 訂閱仍在記憶體，只有使用者刻意存的範本要持久化
> - ⚠️ **實機測試抓到的錯**：處理函式與按鈕都寫好了，但「訂閱完成」實際送出的那一組元件
>   沒有把「💾 存成訂閱組」加進去 → 使用者端看到的是「這個功能不存在」。
>   純看元件限制的檢查抓不到這種錯，因此新增 **§7.6 接線檢查**（按鈕 ↔ 處理函式的雙向驗證）。
>
> **v6**：修正「明明有 11 條路線卻只找到 1 條」——選項值改用短鍵（`g:` / `n:群組:索引`），
> 避免把 44 個同名站牌的 StopUID 串進 Discord 的 100 字元 value 上限而被截斷。
> 並新增 `tcbus diag` 診斷指令與 `cache/` 離線讀取。
>
> **v5**：明確的訂閱動作、同名站牌合併、簡體中文輸入、只顯示強相符結果。
>
> **v4**：**多個候選站 = 多個訂閱**；通知時**從這些訂閱中挑公車最快到的那一班**。
>
> **v3**：**地圖座標那條路先不做**，第一階段以**模糊站牌搜尋**為主要輸入。
>
> **v2**：起點／目的地改為**地點目標（LocationTarget）**，內含一組**候選站牌**。

---

## 0. 最重要的一個架構修正

你原本的架構把 MQTT 當成即時公車資料的主要來源。**研究結果否定了這一點**：

```
❌ 原假設：TDX MQTT → 即時公車位置/ETA → Realtime Cache → 通知
✅ 實情：  TDX MQTT 只有 Bus/News 與 Bus/Alert（沒有位置、沒有 ETA）
```

因此第一階段的架構修正為：

```
                    ┌──────────────────────────────────────┐
                    │  TDX HTTP API（OAuth2 Client Credentials）
                    │                                      │
  啟動時一次 ──────▶│  Route / Stop / StopOfRoute          │──▶ 記憶體靜態索引
                    │  （建索引後不再呼叫）                  │
                    └──────────────────────────────────────┘

                    ┌──────────────────────────────────────┐
   每 30~60 秒 ────▶│  EstimatedTimeOfArrival（N1）         │──▶ RealtimeBusCache
   「一次」呼叫 ────▶│  $filter=StopUID eq 'A' or ...       │    (route,dir,stop) → ETA
                    └──────────────────────────────────────┘
                                     │
                                     ▼
                          Subscription Matcher
                          ├── User A（300 路，5 分鐘前通知）
                          ├── User B（304 路，10 分鐘前通知）
                          └── User C ...

  ┌──────────────────────────────────────────────────────────────┐
  │ 使用者定義的「地點」（LocationTarget）                          │
  │   模糊站牌搜尋 → 建議群組／站牌多選 ──▶ CandidateStopUids[]      │
  │   → 路線匹配只認這一份清單（記憶體運算，零 API 呼叫）            │
  └──────────────────────────────────────────────────────────────┘

                    ┌──────────────────────────────────────┐
   MQTT（第二階段）──▶│  v2/Bus/Alert/City/Taichung           │──▶ 改道/停駛通知
   推播、不計點數      │  v2/Bus/News/City/Taichung            │    （附加價值）
                    └──────────────────────────────────────┘
```

**核心精神不變，而且更貼近你的要求**：
- 靜態資料 → 啟動時抓一次，記憶體快取
- 即時資料 → **一個輪詢迴圈、一次 HTTP 呼叫、全體使用者共用**
- 沒有 EF、沒有 Repository、沒有 Unit of Work、沒有服務分層
- 只有**一個** SQLite 檔案，而且只存「使用者刻意留下的訂閱組」（§8.1）——
  訂閱本身、面板狀態、站牌索引全部只在記憶體
- MQTT 只用在它真正擅長、且**不計點數**的推播資料上

---

## 1. 技術選型

| 項目 | 選擇 | 依據 |
| --- | --- | --- |
| Runtime | **.NET 8.0 (LTS)** | 本機已有 SDK 8.0.412 / 9.0.303 / 10.0.300；Discord.Net 3.20.1 直接支援 net8/9/10 |
| Discord | **Discord.Net 3.20.1** | NuGet 最新穩定（2026-06-07），dev 分支 2026-09-09 仍有 commit；沒有 v4 |
| MQTT | **MQTTnet 5.2.0.1603** | NuGet 最新穩定；TDX 官方範例即用 MQTTnet（第二階段才需要） |
| DI / Hosting | `Microsoft.Extensions.Hosting` 8.x | `BackgroundService` 很適合輪詢與 MQTT 生命週期 |
| JSON | `System.Text.Json` | 內建，source-generated 可選 |
| 儲存 | **純記憶體**（`ConcurrentDictionary`）＋**一個 SQLite 檔案**（只放訂閱組，§8.1）| 依你的要求，重啟後訂閱消失可接受；但使用者自己整理的訂閱範本值得留下 |

> 不用 DSharpPlus（v5 只有 nightly，NuGet 上的 `5.0.0` 是 2023 年 deprecated 套件）、
> 不用 NetCord（尚無穩定版、且需 net10）。

---

## 2. 專案結構（單一 Console 專案）

刻意保持扁平，**沒有 interface 抽象層、沒有 repository、沒有 mediator**。

```
TcBusBot/
├─ TcBusBot.csproj
├─ Program.cs                          # Host 組裝
├─ appsettings.json
├─ appsettings.Development.json        # 放金鑰（.gitignore）
├─ Options/
│   ├─ TdxOptions.cs                   # ClientId / Secret / Mqtt 三兄弟
│   ├─ DiscordOptions.cs               # Token / 通知模式
│   └─ MonitorOptions.cs               # 輪詢間隔 / 提前通知選項 / 分批大小
├─ Tdx/
│   ├─ TdxTokenProvider.cs             # token 快取 + 401 重取
│   ├─ TdxApiClient.cs                 # 唯一的 HTTP 出入口
│   └─ Models/
│       ├─ LocalizedName.cs            # { Zh_tw, En }
│       ├─ BusRoute.cs                 # Route / SubRoutes[]
│       ├─ BusStop.cs                  # Stop
│       ├─ BusStopOfRoute.cs           # StopOfRoute / Stops[]
│       ├─ BusEta.cs                   # EstimatedTimeOfArrival
│       ├─ BusRealTimeNearStop.cs      # RealTimeNearStop
│       └─ BusAlert.cs                 # Alert / News
├─ Bus/
│   ├─ TaichungBusDataService.cs       # 靜態資料 + 所有索引（核心）
│   ├─ LocationTarget.cs               # 地點目標：一組候選站牌
│   ├─ StopNameNormalizer.cs           # ★ 站名正規化（搜尋用／分組用，兩套）
│   ├─ StopSearchService.cs            # ★ 模糊站牌搜尋（本版核心互動）
│   ├─ StopGroup.cs                    # 建議群組（前綴正規化 + 座標叢集）
│   ├─ Geo.cs                          # Haversine（目前只給建議群組用）
│   └─ RouteOption.cs                  # 路線查詢的回傳模型（含代表上下車站）
├─ Subscriptions/
│   ├─ Subscription.cs                 # 訂閱資料
│   ├─ SubscriptionService.cs          # 記憶體 CRUD + 索引
│   └─ SubscriptionMatcher.cs          # 判斷誰要通知 + 去重
├─ Realtime/
│   ├─ RealtimeBusCache.cs             # (route,dir,stop) → EtaRecord
│   └─ EtaPollerService.cs             # BackgroundService：唯一的輪詢迴圈
├─ Notifications/
│   └─ DiscordNotifier.cs              # 通知佇列 + 節流 + 組 embed
├─ Storage/                            # §8.1：唯一會落盤的東西
│   ├─ SqliteDatabase.cs               # ★ 零套件 SQLite（P/Invoke winsqlite3.dll）
│   └─ SavedGroupStore.cs              # 訂閱組 CRUD（JSON payload + 反正規化欄位）
├─ Discord/
│   ├─ DiscordBotService.cs            # BackgroundService：Gateway + 指令註冊
│   ├─ BusCommandModule.cs             # /bus 指令群
│   ├─ StopAutocompleteHandler.cs      # 站名 autocomplete
│   └─ BusComponentModule.cs           # [ComponentInteraction] 按鈕/選單
└─ Mqtt/
    └─ TdxMqttService.cs               # 第二階段：Alert / News 推播
```

**實際落地時多了一個 Android 宿主專案**（見 §17）：

```
src/TcBusBot.Mobile/          net10.0-android，只做「宿主」
├─ MainApplication.cs         行程啟動時裝好 SQLite 後端與 Console
├─ MainActivity.cs            設定畫面 + 日誌 + 啟停服務
├─ BotService.cs              前景服務：跑同一份 Program.Main
├─ AppSettings.cs             設定 → .env（交給既有的 BotConfig）
├─ AndroidSqliteBackend.cs    android.database.sqlite 的 ISqliteBackend
└─ BotConsole.cs              Console → 畫面 + logcat
```

**約 24 個檔案**，每個檔案單一職責，沒有多餘分層。

> ⚠️ 這是**設計初稿的目錄樹**。實際落地的結構（`TcBusBot.Core` / `TcBusBot.Discord` /
> `TcBusBot.Cli` 三個專案，`.env` 取代 `appsettings.json`）見 **§14.1**。

### 2.1 csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Discord.Net" Version="3.20.1" />
    <PackageReference Include="MQTTnet" Version="5.2.0.1603" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.*" />
  </ItemGroup>
</Project>
```

---

## 3. 設定

> **實際落地的是 `.env`，不是 `appsettings.json`。**
> 優先序：**命令列參數 &gt; 環境變數 &gt; `.env` 檔**。
> 這一節保留設計初稿的 JSON 樣貌，值與實際的 `.env` 鍵一一對應（見 README 的對照表）。

#### 3.1 三種設定來源（命令列 > 系統環境變數 > `.env`）與 `${VAR}` 展開

同一個鍵可以在三個地方設定，左邊的贏；啟動橫幅會印出**每個值實際來自哪裡**
（`來源：.env 的 DISCORD_TOKEN` / `來源：環境變數 DISCORD_TOKEN` / `來源：命令列 --token`）。

`.env` 的值可以引用變數：

```sh
TCBUS_MONGO=${MONGO_URI}          # 先找同檔案的鍵，再找系統環境變數
TCBUS_POLL_INTERVAL=${POLL}       # 兩邊都沒有 → 保留原樣，看得出是哪個沒設
```

| 決定 | 理由 |
| --- | --- |
| 展開順序：**同檔案 → 系統環境變數 → 保留原樣** | 讓「金鑰放系統環境變數、其他放 .env」可行；保留原樣才查得出漏設的變數 |
| 最多三輪展開 | 支援 `A=${B}`、`B=${C}` 串接，同時保證一定停得下來（`A=${B}`/`B=${A}` 不會無限迴圈） |
| 不做行內註解 | `KEY=value # 註解` 的 `#` 之後會留著 —— 值可能是 Token，寧可保留也不要誤刪 |
| **`.env` 會匯出到行程環境變數**（`DotEnv.ApplyToEnvironment`） | 讓行程內任何地方（含其他程式庫）都能 `Environment.GetEnvironmentVariable` 讀到 |
| 匯出**不覆蓋**既有的系統環境變數 | 否則會破壞「環境變數 > .env」的優先序 |
| 匯出時機在 `Resolve` **之後** | 太早匯出會讓 `Resolve` 在環境變數裡先找到 .env 的值，橫幅就把來源誤報成「環境變數」（實測抓到） |
| **找不到 `.env` 檔不是錯誤** | `LoadFromFile(null)` 回傳空字典，解析照樣走到環境變數 → **只用環境變數也能跑**（實機驗證：`.env 檔：（找不到 .env）` ＋ `來源：環境變數 DISCORD_TOKEN`） |
| 空的環境變數（`""` 或只有空白）視為未設定 | 不然「設成空字串」會被當成有效值，把後面的來源蓋掉 |

> 測試：`tcbus selftest` 第 11 節驗「優先序」（命令列／環境變數／.env／別名／空白值／三種命令列寫法），
> 第 11b 節驗「`.env` 與環境變數的互動」（`${VAR}` 引用、匯出、不覆蓋既有值）。
> 這段邏輯抽在 Core 的 `SettingResolver`，所以不需要啟動 Bot 就能驗。

#### 3.2 `.env` 檔的位置（`--env`）

| 來源 | 寫法 | 說明 |
| --- | --- | --- |
| **命令列** | `--env D:\secrets\tcbus.env` | 檔案（**檔名不限**）或**資料夾**（會讀裡面的 `.env`） |
| **環境變數** | `TCBUS_ENV=D:\secrets\tcbus.env` | 適合寫在啟動腳本／服務設定裡 |
| 自動搜尋 | （不指定） | 從「目前工作目錄」與「執行檔目錄」各往上找 8 層 |

設計上的幾個細節：

* **路徑帶引號也能用** —— PowerShell 使用者常直接貼 `'D:\secrets\tcbus.env'`，`FindFile` 會先 `Trim('"', '\'')`。
* **指到資料夾時自動找 `.env`** —— 這是使用者最直覺的輸入，不該只回「找不到檔案」。
* **明確指定卻找不到 → 直接警告**（而不是默默用別的設定）：
  ```
  .env 檔　　　：（找不到 D:\secrets\tcbus.env）
  ⚠️  找不到指定的 .env：D:\secrets\tcbus.env
     用法：--env <路徑>　或　--env <資料夾>　或　設定 TCBUS_ENV
  ```
* **啟動橫幅會印「用了哪個檔、怎麼找到的」** —— 避免「我改了 .env 怎麼沒生效」：
  ```
  .env 檔　　　：D:\secrets\tcbus.env（命令列 --env）
  ```
* 命令列選項三種寫法都吃：`--env <值>`、`--env=<值>`、`--env:"<值>"`。

> 為什麼不用 `Microsoft.Extensions.Configuration`：這個環境的 NuGet 快取沒有它
> （見 §2.1），而且 `.env` + 三個來源的優先序用 100 行就能寫清楚、還能在離線驗收裡測。

```jsonc
// 設計初稿：appsettings.json（金鑰請用 user-secrets 或環境變數覆寫）
{
  "Tdx": {
    "ClientId": "",
    "ClientSecret": "",
    "BaseUrl": "https://tdx.transportdata.tw",
    "City": "Taichung",
    "Mqtt": {
      "Enabled": false,                 // 第二階段才開
      "Host": "mqtt.transportdata.tw",
      "Port": 8883,
      "ClientId": "", "Username": "", "Password": ""
    }
  },
  "Discord": {
    "Token": "",
    "NotifyMode": "DirectMessage"       // DirectMessage | Channel
  },
  "Monitor": {
    "PollIntervalSeconds": 30,          // 30 秒 1 個週期（N1 來源端約 20 秒更新，勿低於 20）
    "MaxStopsPerQuery": 40,             // 每個 $filter 最多幾個 StopUID
    "NotifyBeforeChoices": [2, 5, 10, 15, 30],   // Discord 一列最多 5 個按鈕，這是上限
    "DefaultNotifyBeforeMinutes": 10,
    "MaxRoutesInSelect": 25,            // Discord select 上限
    "StaleDataSeconds": 180,            // SrcUpdateTime 落後超過此值就不發通知
    "NotifyMode": "FastestPerWaitingPeriod"   // §10.2；另可選 EveryBusOnce
  },
  "Search": {
    "MinQueryLength": 2,                // 少於 2 字直接提示（1 個字會命中數百筆）
    "MaxResults": 25,                   // 對齊 Discord select 上限
    "GroupClusterMeters": 500,          // 同名站牌的座標叢集門檻（§5.5）
    "PopularityRouteCap": 40            // 人氣權重的路線數上限（§5.4 Step 2）
  },
  "Storage": {
    "DatabasePath": "tcbus.db",         // §8.1；--db 參數 / TCBUS_DB 環境變數可覆寫
    "MaxGroupsPerUser": 20,
    "MaxNameLength": 40,
    "MaxRoutesPerGroup": 60
  }
}
```

**時區**：TDX 回傳的時間都帶 `+08:00`，用 `DateTimeOffset` 直接解析即可，
不要用 `DateTime.Now` 做判斷；需要「現在幾點」時使用 `TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei")`。

---

## 4. TDX 用戶端

### 4.1 Token 提供者

```csharp
// Tdx/TdxTokenProvider.cs
public sealed class TdxTokenProvider(HttpClient http, IOptions<TdxOptions> opts)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;

            // 官方限制：token endpoint 每 IP 每分鐘最多 20 次 → 一定要快取
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = opts.Value.ClientId,
                ["client_secret"] = opts.Value.ClientSecret
            });

            var res = await http.PostAsync(
                "/auth/realms/TDXConnect/protocol/openid-connect/token", form, ct);
            res.EnsureSuccessStatusCode();

            var doc = await res.Content.ReadFromJsonAsync<TokenResponse>(ct)
                      ?? throw new InvalidOperationException("token 回應為空");

            _token = doc.AccessToken;
            // expires_in 預設 86400 秒；提前 30 分鐘視為過期，避免邊界失效
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(doc.ExpiresIn - 1800);
            return _token;
        }
        finally { _gate.Release(); }
    }

    /// 遇到 401 時呼叫，強制下次重新取得
    public void Invalidate() { _token = null; _expiresAt = default; }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")]   int ExpiresIn);
}
```

### 4.2 API Client

單一檔案、單一出口，**所有 TDX 呼叫都經過它**（方便統一加重試、`Accept-Encoding`、計數）。

```csharp
// Tdx/TdxApiClient.cs
public sealed class TdxApiClient(HttpClient http, TdxTokenProvider token, ILogger<TdxApiClient> log)
{
    private const string City = "Taichung";

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var t = await token.GetTokenAsync(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
            req.Headers.AcceptEncoding.ParseAdd("gzip");

            using var res = await http.SendAsync(req, ct);

            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt < 1)
            {
                token.Invalidate();
                continue;                                     // 401 → 重取 token 再試一次
            }
            // TDX 官方錯誤碼：429 = 超過 API 速率、423 = 超過 50 次/秒、416 = 超過 60 並行連線
            if ((int)res.StatusCode is 429 or 423 or 416)
            {
                var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                log.LogWarning("TDX 限流 {Status}，等待 {Wait}s", (int)res.StatusCode, wait.TotalSeconds);
                if (attempt >= 3) res.EnsureSuccessStatusCode();
                await Task.Delay(wait, ct);
                continue;
            }

            res.EnsureSuccessStatusCode();
            return await res.Content.ReadFromJsonAsync<T>(ct);
        }
    }

    // ── 靜態資料（啟動時各呼叫一次）──
    // $select 可降低「計量」點數（150MB/點），值得加
    public Task<List<BusStop>?> GetStopsAsync(CancellationToken ct)
        => GetAsync<List<BusStop>>(
            $"/api/basic/v2/Bus/Stop/City/{City}?$format=JSON", ct);

    public Task<List<BusStopOfRoute>?> GetStopOfRoutesAsync(CancellationToken ct)
        => GetAsync<List<BusStopOfRoute>>(
            $"/api/basic/v2/Bus/StopOfRoute/City/{City}?$format=JSON", ct);

    public Task<List<BusRoute>?> GetRoutesAsync(CancellationToken ct)
        => GetAsync<List<BusRoute>>(
            $"/api/basic/v2/Bus/Route/City/{City}?$format=JSON", ct);

    // ── 即時資料（輪詢，一次涵蓋多個站）──
    // 只取必要欄位：計量佔總點數成本約 4 成
    private const string EtaSelect =
        "$select=RouteUID,RouteID,RouteName,Direction,StopUID,StopSequence," +
        "EstimateTime,StopStatus,NextBusTime,PlateNumb,Estimates,SrcUpdateTime";

    public Task<List<BusEta>?> GetEtasByStopsAsync(IEnumerable<string> stopUids, CancellationToken ct)
    {
        var filter = string.Join(" or ", stopUids.Select(u => $"StopUID eq '{u}'"));
        var url = $"/api/basic/v2/Bus/EstimatedTimeOfArrival/City/{City}" +
                  $"?$filter={Uri.EscapeDataString(filter)}&{EtaSelect}&$format=JSON";
        return GetAsync<List<BusEta>>(url, ct);
    }
}
```

`Program.cs` 用 typed client 註冊：

```csharp
builder.Services.AddHttpClient<TdxApiClient>(c =>
{
    c.BaseAddress = new Uri(cfg["Tdx:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(30);
});
```

> ⚠️ `TdxTokenProvider` 必須與 `TdxApiClient` 共用同一組 `HttpClient`。
> 最簡單的作法是把 token 邏輯直接放進 `TdxApiClient`（少一層、少一個 DI 陷阱）。
> 以**能動、好懂**為優先。

---

## 5. 靜態資料與索引（`TaichungBusDataService`）

### 5.1 啟動流程

```csharp
public async Task InitializeAsync(CancellationToken ct)
{
    // 3 次 HTTP 呼叫，之後整個 Bot 生命週期不再需要靜態資料 API
    var stopsTask       = _api.GetStopsAsync(ct);
    var routesTask      = _api.GetRoutesAsync(ct);
    var stopOfRouteTask = _api.GetStopOfRoutesAsync(ct);
    await Task.WhenAll(stopsTask, routesTask, stopOfRouteTask);

    BuildTripIndex(await stopOfRouteTask);      // (RouteUID, Direction) → 站序
    BuildStopGroupIndex(await stopsTask);       // 建議群組（前綴 + 座標叢集）
    BuildRouteMeta(await routesTask);           // RouteUID → 名稱、Headsign、業者
    BuildStopSearchIndex();                     // ★ 正規化站名 + 人氣權重（§5.4）
}
```

> `BuildStopSearchIndex()` 不呼叫任何 API，只是把已載入的站牌預先算好
> `SearchKey` / `SearchKeyEn` / `GroupKey` / `RouteCount`。
> **這一步是模糊搜尋能在 5 ms 內完成的關鍵**——正規化只做一次，不是每次查詢都做。

**資料量（官方 / 實測）**
- `StopOfRoute/City/Taichung`：約 **751~765 筆**（每筆 = 一個 `(SubRouteUID, Direction)`）
- `Route/City/Taichung`：約 **341~400 筆**
- `Stop/City/Taichung`：**> 5,000 筆**
- 皆**無需分頁**（官方：不加 `$top` 就回傳全部）
- 解析後記憶體約 **30~50 MB**。一次性成本，可接受。

**重載策略**：靜態資料平台每 **4 小時**向來源抓取一次。
建議每天（或偵測 `VersionID` 改變時）重載一次即可，不需要頻繁輪詢。

### 5.2 記憶體索引結構

```csharp
// 1) 路線方向 → 站序（A→B 匹配的核心）
//    鍵必須包含 Direction：台中實測同一 SubRouteUID 會同時用於去回程
readonly Dictionary<(string RouteUid, int Direction), TripStops> _trips;

sealed record TripStops(
    string RouteUid, string RouteName, int Direction,
    string Headsign,                        // 例：臺中車站 - 靜宜大學
    string[] StopUids,                      // 依 StopSequence 排序
    string[] StopNames,                     // 對齊 StopUids
    Dictionary<string, int> FirstSeqByName  // 站名 → 第一次出現的 sequence
);

// 2) StopUID → 出現過的 (路線, 方向, sequence)
//    用來快速取出「同時經過 A 與 B」的候選
readonly Dictionary<string, List<StopOccurrence>> _byStopUid;
readonly record struct StopOccurrence(string RouteUid, int Direction, int Sequence);

// 3) 建議群組（給使用者一鍵勾選的「站」）
readonly List<StopGroup> _stopGroups;
readonly Dictionary<string, StopGroup> _groupByKey;      // key = 群組內最短 StopUID
readonly Dictionary<string, string> _groupKeyByStopUid;  // StopUID → 群組 key

// 4) ★ 模糊搜尋索引（§5.4）—— 啟動時算好，查詢時只做比對
readonly StopSearchEntry[] _searchEntries;               // 約 5,000+ 筆
readonly Dictionary<string, string> _displayNameByStopUid;
```

### 5.3 地點目標（LocationTarget）：一個地點 = 一組候選站牌

**需求**：起點與目的地都不應限制成「單一站牌」。使用者要能選**多個站牌**當同一個地點
（例：起點 = 臺中車站(A月台) + 臺中車站(臺灣大道) + 干城站）。

**v3 範圍調整**：**地圖座標那條路先不做**，第一階段以**模糊站牌搜尋**為主要輸入方式
（§5.4）。`LocationTarget` 這個抽象仍然保留，因為它的價值在於
「**路線匹配只認一份候選站牌清單**」，而這個價值與輸入方式無關。

```csharp
/// 使用者定義的「一個地點」＝ 一組候選站牌
public sealed class LocationTarget
{
    public required string DisplayName { get; init; }   // 「臺中車站 + 干城站」

    // ★ 唯一參與匹配的欄位
    public required IReadOnlyList<string> CandidateStopUids { get; init; }
}
```

> **刻意保持極簡**：v2 草案裡的 `Type / Latitude / Longitude / RadiusMeters` **全部移除**。
> 沒有第二種輸入方式就不需要多型別欄位——**等第二階段真的要做地圖時再加回來**，
> 屆時只要新增一個「把座標轉成候選站牌」的工廠方法，匹配邏輯完全不用動。
> 這正是保留 `LocationTarget` 抽象的目的：**未來的擴充點被隔離在建立階段**。

**為什麼候選集合必須「建立當下就凍結成 StopUID 清單」**：

| 理由 | 說明 |
| --- | --- |
| 匹配邏輯單一 | 路線搜尋只認 `CandidateStopUids`，未來加地圖輸入也不用改匹配程式 |
| 結果可預期 | 使用者在 UI 看到並確認的那些站牌就固定下來。之後靜態資料重載不會偷偷改變他的訂閱 |
| 可追溯 | 通知要能說「請到 干城站 上車」，這需要當下確定的 StopUID |

於是後續所有程式碼只看這兩行：

```
CandidateOriginStops[]      ──┐
                              ├──▶ Route Matching ──▶ 可用路線
CandidateDestinationStops[] ──┘
```

**顯示名稱**：站名用「、」串起來；超過 3 個就寫「臺中車站 等 5 個站牌」。

### 5.4 ★ 模糊站牌搜尋（第一階段的核心互動）

**目標**：使用者打什麼都能找到對的站牌。實務上會遇到的輸入變體：

| 使用者可能輸入 | 應該要找到 |
| --- | --- |
| `靜宜` | 靜宜大學(專用道)、靜宜大學、靜宜大學靜園餐廳 |
| `台中車站`（台） | 臺中車站(A月台)、臺中車站(臺灣大道) ← **TDX 資料用「臺」，使用者常打「台」** |
| `台中车站`（簡體） | 臺中車站(A月台) | ✅ 簡繁折疊（見 Step 1.5） |
| `干净` / `头发` | 乾淨 / 頭髮 ← 一對多的字（§Step 1.5 的補表） |
| `台中火車站` | 臺中車站(…) ← 使用者口語是「火車站」，官方是「車站」 |
| `臺中 車站`（有空白） | 同上 |
| `Ａ月台`（全形） | 臺中車站(A月台) |
| `Taichung Station` | 臺中車站(A月台) ← 用 `StopName.En` |
| `中火車站`（漏字） | 臺中車站(A月台) ← 子序列比對 |
| **`台中科大`（縮寫）** | **國立臺中科技大學 ← 緊湊子序列（見 Step 2 的「縮寫」）** |
| `靜宜大` | 靜宜大學(專用道) ← 前綴 |

**→ 所以不能用 TDX 的 OData `contains()`。**
它只做原始字串的子字串比對，`台中车站` **不可能**命中 `臺中車站(A月台)`
（臺≠台、車≠车）。**本地正規化 + 模糊比對是唯一能滿足這個需求的作法**，
而且我們本來就把全部站牌放在記憶體，不打 API 也省點數。

#### Step 1：搜尋用正規化（與分組用的是兩套，別混用）

```csharp
// Bus/StopNameNormalizer.cs
public static class StopNameNormalizer
{
    /// 搜尋用：保留括號內容（這樣打「A月台」也找得到），只統一變體與大小寫
    public static string ForSearch(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var t = raw.Normalize(NormalizationForm.FormKC);   // 全形→半形
        t = ChineseText.ToSimplified(t);                   // ★ 簡繁折疊 → 一律用簡體當鍵
        t = t.ToLowerInvariant();                          // 英文大小寫

        // 台灣地名最重要的變體：臺 ↔ 台；以及常見異體字
        t = t.Replace('臺', '台').Replace('檯', '台')
             .Replace('裏', '里').Replace('裡', '里')
             .Replace('爲', '为').Replace('恆', '恒').Replace('堃', '坤');

        // 口語 vs 官方用語（簡繁兩種寫法都留著，轉換失敗時仍有救）
        t = t.Replace("火车站", "车站").Replace("火車站", "車站");

        // 移除空白與標點（保留中文、英數、括號內文字）
        return new string(t.Where(c => !char.IsWhiteSpace(c)
                                       && !char.IsPunctuation(c)).ToArray());
    }

    /// 分組用：去掉括號內容（見 §5.5）—— 這是**另一套**邏輯，不要共用
    public static string ForGroup(string raw) { /* 取第一個 '(' 之前 */ }
}
```

> ⚠️ **`ForSearch` ≠ `ForGroup`**：搜尋要保留括號（打「A月台」要能找到），
> 分組要去掉括號（把 A月台與臺灣大道併成「臺中車站」）。
> 混用會導致「打 A月台 找不到」或「分組把不同站併在一起」。
> **這兩個函式要分開命名、分開測試。**

#### Step 1.5 ★ 簡繁折疊：為什麼是「繁 → 簡」而不是「簡 → 繁」

**這是實測（Windows `LCMapStringEx`）之後才定下來的方向，不是偏好問題：**

| 方向 | 性質 | 實測結果 |
| --- | --- | --- |
| 簡 → 繁 | **一對多**，表只能挑一個 | ❌ `头发`→`頭發`（資料是「頭髮」）、`干净`→`干凈`（資料是「乾淨」）、`里面`→`里面`（根本沒轉） |
| **繁 → 簡** | **多對一**，且幂等 | ✅ `頭髮`/`头發`/`头发` → 都得到 `头发` |

所以搜尋的鍵**一律是簡體**（顯示仍然是 TDX 的原始繁體）：

```
站名索引：國立臺中科技大學 → "国立台中科技大学"
使用者打：国立台中科技大学 / 國立臺中科技大學 / 台中科大 → 都會命中同一筆
```

實測 56 個台灣站牌常見詞，Windows 的表只有 12 個轉不出標準簡體，
而且**全部都是一對多的字**（乾→干、於→于、麼→么、甯→宁、藉→借、菸→烟、
摺→折、慾→欲、塚→冢、蹟→迹…），用一張 20 字的補表處理。

> 為什麼不自己寫完整對照表：完整的簡繁對照表有數千字，維護成本高又容易漏。
> `LCMapStringEx` 是現成的完整表，**補表只需要處理它結構上做不到的那一類字**。
> 非 Windows 環境則退回內建的常用字表（由同一張簡→繁表反轉而來，不需要維護兩份）。

#### Step 2：比對策略與評分

依序嘗試，分數由高到低。**同時回報「這是哪一種相符」（`StopMatchKind`）**：

| 策略 | 條件 | 基礎分 | 顯示？ | 預設勾選？ |
| --- | --- | --- | --- | --- |
| 完全相符 | `key == query` | 1000 | ✅ | ✅ |
| 前綴相符 | `key.StartsWith(query)` | 800 − 長度差 | ✅ | ✅ |
| 英文名稱 | 對 `SearchKeyEn` 做上面幾種 | 同分 × 0.9 | 同上 | 同上 |
| **縮寫**（緊湊子序列） | 子序列且**內部只跳 ≤1 個字**、跨距 ≤ query+2 | 500 − 間隙×40 − 前後贅字 | ✅ | ✅ |
| 子字串 | `key.Contains(query)` | 600 − 起始位置×5 − 長度差 | ✅ | ❌（相關但不夠精確） |
| 鬆散子序列 | 子序列但跳字太多 | 300 − 間隙×10 | ❌ | ❌ |
| 編輯距離 ≤ 2 | 最後手段（Levenshtein） | 200 − 距離×50 | ❌ | ❌ |

**「縮寫」是這次新增的策略**，專門解決 `台中科大` → `國立臺中科技大學`：

```
國立臺中[技]科大   內部只跳 1 個字、跨距 5 ≈ 查詢長度 4  → 縮寫 → 強相符（預設勾選）
臺[灣]大[道]北[勢]路口  跳了 3 個字                      → 鬆散 → 弱相符（會被過濾）
```

> ⚠️ 為什麼「強弱」由**比對種類**決定，而不是比對分數門檻：
> 分數會被長度差與人氣權重調整。用門檻會出現
> 「前綴相符但名字很長 → 分數掉到門檻下 → 明明是強相符卻被當成雜訊」。
> **「哪一種比對成立」才是強弱的真正依據。**

```csharp
// Bus/StopSearchService.cs
public IReadOnlyList<StopSearchHit> Search(string query, int max = 25)
{
    var q = StopNameNormalizer.ForSearch(query);
    if (q.Length < 2) return [];      // 1 個字會命中數百筆，要求至少 2 字

    var hits = new List<StopSearchHit>();

    foreach (var e in _entries)
    {
        var (score, kind) = ScoreDetail(e.SearchKey, q);
        var (scoreEn, kindEn) = ScoreDetail(e.SearchKeyEn, q);
        scoreEn = (int)(scoreEn * 0.9);          // 英文站名是輔助
        var (best, bestKind) = scoreEn > score ? (scoreEn, kindEn) : (score, kind);
        if (best <= 0) continue;

        // 人氣權重：主要站牌（經過路線多）應該排在冷僻站前面
        var popularity = 1.0 + Math.Min(0.5, e.RouteCount / 40.0);

        hits.Add(new StopSearchHit(e, (int)(best * popularity), bestKind));
    }

    return hits.OrderByDescending(h => h.Score)
               .ThenBy(h => h.DisplayName.Length)   // 同名時短名優先
               .Take(max)
               .ToList();
}
```

**人氣權重是必要的**：查「臺中車站」時，主站牌應該排在那種只被一條路線經過的
同名冷僻站前面。`RouteCount` 在 §5.1 建索引時順便算，零額外成本。

#### Step 3：為什麼不需要 trie / n-gram / Lucene

台中全部站牌約 **5,000+ 筆**，每筆的 `SearchKey` 已預先正規化：

- 前三種策略都是 `string.Contains` / `StartsWith`，常數極小
- 只有當前幾種策略都找不到足夠結果時，才對「子序列可能符合」的候選跑 Levenshtein

實測量級：**每次查詢 < 5 ms**，遠低於 Discord autocomplete 的 3 秒限制。
**→ 不需要任何索引結構。直接線性掃描 + 排序就是正解。**
（五千筆的線性掃描在現代 CPU 上是微秒到毫秒級，這正是「不要過度設計」的場合。）

#### Step 4：搜尋結果按「建議群組」呈現

搜尋結果**不要只是平鋪 25 個站牌**，要先按建議群組分桶（§5.5），
讓使用者能一鍵勾選整組：

```
輸入：台中車站   （使用者打「台」，資料是「臺」）

🔍 「臺中車站」→ 1 組、38 個站牌
【臺中車站　9 種站名】
  • 臺中車站(成功路口) · 經過 12 條路線
  • 臺中車站(臺灣大道)（同站 4 個站牌）· 經過 9 條路線
  • 臺中車站(A月台) · 經過 8 條路線
  …

[▼ 選擇站牌或群組（可多選）]
      ☑ 臺中車站（全部 38 個）  →  g:TXG10286
      ☐ 臺中車站(成功路口)     →  n:TXG10286:0
      ☐ 臺中車站(臺灣大道)     →  n:TXG10286:1
      ☐ 臺中車站(A月台)        →  n:TXG10286:7
[全選]  [確認]
```

**同名站牌一律合併**：同一個站名的多個 `StopUID` 只出現一列。
在台中這非常常見 —— `國立臺中科技大學` 有 **44 個** StopUID、
`臺中車站` 有 **38 個**（每個路線方向各自登記一個站牌，
依 `StationID` 分屬 4~10 個實際站位）。

##### ⚠️ 選項的值不能用 StopUID，必須用短鍵

Discord 的 Select 選項 `value` 上限是 **100 字元**。
把 44 個 StopUID 串起來約 **400 字元**，會被截斷 ——
截斷後候選集合少掉大半，就會出現「明明有 11 條路線卻只找到 1 條」。

**→ 值只放短鍵，實際站牌由伺服器端還原：**

| 值格式 | 意義 | 長度 |
| --- | --- | --- |
| `g:{群組ShortKey}` | 該建議群組的**全部**站牌 | ≈ 10 |
| `n:{群組ShortKey}:{索引}` | 該群組裡第 N 種站名的全部站牌 | ≈ 13 |
| `s:UID1,UID2` | 直接指定（相容用，UI 不再產生） | 視情況 |

索引的順序由 `TaichungBusDataService.GetGroupNameBreakdown()` 決定
（依首個 StopUID 排序，**穩定且可重現**），編碼與解碼共用同一份邏輯
（`BusUi.BuildStopOptions` / `BusUi.ResolveStopValues`），避免兩邊不同步。

> 實作上另外加了防護：選項值超過 100 字元時會**印出警告**而不是默默截斷，
> 離線驗證也會檢查「每個選項的值都能完整還原成站牌」。

預設勾選規則：**「完全相符」「前綴相符」「縮寫」的群組預設勾選**，
單純子字串相符的**會顯示但預設不勾**，讓使用者自己決定。

> ⚠️ **「要不要顯示」與「要不要預設勾選」是兩件事**，不能共用一個旗標：
> 搜「车站」有 22 組子字串相符 —— 全部都顯示是對的（都相關），
> 全部預設勾起來是錯的（一次訂 22 個站區）。
> 所以 `StopSearchGroupResult` 有兩個旗標：
> `IsStrongMatch`（顯示）與 `IsDefaultPick`（預設勾選）。

#### Step 5：找不到的時候

| 情況 | 處理 |
| --- | --- |
| 查無結果 | 「找不到『xxx』」＋ 提醒可試的寫法；若資料集很小（< 500 站牌）才提示「請設定 TDX 金鑰」 |
| 結果 > 25 | 只顯示強相符的；必要時提示輸入更精確的關鍵字 |
| 輸入 < 2 字 | 直接回 0 筆（1 個字會命中數百筆） |
| **有強相符結果** | **只顯示完全／前綴／子字串／縮寫**，濾掉鬆散子序列與編輯距離的雜訊 |
| 命中過多 | 群組名額分配採「先取每組最高分，再補齊剩餘名額」，避免某一組吃光 25 個名額 |

> **為什麼要濾掉模糊相符**：搜「臺中車站」時，編輯距離比對會撈出
> 「沙鹿車站」「潭子車站」「豐原車站」等 18 組無關的站（距離剛好都是 2），
> 把選項名額塞滿反而找不到真正要的那個。
> 實測過濾後：「臺中車站」從 19 組降到 1 組、「中正國小」從 25 組降到 1 組。

### 5.5 建議群組：讓「臺中車站」一鍵變成候選集合

模糊搜尋找到一堆站牌之後，**需要「建議群組」把它們變成好勾選的候選集合**，
否則光要湊齊「臺中車站(A月台) + 臺中車站(臺灣大道) + 干城站」就要點很多次。

**兩個實測問題必須同時解決**

| 問題 | 實例 |
| --- | --- |
| 同一個「站名」在不同方向是不同 StopUID | `靜宜大學(專用道)` 去程 `TXG13567` / 回程 `TXG21478` |
| **同一個「站區」在不同路線是不同站名** | 300 路上車站叫 `臺中車站(A月台)`（`TXG12251`）；304 路叫 `臺中車站(臺灣大道)`（`TXG11020`） |

若用「站名完全相等」比對，使用者選了「臺中車站(A月台)」就**找不到 304 路**；
若只用座標距離叢集，距離僅 340 公尺的「干城站」會被錯誤併入臺中車站。

**→ 兩段式規則**

```
Step 1  站名正規化：取第一個 '(' 或 '（' 之前的部分，去除前後空白，並折疊成簡體（§5.4 Step 1.5）
          "臺中車站(A月台)"     → "台中车站"
          "臺中車站(臺灣大道)"  → "台中车站"
          "靜宜大學(專用道)"    → "静宜大学"
          "靜宜大學靜園餐廳"    → "静宜大学静园餐厅"   ← 無括號，保持獨立

Step 2  同一正規化名稱內，用座標做貪婪叢集（門檻預設 500 公尺，可設定）
          同名但分處不同行政區的站（例：各區都有的「中正路」）會拆成不同群組
```

```csharp
public sealed record StopGroup(
    string Key,                          // 叢集內字典序最小的 StopUID（短、穩定、可進 custom_id）
    string DisplayName,                  // 正規化後的站名，例："台中车站"（比對用的鍵是簡體）
    IReadOnlyList<string> StopUids,      // 叢集內全部 StopUID（300 的 A月台 + 304 的臺灣大道…）
    IReadOnlyList<string> VariantNames,  // 原始站名變化，用於提示：「含 A月台、臺灣大道」
    double Lon, double Lat);

public static string Normalize(string zhTwName)
{
    var idx = zhTwName.IndexOfAny(['(', '（']);
    var cut = idx >= 0 ? zhTwName[..idx] : zhTwName;
    return cut.Trim();
}
```

**為什麼 Key 用最短的 StopUID 而不是站名**
- `custom_id` 上限 100 字元，StopUID 只有 8 個 ASCII 字元，安全；
- 站名含中文與括號，放進 `custom_id` 容易踩到長度與轉義問題；
- StopUID → 群組的對照表在記憶體，查詢是 O(1)。

> **注意 `StationID` 不能拿來當群組鍵**：台中「組站位（StationGroup）」在官方供應現況表是「－」
> （無此項資料），`StationGroupID` 恆等於 `StationID`；
> 且實測 `靜宜大學(專用道)` 去回程的 `StationID` 分別是 `1387` / `1388`，
> 而 300 路與 304 路的臺中車站分別是 `4980` / `720`。
> `StationID` 是「站位」（道路同側）層級，比使用者心中的「站」更細。
> 因此**站牌的搜尋與建議**一律用上面兩段式產生的 `StopGroup`；
> `StationID` 只在需要更細比對時當輔助鍵。
>
> ⚠️ **`StopGroup` 的角色**：它只是**搜尋輔助**——負責把
> 「臺中車站」這個關鍵字變成一個**預設勾選好的候選集合**，使用者仍可自由增減站牌。
> 真正送進匹配邏輯的永遠是 `LocationTarget.CandidateStopUids`（一組 StopUID），
> 不是 `StopGroup`。這樣未來若加上其他輸入方式（例如第二階段的地圖座標），
> 也都會殊途同歸到同一份候選清單。

### 5.6 ⏸（延後）從地圖座標建立候選集合（Type = Point）

> **第一階段不做。** 這一節保留給第二階段，內容已研究完畢、設計也已可行，
> 只是依你的決定把重心放在模糊站牌搜尋（§5.4）。
> **現在不要實作這一節的任何東西**，包含 Haversine 之外的距離顯示、半徑按鈕、座標輸入解析。
>
> 唯一仍然會用到的是 **Haversine 距離計算本身**——但它只被 §5.5 的
> 「同名站牌的座標叢集」使用（判斷兩個同名站牌是不是同一個站區，
> 例如 340 公尺外的「干城站」不該被併入「臺中車站」）。
> 所以 `NearbyStopFinder` 這個類別**要保留**，只是它不再對外提供「附近站牌查詢」的 UI 功能。

<details>
<summary>（以下是第二階段的設計，現在不需實作 —— 點開查看）</summary>

**不需要任何外部地圖服務**（照你的要求）。TDX 的 `Stop` 資料本來就帶
`StopPosition.PositionLon/PositionLat`，而我們啟動時已經把全台中 5,000+ 個站牌放進記憶體了。

**做法：純記憶體的 Haversine 直線距離**

```csharp
// Bus/NearbyStopFinder.cs
public sealed class NearbyStopFinder(TaichungBusDataService data)
{
    private const double EarthRadiusMeters = 6_371_008.8;

    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = double.DegreesToRadians(lat2 - lat1);
        var dLon = double.DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(double.DegreesToRadians(lat1)) *
                Math.Cos(double.DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// 回傳半徑內站牌，依距離由近到遠排序
    public IReadOnlyList<(BusStop Stop, double Meters)> Find(
        double lat, double lon, int radiusMeters)
        => data.AllStops
               .Select(s => (Stop: s,
                             Meters: HaversineMeters(lat, lon,
                                 s.StopPosition.PositionLat, s.StopPosition.PositionLon)))
               .Where(x => x.Meters <= radiusMeters)
               .OrderBy(x => x.Meters)
               .ToList();
}
```

5,000 個站牌的 Haversine 掃描是微秒等級，**每次查詢零 API 呼叫、零點數**。

> **為什麼不用 TDX 的 `$spatialFilter=nearby(24.13,120.68,500)`？**
> 我實測確認這個 OData 函式可用，但它要打一次 API（會扣點數），
> 而我們記憶體裡已經有全部站牌座標。**本地 Haversine 免費又更快，
> 而且可以同一份資料重複查詢（使用者改半徑時不必再打 API）。**

**半徑選項**：`150m / 300m / 500m / 1km`（設定檔可調）。

**UI 呈現**（照你的規格）：

```
📍 你選擇的位置（24.1377, 120.6864）

附近公車站：
  ☑ 臺中車站(A月台)      120 m
  ☑ 干城站               340 m
  ☐ 民權繼光街口         520 m
  ☐ 中山醫院             780 m

（超過半徑的站牌不列出）

[ 半徑 150m ] [ 300m ] [ 500m ] [ 1km ]
[ 全選半徑內所有站牌 ]   [ 確認 ]
```

按下「確認」→ `CandidateStopUids` 凍結，`DisplayName` 設為
「（24.1377, 120.6864）附近 500 公尺內」。
按「全選半徑內所有站牌」= 直接把該半徑內全部站牌（依距離排序，取前 N 個）當候選。

> ⚠️ **半徑放大會讓候選爆炸**：1 公里在市區可能涵蓋 50~80 個站牌，
> 連帶命中大量路線（超過 Select Menu 的 25 個選項上限 → 需要分頁）。
> 建議 UI 預設 **300 公尺**，並在候選路線超過 25 條時提示使用者縮小半徑。

**額外好處：通知可以顯示步行距離**

因為 `LocationTarget` 保留了 `Point` 的經緯度，通知就能寫：

```
🚌 公車快到了！
路線：304（返程）
上車站：干城站　（距離你選的地點 340 公尺）　第 9 站
```

這是純 Haversine，**不需要 Google Maps API**。

**如何取得座標？** Discord 沒有「點地圖」互動元件，第一階段實務做法：
- 手動輸入 `24.1377,120.6864`
- 或**貼上 Google Maps 網址**，用正規表示式解析
  （`/@(-?\d+\.\d+),(-?\d+\.\d+)` 或 `?q=lat,lon`）
- 或**輸入站名** → 用該站座標當中心點（等於「以某站為中心找附近站牌」，
  這其實是最常用的情境，因為使用者通常知道自己要在哪一站附近）
- Bot 回覆時附上 OpenStreetMap / Google Maps 連結方便核對位置
  （只是組一個 URL，不是呼叫 API）

若未來要做真正的地圖點選，需要一個小網頁（TDX 也有圖資服務），但**第一階段不需要**。

</details>

### 5.7 兩種輸入方式的對照（Point 屬第二階段）

> 第一階段只實作左欄。右欄保留供第二階段對照。

| | **站牌搜尋（第一階段）** | 地圖座標（第二階段） |
| --- | --- | --- |
| 使用者輸入 | 模糊關鍵字 → 多選站牌／群組 | 座標／地圖網址 |
| 候選集合怎麼來 | 模糊搜尋 + 建議群組預設勾選 | Haversine 半徑內站牌（可全選） |
| 是否打 API | 否（記憶體線性掃描） | 否（記憶體 Haversine） |
| 變體容忍 | ✅ 臺/台、簡繁、全形、漏字、英文名 | ❌ 無（座標沒有名稱可比對） |
| 額外顯示 | 站名、經過路線數 | 每個站牌的**距離（公尺）** |
| 排序偏好 | 站數少優先 | 步行距離近優先 |
| **匹配邏輯** | **完全相同，不需要改** | **完全相同，不需要改** |

> 最後一列是重點：**兩種輸入方式都不會動到 §6 的匹配演算法**，
> 因為它們都只是「產生一份 `CandidateStopUids`」。這就是 §5.3 保留
> `LocationTarget` 抽象的價值。

---

## 6. A→B 路線搜尋

### 6.1 演算法：候選集合 × 候選集合

輸入是**兩個 `LocationTarget`**。N 個起點候選 × M 個目的地候選，
**每條 (路線, 方向) 只回傳一筆**，但會帶上**所有合法的候選上車站**。

```csharp
public IReadOnlyList<RouteOption> FindRoutes(
    LocationTarget origin, LocationTarget destination, int max = 200)
{
    // 1) 找出「同時經過起點候選與目的地候選」的 (RouteUID, Direction)
    var candidates = new HashSet<(string RouteUid, int Direction)>();
    foreach (var uid in origin.CandidateStopUids)
        if (_byStopUid.TryGetValue(uid, out var occs))
            foreach (var occ in occs)
                candidates.Add((occ.RouteUid, occ.Direction));

    var results = new List<RouteOption>();

    foreach (var key in candidates)
    {
        var trip = _trips[key];

        // 目的地候選在這條路線上的站序（依站序排序）
        var destSeqs = destination.CandidateStopUids
            .Where(u => trip.FirstSeqByStopUid.ContainsKey(u))
            .Select(u => (Uid: u, Seq: trip.FirstSeqByStopUid[u]))
            .OrderBy(x => x.Seq).ToList();
        if (destSeqs.Count == 0) continue;

        // 2) 對「每一個」合法上車站建立一個 BoardChoice
        var boards = new List<BoardChoice>();
        foreach (var b in origin.CandidateStopUids
                     .Where(u => trip.FirstSeqByStopUid.ContainsKey(u))
                     .Select(u => (Uid: u, Seq: trip.FirstSeqByStopUid[u]))
                     .OrderBy(x => x.Seq))
        {
            // ★ 合法配對：下車站必須在上車站之後，且不能是同一個站牌
            var alight = destSeqs.FirstOrDefault(x => x.Uid != b.Uid && x.Seq > b.Seq);
            if (alight.Uid is null) continue;

            boards.Add(new BoardChoice(b.Uid, Name(b.Uid), b.Seq, alight.Uid, Name(alight.Uid), alight.Seq));
        }

        if (boards.Count == 0) continue;   // 只經過但方向相反 → 不算，這裡擋掉

        results.Add(new RouteOption(key.RouteUid, trip.RouteName, key.Direction,
                                    DirectionLabel(key.Direction), trip.Headsign, boards));
    }

    return results
        .OrderBy(r => r.StopsBetween)                    // 站數少的先
        .ThenBy(r => r.RouteName, NaturalComparer.Instance)   // 300 排在 30 後面
        .ThenBy(r => r.Direction)
        .Take(max).ToList();
}
```

#### 為什麼每條路線只列一次，但保留多個上車站

使用者勾了 3 個起點站牌、2 個目的地站牌，理論上有 6 種組合。
**展開給使用者看毫無意義**——他關心的是「哪幾條路線可以搭」。

但反過來，**也不能只挑一個上車站了事**——使用者的本意是
「這幾個站我都可以上車」。所以：

* **顯示層**：每條路線一列（`RouteOption.BoardChoices` 裡面有全部候選上車站）
* **訂閱層**：每個候選上車站各自變成**一個獨立訂閱**（見 §8）

#### 下車站怎麼挑

每個 `BoardChoice` 的下車站 = **該上車站之後、最早出現的目的地候選站**。
理由：一進入目的地的候選範圍就下車，車程最短，
而且不需要使用者再多做一次選擇。

> 這取代了 v2 草案的「代表配對」設計。原本是「N×M 收斂成一個配對」，
> 現在是「**N 個上車站各自成一個訂閱，通知時再挑最快到的那一班**」——
> 後者更貼近實際等車行為（哪一班先來就搭哪一班）。

#### 合併重複路線

因為鍵是 `(RouteUID, Direction)`，**同一條路線的同一方向在結構上就不可能出現兩次**——
「合併」是「用鍵去重」的自然結果，不需要額外寫去重程式碼。

同一條路線的**去程與返程會各出現一次**，那是兩個真正不同的服務，應該分開列。

#### 排序

**站數少優先，再按路線名**（自然排序，讓 300 排在 30 後面），最後依方向。
站數少的通常也比較快抵達，使用者一眼就能判斷哪條路線比較直接。
顯示用的站數以**最早上車的那個候選站**為準。

> v2 草案的「代表配對」（`PickRepresentative` / `PairSelectionMode`）
> **已經被移除**——它會把「多個候選上車站」硬收斂成一個，
> 反而違背使用者「這幾站我都可以上車」的本意。現在改成「每個候選站各自成一個訂閱」。

### 6.2 實測驗證的預期結果（已實作並通過）

**驗收案例**

```
起點候選   = { 臺中車站(A月台) TXG12251, 臺中車站(臺灣大道) TXG11020, 干城站 TXG12769 }
目的地候選 = { 靜宜大學(專用道) TXG13567 / TXG21478 / TXG19438 }
```

| 路線 | 方向 | 候選上車站（依站序） | 下車站 | 站數 | 說明 |
| --- | --- | --- | --- | --- | --- |
| **300** | **1** | 臺中車站(A月台) `TXG12251` seq 1 | 靜宜大學(專用道) `TXG21478` seq 24 | 23 | Direction 0 是反方向，不可列 |
| **304** | **1** | **干城站 `TXG12769` seq 9**、臺中車站(臺灣大道) `TXG11020` seq 10 | 靜宜大學(專用道) `TXG19438` seq 35 | 25～26 | 兩個候選站各自成一個訂閱 |

**五件必須用這個案例驗收的事**

1. **300 路只能出現 Direction 1**（Direction 0 的終點是臺中車站，不能算）。
2. **304 路必須出現**——它的上車站叫「臺中車站(臺灣大道)」，
   跟 300 路的「臺中車站(A月台)」**不同名**。站名完全相等的比對法會在這裡失敗。
3. **304 路有兩個候選上車站**，而且「干城站」的站序 9 比「臺中車站(臺灣大道)」的 10 更早。
4. **每條路線只出現一次**，不是把 3×2 種站牌組合全部列出來。
5. **訂閱展開**：勾選這 2 條路線 → 建立 **3 個訂閱**（300×1 + 304×2），
   但輪詢只需要查 **3 個不重複的上車站**。

> ✅ 這五項已經在 `tcbus selftest` 中自動驗證（見 §14 的實作進度）。
> 測試資料在 `tests/fixtures/mini/`，**不需要網路、不需要 Discord、不需要 TDX 金鑰**。

---

## 7. Discord UI 互動流程

### 7.1 為什麼不能只用「兩個下拉選單選站」

站牌有上萬筆，而 Discord 的 String Select **最多 25 個選項**；
使用者也不可能在 25 筆裡找到自己的站。

**v2/v3 的另一個問題**：起點/目的地現在是**多選**（一個地點可以有多個候選站牌）。
單純的 autocomplete（一次只回一個值）不夠用。

**方案：Modal 輸入關鍵字 → 模糊搜尋 → 用多選 Select Menu 挑候選站牌／群組**

- Autocomplete 的硬限制：每次最多 **25 筆** choice、`value` ≤100 字元、**3 秒內回應**
- Select Menu 支援**多選**：`min_values` 0–25、`max_values` 1–25

模糊搜尋是純記憶體運算（< 5 ms），3 秒綽綽有餘。

> 補充：Slash command 的 **autocomplete** 仍然可以留著當「快速路徑」
> （`/bus next from:靜宜 to:臺中車站` 這種單站查詢很方便），
> 只是**多選**必須走 Modal + Select Menu 這條路。

### 7.2 完整流程

起訖點是**多選**，所以流程設計成「面板 + 逐步設定」：

```
Step 1  /bus
        ┌──────────────────────────────────────────────┐
        │ 🚌 公車訂閱                                   │
        │ 起點：尚未設定                                 │
        │ 目的地：尚未設定                               │
        │                                              │
        │ [ 設定起點 ]  [ 設定目的地 ]                    │
        │ [ 搜尋路線 ]（兩者都設定後才可用）               │
        └──────────────────────────────────────────────┘

Step 2  按「設定起點」→ 開 Modal 輸入站名關鍵字
        ┌──────────────────────────────────────────────┐
        │ 設定起點                                       │
        │ 站名關鍵字（可只打部分，例如「台中車站」）：      │
        │ [ 台中車站                                  ]  │
        └──────────────────────────────────────────────┘

Step 3  模糊搜尋 → Bot 回一則 ephemeral 訊息（多選）：
        ┌──────────────────────────────────────────────┐
        │ 找到 2 組、共 5 個站牌（可多選，最多 25 項）     │
        │ [ ☑ 臺中車站（4 個站牌）        ▸ 可展開 ]     │
        │      └ ☑ 臺中車站(A月台)                     │
        │        ☑ 臺中車站(臺灣大道)                   │
        │        ☐ 臺中車站(成功路)                     │
        │ [ ☐ 干城站                                    ]│
        │ [ ☐ 臺中車站(後站)                            ]│
        │ [ 全選 ]                                      │
        └──────────────────────────────────────────────┘
        （使用者打「台」，資料是「臺」；打「台中火車站」也找得到 → §5.4）

Step 4  選完 → 面板更新：
        │ 起點：臺中車站(A月台)、干城站（2 個候選站牌）    │
        │ 目的地：靜宜大學(專用道)（1 個候選站牌）         │

Step 5  按「搜尋路線」→
        ┌──────────────────────────────────────────────┐
        │ 2 個起點候選 × 2 個目的地候選                   │
        │ 找到 12 條可用路線（已依站數排序）              │
        │ [ ☑ 300 返程 · 臺中車站(A月台)上車 · 23 站 ]   │
        │ [ ☑ 304 返程 · 干城站上車 · 26 站 ]            │
        │ [ ☐ 305 返程 · … ]                            │
        │ [ 全選 ]  [ 下一頁 ]（路線超過 25 條時）        │
        └──────────────────────────────────────────────┘

Step 6  選完路線 → **先建立訂閱**（預設 10 分鐘），再顯示：
        ┌──────────────────────────────────────────────┐
        │ 已選擇 3 條路線：300、304、305                  │
        │ 提前多久通知？                                 │
        │ [2 分] [5 分] [10 分] [15 分] [30 分]           │
        └──────────────────────────────────────────────┘

Step 7  按「10 分鐘」→ 更新該批訂閱並回覆：
        ┌──────────────────────────────────────────────┐
        │ 🔔 訂閱完成                                    │
        │ 臺中車站(成功路口) 等 9 種站名 → 靜宜大學 等 7 種站名│
        │ 【路線】300、305、306…    【訂閱數】20 個       │
        │ 【提前通知】10 分鐘        【通知方式】本頻道    │
        ├──────────────────────────────────────────────┤
        │ 📊 預計到站時間（依剩餘時間排序）                │
        │ 1. 300　臺中車站(A月台)　約 4 分                │
        │ 2. 305　臺中車站(臺灣大道)　約 9 分             │
        │ …                                              │
        └──────────────────────────────────────────────┘
        [🔄 重新整理] [模擬一則通知] [返回面板]
        [💾 存成訂閱組] [📂 我的訂閱組]      ← ★ 存檔就在這一列
```

> **「💾 存成訂閱組」只在這一列，而且只有這裡。**
> 這張卡片是 `BusUi.FinalCard` + `BusUi.EtaTable` 兩個 embed，
> 按鈕由 `BusUi.EtaComponents()` 產生（第一列是即時查詢動作、第二列是訂閱組動作）。
> 曾經有一段時間它只出現在一個**沒有被使用的** `FinalComponents()` 裡，
> 結果實機上完全看不到 —— 所以 `--dryrun` 現在會做 §7.6 的接線檢查。

**關鍵設計**：Step 6 就先把訂閱建立起來，Step 7 的按鈕只負責「調整提前時間」。
這樣 `custom_id` 只需要面板代碼，不需要塞一長串路線（會超過 100 字元）。

### 7.3 其他指令

| 指令 | 功能 |
| --- | --- |
| `/bus panel` | 開啟訂閱面板（主流程，起點／目的地面板式設定） |
| `/bus list` | 列出自己的訂閱，並用 Select Menu 選擇要取消的訂閱 |
| `/bus groups` | **訂閱組清單**：套用／改名／刪除自己存的範本（§8.1） |
| `/bus next` | **立即查一次 ETA**，不建立訂閱（除錯 + 使用者體驗都好用） |
| `/bus status` | 顯示資料來源、輪詢間隔、訂閱數、儲存後端（管理用） |
| `/bus end` | **結束追蹤**：一次取消自己的全部訂閱，回覆附「↩️ 復原」按鈕（§7.3.1） |
| `/say <message>` | 讓 Bot 幫使用者說一句話（無用小功能；`SAY_ALLOWED_USERS` 可限制使用者） |

> `/say` 是唯一不是 `/bus` 群組的指令（獨立模組 `SayModule`）。
> 它的三個設計：`AllowedMentions.None`（不能被拿來 `@everyone`）、
> 內容裡的 `\n` 換成真換行、`SAY_ALLOWED_USERS` 未設定時所有人都能用。
> `--dryrun` 會檢查它真的進了指令樹、參數是必填字串（≤2000 字），並驗允許名單的解析。

#### 7.3.1 `/bus end` 與「放回原物件」的復原

`/bus end` 是**一次影響很多東西**的操作（一次清掉該使用者所有訂閱），所以它跟合併／批次套用／
批次刪除一樣走 `UndoStack`：`SubscriptionService.RemoveAllForUser` 先**抄下內容再刪**，
`UndoEndedTracking` 復原時呼叫 `SubscriptionService.Restore`。

⚠️ `Restore` 刻意放回**原本的 `SubscriptionGroup` / `Subscription` 實例**，而不是用
`CreateGroup` 重建。原因是重建會拿到新的 id，而：

* 通知去重狀態（`GroupNotifyState`，key 是 `(RouteUID|Direction|PlateNumb)`）掛在群組物件上 ——
  重建等於「這班車沒通知過」，復原後會再通知一次；
* 面板 session 的 `CreatedGroupId` 指向舊 id，復原後會指向一個不存在的群組。

`selftest` 第 19 節因此驗的不是「有沒有清乾淨」而已，而是**復原後 id、訂閱集合與去重狀態
是否與結束前完全相同**，外加「不會動到別人的訂閱」。

### 7.4 ⚠️ Discord 元件限制對照表（設計約束，已確認）

| 限制 | 數值 | 本方案的處理 |
| --- | --- | --- |
| Autocomplete choices | ≤ 25 | 模糊搜尋結果取前 25 筆（§5.4） |
| Autocomplete 回應時限 | 3 秒 | 記憶體線性掃描 + 排序，**< 5 ms** |
| choice value 長度 | ≤ 100 字元 | value 只放 `StopGroup.Key`（約 8 字元） |
| Select Menu 選項數 | ≤ 25 | 站牌候選或路線 > 25 筆時**分頁**（`[下一頁]` 按鈕，把 page 放進 custom_id） |
| **Select Menu 多選** | `min_values` 0–25、`max_values` 1–25 | **§7.2 Step 3 的候選站牌多選用這個**（`max_values = 25`） |
| Select Menu option value | ≤ 100 字元 | 站牌／同名站群組放**短鍵**（`g:<ShortKey>` / `n:<ShortKey>:<index>`，約 12 字元），伺服器端展開成完整 StopUID 清單；路線放 `"{RouteUid}\|{Direction}"`（約 12 字元） |
| **Modal 內能否放 Select Menu** | **[未確認]** | 若可以，Step 2/3 可合併成一步；否則照本方案的「Modal 純文字 → 後續訊息放選單」 |
| Button label | ≤ 80 字元 | 固定「5 分鐘」等短字串 |
| Button **每列數量** | ≤ 5 個 | 提前時間剛好 5 個（2/5/10/15/30）→ 一列，**已是上限** |
| Button 每列 | ≤ 5 個 | 4 個提前時間按鈕剛好一列 |
| ActionRow 每列 | 只能放 1 個 select | select 單獨一列 |
| Embed description | ≤ 4096 字元 | 路線摘要足夠 |
| 互動初始回應 | **3 秒內** | 模糊搜尋與路線匹配都是記憶體運算（< 5 ms）；`/bus next` 會先 `DeferAsync` |
| 互動 token 有效期 | 15 分鐘 | 足夠 |
| Gateway intents | 元件互動不需要任何 intent | 仍建議 `GatewayIntents.Guilds`（讓 guild/channel 快取可用） |

### 7.5 `custom_id` 設計

Discord.Net 3.x **沒有**內建的 persistent view / component registry，
模組是 transient，**所有跨互動的狀態必須放在 singleton service**。

**簡化（重要）**：候選站牌集合可能包含任意多個 StopUID（最多 25 個），
全部塞進 `custom_id` 會超過 100 字元上限。
但既然你已經接受「**訂閱在重啟後消失**」，那麼**面板的編輯中狀態也一併放記憶體**是最一致的設計。

| 步驟 | custom_id 範例 | 說明 |
| --- | --- | --- |
| 面板按鈕 | `bus:panel:<panelToken>` | `panelToken` = 記憶體中的 8 字元隨機碼，指向這次編輯的 origin/destination `LocationTarget` |
| 開搜尋 Modal | `bus:search:<panelToken>:o`（或 `:d`） | 送出後跑 `StopSearchService.Search` |
| 站牌／群組多選 | `bus:pickset:<panelToken>:o` | Select 的 `values` 才是被選中的短鍵清單（`g:` / `n:`，伺服器端展開） |
| 展開／收合群組 | `bus:expand:<panelToken>:o:<groupKey>` | 只更新同一則訊息的選項內容 |
| 搜尋路線 | `bus:find:<panelToken>` | 用記憶體中的兩個 target 跑 `FindRoutes` |
| 路線多選 | `bus:routes:<panelToken>:1` | `values` = `"{RouteUid}\|{Direction}"` 清單 |
| 提前時間按鈕 | `bus:notify:<panelToken>:10` | 調整這批訂閱的提前時間 |
| 訂閱清單啟停 | `bus:toggle:<subscriptionId>` | 訂閱在記憶體，重啟後本來就不存在 |

`custom_id` 長度都遠低於 100 字元上限。

### 7.6 ★ 接線檢查（按鈕 ↔ 處理函式）

**為什麼要有這個**：這個專案實際發生過三次「功能寫好了，但使用者按不到」：

| 症狀 | 真正的原因 |
| --- | --- |
| 看不出「存成訂閱組」這個功能 | 處理函式寫了、`ComponentBuilder` 也寫了，但那組元件**從來沒有被送出去**（實際送出的是另一組按鈕） |
| 面板沒有「我的訂閱」入口 | 處理函式存在，但沒有任何元件用到它的 `custom_id` |
| 空的搜尋結果按了「確認」→「無法提交」 | 空 Select Menu 在 `Build()` 就丟例外（元件問題，不是接線問題） |

前兩種**元件限制全數通過也驗不出來** —— 壞掉的不是限制，而是接線。
所以 `--dryrun` 除了檢查元件限制，還會做雙向核對：

```
▶ 接線檢查  按鈕 ↔ 處理函式
  畫面上出現 30 種 custom_id；模組註冊 32 個處理函式
  ✔ 每個處理函式都有對應的按鈕／選單
  ✔ 每個按鈕／選單都有對應的處理函式
```

| 方向 | 檢查 | 沒過會怎樣 |
| --- | --- | --- |
| 畫面 → 函式 | 每個被送出的 `custom_id` 都要有 `[ComponentInteraction]` / `[ModalInteraction]` | 使用者按了沒反應 |
| 函式 → 畫面 | 每個處理函式都要在某一輪驗證中真的出現過 | **使用者不知道有這個功能**（最難發現的那種） |

實作方式：`BusUi` 送出的元件只要經過 `Print`/`Validate` 就會被記錄，
處理函式則用反射從 `BusComponentModule` 的屬性收集，兩邊用
**與 Discord.Net 相同的 wildcard 規則**（`*` 對應一個冒號段落）比對。

少數 `custom_id` 不是由訊息上的元件觸發（Modal 由 `RespondWithModalAsync` 直接開、
`bus:panel` 只出現在「沒有任何訂閱」的清單），這些在 `ReachableOnlyFromSlashCommand` 明列。

> 教訓：**離線驗證要驗「使用者實際看到的那一組元件」**。
> 之前 `DryRun` 印的是 `FinalComponents()`（一份漂亮的、但沒被使用的按鈕列），
> 所以看起來什麼都有 —— 而那正是它沒發現問題的原因。

**重啟後的行為**：`panelToken` 查不到 → 回覆
「此面板已失效（Bot 曾重新啟動），請重新執行 `/bus`」。
這與「訂閱重啟後消失」是同一個已知限制，行為一致就好，不需要為它額外設計持久化。

**為什麼 Step 6 要先建立訂閱**：這樣「提前時間」按鈕只需要 `panelToken`，
不需要記住使用者勾了哪幾條路線（那串清單會超過 `custom_id` 上限）。
按鈕只是去更新「這個面板剛建立的那批訂閱」。

**若之後想要重啟安全**：把 `panelToken → LocationTarget` 的對應寫進 SQLite 並加 TTL。
儲存層現在已經有了（§8.1 的 `SqliteDatabase`），但**編輯中的面板屬於高頻寫入的短命狀態**，
為它付出的複雜度換不到多少體驗 —— 使用者真正在意的是「重複的訂閱組合」，
那個已經用訂閱組解決了。**面板續命仍然不做。**

```csharp
// Discord/BusComponentModule.cs
[ComponentInteraction("bus:routes:*:*")]
public async Task OnRouteSelectedAsync(string panelToken, int page)
{
    if (!_panels.TryGet(panelToken, out var panel))
    {
        await Context.Interaction.RespondAsync("此面板已失效（Bot 曾重新啟動），請重新執行 `/bus`。",
                                               ephemeral: true);
        return;
    }

    // Select 的 values：每筆是 "{RouteUid}|{Direction}"
    var picked = ((SocketMessageComponent)Context.Interaction).Data.Values
                     .Select(v => v.Split('|'))
                     .Select(p => (RouteUid: p[0], Direction: int.Parse(p[1])))
                     .ToList();

    // 用面板裡的兩個 LocationTarget（含完整候選站牌）重跑搜尋，取得代表上下車站
    var options = _data.FindRoutes(panel.Origin, panel.Destination)
                       .Where(r => picked.Contains((r.RouteUid, r.Direction)))
                       .ToList();

    // 先建立訂閱（預設 10 分鐘）；Step 7 的按鈕只負責調整提前時間
    var subs = _subs.CreateMany(Context.User.Id, panel.Origin, panel.Destination, options,
                                _monitor.DefaultNotifyBeforeMinutes);
    _panels.RememberCreated(panelToken, subs.Select(s => s.Id).ToList());

    await Context.Interaction.UpdateAsync(m =>
    {
        m.Embed = BuildNotifyChooserEmbed(subs);
        m.Components = new ComponentBuilder()
            .WithButton("2 分鐘",  $"bus:notify:{panelToken}:2",  ButtonStyle.Secondary)
            .WithButton("5 分鐘",  $"bus:notify:{panelToken}:5",  ButtonStyle.Secondary)
            .WithButton("10 分鐘", $"bus:notify:{panelToken}:10", ButtonStyle.Primary)
            .WithButton("15 分鐘", $"bus:notify:{panelToken}:15", ButtonStyle.Secondary)
            .WithButton("30 分鐘", $"bus:notify:{panelToken}:30", ButtonStyle.Secondary)
            .Build();
    });
}
```

> `[ComponentInteraction("bus:routes:*:*:*")]` 的 wildcard 會把三段分別綁到
> 方法參數（已確認 Discord.Net 支援 `*` / `**` / `?`）。
> Select Menu 的值會以 `string[]` 傳入，且**必須放在參數最後一個**。

---

## 8. 訂閱模型

**兩層結構**：`SubscriptionGroup`（一次「從 A 到 B」的意圖）
內含多個 `Subscription`（每個候選上車站一個）。

```csharp
// Subscriptions/Subscription.cs

/// 一個訂閱 = 一條 (路線, 方向, 上車站) 的組合
public sealed class Subscription
{
    public required string Id { get; init; }
    public required string GroupId { get; init; }

    public required string RouteUid { get; init; }
    public required string RouteName { get; init; }
    public required int    Direction { get; init; }        // 0 去程 / 1 返程 / 2 迴圈
    public required string Headsign { get; init; }

    public required string BoardStopUid { get; init; }     // 該方向實際停靠的那個站牌
    public required string BoardStopName { get; init; }
    public required int    BoardSequence { get; init; }

    public required string AlightStopUid { get; init; }
    public required string AlightStopName { get; init; }
    public required int    AlightSequence { get; init; }

    public bool Enabled { get; set; } = true;
}

/// 一次「我要從 A 到 B」的完整意圖
public sealed class SubscriptionGroup
{
    public required string Id { get; init; }
    public required ulong  UserId { get; init; }
    public ulong? GuildId { get; init; }
    public ulong? ChannelId { get; init; }                 // null = 私訊

    public required LocationTarget Origin { get; init; }   // 完整保留候選站牌
    public required LocationTarget Destination { get; init; }

    public int  NotifyBeforeMinutes { get; set; }
    public GroupNotifyMode NotifyMode { get; set; } = GroupNotifyMode.FastestPerWaitingPeriod;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public List<string> SubscriptionIds { get; } = new();
    public GroupNotifyState State { get; } = new();        // 去重狀態（見 §10）
}
```

**展開規則**：使用者勾選 N 條路線後，對每條路線的**每一個候選上車站**建立一個訂閱。

```
勾選 300（返程）+ 304（返程）
   ├── 訂閱 300(1) 臺中車站(A月台)@1   → 靜宜大學(專用道)@24
   ├── 訂閱 304(1) 干城站@9            → 靜宜大學(專用道)@35
   └── 訂閱 304(1) 臺中車站(臺灣大道)@10 → 靜宜大學(專用道)@35
共 3 個訂閱，但只有 2 條路線
```

**輪詢用的查詢：只認「不重複的上車站」**

```csharp
// 輪詢迴圈用的查詢：所有訂閱的上車站 UID（去重）
public IReadOnlyCollection<string> GetAllEnabledBoardStopUids()
    => _subs.Values.Where(s => s.Enabled)
                   .Select(s => s.BoardStopUid)
                   .Distinct(StringComparer.Ordinal)
                   .ToArray();
```

> **這是全案最重要的效能性質**：訂閱數隨「路線 × 候選站」成長，
> 但輪詢的 filter 條件數只取決於「**不重複的上車站**」有幾個。
> 而所有訂閱共享同一組起點候選站，所以數量上限就是使用者勾了幾個站牌。
>
> 100 個使用者關注同一個站，仍然只產生 **1 個 filter 條件 → 1 次呼叫**。
> **使用者勾再多候選站牌，成本都不變。**

> 訂閱數量小（數千筆），直接 LINQ 掃描就夠快，
> **不要**額外維護 userId 索引而多一個同步點。

---

### 8.1 訂閱組：把「搜尋 → 選路線 → 選時間」的結果存起來

訂閱本身刻意只放記憶體（重啟消失），但使用者每次重啟都要重新
「搜尋站牌 → 選路線 → 選時間」很煩。**訂閱組就是把這個流程的結果存成具名範本**
（「上班通勤」「回家路線」），下次一鍵套用。

這也讓**儲存層成為必要的**：訂閱可以消失，但使用者刻意整理出來的範本不行。

#### 為什麼是「零套件的 SQLite」

| 選項 | 問題 |
| --- | --- |
| `Microsoft.Data.Sqlite` | 需要 NuGet 套件；Core 專案刻意零外部相依，離線環境也無法還原 |
| JSON 檔案 | 需要自己處理併發寫入與損毀復原 |
| **P/Invoke `winsqlite3.dll`** | ✅ **Windows 10/11 內建完整的 SQLite（實測 3.51.1）**，零套件、離線可建置、產出的是真正的 `.db` 檔 |

`Storage/SqliteDatabase.cs` 只封裝必要的 C API（open / prepare / bind / step / column /
finalize / transaction），約 250 行。非 Windows 環境 `IsAvailable` 回 false，
`SavedGroupStore` 自動退回記憶體模式，其餘程式碼不需分支。

#### 資料表

```sql
CREATE TABLE IF NOT EXISTS saved_groups (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id          TEXT    NOT NULL,      -- Discord 雪花 ID 存字串（避開 unsigned 64-bit）
    name             TEXT    NOT NULL,
    origin_name      TEXT    NOT NULL,      -- 清單顯示用（反正規化，避免每次都解析 JSON）
    destination_name TEXT    NOT NULL,
    route_count      INTEGER NOT NULL,
    notify_minutes   INTEGER NOT NULL,
    payload          TEXT    NOT NULL,      -- JSON：完整候選站牌 + 路線清單
    created_at       TEXT    NOT NULL,      -- ISO-8601 字串，排序即為時間排序
    last_used_at     TEXT,
    use_count        INTEGER NOT NULL DEFAULT 0
);
```

`payload` 用短欄位名省空間（同名查詢時 `user_id + name` 為唯一鍵，同名即覆蓋）。
**一個訂閱組可以有多段行程**（`legs` 陣列）—— 那是「合併多個訂閱組」的結果，見 §8.2：

```jsonc
{
  "legs": [
    { "o": { "name": "臺中車站", "uids": ["TXG12251", "TXG11020"] },
      "d": { "name": "靜宜大學", "uids": ["TXG13567"] },
      "r": [ { "u": "TXG300", "d": 1, "n": "300" } ] },
    { "o": { "name": "靜宜大學", "uids": ["TXG13567"] },
      "d": { "name": "臺中車站", "uids": ["TXG12251"] },
      "r": [ { "u": "TXG300", "d": 0, "n": "300" } ] }
  ],
  "notify": 10
}
```

> **舊格式（單段）必須繼續讀得進來。** 改版前存的是 `{"origin":…,"dest":…,"routes":[…]}`，
> 讀取時由 `SavedGroupPayloadConverter` 自動升級成「1 段行程」——
> 使用者已經整理好的訂閱組不能因為程式改版就消失。
> `tcbus selftest` 有一項專門在測這個（舊 JSON → 1 段行程）。

#### ★ 為什麼存 `RouteUid + Direction`，而不是存訂閱的上下車站牌

**套用時用「目前的」站序資料重新跑一次匹配**：

```
載入 payload → 重建 origin / destination 的候選站牌
            → data.FindRoutes(origin, destination)     ← 用最新站序重新算
            → 只保留 payload 裡列出的 (RouteUid, Direction)
            → 對不上的路線逐條列出並略過（已停駛、改道、站牌遷移）
```

如果直接把舊的 `BoardStopUid` / `BoardSequence` 複製過來，路線改道或站牌遷移後
就會產生**指向錯誤月台或錯誤站序的訂閱**，而且完全不會報錯 —— 那比找不到更糟。

> 這正好呼應 §0 的核心決策：**靜態資料是唯一的真相來源**，衍生出來的東西
> （站序、月台、方向）一律重算，不落盤。

#### 其他設計決定

| 項目 | 決定 | 理由 |
| --- | --- | --- |
| 每人上限 | 20 組 | 防止濫用；超過時明確回報而不是靜默失敗 |
| 每組路線上限 | 60 條 | 單一路線選項的正常上界（實測最多 20 條），超過視為異常 |
| 每組行程段上限 | 10 段 | 合併的上界；超過代表使用者其實想要很多個組 |
| 同名 | **覆蓋**並回報「已更新」 | 比擋下來直覺（合併時會先備份，可以復原） |
| 排序 | `use_count DESC, created_at DESC` | 常用的自動排前面 |
| 權限 | 所有查詢都帶 `user_id` 條件 | 別人的組看不到、改不了、刪不掉 |
| 名稱 | 1~40 字元（只擋空字串與過長） | 參數化查詢，中文／引號／emoji 都安全 |
| 檔案位置 | `--db` CLI 參數 > `TCBUS_DB` 環境變數 > `tcbus.db` | 與 §3 的設定優先序一致 |
| 停用 | 刪掉檔案即可 | 沒有遷移腳本、沒有 schema 版本表（payload 自帶舊格式相容） |

#### UI 與 `custom_id`

```
完成訂閱／按「⏱ 看到站時間」之後的卡片：
  [🔄 重新整理]  [模擬一則通知]  [返回面板]
  [💾 存成訂閱組]  [📂 我的訂閱組]

📂 我的訂閱組                     ← 選單可以一次勾多個
▶ **上班通勤**　（用過 3 次）
　　臺中車站 → 靜宜大學
　　20 條路線 · 提前 10 分鐘
• **通勤全部**
　　**2 段行程**
　　　1. 靜宜大學 → 臺中車站（5 條）
　　　2. 臺中車站 → 靜宜大學（20 條）
　　25 條路線 · 提前 10 分鐘
[▼ 已選 2 組（可多選）]
[▶ 各自獨立訂閱 2 組] [🧩 合併成一個通知流（2 組）] [🔗 合併成新組] [✏️ 改名] [🗑 刪除]
[↩️ 復原：套用 2 個訂閱組] [返回面板]
```

| `custom_id` | 行為 |
| --- | --- |
| `bus:savegroup` / `bus:savegroupmodal` | 存成訂閱組（Modal 輸入名稱） |
| `bus:groups` | 開啟訂閱組清單（等同 `/bus groups`） |
| `bus:groupsel` | **多選**：一次勾選多個組（`min=1, max=25`），值為 `sg:{id}` |
| `bus:groupuse` | ▶ 套用選取的組，**各自獨立**（每組／每段各自一個訂閱） |
| `bus:groupusemerge` | 🧩 套用並**合併成一個通知流**（所有行程放進同一個訂閱群組） |
| `bus:groupmerge` / `bus:groupmergemodal` | 🔗 合併成一個**新的訂閱組**（多段行程，Modal 輸入名稱與提前時間） |
| `bus:groupren` / `bus:grouprenmodal` | 改名（只能改一個；Modal） |
| `bus:groupdel` | 🗑 刪除所有勾選的組（可以復原） |
| `bus:undo` | ↩️ 復原上一個動作（按鈕文字直接寫出要復原什麼） |
| `bus:etas` | 重新整理到站時間（存檔按鈕就在同一列，見 §7.2 Step 7） |

**套用成功的回饋必須說清楚「重算了什麼、合併了什麼」**，例如：

```
▶ 已套用 3 個訂閱組
**靜宜大學 等 7 種站名 → 臺中車站 等 9 種站名 等 3 段行程**
【來源】回程（靜宜→臺中）、回家路線、上班通勤
【訂閱數】45 個　【提前通知】15 分鐘
【通知流】🧩 合併成一個（3 段行程共用）→ 只會通知最快的那一班
```

若有路線對不上，額外列出 `⚠️ 2 條路線目前查不到，已略過：901副、21繞2`。

---

### 8.2 一次套用多個訂閱組、合併、以及復原

#### 三個動作的差別（這是最容易混淆的地方）

| 按鈕 | 建立什麼 | 通知行為 |
| --- | --- | --- |
| **▶ 各自獨立訂閱 N 組** | N 個訂閱群組（每段行程各一個） | 各通知各的 —— 三條通勤路線就會收到三則 |
| **🧩 合併成一個通知流** | **1 個**訂閱群組，內含多段行程 | 從**所有**路線裡挑最快到的那一班，只通知一次 |
| **🔗 合併成新組** | 不建立訂閱，只把勾選的組合併成**一個新的訂閱組**存起來 | 下次一鍵套用（可再選上面兩種模式之一） |

為什麼「合併成一個通知流」要是**同一個群組**：
「挑最快的那一班」這個決策做在群組層級（§10.2 的去重狀態在 `SubscriptionGroup.State`），
分成多個群組就會各挑各的、各通知一次。

#### 多段行程的模型

```csharp
// 一個群組可以有多段行程
public sealed class SubscriptionGroup
{
    public required LocationTarget Origin { get; init; }        // = Legs[0]（顯示用）
    public required LocationTarget Destination { get; init; }
    public List<SubscriptionLeg> Legs { get; } = new();          // 至少 1 段
    ...
}

public sealed class Subscription
{
    public int LegIndex { get; init; }     // 這個訂閱屬於哪一段
}

// 建立：單段是特例，多段才是通則
subs.CreateGroup(userId, origin, dest, options, notify);                 // 單段
subs.CreateMultiLegGroup(userId, plans, notify);                         // 多段（合併通知流）
```

**通知上的「起訖」用訂閱自己那一段**（`group.LegOf(sub)`），不是群組的第一段 ——
否則合併後的通知會顯示完全錯誤的行程。
`--dryrun` 的 Step 13b-2 會拿合併後的群組產生一則模擬通知，檢查這件事。

#### 合併的細節

```csharp
SavedGroupPayloadFactory.Merge(payloads, notifyMinutes)
```

| 情況 | 處理 |
| --- | --- |
| 起訖候選站牌**相同**的兩段 | 併成一段，路線取聯集（同一條通勤路線存了兩次不會變成兩段） |
| 路線重複 | 用 `(RouteUid, Direction)` 去重 |
| 提前通知時間 | 取**最大**的那個（= 提醒最早）。晚通知會讓人錯過公車，早通知只是多一則訊息；Modal 裡可以自己改 |
| 同名 | 覆蓋，但**先備份舊內容**，所以可以復原 |
| 段數/路線數超限 | 明確回報（`TooManyLegs` / `RouteCountMismatch`），不靜默截斷 |

實測（真實資料，3 個組 → 合併）：
```
合併結果：Created → 2 段行程、25 條路線
　1. 靜宜大學 等 7 種站名 → 臺中車站(成功路口) 等 9 種站名（5 條）
　2. 臺中車站(成功路口) 等 9 種站名 → 靜宜大學 等 7 種站名（20 條）
```
（「回家路線」與「上班通勤」的行程相同 → 正確併成一段。）

#### ★ 復原（`UndoStack`）

**為什麼一定要有**：批次套用、合併、批次刪除都是「一次影響很多東西」的操作，
點錯一下就要全部重來。所以每個動作都先把自己「怎麼還原」記下來：

```csharp
public abstract record UndoEntry(string Description)
{
    public abstract string Undo(SubscriptionService subs, SavedGroupStore store, ulong userId);
}

UndoAppliedSubscriptions   // 批次套用 → 移除剛才建立的所有訂閱群組
UndoMergedSavedGroup       // 合併成新組 → 刪掉新組；若覆蓋同名則還原舊內容
UndoDeletedSavedGroups     // 批次刪除 → 把刪掉的內容重新存回去
UndoRenamedSavedGroup      // 改名 → 改回舊名字
```

| 性質 | 決定 |
| --- | --- |
| 深度 | 保留最近 **10** 個動作（後進先出），不是只有一層 |
| 範圍 | **每個使用者 × 每個頻道**一份（跟著面板 session） |
| 生命週期 | **記憶體** —— Bot 重啟後不能復原（與「訂閱重啟就消失」同一個已知限制） |
| 按鈕文字 | 直接寫出要復原什麼（「↩️ 復原：合併成「通勤全部」」），不是只寫「復原」 |
| 復原後 | 明確回報做了什麼（「已刪除剛才合併出來的「通勤全部」。」） |

> 復原刪除是「重新存回去」，所以**新的 id 會不同**（清單位置依使用次數與建立時間排序，
> 可能與原本不同）。內容（行程、路線、提前時間）完全一致。

---

## 9. 即時資料輪詢（`EtaPollerService`）

**這是整個 Bot 唯一會定期打 TDX 的地方。全體使用者共用。**

```csharp
// Realtime/EtaPollerService.cs
public sealed class EtaPollerService(
    TdxApiClient api, SubscriptionService subs, RealtimeBusCache cache,
    SubscriptionMatcher matcher, IOptions<MonitorOptions> opt,
    StaticDataReadySignal ready, ILogger<EtaPollerService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await ready.WaitAsync(ct);                      // 等靜態資料就緒

        var interval = TimeSpan.FromSeconds(opt.Value.PollIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        do
        {
            try { await PollOnceAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.LogError(ex, "輪詢失敗，下一週期重試"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var stops = subs.GetAllEnabledBoardStopUids();

        if (stops.Count == 0)                           // 沒有任何訂閱 → 完全不呼叫 API（省點數）
        {
            cache.Clear();
            return;
        }

        // ★ 一次呼叫涵蓋所有訂閱者：100 個使用者關注同一個站 → 仍然只有 1 次呼叫
        //   過濾維度選 StopUID 而不是路線，理由見 §9.1
        var batchSize = opt.Value.MaxStopsPerQuery;     // 預設 40
        var fresh = new Dictionary<(string, int, string), BusEta>();

        foreach (var chunk in stops.Chunk(batchSize))
        {
            var etas = await api.GetEtasByStopsAsync(chunk, ct);
            if (etas is null) continue;

            foreach (var e in etas)
                fresh[(e.RouteUID, e.Direction, e.StopUID)] = e;
        }

        cache.ReplaceAll(fresh, DateTimeOffset.UtcNow);

        await matcher.EvaluateAsync(cache, ct);         // 交給通知佇列，不 await 送訊息
    }
}
```

> **實際的類別名稱是 `EtaPoller`（`TcBusBot.Discord`），不是 `BackgroundService`** ——
> 這個專案刻意不用 `Microsoft.Extensions.Hosting`（見 §2 的說明），
> 而是由 `Program.cs` 用一個 `Task` 跑 `RunAsync(ct)`。

### 9.1 為什麼用「以 StopUID 過濾」而不是「以 RouteName/RouteUID 過濾」

> 有人會問：**「用 `$filter=RouteName/Zh_tw eq '300' or RouteName/Zh_tw eq '305'` 一次抓兩條路線，不是也省額度嗎？」**
> 答：**一個請求裡用 `or` 串多個條件確實只算 1 次呼叫 —— 這件事我們已經在做，
> 但過濾的維度選 StopUID 比選路線便宜得多。**

| 過濾方式 | 每個週期的呼叫數 | 回應資料量 | 拿到的東西 |
| --- | --- | --- | --- |
| 以路線過濾（`RouteName eq '300'`） | 每 1 個 filter 條件 1 次 | **整條路線所有站牌**：300 路去回程上百站 × 每筆 ~190 bytes ≈ 20 KB | 我們只要其中 1~2 站，其餘 98% 丟掉 |
| **以站牌過濾（`StopUID eq '...'`）** | **每 40 站 1 次** | 只有「這些站的所有路線」= 真正要用的資料 | 剛好就是要通知的那幾班車 |

**兩個理由讓 StopUID 更省：**

1. **點數公式是「呼叫次數 / 1500 ＋ 資料量(MB) / 150」** —— 資料量也要錢。
   以路線過濾會為了 1 個站下載整條路線的站序資料，資料量暴增幾十倍，
   省下的呼叫次數遠遠補不回來。
2. **`StopUID` 是短的 ASCII 識別碼**，不會遇到中文編碼、全形/半形、大小寫的意外；
   路線名稱是中文，而且同一條路線有 `RouteName` / `SubRouteName` 多種寫法
   （`21` / `21延` / `21繞1` 是同一個 `RouteUID` 的不同 subroute）。

**而且以站牌過濾還有兩個附帶好處：**
- **使用者數不影響成本**：100 個人關注同一個站，仍然是 1 次呼叫（站牌去重後只有 1 個）。
- **使用者勾幾個候選站也不影響**：候選站是「同一個地點的多個月台」，
  去重後通常只有幾個 —— 而**不論候選站有幾個，輪詢只查真正被訂閱的上車站**。

### 9.2 `$select`：不要的欄位一個都別拿

實測 TDX 回應裡我們**完全用不到**這些欄位（佔了一筆資料約 **69%** 的位元組）：

| 欄位 | 為什麼不要 |
| --- | --- |
| `RouteName` / `SubRouteName` | `{Zh_tw, En}` 物件（**含英文名**）；路線名在訂閱資料裡就有 |
| `StopName` | 同上；站名在靜態索引裡就有 |
| `RouteID` / `SubRouteUID` / `SubRouteID` / `StopID` | 站牌與路線一律用 UID |
| `Estimates[]` | 多班車陣列；目前只通知最快的那一班 |
| `UpdateTime` | 只用 `SrcUpdateTime` 判斷新鮮度與時間遞減 |

```csharp
private static readonly string EtaSelect =
    "$select=RouteUID,Direction,StopUID,EstimateTime,StopStatus,NextBusTime,PlateNumb,SrcUpdateTime";
```

> ⚠️ 少要欄位會讓那些屬性留在 `null`，所以要改動前必須確認「真的沒有人在讀」。
> `tcbus selftest` 第 15 節會用真實 payload 量給你看，
> 並且擋住「不小心又要求用不到的欄位」。

### 9.3 分批大小

`MaxStopsPerQuery = 40` 時 filter 字串約 1,100 字元（實測滿批 URL 1,643 字元），安全。
若某個部署有 200 個不同的上車站，就是 5 次呼叫／週期。

### 9.4 實際長什麼樣子（`--dryrun` Step 8b 會印出來）

```
▶ Step 8b  輪詢計畫（唯一會花點數的地方）
  （實測：完整一筆約 608 bytes、$select 後約 187 bytes → 少 69%）
  2 個不重複的上車站 → 每週期 1 次呼叫（每批 40 站）
  預估回傳 62 筆 ETA（每筆約 187 bytes，已用 $select 精簡）
  間隔 30 秒 → 2,880 次/日、約 31.84 MB/日（未壓縮）
  點數估算：呼叫 57.6 ＋ 資料量 6.4 = 約 64 點/月
  （官方公式：呼叫次數 / 1,500 ＋ 回傳資料量(MB) / 150）
  ★ 這裡只有 2 個上車站；不論幾個使用者關注同一個站，呼叫次數都不變（100 人共用 1 次呼叫）

  實際請求（第一批，可以自己拿去驗證）：
    https://tdx.transportdata.tw/api/basic/v2/Bus/EstimatedTimeOfArrival/City/Taichung?$filter=…
```

**成本的三個槓桿**（依效果排序）：

| 槓桿 | 效果 | 現況 |
| --- | --- | --- |
| **輪詢間隔** | 30 → 60 秒，呼叫與資料量都減半 | 30 秒（台中 N1 來源端約 20 秒更新，低於 20 秒沒意義） |
| **有意義的資料量** | `$select` 已省 69% | ✅ 已做 |
| 營業時間外不輪詢 | 深夜約可省 20~30% | 未做（要小心末班車，例如 00:30 還有車） |

### 9.5 快取

```csharp
// Realtime/RealtimeBusCache.cs
public sealed class RealtimeBusCache
{
    private volatile Dictionary<(string Route, int Dir, string Stop), BusEta> _map = new();
    public DateTimeOffset LastUpdated { get; private set; }
    public bool IsStale => DateTimeOffset.UtcNow - LastUpdated > TimeSpan.FromMinutes(5);

    public void ReplaceAll(Dictionary<(string, int, string), BusEta> fresh, DateTimeOffset now)
    { _map = fresh; LastUpdated = now; }

    public void Clear() { _map = new(); }

    public BusEta? Get(string routeUid, int dir, string stopUid)
        => _map.TryGetValue((routeUid, dir, stopUid), out var e) ? e : null;
}
```

---

## 10. 通知判定與去重（`SubscriptionMatcher`）

### 10.1 觸發條件

**★ 最重要的一點：TDX 的 ETA 不會自己遞減，必須自己算。**

官方 OAS 原文：

> 「N1 僅於該路線上有任一車輛離站時，來源端才會重新計算並發佈，
> 因此**使用者需自行處理時間遞減機制**，或以
> `EstimateTime - (收到資料時間 - SrcTransTime)`（秒）作為實際預估抵達時間。」

台中市 N1 來源端的抓取頻率約 **20 秒**，但「有車輛離站」才重算，
所以兩次發布之間 `EstimateTime` 是**靜止不變**的。
若不做遞減，使用者會收到「還有 5 分鐘」但公車其實 3 分鐘就到了。

```csharp
private static double? LiveEstimateSeconds(BusEta eta, DateTimeOffset now)
{
    if (eta.EstimateTime is not int raw) return null;
    var elapsed = (now - eta.SrcUpdateTime).TotalSeconds;
    return Math.Max(0, raw - elapsed);
}

private static bool ShouldNotify(Subscription sub, BusEta? eta, DateTimeOffset now, int staleSeconds)
{
    if (eta is null) return false;

    // 1) 資料新鮮度：SrcUpdateTime 太舊就不通知，避免用過期資料誤報
    if (now - eta.SrcUpdateTime > TimeSpan.FromSeconds(staleSeconds)) return false;

    // 2) 必須有預估值。
    //    注意：不要寫成「StopStatus == 0 才有 ETA」——官方明文指出
    //    「部分縣市在 StopStatus = 1（尚未發車）且 EstimateTime > 0 時，
    //      EstimateTime 代表多久後開始發車，屬正常情形」。
    //    另外 PlateNumb == "-1" 代表無車輛，此時 EstimateTime 必為 null。
    var live = LiveEstimateSeconds(eta, now);
    if (live is null or <= 0) return false;          // null = 無預估；0 = 進站中

    // 3) 進入提前通知視窗
    return live.Value <= sub.NotifyBeforeMinutes * 60;
}
```

> `EstimateTime = 0` 官方未明文定義，實務上代表「進站中／即將到站」
> （機制上倒數到約 59 秒後就不再更新，因為預估時間不能為負）。
> 若要通知「即將進站」，把條件改成 `live is >= 0 and <= 30`。
>
> `IsLastBus` 陷阱（官方明文）：`EstimateTime` 為 null 且 `IsLastBus = 0` 時，
> **不能**據此斷定「不是末班車」。要做末班車提示時請顯示「末班資訊」而非斷言。

### 10.2 去重與「挑最快」（在**群組**層級做，不是訂閱層級）

**班次識別**：ETA 回應只有 `PlateNumb`（車牌），沒有班次 ID。
`RealTimeNearStop` 有 `TripStartTime`（實測確認），但那是另一支 API。

**★ 去重鍵必須以「車輛」為單位，且存在群組層級：**

```csharp
// (路線, 方向, 車牌) —— 不含 BoardStopUid！
public static string VehicleKey(BusEta eta)
    => $"{eta.RouteUID}|{eta.Direction}|{eta.PlateNumb}";
```

**為什麼不含 `BoardStopUid`**：一台公車會**依序經過多個候選上車站**
（例：先過干城站 seq 9、再過臺中車站(臺灣大道) seq 10）。
如果去重鍵含站牌，同一班車會在不同站各通知一次 —— 這正是「多個站 = 多個訂閱」
之後最容易踩到的坑。

**完整決策流程**（`SubscriptionMatcher.EvaluateGroup`）：

```
對每個群組：
  1. 逐一檢查群組內的每個訂閱
       有 ETA 嗎？資料新鮮嗎？EstimateTime 有值嗎？
       進入通知視窗嗎（live <= NotifyBeforeMinutes × 60）？
       這台車在這個群組通知過了嗎（VehicleKey 去重）？
       → 收集成候選清單
  2. 候選為空 → 這個群組這輪不通知
  3. 依 LiveSeconds 排序，取最小的 → 就是「最快到的那一班」
  4. FastestPerWaitingPeriod 模式：
       如果已經在等某班車（CurrentFocusVehicleKey）而且它還沒過站
       → 這一輪不通知（避免洗版）
  5. 標記該車輛已通知；把它設為 focus；附上其他選擇後送出
```

**90 分鐘 rearm**：同一台車跑下一趟時要能再次通知。
`GroupNotifyState.WasNotified(key, now)` 只要發現「上次通知距今 > 90 分鐘」就視為新班次。

**兩種通知模式**（`GroupNotifyMode`）：

| 模式 | 行為 | 適用 |
| --- | --- | --- |
| `FastestPerWaitingPeriod`（**預設**） | 每個「等車期間」只通知一次，挑最快的那一班，訊息中附上其他選擇。那班車過站後才會通知下一班 | 一般通勤，訊息最少 |
| `EveryBusOnce` | 每一班車各通知一次（同一班車跨候選站仍只通知一次） | 想要完整掌握所有選項 |

通知內容範例：

```
🚌 公車快到了！
路線：304（返程）
上車站：干城站　第 9 站
預計約 5 分鐘到站
目的地：靜宜大學(專用道)　還有 26 站
車牌：BBB-222
（其他選擇：300 臺中車站(A月台) 約 8 分）
```

> **升級選項（第二版）**：若你希望班次識別 100% 精準，
> 可以每週期多打一次 `RealTimeNearStop?$filter=RouteUID eq 'X' or …`，
> 用 `(PlateNumb, TripStartTime)` 當班次唯一鍵。代價是 API 呼叫數與點數翻倍。
> 建議第一版先用 PlateNumb（已實測通過），穩定後再決定。

> ✅ 以上行為都已由 `tcbus selftest` 的第 7 節自動驗證，包含：
> 挑最快、同一批資料不重複通知、同一台車跨候選站不重複通知、
> 等待的班車過站後改通知下一班、90 分鐘後視為新班次、以及兩種模式的差異。

> **升級選項（第二版）**：若你希望 100% 精準，
> 可以每週期多打一次 `RealTimeNearStop?$filter=RouteUID eq 'X' or …`，
> 用 `(PlateNumb, TripStartTime)` 當班次唯一鍵。代價是 API 呼叫數與點數翻倍。
> 建議第一版先用 PlateNumb，實測穩定後再決定。
>
> 額外可用的欄位：`RealTimeNearStop.StopSequence` 可以算「還有幾站到你的上車站」，
> `BusStatus = 100` 代表客滿，`A2EventType` 0/1 代表離站/到站。

### 10.3 主迴圈（群組層級）

```csharp
public IReadOnlyList<BusArrivalNotice> EvaluateAll(DateTimeOffset now)
{
    var notices = new List<BusArrivalNotice>();

    foreach (var group in _subs.GetGroups())        // 逐「群組」，不是逐「訂閱」
    {
        var notice = EvaluateGroup(group, now);
        if (notice is not null) notices.Add(notice);  // 每群組每輪最多一則
    }

    return notices;
}

private BusArrivalNotice? EvaluateGroup(SubscriptionGroup group, DateTimeOffset now)
{
    var candidates = new List<Candidate>();

    foreach (var sub in _subs.GetSubscriptions(group))          // 群組內的所有訂閱
    {
        if (!sub.Enabled) continue;

        var eta = _cache.Get(sub.RouteUid, sub.Direction, sub.BoardStopUid);
        if (eta is null) continue;

        var live = LiveEstimateSeconds(eta, now);
        if (live is null || live.Value > group.NotifyBeforeMinutes * 60) continue;

        var vehicleKey = VehicleKey(eta);                       // (路線,方向,車牌)
        if (group.State.WasNotified(vehicleKey, now)) continue; // ★ 跨候選站去重

        candidates.Add(new Candidate(sub, eta, live.Value, vehicleKey));
    }

    if (candidates.Count == 0) return null;

    candidates.Sort((a, b) => a.LiveSeconds.CompareTo(b.LiveSeconds));
    var pick = candidates[0];                                   // ★ 最快到的那一班

    if (group.NotifyMode == GroupNotifyMode.FastestPerWaitingPeriod &&
        group.State.CurrentFocusVehicleKey is { } focus &&
        focus != pick.VehicleKey && StillApproaching(group, focus, now))
        return null;                                            // 還在等那一班，不換車

    group.State.MarkNotified(pick.VehicleKey, now);
    group.State.CurrentFocusVehicleKey = pick.VehicleKey;

    var alternatives = candidates.Skip(1).Take(2)
        .Select(c => new BusArrivalNotice.Alternative(
            c.Subscription.RouteName, c.Subscription.BoardStopName, c.LiveSeconds))
        .ToList();

    return new BusArrivalNotice(group, pick.Subscription, pick.Eta, pick.LiveSeconds, alternatives);
}
```

> `EvaluateAll` 是**純函式**（不碰網路、不碰 Discord），
> 所以可以在單元測試裡餵固定的 ETA 資料、固定的時間點，
> 完整驗證「挑最快」與去重行為 —— 這正是 `tcbus selftest` 第 7 節在做的事。

---

## 11. Discord 通知器（含節流）

官方文件對主動私訊有明確警告：

> "You should not use this endpoint to DM everyone in a server about something.
> DMs should generally be initiated by a user action. If you open a significant
> amount of DMs too quickly, your bot may be rate limited or blocked from opening new ones."

而且 401/403 會累積成 Cloudflare ban（每 IP 每 10 分鐘 10,000 次）。
所以通知一定要**過佇列 + 節流**。

```csharp
// Notifications/DiscordNotifier.cs
public sealed class DiscordNotifier(DiscordSocketClient client, ILogger<DiscordNotifier> log)
{
    private readonly Channel<BusArrivalNotice> _queue =
        Channel.CreateUnbounded<BusArrivalNotice>();
    private readonly SemaphoreSlim _throttle = new(1, 1);

    public void Enqueue(BusArrivalNotice n) => _queue.Writer.TryWrite(n);

    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var n in _queue.Reader.ReadAllAsync(ct))
        {
            await _throttle.WaitAsync(ct);
            try
            {
                if (await client.GetUserAsync(n.Subscription.UserId) is not SocketUser user) continue;

                var dm = await user.CreateDMChannelAsync();
                await dm.SendMessageAsync(embed: BuildEmbed(n));

                await Task.Delay(300, ct);       // 保守節流：約 3 則/秒，遠低於 50 rps 上限
            }
            catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.Forbidden)
            {
                // 403 = 對方關閉私訊 → 停用該訂閱，絕不重試（避免累積成 IP ban）
                n.Subscription.Enabled = false;
                log.LogWarning("使用者 {User} 無法接收私訊，已停用其訂閱", n.Subscription.UserId);
            }
            catch (Exception ex) { log.LogError(ex, "通知發送失敗"); }
            finally { _throttle.Release(); }
        }
    }
}
```

通知內容：

```
🚌 公車快到了！

路線：300（返程）
上車站：臺中車站(A月台)　第 1 站
預計約 5 分鐘到站（16:42）
目的地：靜宜大學(專用道)　還有 23 站
車牌：KKA-7868
```

> 顯示分鐘數用 `Math.Round(liveSeconds / 60.0)`；
> `liveSeconds <= 30` 時顯示「即將進站」。
> **務必使用已遞減的秒數，不要直接顯示原始 `EstimateTime`。**

**備選（若使用者很多）**：`NotifyMode = Channel`，讓使用者在頻道訂閱、
Bot 在頻道發通知，並用 Discord 的 `@mention` 標記。
互動查詢一律用 ephemeral 回覆以免洗版。

---

## 12. MQTT 服務（第二階段，選配但值得做）

**用途僅限**：路線改道／停駛／減班公告、業者最新消息。**不是到站資料來源。**

```csharp
// Mqtt/TdxMqttService.cs
public sealed class TdxMqttService(IOptions<TdxOptions> opt, SubscriptionService subs,
                                   DiscordNotifier notifier, ILogger<TdxMqttService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opt.Value.Mqtt.Enabled) return;

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();

        client.ApplicationMessageReceivedAsync += async e =>
        {
            var json = e.ApplicationMessage.ConvertPayloadToString();
            log.LogInformation("MQTT {Topic} payload: {Json}", e.ApplicationMessage.Topic, json);
            // ⚠️ 第一次接上時先只看 log，確認 Bus payload 的 envelope 形狀後再寫解析
            var alert = JsonSerializer.Deserialize<BusAlertRecord>(json);
            if (alert is null) return;

            foreach (var sub in subs.GetByRouteNames(alert.RouteNames))   // RouteID 與 RouteUID 都要比
                notifier.Enqueue(BusAlertNotice.From(sub, alert));
        };

        client.DisconnectedAsync += async e =>
        {
            // 官方要求：自行實作斷線重連（官方範例等 10 秒）
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            if (!ct.IsCancellationRequested) await ConnectAndSubscribeAsync(client, ct);
        };

        await ConnectAndSubscribeAsync(client, ct);
        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task ConnectAndSubscribeAsync(IMqttClient client, CancellationToken ct)
    {
        var o = opt.Value.Mqtt;
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(o.Host, o.Port)
            .WithTlsOptions(t => t.UseTls())          // MQTTS 必須
            .WithCredentials(o.Username, o.Password)
            .WithClientId(o.ClientId)
            .WithCleanSession(true)                   // 官方建議 true
            .Build();

        await client.ConnectAsync(options, ct);

        // 直接用 City/# 再用 payload 過濾
        await client.SubscribeAsync(new MqttTopicFilterBuilder()
            .WithTopic("v2/Bus/Alert/City/#").WithAtLeastOnceQoS().Build(), ct);
        await client.SubscribeAsync(new MqttTopicFilterBuilder()
            .WithTopic("v2/Bus/News/City/#").WithAtLeastOnceQoS().Build(), ct);
    }
}
```

**重要注意事項**
- **同一組 ClientId/Username/Password 只能一條連線**，第二條會踢掉第一條。
  → 不要同時在本機和伺服器跑同一個 Bot。
- 只能訂閱不能發佈。
- 目前不計點數，但官方說未來會納入（QoS 0 送後即計、QoS 1/2 以 ack 計）。
- MQTTnet 5.x 的 API 與 4.x 不同（`MqttClientFactory` 等），
  可直接參考本專案 `reference/MQTTSampleCode/C#/Program.cs`。
- **[未確認]** Bus topic 的 payload envelope 形狀 → **先 log 再解析**。

---

## 13. 效能與成本

### 13.1 使用者數量 vs API 呼叫數

| 情境 | 不同上車站數 | 每週期呼叫數 | 說明 |
| --- | --- | --- | --- |
| 1 個使用者訂 300 路臺中車站 | 1 | **1** | |
| 100 個使用者都訂 300 路臺中車站 | 1 | **1** | 完全符合你的要求 |
| 100 個使用者各訂不同起點 | 100 | **3**（100/40 無條件進位） | 與使用者數脫鉤，只與「不同站數」有關 |
| **某使用者起點有 25 個候選站牌** | **1**（只有代表上車站） | **1** | ★ 候選站牌多寡**不影響**成本，見下 |
| 沒有任何訂閱 | 0 | **0** | 完全不呼叫 |

> ★ **候選站牌集合不會破壞這個性質**：因為每條路線只保留**一個代表上車站**，
> 使用者就算把起點設成 60 個候選站牌，輪詢的 filter 條件數量
> 仍然只取決於「所有訂閱中**不重複的代表上車站**有幾個」。
> **候選集合只在記憶體匹配時用到，不會增加任何 API 呼叫。**

### 13.2 每日呼叫量與點數估算（已用官方公式計算）

**官方點數公式（公車 v2 官方 OAS `info.description` 明文）**

```
消耗點數 = 呼叫次數 / 1,500  +  回傳資料量(MB) / 150
```

```
假設：30 秒輪詢、1 個批次、每天營運 18 小時
      18 × 60 × 2 = 2,160 次/日  →  約 65,000 次/月
      每次 ETA 回應約 80 KB     →  約 5 GB/月
```

| 項目 | 數值 | 點數 |
| --- | --- | --- |
| 計次 | 65,000 次 | 65,000 / 1,500 ≈ **43 點** |
| 計量 | 約 5,000 MB | 5,000 / 150 ≈ **33 點** |
| **合計** | | **≈ 76 點/月** |

**結論：銅級會員（NT$200/月，200 點、5 次/秒）足夠正式運行，還有約 2.6 倍餘裕。**

| 情境 | 估計點數 | 建議方案 |
| --- | --- | --- |
| 開發測試（5 分鐘輪詢，少量訂閱） | ≈ 10 點 | 銅級（或基礎會員勉強夠跑 2~3 天） |
| **正式運行（30 秒輪詢）** | **≈ 76 點** | **銅級 NT$200/月** |
| 使用者成長 10 倍（10 個批次） | ≈ 760 點 | 銀級 NT$1,000/月 |
| 免費基礎會員 | 3 點/月 | ≈ 4,500 次呼叫，**只夠開發驗證** |

**其他省點數的手段（依效果排序）**
1. **沒有啟用中的訂閱時完全不輪詢** → 深夜與無使用者時歸零。
2. **拉長間隔到 60 秒** → 呼叫數與資料量都減半，點數約 38 點/月。
3. **`$select` 只取必要欄位** → 直接降低「計量」那一半的成本。
   這個很容易被忽略，但計量佔了總成本的 4 成。
4. **只在「有訂閱進入通知視窗」時提高頻率**（平常用 60 秒，接近時用 20 秒）。
5. **注意 N1 來源端約 20 秒才更新一次**，把間隔調到比 20 秒更短**完全沒有意義**，
   只會浪費點數。**20 秒是實務下限。**

> 訪客模式（不帶金鑰）每日只有 20 次，**絕對不能當正式方案**。

### 13.3 記憶體

| 項目 | 估計 |
| --- | --- |
| 靜態索引（StopOfRoute + Stop + Route） | 30~50 MB |
| 訂閱（每個約 500 bytes + NotifyState） | 10,000 訂閱 ≈ 5 MB |
| 即時快取（每筆約 300 bytes） | 幾百 KB |
| **合計** | **< 100 MB** |

單一 gateway client、`GatewayIntents.Guilds`、`MessageCacheSize = 0`，
Discord.Net 本身幾乎不佔記憶體。

---

## 14. 開發里程碑與目前進度

| 階段 | 內容 | 驗收方式 | 狀態 |
| --- | --- | --- | --- |
| **M0** 骨架 | 專案、Options、`appsettings` | `dotnet build` 通過 | ✅ **完成** |
| **M1** TDX 用戶端 | Token + `TdxApiClient` + Models | 真實 JSON payload 可正確解析 | ✅ **完成**（Models 與解析已驗證） |
| **M2** 靜態索引＋搜尋＋匹配 | `TaichungBusDataService` + `StopNameNormalizer` + `StopSearchService` + 建議群組 + `LocationTarget` + 候選集合匹配 + 訂閱群組 + Matcher | **`tcbus selftest` 244 項全數通過** | ✅ **完成** |
| **M3** Discord UI | Slash command + Modal + 多選 Select + Buttons + **訂閱組（§8.1）** | `--dryrun` 全元件符合限制 **＋ 接線檢查（§7.6）通過**；Discord 端 Step 1→13 | 🟡 進行中（實機驗收中） |
| **M4** 即時資料 | `EtaPollerService`（輪詢）+ `DiscordNotifier`（節流） | 實際等到通知 | 🟡 部分（Matcher / Cache / 訂閱清單的預計到站時間已完成） |
| **M5** 收尾 | `/bus remove`、`/bus status`、錯誤處理 | 長時間掛機不爆掉 | ⬜ 待做 |
| **M6**（選配）MQTT | Alert / News 推播 | 改道公告能推給訂閱者 | ⬜ 待做 |
| **M7** Android APK | `TcBusBot.Mobile`（前景服務 + 工作目錄） | 實機安裝後可啟動、可跑 Bot | ✅ **實機驗證**（§17.4c） |
| **M8** 儲存後端 | MongoDB／SQLite／文字檔自動挑選與退回 | `selftest` 第 16 節 | ✅ **完成**（真的連上 MongoDB 待驗） |
| **M9** Termux 跑法 | `publish-termux.ps1` + 手機步驟 | 發佈產物 `--dryrun` 通過 | ✅ **完成**（手機端待你執行） |

### 14.1 目前實際存在的程式碼

```
TcBusBot.sln
├─ src/TcBusBot.Core/            ← 零外部套件相依（純 BCL）
│   ├─ Models/TdxModels.cs              TDX DTO + 容錯轉換器
│   ├─ Bus/StopNameNormalizer.cs        ★ 搜尋用 / 分組用，兩套正規化
│   ├─ Bus/ChineseText.cs               簡繁折疊（統一轉成簡體；LCMapStringEx + 一對多補表）
│   ├─ Bus/StopSearchService.cs         ★ 模糊搜尋（六種策略 + 人氣權重 + 群組名額分配）
│   ├─ Bus/StopAreaGrouping.cs          建議群組（前綴 + 座標叢集）
│   ├─ Bus/Geo.cs                       Haversine + 自然排序
│   ├─ Bus/TaichungBusDataService.cs    ★ 靜態索引 + 候選集合匹配 + 群組名解析
│   ├─ Bus/LocationTarget.cs            LocationTarget / BoardChoice / RouteOption
│   ├─ Tdx/TdxApiClient.cs              Token + HTTP（含 gzip 解壓）
│   ├─ DataSources/                     靜態資料載入（TDX / 磁碟快取 / 最小資料集）
│   ├─ Realtime/RealtimeBusCache.cs     全體使用者共用的 ETA 快取
│   ├─ Subscriptions/                   ★ Subscription / SubscriptionGroup（多段行程）/ Service / Matcher / UndoStack
│   ├─ Storage/SqliteDatabase.cs        ★ 零套件 SQLite（P/Invoke winsqlite3.dll）
│   ├─ Storage/SavedGroupStore.cs       ★ 訂閱組持久化（多段行程 payload + 舊格式相容，§8.1）
├─ src/TcBusBot.Discord/          ← Discord.Net 3.18.0（唯一外部套件）
│   ├─ Program.cs / BotConfig.cs / BotRuntime.cs / BusUi.cs / BusSession.cs
│   ├─ Modules/BusModule.cs / BusComponentModule.cs
│   ├─ EtaPoller.cs / SimpleServiceProvider.cs
│   └─ DryRun.cs                        離線檢查所有 Discord 元件限制
└─ src/TcBusBot.Cli/             ← 離線開發工具（`tcbus`）
    ├─ Program.cs                       selftest / search / route / diag
    └─ SelfTest.cs                      ★ 219 項離線驗收測試（含簡繁折疊、縮寫搜尋、輪詢成本、儲存後端、.env 與環境變數、合併與復原）
```

`src/TcBusBot.Discord/DryRun.cs` 除了檢查元件限制，還會做
**按鈕 ↔ 處理函式的雙向接線檢查**（§7.6）—— 這是實機測試抓到的錯所留下的防護。

**驗收指令**（不需要網路、TDX 金鑰、Discord Token）：

```powershell
dotnet run --project src\TcBusBot.Cli -- selftest              # 244 項驗收
dotnet run --project src\TcBusBot.Cli -- search 台中車站         # 模糊搜尋 + 建議群組
dotnet run --project src\TcBusBot.Cli -- route 台中車站 靜宜大學    # 匹配 + 訂閱展開
dotnet run --project src\TcBusBot.Cli -- diag 台中科技大學 大坑口   # 逐條說明路線為何被排除
dotnet run --project src\TcBusBot.Discord -- --dryrun          # Discord 元件限制檢查
```

**為什麼 M0~M2 先做成「零相依」**：
核心邏輯（站牌搜尋、路線匹配、通知判定）完全不需要 Discord 或網路，
把它們抽成零相依的 `TcBusBot.Core`，就能在**幾秒內**跑完整套驗收，
而且改壞了馬上知道。Discord 與 MQTT 留在後面接，接的時候不用怕動到核心。

> 下一次要做的：**M3 Discord UI**（Discord.Net 3.18.0 已在 NuGet 快取中）
> 與 **M4 輪詢迴圈**（`TdxApiClient` 接上真實 TDX）。

**M2 是最關鍵的里程碑**：它決定了路線匹配對不對 —— 現在已經確定是對的。

---

## 15. 風險與注意事項

| 風險 | 影響 | 對策 |
| --- | --- | --- |
| **TDX 點數/頻率限制** | 免費 3 點/月只夠開發 | 正式用銅級（約 76 點/月，NT$200）；無訂閱時不輪詢；`$select` 精簡欄位 |
| **ETA 不會自動遞減** | 通知時間比實際早到 | 用 `EstimateTime - (now - SrcUpdateTime)` 自行遞減（已納入 §10.1） |
| **MQTT 不提供 ETA** | 架構必須用 HTTP 輪詢 | 已納入設計（本方案核心修正） |
| **MQTT 只能一條連線** | 多實例部署會互相踢掉 | 每實例用不同金鑰（每帳號上限 3 組） |
| **`SubRouteUID` 不保證方向唯一** | 去回程混在一起 | 一律用 `(RouteUID, Direction)` 當鍵（已納入） |
| **同站區不同站名**（A月台 vs 臺灣大道） | 300 找得到、304 找不到 | 站名前綴正規化 + 座標叢集（已納入 §5.4） |
| **同名站多個 UID** | 匹配錯誤、訂閱到錯的月台 | 站區群組 + 群組內全部 StopUID（已納入） |
| **模糊搜尋找不到站** | 使用者以為站名打錯就放棄 | 多層正規化（臺/台、簡繁、全形、口語「火車站」）+ 縮寫與漏字容忍 + 查無結果時回 3 個最接近建議（§5.4 Step 5） |
| **簡體字查不到** | 打「台中车站」找不到 | ✅ **已解決**：`LCMapStringEx`（`LCMAP_SIMPLIFIED_CHINESE`）把索引與查詢都折疊成簡體；一對多的字用 20 字補表處理（§5.4 Step 1.5） |
| **打縮寫找不到**（`台中科大`） | 使用者覺得「明明有這個站」 | ✅ **已解決**：緊湊子序列算強相符，不再被強相符過濾器藏起來（§5.4 Step 2） |
| **簡→繁挑錯字** | `干净`→`干凈`，永遠對不上「乾淨」 | ✅ **已解決**：不做簡→繁，改成繁→簡（多對一、幂等），並保留 `ToTraditional` 只給顯示用 |
| **搜尋結果被無關站牌塞滿** | 搜「臺中車站」卻出現「沙鹿車站」「潭子車站」 | ✅ **已解決**：有完全／前綴相符的結果時只顯示那些（`BusUi.PreferStrongMatches`） |
| **同名站牌重複出現** | 同一個站名列了兩次（去回程各一個 StopUID） | ✅ **已解決**：同名合併成一個選項，值用短鍵，由伺服器端展開（§5.4 Step 4） |
| **選項值超長被截斷** | 候選集合少掉大半 → 「明明有 11 條路線卻只找到 1 條」 | ✅ **已解決**：值改用短鍵（`g:` / `n:`），超過 95 字元就警告（§5.4 Step 4） |
| **選了路線卻沒訂閱** | 使用者以為選單勾了就會訂閱 | ✅ **已解決**：勾選只是選取，要按「✅ 訂閱這 N 條路線」才建立（§7.2 Step 7–8） |
| **搜尋結果被熱門站吃光** | 冷門但正確的站牌排不進前 25 | 先取「每組最高分」再補齊剩餘名額（§5.4 Step 5 最後一項） |
| **同名站跨區被誤併** | 使用者選了別區的同名站 | 建議群組用「前綴 + 座標 500m 叢集」兩段式，不只看站名（§5.5） |
| **`ForSearch` 與 `ForGroup` 混用** | 打「A月台」找不到，或分組把不同站併在一起 | 兩個函式分開命名、分開寫測試（§5.4 Step 1） |
| **選項值超過 100 字元被截斷** | 候選集合少掉大半 → 「明明有 11 條路線卻只找到 1 條」 | 值改用短鍵（`g:` / `n:群組:索引`）；超過 100 字元印警告而非默默截斷；離線驗證檢查「每個選項值都能完整還原」（§5.4 Step 4） |
| **同名站牌幾十個** | 台中 `國立臺中科技大學` 44 個、`臺中車站` 38 個 StopUID | 依站名合併成一個選項；短鍵由伺服器端展開 |
| **起訖候選集合重疊** | 可能算出無意義的「同站上車同站下車」 | 配對時強制 `b.StopUid != a.StopUid` 且 `b.Seq < a.Seq`；UI 提示重疊 |
| **同一台車跨候選站重複通知** | 一台車經過 2 個候選站會通知 2 次 | 去重鍵不含 `BoardStopUid`，改用 `(路線,方向,車牌)` 且存在群組層級（§10.2，已測試） |
| **通知洗版**（多班車同時進入視窗） | 一次收到好幾則訊息 | 預設 `FastestPerWaitingPeriod`：只通知最快那班，其他以「其他選擇」附在同一則（§10.2，已測試） |
| **`EstimateTime` 欄位會整欄消失** | NullReferenceException | C# 用 `int?`，用「有值」判斷而非看 `StopStatus` |
| **`Direction` 不只 0/1** | 誤判迴圈路線 | 用 `int`；標準定義 0=去程、1=返程、**2=迴圈** |
| **TDX 資料延遲或中斷** | 誤報「5 分鐘到」 | `SrcUpdateTime` 新鮮度檢查 + `IsStale` 整批跳過 |
| **舊 PTX 站已無動態資料** | 抓不到 A1/A2/N1 | 一律走 TDX `tdx.transportdata.tw`，不要用 `ptx.transportdata.tw` |
| **使用者關閉私訊** | 403，累積成 IP ban | 403 時停用該訂閱並記錄，絕不重試 |
| **大量使用者同時通知** | 觸發 Discord 限流 | 通知佇列 + 節流（3 則/秒） |
| **重啟後訂閱與面板狀態消失** | 使用者要重新設定 | 你已接受；面板失效時回覆明確訊息，不讓使用者卡住 |
| **重啟後訂閱組不見** | 使用者整理的範本要重建 | ✅ **已解決**：訂閱組存進 SQLite（§8.1），只有「當下的訂閱」仍在記憶體 |
| **非 Windows / 找不到 `winsqlite3.dll`** | 訂閱組無法持久化 | `IsAvailable` 為 false 時退回記憶體模式，其餘功能照常；`/bus status` 與訂閱組清單會標示「記憶體模式」 |
| **套用舊訂閱組時路線已改道** | 訂閱到錯誤的月台或站序 | ✅ **已解決**：套用時用目前站序**重新匹配**，對不上的路線逐條列出並略過（§8.1） |
| **訂閱組名稱被用來做 SQL 注入** | 資料外洩或刪表 | 全部走參數化查詢（`SqliteDatabase.Prepare` 綁參數，不拼字串） |
| **一次套用／合併很多組時點錯** | 要全部重來 | ✅ **已解決**：每個動作都記下「怎麼還原」，`↩️ 復原` 可以連按 10 次（§8.2） |
| **合併覆蓋掉同名的舊組** | 舊內容消失 | ✅ **已解決**：合併前先備份，復原時寫回舊內容（有測試） |
| **舊版存的訂閱組讀不出來** | 使用者整理好的範本消失 | ✅ **已解決**：payload 有舊格式轉換器（單段 → 1 段行程），`selftest` 有測 |
| **合併後通知顯示錯的行程** | 使用者看到不是自己要搭的路線 | ✅ **已解決**：通知的起訖用「訂閱自己那一段」（`group.LegOf(sub)`），`--dryrun` Step 13b-2 會驗 |
| **復原紀錄在記憶體** | Bot 重啟後不能復原 | 已知限制（與「訂閱重啟就消失」一致）；UI 沒得按時不會誤導 |
| **手機版與桌面版行為分岔** | 兩套要各修一次 bug | ✅ **已避免**：手機版與桌面版**編譯同一份原始碼**（§17.1） |
| **Android 的 Doze 停掉網路** | 掛整晚卻沒通知 | 前景服務 + 電池最佳化白名單；App 內有按鈕直接帶使用者去設定（§17.3） |
| **服務自行重啟時跳過 Activity** | 訂閱組靜默退回記憶體模式 | SQLite 後端裝在 `MainApplication`，不是 Activity（§17.4） |
| **同一個 Token 兩地同時跑** | 通知重複 | 文件明確警告：換機器要先停掉原本那台 |
| **MongoDB 連不上讓 Bot 停擺** | 完全不能用 | ✅ 3 秒逾時 → 退回本機儲存 + 明確警告（§18.3） |
| **連線字串被印進日誌** | 帳密外洩 | ✅ 只印 `mongodb+srv://***@host`；`selftest` 有測 |
| **沒有 SQLite 的平台讓訂閱組消失** | 使用者白整理 | ✅ 文字檔後備（Termux 就是靠這個，§18.3） |
| **復原刪除後 id 不同** | 清單順序可能變 | 內容完全一致；文件與訊息都有說明 |
| **功能寫好了但畫面上沒有按鈕** | 使用者以為功能不存在（**真的發生過**：存成訂閱組） | ✅ 已加入 `--dryrun` 的**雙向接線檢查**（§7.6），而且驗證的是**實際送出的那一組元件** |
| **台中變體路線（`500延區1`）** | Alert 比對漏掉 | Alert 比對時 `RouteID` 與 `RouteUID` 都試 |

---

## 16. 待你確認的問題

已定案（你已同意 / 已實作驗證）：

- ✅ **多個站 = 多個訂閱**，通知從中挑**最快到**的那一班
- ✅ **站牌多選流程**：Modal 打關鍵字 → 多選建議群組／站牌
- ✅ **群組預設勾選**：完全相符／前綴相符的群組預設勾選，子字串命中的不勾
- ✅ **ETA 自行遞減**、**去重鍵不含站牌**、**90 分鐘 rearm**
- ✅ **訂閱組 + SQLite**（§8.1）：`/bus groups`、存成訂閱組、套用／改名／刪除
- ✅ **訂閱完成後顯示所有訂閱的預計到站時間並排序**（`/bus list`）
- ✅ **簡繁折疊（統一到簡體）+ 縮寫搜尋**（§5.4 Step 1.5、Step 2）
- ✅ **一次訂閱多個訂閱組**、**合併成多段行程的訂閱組**、**合併成一個通知流**、**復原**（§8.2）

仍待你決定：

1. **TDX 方案**：開發期可先用免費基礎會員（3 點/月 ≈ 4,500 次呼叫），
   正式上線建議**銅級 NT$200/月**（估算約需 76 點/月）。你要怎麼安排？
   → 這決定 M4 能不能接上真實資料實測。
2. **輪詢間隔**：建議 **30 秒**（點數約 76 點/月）；
   若要更省可設 60 秒（約 38 點/月）。台中 N1 來源端約 20 秒才更新，**低於 20 秒沒有意義**。
3. **通知管道**：預設私訊（DM）還是頻道？
   私訊體驗好，但官方明文不鼓勵大量主動私訊；頻道適合多人共用同一條通勤路線。
4. ~~提前通知的選項~~ → ✅ 已定案：**2 / 5 / 10 / 15 / 30 分鐘**（已經加上 2 分鐘）。
5. **通知模式**：確認預設用 `FastestPerWaitingPeriod`（只通知最快那班，其他附在同一則）？
   另一個選項是 `EveryBusOnce`（每班車各通知一次）。
6. ~~簡體字支援~~ → ✅ 已實作（統一到簡體，見 §5.4 Step 1.5）。
7. ~~訂閱組要不要用資料庫~~ → ✅ 已實作（SQLite via `winsqlite3.dll`，零套件）。
8. ~~縮寫搜尋（台中科大）~~ → ✅ 已實作（緊湊子序列算強相符）。

下一步：M4 的即時輪詢已寫好，只差 TDX 金鑰就能跑真實到站通知。
後續可做：私訊／頻道的通知方式切換鈕、`/bus remove`、MQTT 的 Alert/News 推播（M6）。

---

## 17. Android 版（掛在舊手機上當常駐服務）

**目標**：把同一個 Bot 裝進舊手機，插著電掛在家裡 —— 不用一直開著電腦。

### 17.1 ★ 鐵則：不複製 Bot，只換宿主

```
TcBusBot.Core ────┐
                  ├──> TcBusBot.Discord ──┬──> 主控台（dotnet run）
TcBusBot.Mobile ──┘   （Bot 本體：           └──> Android APK（前景服務）
   （只做宿主）          Program.Main / 指令 / 面板 / 輪詢 / 訂閱組）
```

`TcBusBot.Mobile` **用 `<Compile Include="..\TcBusBot.Discord\**\*.cs">` 把同一批原始碼編進來**，
所以「手機版能力一致」不是靠人工同步，而是**磁碟上只有一份實作**。
手機專案裡只有四件事：前景服務、設定畫面、SQLite 後端、Console 轉接。

> **為什麼不是 `ProjectReference`（試過，行不通）**
>
> Android App 必須是 self-contained（Mono 執行階段要打包進去），
> 而 .NET SDK 不允許 self-contained 專案參考「非 self-contained 的 Exe 專案」：
>
> ```
> error NETSDK1150: 引用的項目 TcBusBot.Discord.csproj 是非自包含的可執行檔。
>                    非自包含可執行檔不能由自包含可執行檔引用。
> ```
>
> 把 App 加上 `<SelfContained>false</SelfContained>` 可以繞過這個檢查 ——
> 但**會讓 Android 的執行階段完全起不來**。實機 A/B 測試（同一支 hello-world 專案）：
>
> | 設定 | 結果 |
> | --- | --- |
> | 預設（self-contained） | ✅ 正常啟動 |
> | `SelfContained=false` | ❌ `java.lang.UnsatisfiedLinkError: No implementation found for void crc64…MainActivity.n_onCreate(android.os.Bundle)` |
>
> 症狀是「所有受管理程式碼都沒接上」：`MonoRuntimeProvider` 有跑，但
> Mono 沒有初始化，於是每一個 ACW 的 native 方法都找不到實作。
>
> 用 `<Compile Include>` 共用原始碼同時滿足兩邊：
> `TcBusBot.Discord` **專案完全不動**（維持 Exe、`dotnet run` 照用），
> 手機版維持 self-contained（執行階段正常），而且仍然只有一份原始碼。
> 搭配 `<GenerateProgramFile>false</GenerateProgramFile>`，
> 讓 `TcBusBot.Discord.Program.Main` 當唯一的進入點（見 §17.4）。

| 手機專案裡的檔案 | 職責 |
| --- | --- |
| `MainApplication` | 行程一啟動就裝好 SQLite 後端與 Console 轉接 |
| `MainActivity` | 填 Token／TDX 金鑰、看日誌、啟停服務、關閉電池最佳化 |
| `BotService` | Android 前景服務；一條執行緒跑 `Program.Main(args)` |
| `AppSettings` | 設定寫成 `.env` → 交給既有的 `BotConfig` |
| `AndroidSqliteBackend` | `android.database.sqlite` 的 `ISqliteBackend` 實作 |
| `BotConsole` | `Console.WriteLine` → App 日誌區 + logcat |

**為了讓這件事成立，桌面版只做了三處「加法」**（行為不變）：

| 改動 | 為什麼 |
| --- | --- |
| `Program.RequestStop()` | 手機沒有 Ctrl+C；停止訊號改由服務發出 |
| `Console.OutputEncoding` 包 try/catch | Android 不支援設定輸出編碼 |
| SQLite 抽出 `ISqliteBackend` | Windows 用 `winsqlite3.dll`，Android 用系統的 `android.database.sqlite` |

> 為什麼不把手機版寫成另一個 Bot 實作：那份「一份程式碼」的價值會在第一次修 bug 時就消失
> —— 兩邊的行為會慢慢分岔，而使用者只會覺得「手機版壞了」。

### 17.2 為什麼 Android 不能用 `winsqlite3.dll` 那一套

`libsqlite3.so` 是 Android 的**私有**函式庫（不在 `public.libraries.txt`），
**從 API 24（Android 7）起應用程式 `dlopen` 不到它**。
所以「P/Invoke 系統的 SQLite」在 Android 上是走不通的。

三個選項的取捨：

| 選項 | 問題 |
| --- | --- |
| 編譯 SQLite amalgamation（NDK） | 要引入 25 萬行 C 與原生建置流程 |
| `SQLitePCLRaw` / `Microsoft.Data.Sqlite` | 需要 NuGet（本專案刻意零套件），而且離線建置會失敗 |
| **`android.database.sqlite`（.NET 繫結）** | ✅ 系統就有完整的 SQLite，零原生、零套件 |

所以 `SavedGroupStore` 改成依賴 `ISqliteBackend`，
由啟動端用 `SqliteBackends.Factory` 指定後端 —— 產出的都是標準 SQLite 檔，
手機與電腦之間可以互相複製。

### 17.3 為什麼一定要前景服務

Android 會隨時回收背景程序；這個服務要「整晚掛著等公車快到」，所以：

```csharp
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class BotService : Service
{
    public override StartCommandResult OnStartCommand(...)
    {
        StartForeground(1, BuildNotification("Bot 啟動中…"));   // 常駐通知 = 不被回收
        StartBot();                                             // 一條執行緒跑 Program.Main
        return StartCommandResult.Sticky;                        // 被殺掉後自動重來
    }
}
```

對應的 manifest 權限：`FOREGROUND_SERVICE`、`FOREGROUND_SERVICE_DATA_SYNC`（API 34+ 要求）、
`POST_NOTIFICATIONS`（API 33+）、`INTERNET`、`WAKE_LOCK`，
以及 `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`（App 內有一顆按鈕直接帶使用者去設定）。

> **Doze 是真正的敵人**：螢幕關掉、靜置一段時間後，Android 會暫停 App 的網路存取 ——
> 輪詢就會停。前景服務 + 電池最佳化白名單兩者都要做，才會整晚都在。

### 17.4 兩個「服務自行重啟」的坑

1. **`START_STICKY` 重啟時不會經過 `MainActivity`**
   → 如果 SQLite 後端只裝在 Activity 裡，重啟後訂閱組會**靜默地**退回記憶體模式
   （使用者看到「訂閱組不見了」，而且沒有任何錯誤訊息）。
   所以後端與 Console 轉接都放在 `MainApplication.OnCreate`。
2. **沒有 TDX 金鑰時的離線資料集**
   桌面版是去找 `tests/fixtures`；APK 裡沒有那個目錄。
   所以把 `mini/*.psv` 打包成 Android asset，第一次啟動複製到 `files/fixtures/`，
   再用既有的 `--fixtures` 參數指過去 —— 桌面版的那段程式碼一行都不用改。

### 17.4b 不要在 Android 用自訂的 `[Application]` 子類別（實測會當）

一開始把「Console 轉接 + SQLite 後端」放在自訂的 `[Application]` 子類別，
覺得那是最自然的時機。**實機一啟動就掛**：

```
E AndroidRuntime: FATAL EXCEPTION: main
E AndroidRuntime: java.lang.UnsatisfiedLinkError: No implementation found for void
    crc64….MainApplication.n_onCreate() (tried Java_…_n_1onCreate and …)
```

原因在產生的 Java stub（`obj/…/android/src/crc64…/*.java`）差別：

```java
// MainActivity() —— 正常
public MainActivity () {
    super ();
    mono.android.TypeManager.Activate ("TcBusBot.Mobile.MainActivity, TcBusBot.Mobile", "", this, …);
}
// MainApplication() —— 沒有那行 Activate，只有 setContext
public MainApplication () { mono.MonoPackageManager.setContext (this); }
```

`TypeManager.Activate` 才是「把 Java 物件接上受管理實例並註冊 JNI native 方法」的那一步。
Application 的 stub 沒有它，於是 `Application.onCreate` → `n_onCreate()` 找不到實作。

**改法**：把初始化移到 **Activity 與 Service 的 `OnCreate`**
（兩者的 stub 都有 `TypeManager.Activate`）。原本放在 Application 只是為了
「服務自行重啟時也初始化得到」，而 `Service.OnCreate` 本來就一定會跑到，功能沒有損失。

**還有一個 manifest 的坑**（同一輪修掉）：手寫 `<activity android:name=".MainActivity">`
會產生一個**不存在的 Java 類別**的項目（真正的類別是 `crc64…MainActivity`），
合併後的 manifest 會同時出現兩筆、啟動器點到錯的那筆就是 `ClassNotFoundException`。
**元件交給 `[Activity]` / `[Service]` 屬性產生**，manifest 只寫權限與 application 層級設定。

### 17.4c 實機驗證（Android 14 / arm64，`adb` 實測）

| 驗證項目 | 結果 |
| --- | --- |
| APK 安裝 | ✅ `adb install -r` Success |
| 啟動不當機 | ✅ 行程存在、`AndroidRuntime` 沒有例外 |
| 受管理執行階段 | ✅ 有我們的日誌：`I TcBusBot: [宿主] 初始化完成，工作目錄：/data/user/0/com.tcbusbot.mobile/files` |
| 工作目錄解析 | ✅ 未授權儲存權限時正確退回私有目錄（不會假裝成功） |
| A/B 對照 | ✅ 同一支 hello-world：預設設定可跑、`SelfContained=false` 必當（見 §17.1） |
| 手動按「啟動服務」 | ⏳ 由使用者驗收（需要解鎖螢幕＋Token；我不代按） |

### 17.5 建置參數（為什麼是這幾個）

| 參數 | 值 | 理由 |
| --- | --- | --- |
| `TargetFramework` | `net10.0-android` | 本機只裝了 .NET 10 的 Android workload |
| `SupportedOSPlatformVersion` | `24` | **Android 7.0**（使用者的舊手機） |
| `AndroidLinkMode` / `PublishTrimmed` | `None` / `false` | Discord.Net 大量反射；裁剪後會「執行期才炸」 |
| `RunAOTCompilation` | `false` | 同上；而且沒開裁剪時 AOT 會直接報 XA1030 |
| `GenerateProgramFile` | `false` | 進入點用共用的 `TcBusBot.Discord.Program.Main`，不要再自動產生一個空 Main |
| `RuntimeIdentifiers` | `android-arm64;android-arm` | 舊手機可能是 32 位元 |
| `AndroidPackageFormat` | `apk` | 要的是可側載的 APK，不是 `.aab` |

> **不要對被參考的 net8.0 專案宣告 Android RID**（例如加上
> `<RuntimeIdentifiers>android-arm;android-arm64</RuntimeIdentifiers>`）：
> 那會讓 NuGet 還原去要 Android 專屬的 runtime pack（離線環境直接失敗，
> `NU1301`）。RID 只宣告在 Android 專案自己身上。

實測產物：**`dist/tcbusbot-mobile.apk`，約 44 MB**，
`minSdkVersion 24`、`native-code: arm64-v8a, armeabi-v7a`、已用 debug keystore 簽署（可直接安裝）。

### 17.6 工作目錄（為什麼不能只用 App 私有目錄）

App 私有目錄 `/data/data/（套件名稱）/files` 在**沒 root 的手機上幾乎拿不出來**：
檔案管理員看不到、MTP 看不到，只有 `adb`（`run-as`）能撈。
「用電腦看一下 `app.env`、把 `tcbus.db` 拿出來」這種事會變得非常痛苦。

所以 App 裡有一顆 **「📁 選擇工作目錄」**：

| 選項 | 路徑 | 誰看得到 |
| --- | --- | --- |
| 私有（後備） | `/data/data/（套件名）/files` | 只有 adb |
| App 專屬外部 | `/storage/emulated/0/Android/data/（套件名）/files` | Android 10 以前：檔案管理員／MTP |
| **手機儲存空間**（預設） | `/storage/emulated/0/TcBusBot` | **檔案管理員、USB、同步 App** |
| 自訂 | 使用者輸入的絕對路徑 | 看那個位置 |

實作上有三個關鍵決定：

1. **選擇存在 `SharedPreferences`，不是存在工作目錄裡**
   —— 否則「換目錄」這個動作本身就沒地方記。（`WorkDir.Set/Resolve`）
2. **一定要實際試寫一個檔案**（`WorkDir.EnsureStructure` 的 `.tcbus-write-test`）
   —— 目錄 `Directory.Exists` 為 true 不代表有寫入權限，
   這正是 Android 11+ 公開目錄的狀態：能列、不能寫。
   不先驗證的話，使用者會看到「切換成功」但 Bot 寫不進資料庫。
3. **公開目錄的授權依版本分岔**：
   Android 10 以前是 `WRITE_EXTERNAL_STORAGE`（執行期權限）；
   Android 11 以後是「所有檔案存取權」（`MANAGE_EXTERNAL_STORAGE`，要帶使用者去設定頁）。
   Manifest 用 `maxSdkVersion` 把舊權限限制在 ≤29，避免新系統上要一個多餘的權限。

> 換目錄**不會搬檔案**：新目錄如果已經有 `app.env` / `tcbus.db` 就直接用那一份，
> 沒有的話就從乾淨的狀態開始（原本目錄的檔案留著不動）。

### 17.7 已知限制

| 限制 | 說明 |
| --- | --- |
| 同一組 Token 不要兩地同時跑 | 會有兩個閘道連線 → 通知重複 |
| 訂閱組不自動同步 | 手機與電腦各有一份 `tcbus.db`；用 `adb` 複製即可 |
| APK 較大（44 MB） | 關閉裁剪的必然代價（換取「不會在手機上執行期炸」） |
| debug keystore 簽署 | 自用足夠，上架才需要正式簽章 |
| 只支援單一使用者情境 | 面板 session 與復原紀錄都在記憶體，重啟後消失（與桌面版一致） |

---

## 18. Termux 跑法（手機上的控制台）與儲存後端

### 18.1 為什麼要有這條路

APK 那條路能跑，但成本不低：前景服務、通知權限、電池白名單、工作目錄權限、
簽章、44 MB 的 APK……而這些幾乎都跟「公車通知」本身無關。

**如果只是要「讓它在手機上一直跑」，Termux 更直接**：

| | APK | **Termux** |
| --- | --- | --- |
| 產物 | 44 MB APK（要簽章、要安裝） | 13 MB 資料夾（zip 後 5 MB，解開就跑） |
| 看日誌 | App 內建日誌區／`adb logcat` | **完整控制台**，跟桌面版一字不差 |
| 常駐 | 前景服務 + 電池白名單 + 通知權限 | `termux-wake-lock` + 背景執行 |
| 檔案 | App 私有目錄（要用工作目錄選擇器搬出來） | 就是 Termux 的家目錄（`~/tcbus`） |
| 額外工作 | Android 專案、manifest、SQLite 後端 | 一個發佈腳本 |

### 18.2 為什麼現在才可行（三個前提）

1. **Termux 有官方的 .NET 套件了**：`dotnet-runtime-8.0`、`dotnet-host-8.0`、
   `dotnet-sdk-8.0`（8/9/10 都有，2026-09 還在更新）。以前只能靠 `proot-distro`
   跑 glibc 容器，現在直接 `pkg install` 就好。
2. **發佈要「可攜式」**：Termux 的 .NET 是針對 Android(bionic) 重新打包的，
   官方 `linux-arm64` 的 apphost／runtime pack 對不上，所以
   `publish-termux.ps1` **刻意不指定 RID**（`-p:UseAppHost=false`），
   任何有 .NET 8 執行階段的環境都能跑。
3. **儲存不能依賴 SQLite**（下一節）。

### 18.3 儲存後端：四選一，而且失敗不硬撐

```
MongoDB（設了 TCBUS_MONGO）→ SQLite → 文字檔 → 記憶體
```

抽成 `ISavedGroupRepository` 之後，三個實作對外行為完全一致：

| 實作 | 用什麼 | 注意 |
| --- | --- | --- |
| `MongoSavedGroupRepository` | `MongoDB.Driver` 3.9.0 | 對外的 `long id` 用 `seq`（counters 集合原子遞增，等價於 AUTOINCREMENT）；`payload` 存 **JSON 字串**，沿用既有的舊格式相容轉換器，Atlas 上也看得懂 |
| `SqliteSavedGroupRepository` | `ISqliteBackend`（Windows `winsqlite3`／Android `android.database.sqlite`） | 原本的實作，只是搬出來 |
| `TextSavedGroupRepository` | 一行一筆的 `saved_groups.txt` | 沒有 SQLite 時的後備（**Termux 就是靠這個**）；先寫 `.tmp` 再置換，壞掉的行個別跳過 |

**三個設計決定值得記下來：**

* **連不上 MongoDB 要「退回 + 警告」，不是「硬撐」也不是「默默變記憶體」**
  —— 前者讓 Bot 停擺，後者會讓使用者以為資料存好了。所以：
  3 秒逾時 → 退回本機 → 印出
  `[儲存] MongoDB 連不上（TimeoutException: …）→ 改用本機儲存（這次的訂閱組不會同步到其他機器）`。
  例外訊息會**截成一行 120 字**（MongoDB 的例外很長，整段印出來只會洗版）。
* **連線字串等同密碼**：啟動橫幅只印 `mongodb+srv://***@cluster0.xxxxx.mongodb.net/...`，
  測試裡有一項專門在驗「不會洩漏帳密」。
* **文字檔要真的看得懂**：中文不能變成 `\u4E0A`，所以 JSON 用了
  `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`，而且寫檔不用 BOM
  （`Encoding.UTF8` 會加 BOM，讓第一行不再是 `#…`）。這兩點都是測試抓出來的。

### 18.4 實機驗證到什麼程度

| 項目 | 狀態 |
| --- | --- |
| 發佈產物可執行 | ✅ `publish-termux.ps1 -SelfTest` 會在本機跑一次 `--dryrun`（exit 0）才算成功 |
| 連線字串遮罩 | ✅ 實跑：`mongodb+srv://***@cluster0.abc.mongodb.net/...` |
| 連不上時的退回 | ✅ 實跑：3 秒逾時 → 一行警告 → 改用 SQLite，Bot 繼續運作 |
| 未設定 MongoDB | ✅ 實跑：走原本的本機路徑 |
| **真的連上 MongoDB** | ⏳ 需要一組連線字串（Atlas 免費層即可）才能驗；**這是我唯一沒能實測的一段** |
| 手機上跑 Termux | ⏳ 需要你在手機上 `pkg install` ＋ 填 Token |

---

## 19. 部署到 Render（Web Service ＋ 健康檢查 ＋ 防休眠）

### 19.1 為什麼需要一個 HTTP 端點

Render 的免費層**只提供 Web Service**：服務必須監聽 HTTP 埠，否則平台判定部署失敗。
所以同一個程式要同時當「Discord Bot」與「Web Service」。

### 19.2 ★ 為什麼不是 `Microsoft.NET.Sdk.Web` + `WebApplication`

使用者最初的草案是改成 `Microsoft.NET.Sdk.Web`，用 `WebApplication.CreateBuilder`。
**實測後改成用 BCL 的 `TcpListener` 自己回 HTTP**，理由如下：

| 平台 | ASP.NET Core 可用嗎 | 影響 |
| --- | --- | --- |
| Render（Linux 容器） | ✅ | 沒問題 |
| Windows／Linux 桌面版 | ✅ | 沒問題 |
| **Termux（手機控制台版）** | ❌ Termux 只有 `dotnet-runtime`，沒有 ASP.NET Core 執行階段 | 手機版會「裝了但起不來」 |
| Android APK（已擱置） | ❌ 沒有 `Microsoft.AspNetCore.App` 框架參考 | 當時會讓 APK 建不起來 |

手機版與桌面版**編譯同一份原始碼**（§17.1），所以把 ASP.NET Core 放進共用程式碼
會波及所有宿主；而我們需要的只是「聽一個埠、回 200」。
`System.Net` 是 BCL 內建的，零套件、零框架參考，每個平台都能跑。

**還試過 `HttpListener`（也是 BCL），但退回了**：它在 Windows 上需要 HTTP.SYS 的 URL ACL
（非管理員連 `http://localhost:port/` 都綁不起來），
變成「Render 上可以、本機跑不起來」—— 那就沒辦法在本機驗證，也違背「本機能 dotnet run」。

最後用 `TcpListener` 自己寫 HTTP/1.1 回應（約 200 行），任何平台、任何權限都能綁：

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Content-Length: 341
Cache-Control: no-store
Connection: close
```

> 這是**真正的 HTTP**：有 status line、標準標頭、body；
> curl／瀏覽器／Render 的健康檢查／UptimeRobot 都直接可用（實測）。

### 19.3 端點與狀態

| 路徑 | 回應 |
| --- | --- |
| `GET /` | `200 TcBusBot is running!`（Render 的 healthCheckPath 用 `/health`） |
| `GET /health` | `200` JSON：`uptimeSeconds`、`discordReady`、`storage`、`dataSource`、`poller`、`subscriptionGroups`、`subscriptions`、`cachedEtas` |
| 其他 | `404`；非 GET／HEAD → `405` |

**開埠的時機很重要**：健康檢查端點在「**連 Discord 之前**」就啟動。
Render 會在啟動後不久探測，等到 Discord 連上（本來就要好幾秒）才開埠會被判定部署失敗。

狀態由 `BotStatus`（靜態快照）提供：端點啟動時 `subs`／`cache` 還不存在，
用區域變數會抓不到（C# 也不允許區域函式捕捉之後才宣告的變數），
所以由 `Program` 邊建立邊填進去。

### 19.4 防休眠（keep-alive）

免費層閒置約 15 分鐘會停掉服務。設了 `APP_URL`（或 Render 自動注入的
`RENDER_EXTERNAL_URL`）之後，`KeepAliveLoop` 每 `KEEP_ALIVE_MINUTES`（預設 10）分鐘 ping 一次自己：

```
[keep-alive] 14:12:03 ping https://…/health → HTTP 200（成功 3／失敗 0）
```

> ⚠️ **誠實說明極限**：服務真的睡著之後就沒有東西可以 ping 自己了。
> 自我 ping 只能「在醒著時維持清醒」，**不能把自己叫醒**；
> 要保證隨時都醒著需要**外部**監控或付費方案。程式啟動時會把這句話印出來。

### 19.5 容器／Render 才會遇到的坑

| 坑 | 症狀 | 處理 |
| --- | --- | --- |
| `ENTRYPOINT ["dotnet","TcBusBot.Discord.dll"]` | 容器啟動即失敗 `Could not find …` | 專案 `AssemblyName` 是 `tcbus-bot` → 用 **`tcbus-bot.dll`** |
| 非 root 使用者寫不進 `/app` | 訂閱組靜默退回記憶體模式（只有一行警告） | Dockerfile 設 `TCBUS_DB=/tmp/tcbus.db`、`TCBUS_CACHE=/tmp/tcbus-cache` |
| 容器檔案系統是暫時的 | 重新部署後訂閱組消失 | 設 `TCBUS_MONGO`（Atlas 免費層） |
| 離線資料集不在映像裡 | 沒有 TDX 金鑰時啟動失敗 | Dockerfile `COPY --from=build /src/tests/fixtures ./tests/fixtures`（`Program` 會從工作目錄往上找） |
| Render 的 Blueprint 欄位名 | `env: docker`／`type: worker` 不生效 | 正確是 **`runtime: docker`**、**`type: web`**（免費層沒有 worker） |

### 19.6 MongoDB 連不上的診斷（雲端部署的頭號問題）

實測回報：Render 上出現 `TimeoutException: A timeout occurred after 2998ms selecting a server…`。
兩個問題都修了：

| 問題 | 修正 |
| --- | --- |
| **逾時太短**（3 秒） | 雲端第一次連 Atlas 要 DNS SRV → TLS → 複製集探索，3 秒不夠。改成**連線 10 秒／伺服器選擇 15 秒／Socket 20 秒**（`MongoSavedGroupRepository`） |
| **訊息看不出原因** | 新增 `MongoDiagnostics`：印出遮罩後的連線字串、**SRV 記錄查詢結果**（`DnsClient`，本來就是 driver 的相依），再照發生機率列出檢查清單 |

`srv` 查詢結果把問題一刀切開：
**查得到節點** → DNS 沒問題，連不上就是白名單／帳密／TLS；
**查不到** → DNS 被擋或叢集名稱錯。實測本機 `查到 1 個節點`，
所以本機的失敗是網路層（沙箱沒有對外 TLS），而不是 DNS。

另外新增 `MongoRecheckLoop`：連不上時**每 5 分鐘重測**，連上就印一則提醒
（**不自動切換** —— 中途用本機儲存寫入的訂閱組不會自動搬過去，
自動切換會讓使用者誤以為資料都在雲端）。使用者重啟服務即切換。

> 開埠時機在這裡也幫上忙：健康檢查端點在**連 MongoDB 之前**就啟動，
> 所以「等 15 秒才知道 Mongo 連不上」不會影響 Render 的健康檢查。

### 19.7 驗證到什麼程度

| 項目 | 狀態 |
| --- | --- |
| 端點：`/`、`/health`、404、405、HEAD、查詢字串、結尾斜線 | ✅ `tcbus selftest` 第 17 節（真的開埠、真的用 HttpClient 打） |
| 隱私：健康檢查只回狀態，不含 Token／連線字串 | ✅（`BotStatus` 只放訂閱數、快取筆數等） |
| 防休眠：ping 成功會計數、打不通不丟例外 | ✅ 同一節（離線測試原本驗不到，因為它要等 Discord 登入成功才啟動） |
| 本機啟動 ＋ 原始 HTTP 位元組 | ✅ 實跑：`dotnet tcbus-bot.dll` ＋ TcpClient 讀到 `HTTP/1.1 200 OK` |
| **Docker 映像建置** | ⏳ 本機沒有 docker，未實測 |
| **Render 實際部署** | ⏳ 需要你的帳號與 repo |

---

## 附錄：本方案可直接查核的官方來源

| 主題 | 來源 |
| --- | --- |
| Token / 錯誤碼 / 頻率 | [TDX API 授權驗證與使用方式](https://motc-ptx.gitbook.io/tdx-xin-shou-zhi-yin/api-shi-yong-shuo-ming/api-shou-quan-yan-zheng-yu-shi-yong-fang-shi) |
| OData 語法（`$top` 不加即回全部） | [TDX OData 查詢](https://motc-ptx.gitbook.io/tdx-xin-shou-zhi-yin/api-te-se-shuo-ming/zhi-yuan-odata-cha-xun-yu-fa/odata-cha-xun.md) |
| 收費方案 | [TDX 訂閱收費](https://tdx.transportdata.tw/pricing) |
| 台中供應現況（無「組站位」） | [基礎資料供應現況表](https://tdx.transportdata.tw/data-provide) |
| **StopStatus / Direction / IsLastBus 值域** | [公共運輸旅運資料標準 公車系統標準資料產製範例說明文件（§18 公車 N1）](https://www.motc.gov.tw/ch/app/data/doc?aplistdn&type=s&detailNo=6&preview&serno=201712210001&module=bussiness&id=2737) |
| **計費方式 1500 次/點 + 150MB/點、ETA 需自行遞減** | [TDX 公車 v2 官方 OAS](https://tdx.transportdata.tw/webapi/File/Swagger/V3/2998e851-81d0-40f5-b26d-77e2f5ac4118)（本機存檔：`reference/TDX-公車v2-OpenAPI.json`） |
| 台中 N1 不提供過站後的下班車預估 | [公車 API 動態資料使用注意事項](https://motc-ptx.gitbook.io/tdx-zi-liao-shi-yong-kui-hua-bao-dian/data_notice/public_transportation_data/bus_dynamic_data.md) |
| **MQTT 完整主題表（無 ETA）** | [tdxmotc/MQTTSampleCode](https://github.com/tdxmotc/MQTTSampleCode)（本機存檔：`reference/MQTTSampleCode/`） |
| MQTT 只開通阻與最新消息 | [TDX 2026-05-19 公告](https://tdx.transportdata.tw/news/detail/c147c38a-10cf-4a52-9022-e1ded0b7a3bc) |
| 縣市代碼 `Taichung` | [TDX 運輸資料介接指南 表 3.1](https://bookdown.org/chiajungyeh/TDX_Guide/bus-data.html) |
| Streaming 端點不含台中 | [TDX 公車 v2 官方 OAS](https://tdx.transportdata.tw/webapi/File/Swagger/V3/2998e851-81d0-40f5-b26d-77e2f5ac4118) |
| Discord 互動限制 | [Discord Components Reference](https://docs.discord.com/developers/components/reference) ・ [Autocomplete](https://docs.discord.com/developers/interactions/application-commands#autocomplete) |
| Discord.Net 元件 API | [Discord.Net 元件指南](https://docs.discordnet.dev/guides/components_v2/interaction.html) |
