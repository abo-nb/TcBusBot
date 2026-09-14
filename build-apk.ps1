<#
.SYNOPSIS
  建置 Android APK（手機版），可選擇直接安裝到接上的手機。

.DESCRIPTION
  這個腳本把「一堆容易忘的建置參數」固定下來：
    * AndroidLinkMode=None / PublishTrimmed=false / RunAOTCompilation=false
      → Discord.Net 大量用反射，裁剪後會在手機上執行期才炸；
        而且這個環境的 ILLink 任務宿主跑不起來（MSB4216）。代價是 APK 較大。
    * SelfContained=false → 讓 Android App 可以參考一般的 Exe 專案（否則 NETSDK1150）
    * RuntimeIdentifiers=android-arm64;android-arm → 舊手機可能是 32 位元
    * AndroidSdkDirectory / JavaSdkDirectory → 不依賴環境變數

.EXAMPLE
  .\build-apk.ps1
  .\build-apk.ps1 -Install
  .\build-apk.ps1 -Install -StopExisting    # 先停掉手機上正在跑的服務再裝
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Install,
    [switch]$StopExisting,

    [string]$AndroidSdk = $(if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { 'E:\SDK\android\android-sdk' }),
    [string]$JavaSdk    = $(if ($env:JAVA_HOME)       { $env:JAVA_HOME }       else { 'E:\SDK\android\jdk' }),
    [string]$Adb        = $(if ($env:ADB)             { $env:ADB }             else { 'E:\softwaer\platform-tools\adb.exe' })
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\TcBusBot.Mobile\TcBusBot.Mobile.csproj'

Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  建置 TcBusBot 手機版 APK（Android 7.0 / API 24 以上）" -ForegroundColor Cyan
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Cyan

if (-not (Test-Path $AndroidSdk)) { throw "找不到 Android SDK：$AndroidSdk（用 -AndroidSdk 指定）" }
if (-not (Test-Path $JavaSdk))    { throw "找不到 JDK：$JavaSdk（用 -JavaSdk 指定）" }

Write-Host "  Android SDK : $AndroidSdk"
Write-Host "  JDK         : $JavaSdk"
Write-Host "  組態        : $Configuration"
Write-Host ""

if ($StopExisting -and (Test-Path $Adb)) {
    Write-Host "→ 先停掉手機上正在跑的服務…" -ForegroundColor Yellow
    & $Adb shell am force-stop com.tcbusbot.mobile 2>$null | Out-Null
}

Write-Host "→ 建置中（第一次會比較久）…" -ForegroundColor Yellow
# -m:1：這個環境的多節點 MSBuild 會安靜地失敗（0 errors 但 exit 1）
dotnet build $project `
    -f net10.0-android `
    -c $Configuration `
    -m:1 `
    -p:AndroidSdkDirectory=$AndroidSdk `
    -p:JavaSdkDirectory=$JavaSdk `
    -p:AndroidLinkMode=None `
    -p:PublishTrimmed=false `
    -p:RunAOTCompilation=false

if ($LASTEXITCODE -ne 0) { throw "建置失敗（exit $LASTEXITCODE）" }

$outDir = Join-Path $root "src\TcBusBot.Mobile\bin\$Configuration\net10.0-android"
$apk = Get-ChildItem $outDir -Filter '*-Signed.apk' | Select-Object -First 1
if (-not $apk) { $apk = Get-ChildItem $outDir -Filter '*.apk' | Select-Object -First 1 }
if (-not $apk) { throw "找不到 APK，請檢查 $outDir" }

# 放到一個好找的地方
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$target = Join-Path $dist 'tcbusbot-mobile.apk'
Copy-Item $apk.FullName $target -Force

Write-Host ""
Write-Host "✅ 建置完成" -ForegroundColor Green
Write-Host "   APK：$target（$([math]::Round((Get-Item $target).Length / 1MB, 1)) MB）"

$signed = $apk.Name -like '*-Signed.apk'
Write-Host $(if ($signed) {
    "   已用 debug keystore 簽署 → 可以直接安裝（自用足夠）"
} else {
    "   ⚠️ 這是未簽署的 APK，要先簽署才能安裝"
})

if ($Install) {
    if (-not (Test-Path $Adb)) { throw "找不到 adb：$Adb（用 -Adb 指定）" }

    $devices = & $Adb devices | Select-String -Pattern 'device$'
    if (-not $devices) {
        throw "沒有偵測到手機。請開啟「開發者選項 → USB 偵錯」，插上 USB 後再試。"
    }

    Write-Host ""
    Write-Host "→ 安裝到手機…" -ForegroundColor Yellow
    & $Adb install -r $target
    if ($LASTEXITCODE -ne 0) { throw "安裝失敗（exit $LASTEXITCODE）" }

    Write-Host "✅ 已安裝。在手機上開啟「台中公車通知」→ 填 Token → 儲存設定 → 啟動服務" -ForegroundColor Green
    Write-Host ""
    Write-Host "   建議同時做這兩件事（不然手機睡著後會停止輪詢）：" -ForegroundColor Yellow
    Write-Host "     1. 進 App 按「🔋 關閉電池最佳化」"
    Write-Host "     2. 設定 → 應用程式 → 台中公車通知 → 電池 → 不受限制"
    Write-Host ""
    Write-Host "   看即時日誌： adb logcat -s TcBusBot:I"
}
