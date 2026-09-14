FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/TcBusBot.Cli/TcBusBot.Cli.csproj", "src/TcBusBot.Cli/"]
COPY ["src/TcBusBot.Core/TcBusBot.Core.csproj", "src/TcBusBot.Core/"]
RUN dotnet restore "src/TcBusBot.Cli/TcBusBot.Cli.csproj"
COPY . .
WORKDIR "/src/src/TcBusBot.Cli"
RUN dotnet build "./TcBusBot.Cli.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./TcBusBot.Cli.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "tcbus.dll"]
