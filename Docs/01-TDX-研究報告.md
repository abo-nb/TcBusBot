# TDX 台中市公車：HTTP API 與 MQTT 研究報告

> 目的：確認 TDX 目前（2026-09）對「台中市公車」提供的 HTTP API 與 MQTT Topic / Payload，
> 判斷哪些資料足以實現「選起點 → 選終點 → 找出可搭路線 → 訂閱 → 公車快到站時 Discord 通知」。
>
> 標記說明：
> **[實測]** = 本次研究實際呼叫 TDX 端點取得的真實回應（台中市）
> **[官方文件]** = TDX 官方 OAS / 官方 README / 交通部資料標準
> **[未確認]** = 尚未證實，需你或後續驗證
>
> 第一手參考檔案已存在本專案：`reference/TDX-公車v2-OpenAPI.json`（官方 OpenAPI 規格全文）、
> `reference/MQTTSampleCode/`（官方 MQTT 範例碼）、`reference/SampleCode-CSharp/`（官方 API 範例碼）。

---

## 0. 一句話結論

> **TDX MQTT 目前完全沒有提供公車即時位置或 ETA，只有「最新消息」與「營運通阻」兩類。
> 因此「公車快到站」通知必須走 HTTP API 輪詢 ETA；MQTT 只能當作加值的異常/公告推播。**

---

## 1. 認證機制

### 1.1 Access Token（OAuth2 / OIDC Client Credentials）**[官方文件]**

| 項目 | 值 |
| --- | --- |
| Endpoint | `https://tdx.transportdata.tw/auth/realms/TDXConnect/protocol/openid-connect/token` |
| Method | `POST` |
| Content-Type | `application/x-www-form-urlencoded` |
| 參數 | `grant_type=client_credentials`、`client_id`、`client_secret` |
| 回傳 | `{ "access_token": "<JWT>", "expires_in": 86400, "token_type": "Bearer" }` |

```bash
curl --request POST \
     --url 'https://tdx.transportdata.tw/auth/realms/TDXConnect/protocol/openid-connect/token' \
     --header 'content-type: application/x-www-form-urlencoded' \
     --data grant_type=client_credentials \
     --data client_id=YOUR_CLIENT_ID \
     --data client_secret=YOUR_CLIENT_SECRET
```

呼叫 API 時帶 `Authorization: Bearer <access_token>`；可加 `Accept-Encoding: br,gzip` 降低傳輸量。

**重要限制 [官方文件]**
- Token endpoint：**每個 IP 每分鐘最多 20 次**（與 API 呼叫限制分開計算）→ 程式必須快取 token。
- API 金鑰由 TDX 會員中心（會員中心 → 資料服務 → API 金鑰）取得，**每帳號最多 3 組**，點數合併計算。
- 只支援 **TLS 1.2 以上**。
- 同一組 ClientId/Secret 可同時在多個服務取得 token，互不影響。

### 1.2 城市代碼 **[實測 / 官方文件]**

台中市在 URL path 中的代碼是 **`Taichung`**（不是 `TXG`）。
回應內部的 `City` = `"Taichung"`、`CityCode` = `"TXG"`，站牌 UID 前綴為 `TXG`（例：`TXG12251`）。

官方縣市代碼表：臺北市 `Taipei`、新北市 `NewTaipei`、桃園市 `Taoyuan`、
**臺中市 `Taichung`**、臺南市 `Tainan`、高雄市 `Kaohsiung`、公路客運 `Intercity`。

MQTT topic 的 `{縣市代碼}` 用同一個字串，即 `v2/Bus/Alert/City/Taichung`。

**→ 台中公車確定使用 v2 端點**（v3 只涵蓋臺鐵與臺南，見 §3.1）。

---

## 2. 收費與頻率限制（會直接影響架構）

TDX 自 **113/1/1** 起依《交通部運輸資料流通服務平臺收費要點》收費 **[官方文件]**。

| 方案 | 月費 | 虛擬點數 | 存取頻率 |
| --- | --- | --- | --- |
| 一般／基礎會員（預設） | 0 元 | **3 點/月** | **5 次/分/金鑰** |
| 銅級 | 200 ~ 800 元 | 200 ~ 800 點 | 5 次/秒/金鑰 |
| 銀級 | 1,000 ~ 4,750 元 | 1,000 ~ 4,750 點 | 10 次/秒/金鑰 |
| 金級 | 5,000 ~ 9,500 元 | 5,000 ~ 9,500 點 | 30 次/秒/金鑰 |
| 白金級 | 10,000 ~ 20,000 元 | 10,000 ~ 20,000 點 | 50 次/秒/金鑰 |
| 專案 | 核定 | 專案核定 | 50 次/秒/金鑰 |

《收費要點》第四點 **[官方文件]**：

> 「平臺應按會員使用各項平臺資料服務之**實際使用次數及資料存取量**，
> 換算並扣抵會員虛擬點數。」

### 2.1 ★ 點數換算公式（已由官方 OAS 第一手證實）

**TDX 公車 v2 官方 OpenAPI 規格的 `info.description` 原文：**

```
計費方式：
計次：1,500次/1點
計量：150MB/1點
```

（本專案已存檔：`reference/TDX-公車v2-OpenAPI.json` 第 5 行；v3 相同。）

**實際公式**

```
消耗點數 = 呼叫次數 / 1,500  +  回傳資料量(MB) / 150      ← 兩者相加，不是擇一
```

**這對本專案的意義（好消息）**

| 情境 | 呼叫數 / 月 | 資料量 / 月 | 點數 | 適用方案 |
| --- | --- | --- | --- | --- |
| 開發測試（5 分鐘一次） | 8,640 | ~0.7 GB | **≈ 10 點** | 銅級（基礎會員 3 點不夠） |
| **正式運行（30 秒輪詢、每日 18 小時、1 批次）** | ~65,000 | ~5 GB | **≈ 76 點** | **銅級（200 點，NT$200/月）綽綽有餘** |
| 使用者成長 10 倍（10 批次） | ~650,000 | ~50 GB | ≈ 760 點 | 銀級（1,000 點，NT$1,000/月） |

> 資料量以「每次 ETA 回應約 80 KB」估算，實際值待壓測修正。

**結論：**
- 免費的一般會員（3 點/月 ≈ 4,500 次呼叫）**足以開發與驗證**（約可跑 2~3 天的每分鐘輪詢）。
- **正式上線建議銅級（NT$200/月）**，比原本擔心的「每個方案都不夠」樂觀非常多。
- **[未確認]**：官網【資料服務定價】頁的逐項換算表是圖片（blob URL）無法讀取，
  因此無法逐項核實是否所有服務都用同一組費率。但「公車 v2/v3 = 1500 次/點 + 150 MB/點」
  是官方 OAS 明文。

### 2.2 其他官方限制 **[官方文件]**

