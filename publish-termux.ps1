<#
.SYNOPSIS
  發佈「手機也能跑」的建置（Termux／Linux ARM64），不需要 APK。

.DESCRIPTION
  產出的是**可攜式**（framework-dependent、不指定 RID）的建置：
  在手機上用 Termux 裝 .NET 執行階段之後，直接
      dotnet tcbus-bot.dll --env ~/tcbus
  就跑起來 —— 有控制台、有完整日誌，跟桌面版是同一個程式。

  為什麼不指定 RID：Termux 的 .NET 是針對 Android(bionic) 重新打包的，
  官方 linux-arm64 的 apphost／runtime pack 反而對不上。
  不指定 RID 的可攜式建置在任何有 .NET 8 執行階段的環境都能跑。

.EXAMPLE
  .\publish-termux.ps1
  .\publish-termux.ps1 -Zip          # 另外打包成 zip，方便傳到手機
#>
[CmdletBinding()]
param(
    [string]$Output = 'dist\termux',
    [switch]$Zip,
    [switch]$SelfTest                  # 發佈後順便在本機跑一次（確認產物完整）
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$outDir = Join-Path $root $Output

Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  發佈 Termux／Linux 版（手機上用控制台跑，不需要 APK）" -ForegroundColor Cyan
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Cyan

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

# UseAppHost=false：不要產生 Windows 的 .exe，手機上用 `dotnet tcbus-bot.dll` 啟動
dotnet publish (Join-Path $root 'src\TcBusBot.Discord\TcBusBot.Discord.csproj') `
    -c Release `
    -m:1 `
    -o $outDir `
    -p:UseAppHost=false

if ($LASTEXITCODE -ne 0) { throw "發佈失敗（exit $LASTEXITCODE）" }

# 把「最小離線資料集」一起帶上：手機上沒有 TDX 金鑰時仍然能用
$fixtures = Join-Path $outDir 'fixtures\mini'
New-Item -ItemType Directory -Force -Path $fixtures | Out-Null
Copy-Item (Join-Path $root 'tests\fixtures\mini\Stops.psv') $fixtures -Force
Copy-Item (Join-Path $root 'tests\fixtures\mini\StopOfRoute.psv') $fixtures -Force

# 手機上的啟動腳本
$runScript = @'
#!/data/data/com.termux/files/usr/bin/sh
# TcBusBot：在 Termux 上跑（先把這個資料夾放到 ~/tcbus）
#
# 第一次使用：
#   pkg update && pkg install dotnet-runtime-8.0 dotnet-host-8.0
#   （或直接安裝整合包：pkg install dotnet8.0）
#
# 然後：
#   cd ~/tcbus && sh run.sh
#
# 想讓它在背景一直跑：
#   termux-wake-lock
#   nohup dotnet tcbus-bot.dll --env ~/tcbus > log.txt 2>&1 &

set -e
cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "找不到 dotnet。請先安裝："
  echo "  pkg update && pkg install dotnet-runtime-8.0 dotnet-host-8.0"
  exit 1
fi

# 沒有設定檔就先複製一份範本
if [ ! -f app.env ]; then
  cp app.env.example app.env 2>/dev/null || true
  echo "已建立 app.env，請填入 DISCORD_TOKEN 後再執行一次。"
  exit 1
fi

# 訂閱組：有 MongoDB 連線字串就用（TCBUS_MONGO=...），否則用本機的 SQLite／文字檔
exec dotnet tcbus-bot.dll --env "$PWD" --db "$PWD/tcbus.db" --cache "$PWD/cache" \
     --fixtures "$PWD/fixtures"
'@
Set-Content -Path (Join-Path $outDir 'run.sh') -Value $runScript -Encoding UTF8 -NoNewline

$envExample = @'
# TcBusBot 設定（Termux 版；格式與桌面的 .env 完全相同）
DISCORD_TOKEN=

# TDX 金鑰（可留空：會用內建的離線資料集，但沒有真實到站時間）
TDX_CLIENT_ID=
TDX_CLIENT_SECRET=

# 訂閱組要放哪裡：
#   留白 = 本機檔案（SQLite，沒有的話自動用 saved_groups.txt）
#   填了 = 存到 MongoDB（手機／電腦／雲端共用同一份，例：mongodb+srv://...）
TCBUS_MONGO=

TCBUS_POLL_INTERVAL=30
TCBUS_NOTIFY_MINUTES=10
'@
Set-Content -Path (Join-Path $outDir 'app.env.example') -Value $envExample -Encoding UTF8

$readme = @'
# 在手機上跑（Termux，不用 APK）

舊手機 ＋ Termux ＋ 這個資料夾 = 一台小小的公車通知主機。
跟桌面版是**同一個程式**（同一份 Program / 指令 / 輪詢 / 訂閱組）。

## 1. 安裝 Termux 與 .NET

Termux 從 F-Droid 或 GitHub 安裝（**不要用 Google Play 的舊版**）。
打開 Termux 後：

```sh
pkg update
pkg install dotnet-runtime-8.0 dotnet-host-8.0
# 或一行裝到好（含 SDK，比較大）：pkg install dotnet8.0
dotnet --info        # 確認裝好了
```

> 這台手機是 arm64（Android 14）。Termux 目前有 .NET 8 / 9 / 10 的套件。

## 2. 把這個資料夾放進手機

用 USB、`adb push`、或 Termux 的 `termux-setup-storage`（之後檔案在 `~/storage/shared`）。

```sh
# 電腦端（在 dist 目錄）
adb push termux /sdcard/tcbus
```

```sh
# 手機端（Termux）
termux-setup-storage
cp -r ~/storage/shared/tcbus ~/tcbus
cd ~/tcbus
```

## 3. 填設定

```sh
cp app.env.example app.env
vi app.env      # 至少填 DISCORD_TOKEN
```

## 4. 跑起來

```sh
sh run.sh
```

看到 `Bot 已上線` 就成功了。想讓它一直在背景跑：

```sh
termux-wake-lock                                   # 避免 CPU 睡著（重要）
nohup dotnet tcbus-bot.dll --env ~/tcbus > log.txt 2>&1 &
tail -f log.txt
```

## 5. 建議設定（不然手機會把它殺掉）

* `termux-wake-lock`（上面那行）
* 系統設定 → 應用程式 → Termux → 電池 → **不受限制**
* 插著電

## 常見問題

| 症狀 | 原因／處理 |
| --- | --- |
| `dotnet: not found` | 還沒裝執行階段：`pkg install dotnet-runtime-8.0 dotnet-host-8.0` |
| 中文搜尋打簡體找不到 | Termux 沒有 Windows 的簡繁轉換表，會退回內建常用字表（覆蓋常見字） |
| 訂閱組想跨機器共用 | 在 `app.env` 填 `TCBUS_MONGO=mongodb+srv://...` |
'@
Set-Content -Path (Join-Path $outDir 'README-手機.md') -Value $readme -Encoding UTF8

if ($SelfTest) {
    Write-Host ""
    Write-Host "→ 在本機驗證這個發佈產物（--dryrun）…" -ForegroundColor Yellow
    Push-Location $outDir
    dotnet tcbus-bot.dll --dryrun *> $null
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { throw "發佈產物跑不起來（--dryrun exit $code）" }
    Write-Host "  ✅ 發佈產物可執行" -ForegroundColor Green
}

$size = [math]::Round(((Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ""
Write-Host "✅ 已發佈到 $outDir（$size MB）" -ForegroundColor Green
Write-Host "   手機上的安裝步驟見 $Output\README-手機.md"

if ($Zip) {
    $zipPath = Join-Path $root 'dist\tcbus-termux.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath
    Write-Host "   已打包：dist\tcbus-termux.zip（$([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB）"
}
