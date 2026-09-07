# syntax=docker/dockerfile:1

# VRChat Collector Package 的构建入口，构建上下文是仓库根。
#
# Package 里的 artifact selector 由 --create-package 运行时的 OS/arch 决定，所以 Headless
# 容器要用的 Package 必须在 linux 容器里产出，而不是在 macOS/Windows 宿主上直接构建。
# 用法见 scripts/build-vrchat-package.sh / .ps1。

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS package-build
WORKDIR /src

COPY Directory.Build.props ./
COPY shared/Heartbeat.Core/Heartbeat.Core.csproj shared/Heartbeat.Core/
COPY collection/protocol/Heartbeat.Collection.CollectorProtocol/Heartbeat.Collection.CollectorProtocol.csproj collection/protocol/Heartbeat.Collection.CollectorProtocol/
COPY collection/collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj collection/collectors/Heartbeat.Collector.VRChat/
COPY collection/contracts/segment-rotation-policy.json collection/contracts/
COPY collection/contracts/facts/vrchat-presence-segment.schema.json collection/contracts/facts/
RUN --mount=type=cache,id=heartbeat-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore collection/collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj

COPY shared/Heartbeat.Core/ shared/Heartbeat.Core/
COPY collection/protocol/Heartbeat.Collection.CollectorProtocol/ collection/protocol/Heartbeat.Collection.CollectorProtocol/
COPY collection/collectors/Heartbeat.Collector.VRChat/ collection/collectors/Heartbeat.Collector.VRChat/
ARG COLLECTOR_VERSION=0.1.0
# Cache mounts can be evicted independently of the restore layer. Let publish
# restore missing packages before compiling, even when that layer was cached.
RUN --mount=type=cache,id=heartbeat-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish collection/collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj \
        -c Release -p:Version="$COLLECTOR_VERSION" -o /out/vrchat \
    && /out/vrchat/Heartbeat.Collector.VRChat --create-package /out/vrchat-package

# 只承载 Package 内容，供 `--output type=local` 导出到宿主目录。
FROM scratch AS package
COPY --from=package-build /out/vrchat-package/ /