| 項目 | 限制 |
| --- | --- |
| **訪客模式（未帶金鑰）** | 限以瀏覽器存取、限基礎服務、**每來源 IP 每日至多 20 次** |
| 基礎會員 | **5 次/分/金鑰** |
| 金鑰數量 | 每帳號最多 **3 把**（點數合併計算） |
| 並行連線 | 每 IP **60 條**（超過回 `416`） |
| 瞬間流量 | **50 次/秒**（超過回 `423`） |
| 錯誤碼 | `401` 無 token / invalid token / scope 不足、`429 API rate limit exceeded`、`416` 超過並行連線、`423` 超過 50/s |
| Token endpoint | 每 IP 每分鐘 20 次（與 API 限制分開計算） |
| 點數用罄 | 用罄後給 **5% 緩衝**，緩衝用完（105%）當月即停止服務；80% 起寄信提醒 |

**架構影響（非常重要）**
- 「一個使用者一個 HTTP 請求」不只是效能問題，更是**成本問題**（計次 + 計量都扣點）。
- 架構必須保證「一次輪詢，全體使用者共用」。
- 靜態資料用 `$select` 精簡欄位，直接降低「計量」那一半的成本。
- **MQTT 目前不納入點數計算**（官方公告：開放初期尚未納入收費範圍）→ 能用 MQTT 的資料盡量用 MQTT。

---

## 3. HTTP API 端點總表（全部以台中實測）

Base：`https://tdx.transportdata.tw/api/basic`

| # | 資料項 | Endpoint | 用途 | 實測 |
| --- | --- | --- | --- | --- |
| 1 | 路線 | `GET /v2/Bus/Route/City/Taichung[/{RouteName}]` | 路線基本資料、去回程 Headsign、SubRoutes[] | ✅ 200 |
| 2 | 站牌 | `GET /v2/Bus/Stop/City/Taichung` | 站牌名稱、座標、StationID | ✅ 200 |
| 3 | **路線站序** | `GET /v2/Bus/StopOfRoute/City/Taichung[/{RouteName}]` | **A→B 匹配的核心資料** | ✅ 200 |
| 4 | 顯示站序 | `GET /v2/Bus/DisplayStopOfRoute/City/Taichung` | 同上但為顯示用（無 SubRouteUID） | ✅ 200 |
| 5 | **預估到站 ETA** | `GET /v2/Bus/EstimatedTimeOfArrival/City/Taichung[/{RouteName}]` | **通知的核心資料（N1）** | ✅ 200 |
| 6 | 車機定點 | `GET /v2/Bus/RealTimeNearStop/City/Taichung` | 車輛目前在「哪一站」，含 TripStartTime（A2） | ✅ 200 |
| 7 | 車機定時 | `GET /v2/Bus/RealTimeByFrequency/City/Taichung` | 車輛 GPS 座標（A1） | ✅ 200 |
| 8 | 營運通阻 | `GET /v2/Bus/Alert/City/Taichung` | 改道/停駛公告（MQTT 也有） | ✅ 200 |
| 9 | 最新消息 | `GET /v2/Bus/News/City/Taichung` | 業者公告（MQTT 也有） | ✅ 200 |
| 10 | 車輛 | `GET /v2/Bus/Vehicle/City/Taichung` | 車輛屬性（低地板/電動…），**不綁路線** | ✅ 200 |
| 11 | 站位 | `GET /v2/Bus/Station/City/Taichung` | 站位（Station）層級資料 | ✅ |
| 12 | 業者 | `GET /v2/Bus/Operator/City/Taichung` | 業者基本資料 | ✅ |
| 13 | 資料版本 | `GET /v2/Bus/DataVersion/City/Taichung` | 判斷靜態資料是否需重載 | ✅ |
| 14 | 票價 | `GET /v2/Bus/RouteFare/City/Taichung[/{RouteName}]` | 票價 | ✅ |
| 15 | 線形 | `GET /v2/Bus/Shape/City/Taichung[/{RouteName}]` | WKT `LINESTRING` + `EncodedPolyline` | ✅ |
| 16 | 班表 | `GET /v2/Bus/Schedule/City/Taichung[/{RouteName}]` | 班表。**台中 `Timetables[].StopTimes` 常只有頭站時刻** | ✅ |
| — | 子路線 | `GET /v2/Bus/SubRoute/City/Taichung` | — | ❌ **404 不存在** |

### 3.1 關於 SubRoute 的重要更正

**[實測]** `/v2/Bus/SubRoute/City/{City}` 在 TDX v2 **不存在**
（台中與台北皆回 `{"message":"Resouce Not Found"}`）。v2 官方 OAS 的 `paths` 清單裡確實沒有這個路徑。

`/v3/Bus/SubRoute/City/{City}` 存在，但 **v3 的 City enum 只有 `Tainan`**
（實測回 `{"Message":"City: 'Taichung' is not accepted but Tainan"}`）。
**→ 台中公車確定走 v2，不要用 v3。**

子路線（SubRoute）資訊藏在其他地方，實務上完全夠用：
- `Route.SubRoutes[]`：`SubRouteUID`、`SubRouteID`、`SubRouteName`、`Direction`、`Headsign`
- `StopOfRoute`／`ETA`／`RealTimeNearStop` 每筆都帶 `SubRouteUID`、`SubRouteName`

台中實測中，多數路線的 `RouteUID == SubRouteUID`（例：`TXG300`），
**而且同一條路線的去回程會共用同一個 SubRouteUID**（見 §6.4）→ 這是設計上的關鍵陷阱。

### 3.2 OData 查詢能力 **[實測]**

TDX v2 是 OData 介面，以下語法都實測可用：

| 參數 | 實測範例 | 結果 |
| --- | --- | --- |
| `$top` | `?$top=2` | ✅ |
| `$skip` | `?$skip=5000` | ✅ |
| `$select` | `?$select=StopUID,StopName` | ✅ 只回傳指定欄位（**省點數用**） |
| `$orderby` | `?$orderby=StopSequence` | ✅ |
| `$filter`（單條件） | `?$filter=StopStatus eq 0` | ✅ |
| `$filter`（多值 or） | `?$filter=StopUID eq 'TXG12251' or StopUID eq 'TXG13567'` | ✅ **省成本的關鍵** |
| `$filter`（複合） | `?$filter=RouteUID eq 'TXG300' and Direction eq 1` | ✅ |
| `$filter`（巢狀物件） | `?$filter=StopName/Zh_tw eq '臺中車站(A月台)'` | ✅ |
| `$filter`（模糊） | `?$filter=contains(StopName/Zh_tw,'靜宜')` | ✅ **站名搜尋可用** |
| `$format` | `?$format=JSON` | ✅ |
| 路徑式篩選 | `/StopOfRoute/City/Taichung/300` | ✅ 單一路線 |
| 空間查詢 | `?$spatialFilter=nearby(24.13,120.68,500)` | ✅ 可用於找附近站牌 |
| 健康檢查 | 任一服務加 `?health=true` | ✅ 回 `299` |
| `$count=true` | — | ❌ **v2 不支援**（會被忽略） |

**分頁：不需要。**
官方文件明文：「`top` 取最前筆數，**若不加此語法則會回傳所有資料**」。
OAS 裡 `$top` 的 `default: 30` 只是 Swagger UI 的輸入預設值，不是伺服器端上限。
台中實測資料量：`StopOfRoute` 約 **751~765 筆**、`Route` 約 **341~400 筆**、
`Stop` **> 5,000 筆**，皆可單次取回。

