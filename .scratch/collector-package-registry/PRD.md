# Collector Host Runtime 与独立 Package 交付

Status: ready-for-human

## Problem

Collector Runtime、Protocol 与三类 Execution Driver 已经存在。第一条 tracer 已把 VRChat 移出 Headless
image，并建立 Desktop/Headless 共享的 Installation module；Backend workflow 也已停止顺带部署 Headless。
宿主组合已按 [ADR-049](../../docs/adr/049-named-optional-collectors-outside-host-composition.md) 收敛：Desktop
与通用 Hub Runtime 只组合通用 seam 加 System BuiltIn，不认识任何具名可选 Collector。
VRChat 的显式 tag 与不可变 Web Release 已完成真实发布，0.2.1 可从生产 Caddy 的精确 Version 路径读取；
当前已按 [ADR-050](../../docs/adr/050-generic-collector-marketplace-and-runtime-owned-instances.md) 实现通用
Collector Marketplace：Registry Catalog 展示全部托管 Collector 的最新版，用户一键安装，Package 声明默认
Instance，Runtime 成为动态 Instance 唯一权威。Headless 独立 deploy、VRChat Catalog 发布与生产
安装/授权/恢复/卸载 smoke 均已完成。通用 ExternalHost 接入已按
[ADR-051](../../docs/adr/051-generic-external-host-identity-and-browser-delivery.md) 实现（issue 06）：一条
`/v1/collector-protocol/external-host` route 承载全部自己出现的 External Host，所有权按 External Host Identity
而非整个 Instance 隔离。Desktop Marketplace 已实现并与 Headless 共用完整生命周期 Runtime；当前主缺口是
Browser 独立 Release 与真实 Desktop Browser 纵切，Desktop 最终包的新启动 smoke 仍待 CI/human gate。

Browser 已实现通用协议适配、精确 Package 身份、Marketplace manifest 与四 target 独立 release workflow，
并由 Collector-owned 真实 TypeScript → .NET handler 测试验证互通。首个公开 tag、最终 Desktop 发布与
Windows/macOS Chrome/Edge 实机验收仍待完成（issue 07 ready-for-human）。宿主无需恢复任何具名分支。

2026-09-01 以前的 Registry/Approve/Switch 实现已撤回。issues 01/02 已按 ADR-048/050 完成；issues 06/07
已按 [ADR-051](../../docs/adr/051-generic-external-host-identity-and-browser-delivery.md) 重写，issue 10 承接
Desktop Marketplace。

## Outcome

- Desktop、Frontend、Analytics Backend、Headless Hub 是独立发布单元。
- System Collector 使用 BuiltIn Delivery，随 Desktop Release。
- Browser、VRChat 与未来非 BuiltIn Collector 各自显式构建和发布 Package。
- Desktop 与 Headless 复用同一个 Collector Host Runtime 与 Collector Package Installation module。
- Registry Catalog 只发现各 Collector 最新 Release；Host 一键安装后仍以精确 Installation/Instance 为权威。
- 用户不管理 Release URL、SubjectId、InstanceKey 或 JSON config；默认 Instance 由 Package Blueprint 声明。

目标拓扑和完整顺序见
[Collector Host Runtime 与独立交付目标架构](../../docs/architecture/collector-delivery-implementation-roadmap.md)。

## Fixed decisions

- Artifact Delivery 与 Execution Driver 正交；Browser 仍是 ExternalHost，VRChat 是 ManagedProcess，System
  是 InProcess。
- 具名可选 Collector 不进入 Host composition（ADR-049）：宿主里唯一可以按名字出现的 Collector 是 System
  BuiltIn，其余 Collector 只通过通用 seam 接入，宿主 UI、主题与 startup smoke 同受此边界约束。
- 统一 Protocol 指语义一致，不要求 InProcess、stdio 与 loopback HTTP 使用相同 transport。
- Shared Runtime 拥有 Installation、Instance、Activation、Driver、Protocol 与 Runtime State。
- Desktop/Headless 保留各自的 Subject 投影、上传、管理入口、UI、平台能力与部署配置 adapter。
- Package 继续经过现有 manifest/artifact/hash 校验；不新增签名 trust root。
- 普通 `main` 只验证；Collector 的用户可见 Package 必须由显式 Collector tag 发布。
- System 不进入 Web Registry，也不形成独立 Update Offer。
- Browser 一次安装只创建一个 Machine-scoped Instance；浏览器/Profile 以 External Host Identity 形成并行
  Activation 与独立 Stream，不按 App 创建 Instance。
