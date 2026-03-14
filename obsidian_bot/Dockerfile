FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY obsidian_bot/ObsidianBot.csproj obsidian_bot/
RUN dotnet restore obsidian_bot/ObsidianBot.csproj
COPY obsidian_bot/ obsidian_bot/
RUN dotnet publish obsidian_bot/ObsidianBot.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "ObsidianBot.dll"]