> **[實測] 觀察（已找到解釋）**：本次研究的呼叫都**沒有帶 Authorization header**
> 仍成功取得資料。這是 TDX 的**「訪客模式」**：
> **限瀏覽器、限基礎服務、每來源 IP 每日 20 次** **[官方文件]**。
> → **絕對不能依賴**：正式程式必須使用 OAuth 金鑰。

### 3.3 Streaming 端點：台中不適用 **[官方文件]**

TDX 另有「逐筆更新（Streaming / UDP 推播）」端點：

```
/v2/Bus/{RealTimeByFrequency|RealTimeNearStop|EstimatedTimeOfArrival}/Streaming/City/{City}[/{RouteName}]
```

但其 City enum **只包含公總代管的縣市**（新竹市/新竹縣/苗栗/彰化/南投/雲林/嘉義市/嘉義縣/
屏東/宜蘭/花蓮/台東/澎湖/基隆），**不含台中市**。
→ 台中只能使用批次版端點（無 `/Streaming`）。**這是「台中即時資料只能輪詢」的第二個佐證。**

### 3.4 舊 PTX 站不可作為動態來源 **[實測]**

`ptx.transportdata.tw`（TDX 的前身）的 A1／A2／N1 **已全面回傳 `[]`**（台北、高雄、台中皆同）。
PTX v3 的 City 只接受 `Tainan`。**動態資料一律走 `tdx.transportdata.tw`。**

### 3.5 站牌座標與「附近站牌」查詢 **[實測 / 官方文件]**

因為需求改成「地點可以是一個地圖座標 + 半徑」，這裡確認 TDX 提供的座標資料是否足夠：

| 需求 | TDX 支援 | 說明 |
| --- | --- | --- |
| 站牌經緯度 | ✅ | `Stop.StopPosition.PositionLon/PositionLat`（實測 `TXG12251` → `120.686459, 24.137749`） |
| 站牌座標也內含在站序資料 | ✅ | `StopOfRoute.Stops[].StopPosition` 同樣有座標，啟動時抓一份就夠 |
| 伺服器端空間查詢 | ✅ | OData `$spatialFilter=nearby(Lat,Lon,半徑)`（實測可用）——**但會扣點數** |
| 直線距離計算 | ✅ 自行計算 | Haversine 即可，台中全市約 5,000+ 站牌，記憶體掃描是微秒等級 |

**→ 結論：第一階段完全不需要 Google Maps 等外部地圖服務。**
站牌座標就在 TDX 靜態資料裡，啟動時快取進記憶體後，
「附近站牌」與「步行距離」都是本地純計算，**零 API 呼叫、零點數**。

> **📌 範圍註記（2026-09 決定）**：專案第一階段**不做地圖座標查詢**，
> 改以**模糊站牌搜尋**作為主要輸入方式。
> 因此上述「附近站牌」的功能目前**只保留給內部使用**——
> 判斷「同名站牌是否屬於同一個站區」時需要算距離
> （例如 340 公尺外的「干城站」不該被併入「臺中車站」）。
> 這一節的事實仍然有效，供第二階段要接回來時參考。

> 建議**不要**用 `$spatialFilter=nearby`：它每次都要打一次 API（計次 + 計量都扣點），
> 而我們記憶體裡已經有全部站牌座標。本地 Haversine 免費、更快，
> 而且使用者調整半徑時可以重複查詢而不必再打 API。

> **未確認**：TDX 沒有提供「步行距離 / 路網距離」，只有直線距離。
> 若日後要走路網距離，需要另一個服務（TDX 圖資服務或其他路徑規劃 API），第一階段不做。

---

## 4. 實測 Payload 樣本（真實資料，2026-09-12）

### 4.1 `StopOfRoute`（A→B 匹配的核心）

```jsonc
[
  {
    "RouteUID": "TXG300",
    "RouteID": "300",
    "RouteName": { "Zh_tw": "300", "En": "300" },
    "Operators": [ { "OperatorID": "1", "OperatorName": { "Zh_tw": "台中客運", "En": "Taichung Bus Co., Ltd." }, "OperatorCode": "TaichungBus", "OperatorNo": "0501" } ],
    "SubRouteUID": "TXG300",
    "SubRouteID": "300",
    "SubRouteName": { "Zh_tw": "300", "En": "300" },
    "Direction": 0,
    "City": "Taichung",
    "CityCode": "TXG",
    "Stops": [
      {
        "StopUID": "TXG13567",
        "StopID": "13567",
        "StopName": { "Zh_tw": "靜宜大學(專用道)", "En": "Providence University(Dedicated Road)" },
        "StopBoarding": 0,
        "StopSequence": 1,
        "StopPosition": { "PositionLon": 120.576539, "PositionLat": 24.225899, "GeoHash": "wsm9zf1nh" },
        "StationID": "1387",
        "StationGroupID": "1387",
        "LocationCityCode": "TXG"
      }
      // ... 依 StopSequence 遞增
    ],
    "UpdateTime": "2026-09-11T22:37:51+08:00",
    "VersionID": 11394
  }
]
```

- 陣列中**每個 `(SubRouteUID, Direction)` 是一筆獨立元素**。
- `Stops[]` **已依 `StopSequence` 排序**（可直接信任順序）。
- `StopName` / `RouteName` / `SubRouteName` / `OperatorName` 都是**本地化物件**
  `{"Zh_tw": "...", "En": "..."}`，不是字串。
- `StopBoarding`（上下車限制）的逐值定義 **[未確認]**（OAS 檔案過大未取得該段）。

### 4.2 `Route`

```jsonc
[{
  "RouteUID": "TXG1", "RouteID": "1", "HasSubRoutes": true,
  "RouteName": { "Zh_tw": "1", "En": "1" },
  "BusRouteType": 11,
  "DepartureStopNameZh": "中臺科技大學校區", "DestinationStopNameZh": "捷運四維國小站",
  "RouteMapImageUrl": "https://citybus.taichung.gov.tw/ebus/route-map/1",
  "SubRoutes": [
    { "SubRouteUID": "TXG1", "SubRouteID": "1", "Direction": 0,
      "SubRouteName": { "Zh_tw": "1", "En": "1" },
      "Headsign": "中臺科技大學校區 - 捷運四維國小站",
      "HeadsignEn": "C.T.U.S.T. Campus - MRT Sihwei Elementary School Station",
      "OperatorIDs": ["15","3"] },
    { "SubRouteUID": "TXG1", "SubRouteID": "1", "Direction": 1,
      "SubRouteName": { "Zh_tw": "1", "En": "1" },
      "Headsign": "中臺科技大學校區 - 捷運四維國小站",
      "Direction": 1 }            // ← 同一個 SubRouteUID，只有 Direction 不同
  ],
  "City": "Taichung", "CityCode": "TXG"
}]
```

### 4.3 `EstimatedTimeOfArrival`（通知的核心）

未發車的樣本（`StopStatus = 1`，**沒有 EstimateTime，只有 NextBusTime**）：

