# ─────────────────────────────────────────────────────────────
#  TcBusBot — Docker 映像（部署到 Render 這類容器平台）
#
#  為什麼需要容器：Render 的免費層只提供「Web Service」，
#  它要求服務監聽一個 HTTP 埠。本專案的 TcBusBot.Discord 內建了健康檢查端點
#  （HealthEndpoint.cs，用 BCL 的 HttpListener 寫的，不需要 ASP.NET Core），
#  所以同一個程式就能同時當 Discord Bot 與 Web Service。
#
#  ⚠️ ENTRYPOINT 是 tcbus-bot.dll，不是 TcBusBot.Discord.dll：
#     專案的 <AssemblyName> 是 tcbus-bot，寫錯的話容器會直接
#     「Could not find TcBusBot.Discord.dll」起不來。
#
#  本機測試：
#     docker build -f src/TcBusBot.Discord/Dockerfile -t tcbusbot .
#     docker run --rm -p 8080:8080 -e DISCORD_TOKEN=xxx tcbusbot
#     curl http://localhost:8080/health
# ─────────────────────────────────────────────────────────────

# ── 執行階段 ───────────────────────────────────────────────
# 用 runtime 而不是 aspnet：本專案沒有用 ASP.NET Core（健康檢查是自己寫的
# HttpListener），不需要那個比較大的映像。
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base

# .NET 8 官方映像內建 $APP_UID（非 root 使用者）—— 不要用 root 跑
USER $APP_UID
WORKDIR /app

# Render 會用 PORT 指定埠（程式會讀 PORT，預設 8080）
EXPOSE 8080

# ⚠️ 容器裡的 /app 屬於 root，而我們用非 root 身分執行 —— 所以「預設的相對路徑」
#    （tcbus.db、cache/）是**寫不進去的**。不指到 /tmp 的話，訂閱組會一路退回
#    記憶體模式，而且只會看到一行警告。
#    ⚠️ 而且容器檔案系統是暫時的：重新部署就清空。要真的留住訂閱組請設 TCBUS_MONGO。
ENV TCBUS_DB=/tmp/tcbus.db
ENV TCBUS_CACHE=/tmp/tcbus-cache

# ── 建置階段 ───────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# 先只複製 csproj 再 restore：改程式碼時可以重用這一層的快取
COPY ["src/TcBusBot.Discord/TcBusBot.Discord.csproj", "src/TcBusBot.Discord/"]
COPY ["src/TcBusBot.Core/TcBusBot.Core.csproj", "src/TcBusBot.Core/"]
RUN dotnet restore "src/TcBusBot.Discord/TcBusBot.Discord.csproj"

COPY . .

WORKDIR "/src/src/TcBusBot.Discord"
RUN dotnet build "./TcBusBot.Discord.csproj" -c $BUILD_CONFIGURATION -o /app/build

# ── 發佈階段 ───────────────────────────────────────────────
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./TcBusBot.Discord.csproj" \
        -c $BUILD_CONFIGURATION \
        -o /app/publish \
        -p:UseAppHost=false

# ── 最終映像 ───────────────────────────────────────────────
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# 內建的最小離線資料集：沒有 TDX 金鑰時用得到。
# Program.cs 會從工作目錄往上找 tests/fixtures，放在 /app/tests/fixtures 就找得到。
COPY --from=build /src/tests/fixtures ./tests/fixtures

ENTRYPOINT ["dotnet", "tcbus-bot.dll"]
