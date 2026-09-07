# 07 — Browser 独立 Package Release 与 Desktop smoke

Status: ready-for-human

Owner: Collection / Browser Collector + Release

Priority: P1 — 用 Browser 证明 ExternalHost Collector 可以独立于 Desktop 构建、发布、安装和连接，完成目标
架构的第二条真实纵切。

## What to build

1. Browser Collector 自己识别首版支持的 Chrome/Edge 与 Windows/macOS，并向通用 ExternalHost `hello`
   直接发送稳定 `appIdentityKey` 和 Profile 持久化的 `externalHostIdentity`；不再发送 `appHint`。
2. Browser Package 声明 Marketplace presentation、Machine-scoped Default Instance 与
   `appIdentityKey + externalHostIdentity` identifying dimensions；Artifact 继续完整枚举 sideload payload。
3. 增加只由 `collector-browser/vX.Y.Z` 触发的独立 workflow，构建一份确定性 Package，并把相同字节作为
   Windows x64/arm64、macOS x64/arm64 四个 target 的精确 Release 与 Catalog Latest 发布。
4. 操作者从 Installation 手工 Load unpacked。Package、Host 和 UI 都不定义人工安装/移除说明；Desktop 不
   自动操作浏览器。
5. 完成真实 Desktop + Chrome/Edge smoke，证明安装、等待、单/多连接、重连、事实上传与完整卸载。

## Acceptance

- [x] Browser 扩展的生产代码使用通用 ExternalHost route/wire shape；不再出现
      `/v1/collector-protocol/browser`、`appHint` 或 Host 专属兼容分支。
- [x] Chrome/Edge 在 Windows/macOS 上生成对应稳定 `appIdentityKey`；无法可靠识别时 Collector 不开始
      Activation，不让 Hub 猜测产品身份。
- [x] `externalHostIdentity` 按扩展安装/Profile 本地持久化；Service Worker 重启产生新 ActivationId 但保留
      identity，正常重连复用原 Stream。
- [x] Package manifest 的唯一默认 Instance 是 Machine-scoped；Chrome、Edge 和不同 Profile 不创建额外
      Instance。
- [x] Package outputs 只声明 `appIdentityKey` 与 `externalHostIdentity` identifying dimensions；Fact payload
      不复制这些身份字段，页面 `identityKey` 仍表示规范化 URL。
- [x] workflow 只监听严格的 `collector-browser/vX.Y.Z` tag；普通 main、PR、Desktop tag 与其他 Collector tag
      不发布 Browser，也不构建/发布 Desktop。
- [x] 一次构建产生一份确定性 zip；四个 target 的 `release.json`/Catalog entry 指向相同 artifact bytes 与
      SHA-256，旧 tag rerun 不覆盖不同字节，较旧版本不回退 Catalog Latest。
- [x] Browser Release 不要求修改或部署 Desktop、Headless、Frontend、Analytics；Desktop Release 仍不包含
      Browser Package。
- [x] 自动 fixture 覆盖 Chrome/Edge identity、未知/冲突品牌、Profile identity 持久化、generic handshake、
      两 Profile 并行、同 Profile 重连、ACK/Gap/drain 与 package identity mismatch。
- [ ] 真实 macOS/Windows Desktop 安装后状态先为“等待连接”；Chrome 与 Edge Load unpacked 后分别连接，
      同时运行显示正确连接数，Backend 收到正确 Source/AppIdentityKey/URL identity。
- [ ] 完整卸载后 Runtime/Secret/data/Installation 消失；仍加载的扩展重连得到 `package_not_installed`，
      Desktop 与 System Collector 保持运行。


## Non-goals

- 不实现 Chrome Web Store、Edge Add-ons 或企业策略分发。
- 不实现 Browser Package 更新、热切换、LKG 或扩展自动 reload；新版本需要先卸载再安装。
- 不支持或承诺 Brave、Opera、Vivaldi、Firefox；它们可以在后续真实验证后由 Browser Collector 自己扩展。
- 不增加 Host 侧 Browser/AppHint 映射、具名状态类型、UI 卡片或 setup launcher。

## Dependencies

- [issue 06](./06-browser-external-host-update.md)：通用 ExternalHost route、identity ownership 与卸载语义。
- [issue 10](./10-desktop-collector-marketplace.md)：Desktop 的通用 Catalog/安装/状态界面。
- [ADR-051](../../../docs/adr/051-generic-external-host-identity-and-browser-delivery.md)：Browser 独立交付与身份决策。

## Human gate

代码与自动验证完成后保持 `ready-for-human`，直到：