```jsonc
{
  "PlateNumb": "",
  "StopUID": "TXG12251", "StopID": "12251",
  "StopName": { "Zh_tw": "臺中車站(A月台)", "En": "Taichung Station(Platform A)" },
  "RouteUID": "TXG300", "RouteID": "300", "RouteName": { "Zh_tw": "300", "En": "300" },
  "SubRouteUID": "TXG300", "SubRouteID": "300", "SubRouteName": { "Zh_tw": "300", "En": "300" },
  "Direction": 1,
  "StopSequence": 1,
  "StopStatus": 1,
  "NextBusTime": "2026-09-12T05:46:00+08:00",
  "SrcUpdateTime": "2026-09-12T00:45:45+08:00",
  "UpdateTime": "2026-09-12T00:46:05+08:00"
}
```

營運中、有 ETA 的樣本（`StopStatus = 0`，`EstimateTime` 單位為**秒**）：

```jsonc
{
  "PlateNumb": "KKA-7868",
  "StopUID": "TXG10652", "StopName": { "Zh_tw": "逢甲大學(福星路)" },
  "RouteUID": "TXG160", "RouteID": "160", "Direction": 0, "StopSequence": 8,
  "EstimateTime": 960,                 // 960 秒 = 16 分鐘
  "StopStatus": 0,
  "NextBusTime": "2026-09-12T06:44:00+08:00",
  "Estimates": [                       // ★ 同一站同一路線可能有多班車
    { "PlateNumb": "KKA-7868", "EstimateTime": 960, "IsLastBus": false }
  ],
  "SrcUpdateTime": "2026-09-12T00:44:26+08:00",
  "UpdateTime": "2026-09-12T00:44:45+08:00"
}
```

末班車已過的樣本（`StopStatus = 3`，沒有 EstimateTime 也沒有 NextBusTime）：

```jsonc
{ "StopUID": "TXG13567", "RouteUID": "TXG303", "Direction": 0, "StopSequence": 31, "StopStatus": 3 }
```

#### `StopStatus` 語意

TDX 公車 v2 官方 OAS 的 schema 註解 **[官方文件，第一手]**：

> 「車輛狀態備註：`[0:'正常', 1:'尚未發車', 2:'交管不停靠', 3:'末班車已過', 4:'今日未營運']`」

| 值 | 意義 | 有 EstimateTime？ |
| --- | --- | --- |
| 0 | 正常 | ✅ 有值 |
| 1 | 尚未發車 | ⚠️ 多數為 null，**部分路線因有固定發車時間而有值** |
| 2 | 交管不停靠 | ❌ null |
| 3 | 末班車已過 | ❌ null |
| 4 | 今日未營運 | ❌ null |
| 5 | 其他 | 交通部《公共運輸旅運資料標準》有列此值，**但 TDX OAS 未列** → 程式要用 default 分支容錯 |

> ⚠️ **兩份官方文件不一致**：交通部資料標準列到 `5 = 其他`，
> 但 TDX 公車 v2 的 OAS schema 只列 `0~4`。
> C# 建議用 `int` 搭配 `switch` 的 default 當「未知狀態」，不要用 enum 硬轉。

#### `Direction` 語意 **[交通部資料標準]**

`0` = 去程、`1` = 返程、**`2` = 迴圈**（A1 車機定時資料另有 `255` = 未知）。
→ C# 的 `Direction` 不要用 bool，也不要做 `dir == 1` 的二分假設，用 `int`。

#### ★ `EstimateTime` 的完整官方定義（OAS schema 原文）

> 「到站時間預估(秒)
> [`StopStatus` 值為 2~4 **或 `PlateNumb` 值為 -1** 時，`EstimateTime` 值為 null；
>  `StopStatus` 值為 1 時，`EstimateTime` 值多數為 null，僅部分路線因有固定發車時間，故有值；
>  `StopStatus` 值為 0 時，`EstimateTime` 有值。]」

**四個必須知道的行為**

1. **`PlateNumb = -1` 是 sentinel**，代表「無車輛」，此時 `EstimateTime` 為 null。
   判斷時除了 `StopStatus` 也要看 `PlateNumb`。

2. **台中實測時看到的是「欄位整個消失」而非 `null`。**
   對 `System.Text.Json` 兩者結果相同（`int?` 都是 null），
   但**不要**假設欄位一定存在。

3. **官方 OAS 補充**：
   > 「[部分縣市] 當 StopStatus = 1（尚未發車）且 EstimateTime > 0（有值）的情形，屬正常情形，
   > 雖目前尚未發車，但提供 EstimateTime 值為預計多久後開始發車之時間。」

   → **不能**用「`StopStatus == 0` 才有 ETA」的簡化邏輯；
   正確條件是「`EstimateTime` 有值」。

4. **★ 最容易被忽略的一點：TDX 的 ETA 不會自動遞減，你必須自己算。**
   官方 OAS 原文：
   > 「N1 僅於該路線上有任一車輛離站時，來源端才會重新計算並發佈，
   > 因此**使用者需自行處理時間遞減機制**，或以
   > `EstimateTime - (收到資料時間 - SrcTrasTime)`（秒）作為實際預估抵達時間。」

   （`SrcTrasTime` 為官方原文錯字，實際欄位是 `SrcUpdateTime`。）

   ```
   實際預估秒數 = EstimateTime - (現在時間 - SrcUpdateTime).TotalSeconds
   ```

   這對「提前 N 分鐘通知」至關重要：若不做遞減，公車會比通知所說的更早到。

5. **`EstimateTime = 0`**：官方未明文定義。機制上倒數到約 59 秒後就不再更新
   （預估時間不能為負），實務上代表「進站中／即將到站」。**[實務推定，非官方明文]**

6. **`IsLastBus` 陷阱（官方明文）**：`EstimateTime` 為 null 且 `IsLastBus = 0` 時，
   **不能**據此斷定「不是末班車」。
   另外舊臺北 5284/IMP 協定用負值表示狀態（`-1` 未發車、`-2` 交管、`-3` 末班已過、`-4` 今日未營運），
   **TDX v2 不使用負值**，不要沿用那套邏輯。

### 4.4 `RealTimeNearStop`（車輛目前接近哪一站，A2）

```jsonc
[{
  "PlateNumb": "687-U8",
  "OperatorID": "1", "OperatorNo": "0501",
  "RouteUID": "TXG243", "RouteID": "243", "RouteName": { "Zh_tw": "243" },
  "SubRouteUID": "TXG243", "SubRouteID": "243", "SubRouteName": { "Zh_tw": "243" },
  "Direction": 0,
  "StopUID": "TXG13763", "StopID": "13763",
  "StopName": { "Zh_tw": "亞大醫院", "En": "Asia University Hospital" },
  "StopSequence": 49,          // ★ 車輛目前在第 49 站 → 可推算「還有幾站到你的上車站」
  "DutyStatus": 0, "BusStatus": 0, "A2EventType": 0,
  "GPSTime": "2026-09-12T00:24:13+08:00",
  "TripStartTimeType": 0,
  "TripStartTime": "2026-09-11T23:19:22+08:00",   // ★ 可當「班次唯一鍵」，去重最可靠
  "SrcUpdateTime": "2026-09-12T00:43:15+08:00",
  "UpdateTime": "2026-09-12T00:43:45+08:00"
}]
```

### 4.5 `RealTimeByFrequency`（車輛 GPS，A1）

