# syntax=docker/dockerfile:1

# Publish the Headless Hub without invalidating restore on unrelated source changes.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS headless-publish
WORKDIR /src

COPY Directory.Build.props ./
COPY shared/Heartbeat.Core/Heartbeat.Core.csproj shared/Heartbeat.Core/
COPY collection/hub/Heartbeat.Collection.Hub/Heartbeat.Collection.Hub.csproj collection/hub/Heartbeat.Collection.Hub/
COPY collection/hub/Heartbeat.Collection.Headless/Heartbeat.Collection.Headless.csproj collection/hub/Heartbeat.Collection.Headless/
COPY collection/contracts/segment-rotation-policy.json collection/contracts/
RUN --mount=type=cache,id=heartbeat-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore collection/hub/Heartbeat.Collection.Headless/Heartbeat.Collection.Headless.csproj

COPY shared/Heartbeat.Core/ shared/Heartbeat.Core/
COPY collection/hub/Heartbeat.Collection.Hub/ collection/hub/Heartbeat.Collection.Hub/
COPY collection/hub/Heartbeat.Collection.Headless/ collection/hub/Heartbeat.Collection.Headless/
# Cache mounts can be evicted independently of the restore layer. Let publish
# restore missing packages before compiling, even when that layer was cached.
RUN --mount=type=cache,id=heartbeat-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish collection/hub/Heartbeat.Collection.Headless/Heartbeat.Collection.Headless.csproj \
    -c Release -o /out/headless /p:UseAppHost=false

# Run the Headless Hub on the ASP.NET runtime-only image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS headless-runtime
WORKDIR /app
COPY --from=headless-publish /out/headless/ ./
VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "Heartbeat.Collection.Headless.dll", "/app/heartbeat-headless.json"]
