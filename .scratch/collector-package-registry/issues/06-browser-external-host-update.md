# 06 — 通用 ExternalHost 接入与身份级 Activation 所有权

Status: ready-for-human

Owner: Collection / Collector Host Runtime

Priority: P1 — Browser 恢复接入前必须先让共享 Runtime 正确承载一个 Instance 下的多个 External Host；
不得用 Browser 专属 handler 绕过这一步。

## What to build

在 `Heartbeat.Collection.Hub` 中实现不含具名 Collector 的 ExternalHost 纵切：

1. 用一个通用 loopback 路由承载 Collector Protocol。`hello` 携带精确 Package/Artifact 身份、
   `externalHostIdentity`、`appIdentityKey` 与协议能力；handler 从 Installation 和 Runtime-owned 默认 Instance
   解析连接，不接受 URL、Package 特例或 source 级注册表。
2. 把 ExternalHost 的并发所有权从“整个 Collector Instance”缩小到 External Host Identity 与它打开的
   Fact Stream。不同 identity 可以在同一 Instance 下并行；同 identity 重连只替换自己的旧 Activation。
3. Stream 使用 Package 声明的 `appIdentityKey + externalHostIdentity` identifying dimensions。相同 identity
   改报不同 `appIdentityKey` 时拒绝并返回结构化身份冲突；正常重连复用原 Stream。
4. Segment 通用投影直接消费 `appIdentityKey` dimension；删除 `ICollectorAppHintResolver`、Null adapter 与
   Host 侧 `appHint` 解析。
5. Installation 与 Instance 已存在但没有 ExternalHost Activation 时，管理读模型报告
   `WaitingForExternalHost`；这不是 Activation failure。
6. 完整卸载先撤销该 Instance 下全部 pending/active ExternalHost Activation，再走 Runtime-owned Instance、
   Secret、data 与 Installation 删除；后续连接得到 `package_not_installed`。

## Acceptance

- [x] 通用 ExternalHost route、handler、DTO 与组合入口不包含 Browser、Chrome、Edge、VRChat 或其他具名
      Collector；默认未安装连接只被拒绝，不影响 Host 启动。
- [x] `hello` 必须匹配一份已验证 Installation 中当前平台唯一的 `externalHost` Artifact，以及该 Package 的
      Runtime-owned 默认 Instance；错 Package/version/artifact/hash 均 fail closed。
- [x] 两个不同 `externalHostIdentity` 可以在同一个 Collector Instance 下同时 Ready，并分别写入自己的
      Fact Stream；现有 Instance 级 `stream_writer_conflict` 前置检查被移除。
- [x] 同 identity 的新 Activation 以 `LeaseReplaced` 停止旧 Activation、取得原 Stream writer lease，且不
      停止其他 identity；并发重连有确定的单 owner 结果。
- [x] Runtime 持久化或可从持久 Stream identity 判定 `externalHostIdentity → appIdentityKey` 绑定；同 identity
      改 App Key 被拒绝，Host restart 后仍不能静默改绑。
- [x] `appIdentityKey` 作为通用 Stream dimension 进入 ActivitySegment 投影；Backend 不认识该 Key 时沿既有
      provisional App 路径保留事实，不要求 Host 产品映射。
- [x] `ICollectorAppHintResolver`、`CollectorAppHintResolution`、Null adapter 及相关组合测试完整删除；
      `appHint` 不再是新 ExternalHost wire/Stream contract。
- [x] 已安装无连接时状态为 `WaitingForExternalHost`；有 N 个 Ready identity 时通用快照能给出连接数；身份
      冲突使用稳定 failure code，普通状态文案不进入 Runtime。
- [x] `RemoveInstanceAsync` 能停止并等待该 Instance 下全部 ExternalHost lifetime 完成后删除；失败时不伪造
      已卸载，成功后旧客户端重连得到 `package_not_installed`。
- [x] 使用 Reference ExternalHost fixture 覆盖未安装、错精确身份、两个 identity 并行、同 identity 替换、
      identity/App 冲突、restart 后重连、卸载中重连与卸载后重连。
- [x] 既有 InProcess、ManagedProcess 与 ExternalHost Collector Protocol conformance 全绿；生产 Host/Hub
      目录的具名 Collector 消融搜索继续通过。

## Non-goals

- 不增加 Desktop Marketplace UI；由 issue 10 承接。
- 不修改 Browser Collector 或发布 Browser Package；由 issue 07 承接。
- 不实现 Package 更新、热切换、LKG、自动重连策略、签名或第三方市场。
- 不定义 `manualSetup`/`manualRemoval` schema，不提供打开目录或执行 Package 命令的能力。

## Dependencies

- [ADR-049](../../../docs/adr/049-named-optional-collectors-outside-host-composition.md)：Host 不认识具名可选 Collector。
- [ADR-050](../../../docs/adr/050-generic-collector-marketplace-and-runtime-owned-instances.md)：Installation、默认
  Instance 与 Runtime 权威。
- [ADR-051](../../../docs/adr/051-generic-external-host-identity-and-browser-delivery.md)：本 issue 的身份与所有权决策。
- [issue 01](./01-static-registry-index.md)：共享 Marketplace/Installation 已存在。

## Comments

### 2026-09-04 — 重写旧 approval/LKG 规格