```jsonc
[{
  "PlateNumb": "685-U8", "OperatorID": "1", "OperatorNo": "0501",
  "RouteUID": "TXG33", "RouteID": "33", "Direction": 1,
  "SubRouteUID": "TXG33", "SubRouteID": "33",
  "BusPosition": { "PositionLon": 120.620813, "PositionLat": 24.11213, "GeoHash": "wsmc0v3rk" },
  "Speed": 5, "Azimuth": 319,
  "DutyStatus": 0, "BusStatus": 0,
  "GPSTime": "2026-09-12T00:43:29+08:00",
  "SrcUpdateTime": "2026-09-12T00:43:30+08:00",
  "UpdateTime": "2026-09-12T00:44:00+08:00"
}]
```

#### A1 / A2 的狀態碼 **[官方文件：交通部公共運輸旅運資料標準]**

| 欄位 | 值域 |
| --- | --- |
| `DutyStatus`（勤務狀態） | `0` 正常、`1` 開始、`2` 結束 |
| `BusStatus`（車況） | `0` 正常、`1` 車禍、`2` 故障、`3` 塞車、`4` 緊急求援、`5` 加油、`98` 偏移路線、`99` 非營運狀態、`100` 客滿、`101` 包車出租、`255` 未知 |
| `A2EventType`（A2 事件類型） | `1` 到站訊息、`0` 離站訊息 |

**實務用途**：`BusStatus = 100`（客滿）與 `A2EventType` 可做進階提示
（例：「公車已離站」、「車輛故障，請改搭」）。第一版不需要，但模型先留欄位不吃虧。

### 4.6 `Stop`

```jsonc
[{
  "StopUID": "TXG12251", "StopID": "12251", "AuthorityID": "007",
  "StopName": { "Zh_tw": "臺中車站(A月台)", "En": "Taichung Station(Platform A)" },
  "StopPosition": { "PositionLon": 120.686459, "PositionLat": 24.137749, "GeoHash": "wsmc661hw" },
  "Bearing": "NE",
  "StationID": "4980", "StationGroupID": "4980",
  "City": "Taichung", "CityCode": "TXG", "LocationCityCode": "TXG",
  "UpdateTime": "2026-09-11T22:37:51+08:00", "VersionID": 11394
}]
```

### 4.7 `Alert`（營運通阻）

```jsonc
[{
  "AlertID": "403586793",
  "Title": "施工改道",
  "Description": "因辦理「清水區靈泉小區等汰換管線工程(三)」，本市97、123、…路公車將自115年9月14日起取消停靠「清水高中(學園街)(雙向)」…",
  "Department": "台中市政府公共運輸處",
  "Status": 2, "Cause": 4, "Effect": 1,
  "Scope": { "Routes": [ { "RouteID": "186", "RouteName": { "Zh_tw": "186", "En": "" }, "Direction": 0 } ] },
  "PublishTime": "2026-09-11T00:00:00+08:00",
  "StartTime": "2026-09-11T11:44:40+08:00",
  "EndTime": "2026-11-13T23:59:59+08:00",
  "UpdateTime": "2026-09-12T00:06:45+08:00"
}]
```

> `Scope.Routes[].RouteName.Zh_tw` 有時是 **`RouteID`**（`186`）有時是 **`RouteUID`**（`5003` = `500延區1`），
> 比對訂閱路線時建議兩種都比。

### 4.8 資料供應現況 **[官方文件]**

TDX「基礎資料供應現況表」中，**臺中市公車全部項目皆為 ● 已上架（自動化介接）**：
A1 車機定時、A2 車機定點、N1 預估到站、營運通阻、最新消息、站牌、站位、業者、車輛、
路線、站序、路線線形、路線簡圖、定期班表、每日班表、每日站別、首末班車、票價、站間旅行時間。

**唯一缺項：「組站位（StationGroup）」= `－`（無此項資料）** →
台中的 `StationGroupID` 恆等於 `StationID`，**不可當跨路線群組鍵**（見 §6.5）。

→ 台中公車資料在 TDX 上是**完整且即時**的，第一階段不需要接台中市自己的 Open Data。

---

## 5. MQTT 研究（最關鍵的結論）

來源：官方 MQTT 指引與範例碼 repo `https://github.com/tdxmotc/MQTTSampleCode` **[官方文件]**

### 5.1 連線與認證

| 參數 | 值 |
| --- | --- |
| Host | `mqtt.transportdata.tw` |
| Port | `8883` |
| TLS | **必須**（MQTTS）。官方 C# 範例設 `SslProtocol = Tls13`、`AllowUntrustedCertificates = false` |
| ClientId | TDX 會員中心 → 資料服務 → 資料存取金鑰 |
| Username | 同上 |
| Password | 同上 |

- **TDX 自 2026 年 5 月起開放 MQTT 介接。**
- 會員**只能訂閱（Subscribe），不能發佈（Publish）**。
- **同一組帳號/密碼/ClientId 只能建立一條連線**；嘗試建立第二條會**自動中斷第一條**。
  → Bot 若要多實例部署，必須為每個實例申請不同金鑰（每帳號上限 3 組）。
- 連線可重複使用，**不要每次訂閱就重連**。
- `CleanSession = true`（官方建議，動態資料不需要補收斷線期間的舊資料）。
- 需自行實作**斷線自動重連**（官方範例等 10 秒後重連）。
- QoS 0/1/2 可選；**QoS 0 不論是否收到都算一次使用**，QoS 1/2 以 ack 為準。
- **Payload 為 JSON，內容與 HTTP API 回傳完全一致，但以「最小單位」推送。**
- **[未確認]**：MQTT 協定版本（3.1.1 / 5.0）、keepalive、每連線最大訂閱數、MQTT 專屬 rate limit、
  port 1883 是否可用 —— 官方文件皆未載明。

### 5.2 目前提供的完整 Topic 清單 **[官方文件，2026-09 現況]**

| 資料項 | MQTT Topic |
| --- | --- |
| 縣市公車最新消息資料 | `v2/Bus/News/City/{縣市代碼}` |
| 公總公車最新消息資料 | `v2/Bus/News/InterCity` |
| 縣市公車營運通阻資料 | `v2/Bus/Alert/City/{縣市代碼}` |
| 縣市公車營運通阻資料 | `v3/Bus/Alert/City/{縣市代碼}` |
| 公總公車營運通阻資料 | `v2/Bus/Alert/InterCity` |
| 臺鐵動態營運通阻資料 | `v3/Rail/TRA/Alert` |
| 高鐵即時營運通阻資料 | `v2/Rail/THSR/AlertInfo` |
| 捷運(輕軌)營運通阻資料 | `v2/Rail/Metro/Alert/{軌道系統代碼}` |
| 航運營運通阻資料 | `v3/Ship/Alert/International` |

City 層的 `#` 通配可用（官方範例即有 `v2/Rail/Metro/Alert/#`）。

### 5.3 **MQTT 沒有**的東西（四條獨立證據）

- ❌ `v2/Bus/RealTimeByFrequency/City/{City}`（A1 車輛 GPS）
- ❌ `v2/Bus/RealTimeNearStop/City/{City}`（A2 車輛接近站牌）
- ❌ `v2/Bus/EstimatedTimeOfArrival/City/{City}`（N1 預估到站時間）
- ❌ 任何 Route / Stop / StopOfRoute / Vehicle / Station 資料 topic

