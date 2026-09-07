# syntax=docker/dockerfile:1

# Publish the Analytics application.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS analytics-publish
WORKDIR /src

COPY Directory.Build.props ./
COPY shared/Heartbeat.Core/Heartbeat.Core.csproj shared/Heartbeat.Core/
COPY server/Heartbeat.Server/Heartbeat.Server.csproj server/Heartbeat.Server/
RUN --mount=type=cache,id=heartbeat-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore server/Heartbeat.Server/Heartbeat.Server.csproj

COPY shared/ shared/
COPY collection/contracts/segment-rotation-policy.json collection/contracts/
COPY server/Heartbeat.Server/ server/Heartbeat.Server/
# Cache mounts can be evicted independently of the restore layer. Let publish
# restore missing packages before compiling, even when that layer was cached.
RUN --mount=type=cache,id=heartbeat-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish server/Heartbeat.Server/Heartbeat.Server.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# Run Analytics on the ASP.NET runtime-only image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS analytics-runtime
WORKDIR /app

# Kestrel listens on 8080 inside the container
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=analytics-publish /app/publish .

ENTRYPOINT ["dotnet", "Heartbeat.Server.dll"]