- 推送一个真实 `collector-browser/vX.Y.Z` tag，四个公网 target 的 metadata 与 artifact 可回读；
- 在真实 Windows/macOS Desktop 上至少各完成 Chrome 或 Edge 的 Load unpacked 与连接；
- 至少一台机器完成 Chrome + Edge 或两个 Profile 并行、重连、事实到达 Backend 与卸载后拒绝重连 smoke。

## Comments

### 2026-09-04 — 重写旧 VRChat approval smoke

VRChat 0.2.1 的 Catalog 发布和生产安装/授权/恢复/卸载 smoke 已完成，旧 issue 07 的 approval/LKG 内容不再
成立。Owner 确认下一条目标是完全通用的 ExternalHost + Browser 独立 Package；Host 不能通过任何具名代码
感知 Browser。本 issue 因此只承接 Collector 自身、独立 Release 与真实设备验收。

### 2026-09-07 — Implementation closeout

Browser 自己识别 Chrome/Edge 的 Windows/macOS App identity；不可靠身份不建立 Activation。旧 Browser
route 和 appHint 生产字段已删除；hello 使用最终 staging manifest 派生的精确 Package/Artifact 引用。
Service Worker 重启丢弃上一个 run 的协议会话和 message attempts，但保留 Profile identity 与 durable
Facts/Gaps；新 Activation 由通用 Host 复用持久 Stream。

新增 Collector-owned .NET TestHost，通过真实 HTTP 验证 TypeScript 客户端与当前通用 handler 的互通、
多 Profile、同 identity 重连、Host 重启、身份冲突、Package mismatch、丢失 ACK/幂等 Gap 与卸载后的拒绝。
Fixture 不进入任何生产 Host 的项目引用；当前宿主组合无需具名修改。

独立 `collector-browser/vX.Y.Z` workflow 构建一份确定性 zip，四 target 共享 artifact bytes/hash。
发布脚本拒绝同版本覆盖，在四 target 公网逐字节回读之后，使用与 VRChat publisher 相同的 Catalog flock
更新自己的 target entry；旧版本不回退 Latest。build 同步 Package payload，避免源码与已提交 bundle 漂移。

红灯证据：未知 App 身份原先仍会激活；旧协议客户端连接真实新 handler 返回 unavailable；Package 缺少
默认 Instance/精确引用；新 worker 原先复用旧 activation attempt；独立 release/publish CLI 原先不存在。
这些边界已逐段先失败再实现。自动验证与 review 最终结果在后续记录。

仍待 owner 完成 Human gate：真实 tag 与公网回读、最终源码的 Windows/macOS Desktop 包，以及跨浏览器/
Profile 的实机采集、Backend 到达与完整卸载。Desktop v4.1.0 尚不包含通用 binding/Marketplace。
本次未自动推送 tag 或变更生产 Registry；issue 保持 ready-for-human。

### 2026-09-07 — Final verification and review

- `npm --prefix collection/collectors/Heartbeat.Collector.Browser run build`：TypeScript 与 Vite 构建通过，
  checked-in Package payload 同步；源码和 Package 不再包含 appHint 或旧 Browser route。
- `npm --prefix collection/collectors/Heartbeat.Collector.Browser test`：11 files / 84 passed，包含 5 个真实
  TypeScript → .NET HTTP integration 场景，以及确定性四 target artifact、不可变发布与 Catalog 不回退验证。
- `dotnet test Heartbeat.slnx -c Release --nologo`：所有 solution 测试通过。当前运行平台是 macOS arm64；
  这不替代 Windows 原生 packaged-host/浏览器 smoke。
- `node scripts/collector-contracts.mjs check`、actionlint 1.7.7（本次两个 workflow；未启用可选 shellcheck/
  pyflakes 外部检查器）和 `git diff --check` 通过。
- code-review 基线 `8bdc11b051d023862d1eee8467e05ea834226a6e`，排除 owner 既有
  `scripts/count-lines.py` 改动；Standards 0 findings，Spec 0 findings。
- 本地候选 `.local/browser-issue07.l0s2OF/release/heartbeat.collector.browser-0.1.0.zip`：53261 bytes，
  SHA-256 `ed7a9799fa2243fc5feffe1358b7cbca919b54cc7e05b61dc89e26c19ac3c774`，
  四 target 的本地静态目录安装和 Catalog 发布 fixture 通过。该路径是本机验证产物，不是公网 Release。

Lifecycle/friction closeout：已同步 Browser README、目标路线图和 PRD；不新增重复 issue，不重写已接受
ADR。代码与自动验证完成，真实 tag、公开 Release 与最终 Desktop/Chrome/Edge 实机验收仍未完成，状态
保持 ready-for-human。Desktop v4.1.0 的下载版仍不具备新通用能力，需另行发布最终 Desktop。