**證據一（官方公告）**：TDX 2026-05-19 MQTT 上架公告原文 **[官方文件]**

> 「**優先針對公共運輸之「最新消息」與「營運通阻」推出 MQTT 服務**」
> 並說明「目前尚未納入收費範圍」。

**證據二（官網徽章）**：TDX 官網「資料服務／基礎服務」清單中，公車類**只有兩項掛 MQTT 標籤**：
「公路客運之最新消息」「公路客運之營運通阻資料」。
同頁的「動態定時資料(A1)／動態定點資料(A2)／**預估到站資料(N1)**」**沒有任何 MQTT 標籤**。

**證據三（公告時間軸）**：檢視 TDX 最新消息清單（2025-01 ~ 2026-09 全部公告），
**沒有任何 MQTT 擴大開放、新增 topic、或下架終止的公告**；MQTT 只在 2026-05-19 那則出現一次。

**證據四（台中替代管道也不通）**：TDX 的「逐筆更新（Streaming）」端點 City enum
**不含台中市**（只有公總代管的縣市）。→ 台中連串流版都沒有，只能輪詢。

**→ 結論：MQTT 完全無法支撐「公車快到站」通知。你的直覺是對的，這個必須先問清楚。**

### 5.3.1 台中市的 MQTT 訂閱字串與 payload 形狀

縣市代碼確認為 **`Taichung`**：

```
v2/Bus/Alert/City/Taichung
v3/Bus/Alert/City/Taichung
v2/Bus/News/City/Taichung
```

**Payload 形狀**：官方只保證「JSON 格式、符合運具資料標準、與 API 回傳內容一致、以最小單位推送」。
官方唯一的 payload 範例是捷運的 envelope 形狀：

```json
{ "AuthorityCode": "TRTC", "Alerts": [ { } ] }
```

**Bus 的實際 payload 範例 = [未確認]**（找不到任何真實攔截範例）。
合理推論是同性質 envelope（以城市為單位、內含 `Alerts` / `News` 陣列），
但需用會員金鑰實連攔一筆才能確認。**測試時請先 log 原始 payload 再解析。**

### 5.4 MQTT 在本專案的正確定位

| 用途 | 可行？ | 做法 |
| --- | --- | --- |
| 公車快到站通知 | ❌ | 必須用 HTTP `EstimatedTimeOfArrival` 輪詢 |
| 路線改道／停駛／減班通知 | ✅ | 訂閱 `v2/Bus/Alert/City/Taichung`（或 `…/City/#` 再用 payload 過濾） |
| 業者公告推播 | ✅ | 訂閱 `v2/Bus/News/City/Taichung` |
| 靜態資料 | ❌ | 啟動時用 HTTP 抓一次 + 記憶體快取 |

**額外好處**：MQTT 開放初期**不計點數**，而 Alert/News 若用 HTTP 輪詢反而會燒點數。
所以「MQTT 只用於 Alert/News」不只是妥協，而是**成本上最正確的選擇**。

### 5.5 官方對 MQTT 的風險提醒 **[官方文件]**

> 「開放初期仍有資料即時性、服務穩定性、使用便利性等層面的建議還有賴 TDX 會員回饋給平臺，
> 故開放初期透過 MQTT 取得的資料暫不納入點數計算，請會員在改接之前審慎評估。」

→ 不要把核心功能（到站通知）壓在 MQTT 上。

---

## 6. 「A → B 可搭路線」匹配：資料結構驗證與演算法

### 6.1 需求對照

| 你的要求 | 資料怎麼支援 |
| --- | --- |
| 去程／回程 | `StopOfRoute[].Direction`（0/1/2），每個方向一筆獨立站序 |
| SubRoute | `StopOfRoute[].SubRouteUID/SubRouteName`、`Route.SubRoutes[]` |
| 同一路線不同方向 | 見 §6.4，**必須以 Direction 區分** |
| 同名但不同 UID 的站點 | `StopUID` 每站唯一；同名站可能有多個 UID（§6.5） |
| 路線上的站點順序 | `Stops[].StopSequence`，且**陣列已排序** |
| 「雖然都經過 A、B 但方向是 B→A 不算」 | 比較同一 Direction 內 A、B 的 sequence，**要求 seqA < seqB** |

### 6.2 用你的例子實測驗證：台中車站 → 靜宜大學

#### 第一個實測案例：300 路

`GET /v2/Bus/StopOfRoute/City/Taichung/300`：

| Direction | 第 1 站 | 第 N 站 | 是否滿足「臺中車站 → 靜宜大學」 |
| --- | --- | --- | --- |
| **0** | 靜宜大學(專用道) `TXG13567` seq 1 | 臺中車站(A月台) `TXG12251` seq 26 | ❌ 方向相反 |
| **1** | 臺中車站(A月台) `TXG12251` seq 1 | 靜宜大學(專用道) `TXG21478` seq 24 | ✅ **正確方向** |

這正是你擔心的情境：**只檢查「路線是否同時包含兩站」會誤判方向**。
必須取該 Direction 的站序陣列，找出 `seq(上車站) < seq(下車站)` 者。

同一次實測也確認了預期的路線（在 靜宜大學(專用道) 這一站出現的路線）：
**300、302、303、304、305、…** 皆經過該站；其中 300、304 等同時服務臺中車站。

#### 第二個實測案例：304 路（Direction 1）

`GET /v2/Bus/StopOfRoute/City/Taichung/304?$filter=Direction eq 1`：

| Sequence | 站名 | StopUID |
| --- | --- | --- |
| 1 | 新民高中(健行路) | TXG13554 |
| 9 | 干城站 | TXG12769 |
| **10** | **臺中車站(臺灣大道)** | **TXG11020** |
| 11 | 第一廣場 | TXG17811 |
| … | … | … |
| **35** | **靜宜大學(專用道)** | **TXG19438** |
| 66 | 港區藝術中心(鎮政路)（終點） | TXG19197 |

→ 304 路 Direction 1 **確實**滿足「臺中車站 → 靜宜大學」（seq 10 < seq 35）。

另外注意：304 路 Direction 1 的終點是「港區藝術中心(鎮政路)」，
但使用者的目的地「靜宜大學(專用道)」在第 35 站（**中途站**）。
**→ 不能只用「路線終點名稱」來判斷能不能到，必須用站序。**

### 6.3 ⚠️ 關鍵陷阱二：「臺中車站」不是一個站，是好幾個不同名的站

把兩個案例放在一起看，就會發現一個**很容易做錯的地方**：

| 路線 | 方向 | 使用者心中的「臺中車站」實際上是 | StopUID | seq |
| --- | --- | --- | --- | --- |
| **300** | 1 | 臺中車站**(A月台)** | TXG12251 | 1 |
| **304** | 1 | 臺中車站**(臺灣大道)** | TXG11020 | 10 |

**如果站點比對用「站名完全相等」，使用者選了「臺中車站(A月台)」就會找不到 304 路。**

