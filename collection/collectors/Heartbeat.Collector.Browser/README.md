# Browser Collector

Chrome/Edge MV3 ExternalHost Collector。它观察各窗口的活动标签页，通过通用 loopback Collector Protocol
把 Segment Fact 交给 Desktop；不持有服务端凭据或地址。System 仍由 Desktop 单独内置。

## 构建与验证

需要 Node.js 24、.NET SDK 10 和 Python 3。Browser 的测试拥有一个独立 .NET TestHost，用真实
`ExternalHostCollectorProtocolHandler` 验证 TypeScript 客户端；生产 Host 不引用 Browser 或这个 fixture。

```bash
npm ci --prefix collection/collectors/Heartbeat.Collector.Browser
npm --prefix collection/collectors/Heartbeat.Collector.Browser run build
npm --prefix collection/collectors/Heartbeat.Collector.Browser test
node scripts/collector-contracts.mjs check
node scripts/collector-contracts.mjs stage browser .local/browser-package --version 0.1.0
python3 scripts/package-browser-release.py --package .local/browser-package --version 0.1.0 --output .local/browser-release
```

build 同步更新已跟踪的 `Package/browser-extension/`。stage 生成 observation/schema/artifact hash 与
`collector-artifact-ref.json` 的精确 Package/Artifact 身份；引用文件由最终 manifest 派生，不进入 artifact
payload hash，避免循环依赖。它不是授权凭据，Host 按自己的 Installation 逐字段核验。

首版只支持 Windows/macOS 上的 Chrome 与 Edge。品牌来自
[User-Agent Client Hints](https://learn.microsoft.com/en-us/microsoft-edge/web-platform/user-agent-guidance)，
平台来自 [chrome.runtime.getPlatformInfo](https://developer.chrome.com/docs/extensions/reference/api/runtime#method-getPlatformInfo)。
未知或冲突品牌不发起 Activation，不根据通用 Chromium UA 猜测 Chrome。

## 独立交付与使用边界

`release-collector-browser.yml` 只响应 `collector-browser/vX.Y.Z` tag，并再次校验严格稳定版本格式。
一份确定性 zip 登记到 Windows/macOS × x64/arm64 四个 target；同版本不同字节拒绝覆盖，旧版本不回退
Catalog Latest。发布先追加精确静态目录，公网逐字节回读成功后才在共享锁内更新自己的 Catalog 条目。
发布 Browser 不构建、发布或重启 Desktop、Headless、Frontend、Analytics。

Desktop Marketplace 安装后创建一个 Machine-scoped Instance，无外部连接时显示“等待连接”。操作者自行从
Installation 的 `browser-extension/` 手工 Load unpacked；Desktop 不代替浏览器加载扩展。多个浏览器/Profile
共享一个 Instance，各自持久化 External Host Identity；Service Worker 重启建立新 Activation，正常重连保留
Stream。未 ACK 的 Facts/Stream Gaps 留在本地 durable outbox。

完整卸载撤销全部 lease；仍加载的扩展重连得到 `package_not_installed`，不会重建已删除的 Instance。
本阶段没有 Store 分发、自动扩展 reload 或 Package 更新；新版本需先卸载再安装。

**Browser 0.1.0 与 Desktop v4.2.0 已于 2026-09-07 独立发布。** Browser 四 target 的公网 metadata、
artifact 长度/SHA-256 与精确 Package 引用已回读验证，Catalog 保留了既有 VRChat 条目。
Desktop 安装包和 Portable 见 [v4.2.0 Release](https://github.com/shenxianovo/Heartbeat/releases/tag/v4.2.0)。
Windows x64/macOS ARM64 packaged-host 启动检查已在发布 CI 通过；实际 Chrome/Edge 加载、采集、Backend
到达与完整卸载仍待实机验收，见 [issue 07](../../../.scratch/collector-package-registry/issues/07-deploy-and-vrchat-smoke.md)。

术语见 [Collection Context](../../CONTEXT.md)，行为契约见
[Conformance Suite](../../protocol/conformance/README.md)，Fact payload 见 [Contracts](../../contracts/README.md)。