旧 issue 06 要求 Browser approved offer、side-by-side reload、exact-ready promotion 与 LKG rollback；这些能力
已被 ADR-048/050 明确移出开发期范围。Owner 重新 grill 后确认：一份 Browser Package 只创建一个
Machine-scoped Instance，不同浏览器/Profile 以 External Host Identity 并行；Collector 直接提供
`appIdentityKey`，Host 不解释 App Hint。本 issue 只建设可被任何 ExternalHost Collector 复用的 Runtime 与
protocol binding。

### 2026-09-04 — 实现完成

通用 route `/v1/collector-protocol/external-host` 落地。旧 Browser handler 被泛化成
`ExternalHostCollectorProtocolHandler`：连接资格只由「本地已验证 Installation + 精确 Package/Artifact 身份」
决定，宿主侧不出现任何具名 Collector。handler 只解析 Marketplace 创建的 Runtime-owned 默认 Instance
（`InstanceKey = "default"`），一个 Package 一台机器一个 Instance，多个 External Host 靠
`appIdentityKey + externalHostIdentity` 两个 identifying dimension 在它下面并行。宿主接入方式是
`AddExternalHostCollectorBinding(...)`；没接入的宿主保留默认 404 adapter，因此 Headless 不受影响。

实现期间发现并修掉三个真问题，都是「route 从可信内部状态改成对端声明」之后才成立的：

1. **对端声明的引用没做形状校验就进了 Installation 解析。** `packageContentHash: "drifted"` 或
   `"sha256:../../etc/passwd"` 会让 `CollectorPackageInstallations.DirectoryFor` 抛 `ArgumentException`，
   而 handler 只 catch `JsonException` 与 `CollectorActivationException` → 请求以未处理异常收场，还回显内部
   消息。现在 hash 必须是小写 `sha256:<64 hex>`、packageId/version 不得含路径语法，违反即 `protocol_invalid_message`。
   旧 Browser handler 没有这个暴露面，因为引用来自宿主自己的状态。
2. **被围栏的 Activation 上 `facts.publish` 抛 `OperationCanceledException` 而不是稳定拒绝码。** 握手各步走
   `RequireHandshakeStep` 会给 `activation_stopping`，只有交付路径是裸抛。已在 handler 侧按同一口径映射成
   409 `activation_stopping`（且只在 caller 没取消时转换，避免把宿主关停当成协议拒绝）。
3. **ExternalHost hello 会创造持久化 Instance。** Instance 表达跨重启用户意图，应由 Marketplace 根据
   Default Instance Blueprint 创建，而不能由一次连接产生。handler 现在先校验 Artifact，再只解析现有
   `default` Instance；只有 Installation 时返回 `instance_not_found`。

### 决策与偏差

- **卸载沿用 `ExternalHostActivationStopReason.DesiredDisabled`，不新增 `InstanceRemoved`。** 该 reason 会报给
  对端，新增取值要动 wire contract；而对端的可执行含义与「宿主不再要你跑」完全一致。
- **`CollectorRuntime.Protocol.cs:177` 的 instance 级 ExternalHost 检查保留。** 它拦的是「同一 Instance 上
  ManagedProcess/InProcess Activation 与 ExternalHost Activation 并存」这种跨 Driver 情形，不是 acceptance 第 3 条
  要求移除的那个 ExternalHost 路径内的 Instance 级前置检查（后者已移除，两个 identity 并行 Ready 有测试）。
- **伪造身份 dimension 返回 409 `protocol_invalid_message`，不是 400。** 409/503 是该 handler 既有的 envelope
  约定（非 retryable → 409），本 issue 不改状态码映射。

### 未覆盖

- `RemoveInstanceAsync` 提交阶段的失败回滚分支没有测试。该分支在围栏之后理论上不可达（不再接受新 hello，
  且已等到全部 terminal），且它位于 data directory 删除之后 —— 真触发的话 Instance 会留在 state 里但 per-instance
  data 已删。属既有顺序，本 issue 未改。
- Headless 没有注册这个 binding（容器里 loopback 接不到桌面上的 External Host）。`HeadlessFleetManager` 只修了
  读模型：没有 ManagedProcess runtime 时不再谎报 `Starting`，而是按 `DescribeExternalHostInstance` 给
  `WaitingForExternalHost` / `Negotiating` / `Ready`。
- Browser Collector 本体与 Package 发布仍归 issue 07；本 issue 只交付它要接入的通用能力。

验证：`dotnet test Heartbeat.slnx -c Release` 全绿（12 个测试项目，1067 passed / 0 failed，Release build 0 warning，其中
`ExternalHostCollectorProtocolHandlerTests` 25 个），`node scripts/collector-contracts.mjs check` 绿，
生产 Host/Hub/Desktop 目录按词边界搜 browser|chrome|msedge|vrchat|firefox|Edge 无命中。

### 2026-09-04 — 收口 review 修正

上一条“实现完成”中关于 Instance 的描述不准确，已被本条取代：`hello` 不再调用
`EnsureDefaultInstance`，只能解析 Marketplace 根据 Default Instance Blueprint 创建的既有 `default` Instance；
只有 Installation 而没有 Instance 时返回 `instance_not_found`，不会创造或复活用户意图。卸载竞态测试也不再
允许连接重新生成 Instance。

同时修正两处被原测试遗漏的协议错误：身份冲突现在先校验持久绑定与当前 session，再决定是否以
`LeaseReplaced` 替换旧 Activation，因此无效 hello 不会踢掉健康 owner；handler 删除了写死的
`facts.segment` 能力列表，能力协商只由 Runtime 按 Package outputs 判定，Event-only ExternalHost Package
已经通过同一公开 handler seam 的验收。不存在的 Instance 也不再被管理读模型报告成“等待连接”。