反過來說，實測也確認：
- 你預期的 300 / 304 / 308 這批路線，**正是靠「臺中車站」這個站區的不同月台**各自發車的。
- 同一個站區還有 干城站（TXG12769，距離臺中車站(A月台)約 340 公尺）
  是**獨立**的一站，不該被合併進來。

**→ 站點分組的正確規則必須是「站名前綴正規化 + 座標叢集」，不能只用距離、
也不能只用完全相等的站名。** 詳見 §6.5。

### 6.4 ⚠️ 關鍵陷阱一：SubRouteUID 不保證方向唯一（台中實測）

**[實測]** 台中路線 `300`：

```
Direction 0 → SubRouteUID = "TXG300"
Direction 1 → SubRouteUID = "TXG300"     ← 相同！
```

路線 `1` 同樣是：

```jsonc
SubRoutes[0] = { "SubRouteUID": "TXG1", "Direction": 0 }
SubRoutes[1] = { "SubRouteUID": "TXG1", "Direction": 1 }
```

**→ 不能只用 `SubRouteUID` 當唯一鍵，必須用 `(RouteUID, Direction)` 或 `(SubRouteUID, Direction)`。**

這個發現直接對應你要求中的「同一路線不同方向」與「不要誤判方向」——
如果照一般教學把 SubRouteUID 當主鍵，台中資料會直接把去回程混在一起。

### 6.5 同名站點的不同 UID，與「站區分組」規則（實測）

以 300 路為例：

| 站名 | Direction | StopUID | StopID | StationID | 座標 |
| --- | --- | --- | --- | --- | --- |
| 靜宜大學(專用道) | 0 | `TXG13567` | 13567 | 1387 | 120.576539, 24.225899 |
| 靜宜大學(專用道) | 1 | `TXG21478` | 21478 | 1388 | 120.577203, 24.225322 |
| 臺中車站(A月台) | 0（終點） | `TXG12251` | 12251 | 4980 | 120.686459, 24.137749 |
| 臺中車站(A月台) | 1（起點） | `TXG12251` | 12251 | 4980 | 同上（雙向共用） |

再加上同樣叫「靜宜大學」的不同站位：`TXG11711`（靜宜大學）、`TXG15230`（靜宜大學靜園餐廳）。
以及 §6.3 揭露的關鍵：「臺中車站」實際上是 `TXG12251`（A月台）**和** `TXG11020`（臺灣大道）兩個不同名的站。

#### 站區分組（Station Area Group）規則

**不能用「站名完全相等」**（會漏掉 304 路），
**也不能只用座標距離**（會把 340 公尺外的「干城站」錯誤併入臺中車站）。

建議規則（兩段式）：

```
1) 站名正規化：取第一個 '(' 或 '（' 之前的部分並去除空白
     "臺中車站(A月台)"     → "臺中車站"
     "臺中車站(臺灣大道)"  → "臺中車站"
     "靜宜大學(專用道)"    → "靜宜大學"
     "靜宜大學靜園餐廳"    → "靜宜大學靜園餐廳"   ← 不含括號，保持獨立

2) 同一正規化名稱內，再依座標做貪婪叢集（門檻約 500 公尺）
     同名但位在不同行政區的站（例：各區都有的「中正路」）會被拆成不同群組
```

**每個群組的最終樣貌**

| 群組顯示名稱 | 內含 StopUID | 說明 |
| --- | --- | --- |
| 臺中車站 | TXG12251(A月台)、TXG11020(臺灣大道)、… | 300 走 A月台、304 走臺灣大道 → **兩條都會被搜出來** |
| 靜宜大學 | TXG13567、TXG21478、TXG11711、… | 去回程不同月台自動涵蓋 |
| 干城站 | TXG12769 | 獨立，不與臺中車站合併 |

**`StationID` 不能拿來當群組鍵**：
- 台中「組站位（StationGroup）」在官方供應現況表是「－」（無此項資料），
  `StationGroupID` 恆等於 `StationID`；
- 實測 `靜宜大學(專用道)` 去回程的 `StationID` 分別是 `1387` / `1388`；
- 而 300 路與 304 路的臺中車站分別是 `4980` / `720`。

`StationID` 是「站位」（道路同側聚合）層級，比使用者心中的「站」更細。
**→ 使用者選站一律用上面兩段式產生的 `StopGroup`；`StationID` 只在需要更細比對時當輔助鍵。**

**結論**：
- 使用者看到與選擇的是一個**站區群組**，不是單一 StopUID。
- Bot 內部對每條候選路線，**在該方向的站序中，取群組內實際出現的 StopUID** 來計算
  sequence 並存進訂閱，**不要**讓使用者選 UID。
- 回傳結果時要**顯示該路線實際的上下車站名**：
  「300 在 臺中車站(A月台) 上車 / 304 在 臺中車站(臺灣大道) 上車」——
  這對使用者是關鍵資訊（不同月台位置不同），也是分組設計的必要配套。

### 6.6 匹配演算法（給實作參考）

```
輸入：fromGroup（起點站區群組）、toGroup（終點站區群組）

候選集合 = stopNameIndex[fromGroup].路線方向鍵 ∩ stopNameIndex[toGroup].路線方向鍵
          路線方向鍵 = (RouteUID, Direction)   ← 用 RouteUID 而非 SubRouteUID（§6.4）

對每個候選 (routeUid, dir)：
    seqs = stopOfRoute[routeUid][dir].Stops      // 已依 StopSequence 排序
    iFrom = min{ seq | stop ∈ seqs 且 stop.StopUID ∈ fromGroup.StopUids }
    iTo   = max{ seq | stop ∈ seqs 且 stop.StopUID ∈ toGroup.StopUids }
    若 iFrom < iTo  →  符合，回傳：
         RouteName、方向、Headsign（終點名）、
         上車站（實際 StopUID/站名/seq）、下車站（同上）、中間站數 = iTo - iFrom
```

**效能**：候選集合由倒排索引取得，台中約 340~400 條路線 / 約 750~765 個方向，
單次查詢是微秒等級的記憶體運算，**完全不需要打 TDX API**。

**邊界情況**
- 路線可能環狀繞行、同一站名出現兩次 → 取 `min(seq)` 當上車、`max(seq)` 當下車。
- 環狀路線（`Direction = 2`）可能重複走訪同一站，需以「索引對」而非站名判斷。
- 使用者可能選到「同一站名但在不同行政區」的兩個站 → 站區群組必須納入座標叢集。
- 若 `fromGroup == toGroup` → 直接擋掉（或視為「同站來回」不支援）。
- `StopBoarding` 可能有上下車方向限制 → **[未確認]**，實作時需實測。

---

## 7. 即時資料：為什麼是 HTTP 輪詢，以及怎麼輪詢才省錢

### 7.1 可用的即時資料（台中全部 ● 已上架）

| 資料 | 來源端更新頻率 **[官方文件]** | 內容 | 適合做什麼 |
| --- | --- | --- | --- |
| **ETA (N1)** | 台中約 **20 秒** | 「某路線某方向在某站的**預估到站秒數**」 | ✅ **通知的觸發來源（首選）** |
| RealTimeNearStop (A2) | 台中約 **30 秒** | 車輛**目前所在站牌** + `TripStartTime` | 補「還有 N 站」、精準班次去重 |
| RealTimeByFrequency (A1) | 台中約 **15 秒** | 車輛 GPS 座標 | 地圖顯示（第一階段不需要） |
| 靜態資料 | 平台每 **4 小時**向來源抓取一次，無異動不換版 | Route / Stop / StopOfRoute | 啟動時抓一次 + 每日重載 |