- ExternalHost Collector 直接提供稳定 `appIdentityKey`；Host 不解析 `appHint`，也不认识具体应用产品。
- 当前不定义 Package 人工加载/移除说明，不提供打开目录或执行 Package 命令的通用能力。

## First tracer

外置本地 VRChat Package，同时建立共享 Installation module：

1. 把 Browser 当前 bundled import 的安装行为提取到共享 module。
2. Headless 使用同一个 module 安装宿主挂载的本地 VRChat Package。
3. 从 Headless Dockerfile 删除 VRChat build/package copy。
4. 证明只替换 VRChat Package 并重启 Headless，无需重建 Headless image。

这条 tracer 完成后再分别出票：Headless 独立 deploy、VRChat tag/static publish、Web Package source、
Browser 独立发布与真实 smoke。

## Out of scope

- 自动更新、channel、SemVer solver、后台检查与通知；
- owner approval、offer、候选稳定窗口、LKG 自动切换与热更新；
- Ed25519、密钥轮换、withdrawn、第三方市场；
- 安装 journal、断电恢复、自动回滚、cache GC；
- 为没有现场证据的旧 Package 或 Installation 状态设计迁移。

## Historical issue disposition

| Issue | 状态 | 新路径 |
|---|---|---|
| 01 static registry index | done | VRChat 0.2.1 Catalog 与生产安装/授权/恢复/卸载 smoke 已完成 |
| 02 explicit release pipeline | done | VRChat 独立 tag 与不可变 Web Release 已完成 |
| 03 shared local installation | ready-for-human | PowerShell CLI 安全/真实构建待跨平台验证 |
| 04 exact package approval | wontfix | ADR-048 明确不做 approval/offer |
| 05 VRChat ready switch | wontfix | ADR-048 明确不做 candidate/LKG switch |
| 06 generic ExternalHost | ready-for-human | 通用 route、身份级 Activation/Stream ownership 与卸载已实现 |
| 07 Browser release and smoke | ready-for-human | 代码与自动验证完成；首个 tag、最终 Desktop 下载版与实机 smoke 待验收 |
| 10 Desktop Marketplace | ready-for-human | 实现与自动测试完成；最终 Windows/macOS 包启动 smoke 待 CI |

## Exit conditions

- [x] Headless 与 Desktop 使用同一个 Package Installation module，并共用 Marketplace lifecycle Runtime。
- [x] VRChat Package 可独立于 Headless image 构建和替换。
- [x] Backend 与 Headless deployment 分离（`deploy-hub.yml` 只部署 Headless Hub）。
- [ ] VRChat 与 Browser 各自通过显式 tag 发布 Web Package。
- [x] System 仍只随 Desktop Release（publish target + Desktop Release 产物断言，issue 08）。
- [x] 宿主启动不依赖可选 Collector：Desktop 构建与产物不含 Browser，Headless 可零 Instance 启动且单
      Instance 失败被隔离（issue 08）。
- [x] 宿主不认识具名可选 Collector：Desktop 与通用 Hub Runtime 只组合通用 seam + System BuiltIn，
      Browser 专属 runtime / protocol handler / 安装目录 / UI 条目全部删除（issue 09）。
- [ ] 通用 ExternalHost 安装/连接能力存在，Browser 由此重新获得宿主接入路径：宿主通用接入与 Desktop
      Marketplace 已完成（issues 06/10），Browser Package 发布与真实接入待 issue 07。
- [x] `facts.segment/v1` 由 Package `FactKind` 与 schema 驱动通用 ActivitySegment 投影，宿主不再硬编码
      具名 schema id 列表（issue 09）。
- [x] 三类 Driver 继续通过统一 Protocol conformance。
- [x] 真实 Headless VRChat Marketplace smoke 有证据。
- [ ] 真实 Desktop Browser smoke 有证据。

## Comments

- 2026-09-01：owner 再次确认目标是独立发布单元与共享 Runtime，而不是在线候选更新系统；ADR-048
  取代 ADR-045/047 的当前实现范围。

### 2026-09-02 — issue 08 落地：宿主启动不依赖可选 Collector

[issue 08](issues/08-host-startup-independent-of-optional-collectors.md) 已实现（`ready-for-human`，剩下的是
真实 tag 才能执行的 release 门禁）。它把 Desktop 与 Browser 在**构建期**解开：Desktop 不再构建、不再打包
Browser，Browser 缺失/损坏只降级；System 改由 publish target 随产物走；Headless 允许零 Instance 并把单
Instance 失败隔离在管理面快照里。