> 官方註明：「**臺中市與高雄市延遲較長，因來源端本身有 Cache 機制**」。
> 並且 **N1 只在「有車輛離站」時才重算**，所以 `EstimateTime` 在兩次發布之間是靜止的
> —— **必須自行做時間遞減**（見 §4.3 第 4 點）。

### 7.2 省成本的關鍵：一次呼叫，全體共用 **[實測可行]**

ETA 端點支援以 **StopUID 集合**過濾：

```
GET /v2/Bus/EstimatedTimeOfArrival/City/Taichung
    ?$filter=StopUID eq 'TXG12251' or StopUID eq 'TXG13567'
    &$select=RouteUID,Direction,StopUID,StopSequence,EstimateTime,StopStatus,PlateNumb,SrcUpdateTime,Estimates
    &$format=JSON
```

**[實測]** 回應只包含這兩個站的所有路線/方向的 ETA 記錄（每筆都很小，約 400 bytes）。

→ **這完全符合你的效能要求**：

```
所有訂閱的「上車站 UID」去重
        ↓
   組成一個 $filter 字串（分批，每批 ~40 站）
        ↓
   一次（或少數幾次）HTTP 呼叫
        ↓
   RealtimeBusCache：Dictionary<(RouteUID, Direction, StopUID), EtaRecord>
        ↓
   Subscription Matcher  ── User A / User B / User C / …
```

100 個使用者關注 300 路臺中車站，仍然只有 **1 次**呼叫。
**成本與使用者數量無關，只與「不同的上車站數量」有關。**

### 7.3 頻率規劃建議

| 方案 | 輪詢間隔 | 每分鐘呼叫 | 每月點數（估） | 適用 |
| --- | --- | --- | --- | --- |
| 保守 | 60 秒 | 1 | ≈ 38 點 | 銅級 |
| **建議** | **30 秒** | **2** | **≈ 76 點** | 銅級 |
| 積極 | 20 秒 | 3 | ≈ 110 點 | 銅級 |
| 無意義 | < 20 秒 | — | — | ❌ N1 來源端約 20 秒才更新 |

搭配「智慧輪詢」可再省：**沒有任何啟用中的訂閱時完全停止輪詢**；
只有即將到站（有訂閱進入 notifyBefore 視窗）時才提高頻率。

---

## 8. 功能對照表：每項需求能不能做到

| 需求 | 資料來源 | 可行性 |
| --- | --- | --- |
| 選起點／終點（站名搜尋） | `Stop` + `$filter=contains(StopName/Zh_tw,…)` 或全量快取 | ✅ |
| 找出 A→B 可搭路線 | `StopOfRoute`（Direction + StopSequence） | ✅ 已實測驗證 |
| 正確處理去回程／方向 | `Direction` + sequence 比較 | ✅ |
| 處理同名不同 UID | 站區群組 + 座標叢集 | ✅（需自建索引） |
| 處理同站區不同站名 | 站名前綴正規化 | ✅（需自建索引，**易漏**） |
| 處理 SubRoute | `SubRoutes[]` + `SubRouteUID`；**不可當唯一鍵** | ✅（需注意 §6.4） |
| 訂閱路線 | 純記憶體 | ✅ |
| 取得即時公車資料 | ETA（HTTP）；MQTT ❌ | ✅ 但**必須輪詢** |
| 判斷「快到上車站」 | `EstimateTime`（秒）+ 自行遞減 | ✅ |
| 不重複通知同一班車 | `PlateNumb`（ETA）/ `TripStartTime`（A2） | ✅ |
| 路線改道公告通知 | MQTT `v2/Bus/Alert/City/Taichung` | ✅（加分） |
| **MQTT 取得 ETA** | — | ❌ **不提供** |

---

## 9. 需要你決定 / 仍需確認的事項

### 9.1 已在本研究中確認（原本的疑問都已解決）

| 原本的問題 | 結論 |
| --- | --- |
| MQTT 有提供 ETA 嗎？ | **沒有**。Bus 只有 Alert / News（4 條獨立證據） |
| 台中在 MQTT 的代碼？ | **`Taichung`** |
| `SubRoute` 端點存在嗎？ | **v2 不存在（404）**；v3 只有 Tainan。子路線資料內含於 `Route.SubRoutes[]` |
| 點數怎麼算？ | **公車 v2/v3：1,500 次/點 + 150 MB/點，兩者合併扣抵**（官方 OAS 明文） |
| 免費會員夠用嗎？ | 3 點/月 ≈ 4,500 次呼叫，**夠開發測試，不夠正式運行** |
| 匿名呼叫可以嗎？ | 那是「訪客模式」：**每 IP 每日 20 次**，只能當應急驗證 |
| `StopStatus` 各值意義？ | 0 正常／1 尚未發車／2 交管不停靠／3 末班車已過／4 今日未營運（標準另有 5 其他） |
| 要不要分頁？ | **不用**。StopOfRoute ≈ 751~765 筆、Stop > 5,000 筆，皆可單次取回 |
| 台中可以用 Streaming 嗎？ | **不行**，City enum 不含台中 |

### 9.2 仍需你決定

1. **TDX 方案**：開發期用免費基礎會員（3 點/月），正式上線用銅級（NT$200/月，估算約需 76 點/月）。
2. **輪詢間隔**：建議 30 秒。台中 N1 來源端約 **20 秒**才更新，低於 20 秒沒有意義。
3. **通知管道**：私訊（DM）還是頻道？（Discord 官方不鼓勵大量主動私訊）
4. **提前通知選項**：5/10/15/30 分鐘是否足夠？

### 9.3 仍未確認（實作時需自行驗證）

1. **MQTT Bus topic 的實際 payload envelope 形狀** —— 官方只給捷運範例，Bus 的抓不到。
   **第一次連線時請先 log 原始 payload 再解析。**
2. **MQTT 協定版本（3.1.1 / 5.0）、keepalive、每連線最大訂閱數、MQTT 專屬 rate limit** —— 官方未載明。
3. **`StopBoarding` 的逐值定義** —— TDX OAS 檔案過大未取得該段；舊站牌資料的 `pgp`
   （`-1` 可下車／`0` 可上下車／`1` 可上車）可作參考但非同一欄位。
   若要做「上車站/下車站」方向限制，需要實測。
4. **`EstimateTime = 0` 的官方明文定義** —— 實務推定為「進站中」。
5. **官網【資料服務定價】的逐項換算表** —— 頁面是圖片無法讀取；
   「公車 v2/v3 = 1500 次/點 + 150MB/點」則是官方 OAS 明文。
6. **台中市自有開放資料平台**（`opendata.taichung.gov.tw`）是否有 TDX 未涵蓋的動態欄位
   —— 目前看起來沒有（其資料集備註本身就指向 TDX）。
7. **MQTT broker 是否能實際連線** —— 本次研究環境無對外網路，無法對 `:8883` 做 TCP/TLS 實測。