**Web Delivery 一步都没做**：Browser 与 VRChat 仍没有独立 tag workflow，没有可下载的 Package，也没有 Web
Package source adapter。所以 Browser 现在的唯一安装方式是手工侧载——这不是终态，是「解耦已完成、发布尚未
开始」的中间状态，对应 issue 02/07 待重写。

### 2026-09-02 — issue 09 落地：宿主不再认识具名可选 Collector

上一条对 issue 08 的判断需要更正：那一轮只做到「Browser 缺席时降级」，宿主仍持有 Browser runtime、
protocol handler、安装目录、平台 AppHint 知识与 UI 卡片，所以「解耦已完成」当时并不成立。
[issue 09](issues/09-named-optional-collectors-out-of-host-composition.md) 才把它做完：Desktop 与通用 Hub
Runtime 只组合通用 seam（Collector Package Installation / Runtime / Instance / Activation / Driver /
Protocol）加 System BuiltIn，决策记在
[ADR-049](../../docs/adr/049-named-optional-collectors-outside-host-composition.md)。

代价是 Browser 在本阶段没有宿主接入能力：`/v1/collector-protocol/browser` 不再存在，手工侧载也连不上，
Browser 从 Desktop UI 消失。Browser 的独立 Package 构建与契约验证保留；它恢复连接后可直接使用
`facts.segment/v1` 通用投影，不需要 Hub 增加 Browser schema 分支。剩余缺口只有通用 ExternalHost
安装/连接能力与独立 Web Delivery。

### 2026-09-02 — issue 02 代码完成：VRChat 精确 Web Release

VRChat 现在有独立的 `collector-vrchat/vX.Y.Z` tag workflow：固定构建 `linux-x64` Package，生成确定性 zip
与不可变 `release.json`，再向服务器静态目录追加精确 Version，并从公网逐字节回读。它不创建 current
pointer，也不触碰 Desktop、Headless、Frontend 或 Analytics。issue 保持 `ready-for-human`：生产 Caddy
静态路由、服务器 x86_64 确认和首个真实 tag 尚未执行。

### 2026-09-04 — ExternalHost grill closeout

Owner 确认 [ADR-051](../../docs/adr/051-generic-external-host-identity-and-browser-delivery.md)：Browser 使用一个
Machine-scoped Instance，多个浏览器/Profile 以 External Host Identity 并行；Collector 直接上报稳定
`appIdentityKey`，Host 不解释 App Hint。同 identity 重连只替换自身旧 Activation，改变 App identity 则拒绝；
无连接是“等待连接”而不是失败。当前只做首次安装与完整卸载，不做更新/LKG，也删除了此前讨论的
`manualSetup`/打开目录 UI。实现顺序固定为 issue 06 → issue 10 → issue 07。

### 2026-09-04 — 通用 ExternalHost 实现完成（issue 06）

`ExternalHostCollectorProtocolHandler` + `AddExternalHostCollectorBinding` 交付了不含具名 Collector 的
ExternalHost 纵切：连接资格只看本地已验证 Installation、Marketplace 已创建的默认 Instance 与精确
Package/Artifact 身份；hello 不能创建或复活 Instance。并发所有权单位缩小到 External Host Identity 与它打开的
Fact Stream。
`ICollectorAppHintResolver` 退役，`appIdentityKey` 作为通用 Stream dimension 直接进入 ActivitySegment 投影。

实现中修掉三个「引用来源从宿主状态变成对端声明」才成立的真问题：畸形/路径穿越的 Package 引用会以未处理
异常收场（现为 `protocol_invalid_message`）；被围栏 Activation 上的 `facts.publish` 裸抛
`OperationCanceledException`（现按握手同一口径给 `activation_stopping`）；ExternalHost hello 曾直接创建持久
Instance（现在只解析安装时按 Default Instance Blueprint 创建的 Instance）。收口 review 还修正了冲突 hello
先踢掉健康 owner，以及 handler 写死 `facts.segment` 两处语义错误。细节见 issue 06 comments。

### 2026-09-07 — Browser implementation closeout

issue 07 代码与自动验证完成：Browser 使用通用 ExternalHost 协议和精确 Package 身份；独立 tag workflow
为四 target 发布相同 artifact bytes。Browser 84 tests（含真实 .NET handler 互通）、完整 .NET solution、
Collector contracts 与 workflow 静态检查通过，Standards/Spec review 均无待修发现。
当前剩余为 Human gate：Browser 首个真实 tag、公网 metadata/artifact 回读、新版 Desktop 发布及跨平台浏览器
实机验收。PRD 与 issue 07 均为 ready-for-human，不将本地候选或自动 fixture 视为已交付的下载版。
