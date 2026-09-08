---
status: proposed
---

# ADR-052: 桌面退出意图与 Host 生命周期所有权

2026-09-07 的设计访谈已确认以下四项决定。用户随后要求先实施架构解耦，策略细节后续再定；
本文区分已实施的所有权调整与尚未实现的目标行为。延续 ADR-003 的“资源异步清理完成前保持 UI 循环”，
修正其初版把清理异常一律解释为禁止退出的策略。

**当前退出策略以 Ticket 06 的 best-effort 决定为准。** 用户于 2026-09-08 改为“不保证一定保存下来，
但尽最大努力，努力策略先弄个最简单的”。这替代下文 05 阶段的未知数据阻止退出及重试／强退 UI
目标；不是把未知重新判为安全。原接纳、fence 与唯一最终动作边界继续有效。

## 已确认

1. 未上传数据已确认持久化时，资源清理失败应记录错误并结束进程；分别报告数据安全性与清理
   结果，不能留下 Host 已销毁但 UI 和单实例锁仍常驻的空壳。
2. 无法确认数据持久化时，不因期限到达自动强退。进入明确的失败交互，说明风险，允许用户选择
   重试或强制退出；超时不是持久化成功证据。
3. 首次接受的 Desktop Exit Intent 固定。立即关闭新变更入口，重复请求共享结果，冲突操作明确
   拒绝；包括安装器调度在内的最终副作用只能执行一次。
4. Collector 的运行、管理操作和资源释放完整归 Host。UI 提交命令并展示状态，不拥有 Collector
   运行子树。应用退出 owner 管整体阶段，Collector 子树和原生观察 session 各自拥有生命周期，
   不把所有对象塞进一个跨层总协调器，也不改变 ADR-046 的协议两侧各自所有权。

## 运行期补充决定（已确认，分片实施）

1. 故障限制在最小能力范围内：可选能力失效只暂停该能力；前台应用等基线失效明确标记 System
   Collector 异常，其他 Collector 继续运行。共享基础设施故障按实际依赖扩大影响范围。
2. Host 已接受的管理变更由 Host 负责执行与保存结果，管理窗口关闭或浏览器断线不隐式取消。
   重新进入管理界面可以查看结果；查询可随界面取消，变更的取消通过明确命令处理。本决定
   不承诺未完成操作跨 Host 重启自动续作，重启后恢复已保存配置和安装事实，未提交操作允许重新发起，不承诺跨重启历史或断点续装。
3. 配置按最新启用意图收敛，实际运行状态独立报告；安装、卸载等显式操作分别处理，不按配置
   合并。用户接受此方向，同时要求避免为合并快速中间状态引入额外复杂度。

Q3 的实现优先采用每个能力一个串行协调者，只保留最新目标，不维护配置历史队列，也不为合并
而引入 debounce 等待。只有尚未开始执行的中间配置可以省略；已经开始的启停须完成安全交接，
已完成的中间状态及其真实影响不回滚。底层执行足够快时，每次变化都可正常生效；发生重叠时，
当前转换结束后检查最新目标，必要时再执行下一次转换。

用户随后确认 Q4/Q5：操作重启恢复遵守上述有限承诺；明确取消只在可安全撤销的阶段接受，进入
不可撤销阶段后返回不可取消并继续收尾，已完成安装通过卸载撤销。Q3 的简单串行协调实现限制也已确认。

实施 spec 与依赖任务记录在 `.scratch/runtime-ownership/`。本轮按这些已确认行为执行，不重新等待已获得的批准。

## 初始架构实施范围（00806ad）

- Windows/macOS 共用应用退出 owner；Host 异步停止和释放完成前保持 UI 循环，平台只负责桌面资源与原生退出。
- Collector 管理运行子树转由 Host 持有；UI 只借用管理命令和读模型，不再创建或停止 Marketplace。
- 通用 Collector Runtime 的组合入口从 System BuiltIn 中移入 Hub；System 只注册自己的 binding。
- Marketplace 通过 Host 的 Stopping 阶段关闭管理操作并等待结束，随后才进入最终上传；内部资源释放共享结果，失败不会跳过同层其他资源。

这部分不引入新的强退期限、失败交互或更新提交协议。当前仍保留“清理失败不自动退出”的临时行为；
它不是第 1、2 项目标的完成证明。更新安装器抢先启动、退出时所有写入口冻结、数据安全结果汇总仍需后续实施，不能由本次 Collector 所有权迁移推定已解决。原生 session 隔离的后续实现见下节。

验证：`dotnet build Heartbeat.slnx --no-restore`、IDE1006 命名检查及全量 1,113 个 .NET 测试通过。
回归覆盖运行中的单线程 UI 上下文、无 Desktop/System 的 Host 组合、停止阶段与最终上传的顺序，
以及取消失败和并发释放的结果保留。打包应用的原生退出与更新安装尚未人工验收，未替换已安装应用。

## 能力会话实施（Ticket 01）

Mac Input Monitoring、Accessibility 与 Windows input hook 的原生线程、句柄、委托及回调身份归单次 session。停止未完成时保留该 session，最终失败通知返回后才允许替换。每个能力使用串行协调过程读取最新配置，不增加 debounce 或配置历史队列。

原生故障通过能力状态报告，保留用户启用意图；权限或实际状态不允许时拒绝迟到观察。转换失败状态在串行过程内发布，调用者的异步等待不再拥有状态写入权。输入能力的停止结果共享，完成路径不依赖调用者 UI 上下文。

后续收紧移除调用者线程上的 `ObservationReconciler`。`ObservationCapability` 统一拥有后台串行执行、最新目标刷新、实际状态发布、权限轮询与停止等待；平台能力只映射配置/权限、执行原生操作并翻译观察。配置提交立即返回，停止完成仍可显式等待。无变化的权限轮询不关闭正常观察接收；原生 session 通过 `RequestStop` 发出停止信号，`Completion` 在清理和最终通知后给出结束结果。

AX 当前标题是按当前 PID 发出的独立查询，不依赖通知 session 是否已切换完成。同步的 Desktop Observation Source Stop 接口仍等待能力停止；它不是配置变更入口，未在本片扩大改造该共享采集契约。原生设备验收与退出产品策略仍保留各自门禁。

Ticket 01 实现和自动验证完成，具体证据与真实 Mac/Windows 验收步骤见 `.scratch/runtime-ownership/issues/01-capability-sessions.md`。当时用户要求完成 01 后暂停；后续 Ticket 02 已按下节实施，03–05 仍未实施。这不代表整套运行期决定或退出产品策略已全部完成。

## Host 管理操作实施（Ticket 02）

Host Management Operation 成为 Marketplace Runtime 的进程期所有权事实。写 interface 只负责同步接纳并
返回 OperationId，不再接受 UI/HTTP CancellationToken；调用者可以取消自己的等待或查询，但不能借此取消
Host 已接纳的执行。显式取消在 operation owner 与 `BeginCommit` 之间线性化：下载、验证等安全阶段可取消，
Installation/Instance 发布、Activation 替换、卸载 drain 或授权交付开始后返回不可取消并继续收尾。

operation owner 是 Marketplace Runtime 内部 module，不是持久任务框架。它只保留每个 Package 当前或最近
一条结果；同一目标的重复进行中命令共享 receipt，冲突命令拒绝。Host 停止时先关闭接纳并取消安全阶段，
再等待提交阶段结束。Desktop presentation 从 Host snapshot 恢复进行中或失败状态；Headless 当前请求 token
仅取消结果等待，完整的操作查询/取消 HTTP surface 仍由 Ticket 03 复用本契约实现。

重启只从 Collector Runtime State 与 Installation 恢复 Instance，不恢复 operation history；未提交或已取消操作
可重新发起。该有限承诺刻意不包含跨重启队列、断点续装、进度历史或通用调度框架。

## Headless 共享 Host 实施（Ticket 03）

本片以解耦与可维护性为目标。Headless 组合根复用 `AddCollectorRuntime`、`AddCollectorMarketplace`、
`CollectorMarketplaceHost` 和共享 hosted lifecycle worker；Fleet 自建 Runtime、Marketplace、HTTP 资源、
初始化信号、管理转发与同步释放路径被删除。原 Fleet 仅余展示职责，命名为 `HeadlessCollectorReadModel`。
Headless adapter 保留 Subject 创建、逐 Instance 投影和上传，管理收尾仍先于最终上传。

HTTP 直接接纳共享 Host Management Operation，并提供同一契约的结果查询和显式取消；所有路由保留 owner
与 client 校验。管理页读取 Host 结果，删除用 Collector 状态 fingerprint 推断操作完成的路径；操作查询
独立于可能等待变更结束的 Catalog 查询。此处只是既有操作契约的调用适配，不新增调度、历史或续作语义。

真实 Host 回归替代手动创建 Fleet 的生命周期测试，覆盖正常启动/停止/异步释放、安装卸载、离线恢复、
HTTP 重新连接后的结果查询与取消、owner/client 拒绝和 Account 投影隔离。旧组合的双重 DI 捕获会重复释放
Fleet 的 CancellationTokenSource，该问题已由删除重复资源 owner 路径消除。

## 交付责任实施（Ticket 04）

用户确认本片以解耦与可维护性为目标，选择小而可演化的结果接口，不建设完整恢复工作流。
System hosted binding 保留 Activation，由它直接提供已有 drain result；重复停止与释放复用同一终态结果。
Upload Stream 汇总数据 owner 的当前保管证据，Host adapter 不分析异常或上传状态来推断安全。
上传源统一保留读取、显式确认；清理错误与持久余量分别保留。未知余量只能由新的 owner 证据解除，
不能把后续空批误当作恢复。

Headless pipeline 在内部串行化周期上传与卸载时的交接。仍有本地数据或未知余量时拒绝删除 pipeline 数据，
保留 Instance 供后续重试；没有增加独立归档队列或恢复 UI。Ticket 04 深化后，Segment 源拥有现有
缓存与全部已提交投影，上传请求有界，接纳背压留在 Runtime 的提交边界；淘汰补偿账目和重注入
路径已删除，详见修订后的 ADR-022。

最终应用退出的跨子树汇总与动作仍由 Ticket 05 承接；本片提供的单轮结果不是完整退出安全结论。

## 应用退出补充确认（Ticket 05，2026-09-08）

先完成解耦与结果边界。安装器只能在子树数据安全得到确认、停止与清理完成后由应用最终动作调度。
调度失败记录错误并正常退出，用户下次手动启动旧版本；本片不恢复原采集会话。
数据保管未知时阻止安装器和原生退出，保留退出结果且不重新开放变更；重试／强退交互后置。
这会在无法证明持久化时阻止自动退出，是明确保留的产品限制。离线但本地余量持久化不属于此限制。

## 应用接纳与最终动作实施（Ticket 05）

HostOperationAdmission 在退出意图固定时关闭短命令接纳；平台设置 adapter 与 Marketplace management
facade 复用它。接纳只覆盖同步配置提交或操作 receipt，不持有已接纳任务的执行。后者继续由 Host
Management Operation owner 取消安全阶段并等待提交完成。相同退出意图复用结果，冲突意图拒绝。

共享应用 owner 消费各 hosted 子树的 IHostShutdownEvidence：System 的终态 drain、Marketplace 的
托管 Activation 余量、上传源当前保管以及 ExternalHost listener 的停产证据。ExternalHost 自身 outbox
仍归外部宿主，不要求其弱 lease revoke 提供跨进程持久化证明。关闭 Input Recording 只停止上传该流，
退出仍读取其保管证据；单轮 LastDrain 不是停产后的安全结论。缺少证据或未确认余量均保留未知。

安全时继续尝试所有清理，保留错误，随后执行唯一安装器动作和原生退出。未知时保留停止后的 owner
和明确结果，不销毁其数据资源，也不重新打开接纳。原生 UI 入口接住失败，避免用未处理异常意外退出。
更新的准备仅固定当时已就绪的候选；晚到下载不会改变已固定的最终动作。平台上的重复更新编排已删除。

自动验证：全量 .NET 1,188/1,188 通过，双轴复审各 0 项未解决。观察者通知异常不会中断退出；
再次 Install 与 Retry 都保留旧 Activation 未知证据，不用新 Activation 的空余量覆盖旧责任。

退出恢复交互、期限和打包更新的真实安装验收由
`.scratch/runtime-ownership/issues/06-exit-recovery-and-native-verification.md` 明确承接。

## Best-effort 退出（Ticket 06，2026-09-08）

应用固定退出意图、关闭接纳，调用现有 Host 停止一次。各子树继续拥有停产、durable first stage、
outbox 和 fence；退出层不增加重试，不重新开放旧 writer。周期上传先取消并归还保管责任，再执行
一轮末次交付；整轮共享 5 秒联网预算，并受已有 Host cancellation 限制。取消后仍给上传源本地
保存机会，已关闭的 Input Recording 不恢复上传。本预算由 Desktop/Headless 共享调度 owner 执行。

收集停产后的真实保管证据及清理错误，未知时记录“部分数据尚未确认保存，退出可能丢失”及 owner
诊断，然后继续尝试全部清理、执行已固定的安装器动作和原生退出。退出成功不等于数据安全；
`IsDataSafe` 仍只由持久化证据决定。安装器调度失败仍记录错误并正常退出，不恢复原采集会话。
不新增失败阻塞页、重试按钮、强退确认或跨重启恢复任务。

代码核实：System ingress／outbox 的失败尾部没有停机后的保存 owner；协议终态 fence 与复用的
Stop 不能提供重试。ManagedProcess 退出或未报告的未知余量也不能靠新 Activation 解除。正常
Segment 投影已有 Runtime durable 证据，缓存写失败本身不应成为退出阻塞。已有持久数据按原重启
重放路径继续交付；没有保存的数据在进程结束后可能丢失，强杀／崩溃也不保证执行退出流程。

5 秒只约束可取消的末次联网交付，不是整体退出硬期限。仍须等待正在运行的 owner 归还责任、原生
清理及同步文件操作；不抢占它们、不启动一个留有后台 writer 的超时旁路。整体硬期限与真实卡死
故障的处理，只有实际需要时再做独立切片。自动验证与原生／安装验收见 06 issue。

## 后续边界

- 整体退出硬期限、原生 session／同步存储不返回时的处理；5 秒末次联网预算不提供这个承诺。
- 真实故障证明有收益时，再设计窄的停机后持久化责任交接；当前不预建恢复能力或交互。
- 打包更新与原生退出的真实 Mac/Windows 验收（Ticket 06），以及 01 的权限／session 验收。

这些边界不从当前代码的异常形状或 Dispose 调用顺序推导完成结论。

## 已核实的设计约束

- `Host.StopAsync` 正常返回不能证明数据安全。实施前 System hosted binding 丢弃已有的 Drain Outcome，
  UploadWorker 只记录末次上传错误；Ticket 04 已提供 owner 交接结果，应用级汇总仍由 Ticket 05 承接。
- Collector Activation 终态会 fence 旧 writer。未知余量时只保留进程或重复 Stop，并不能恢复落盘
  能力；恢复必须保留/转交本地保存责任，不能重新开放已 fence 的协议 writer（ADR-046）。
- 固定的 Velopack 1.2.0 调度立即启动外部安装器，没有提交/撤销握手。其 XML 的“60 秒后放弃”
  与对应官方源码不一致：等待结束或报错后继续 apply，Windows 路径包含强制停止。因此在数据
  安全确认前启动安装器，会破坏 05 阶段“不确认持久化便不退出”的决定。06 已改为 best-effort，
  但仍要求停止与清理完成后才调度安装器，不让外部安装器抢先终止正在保存的 owner。证据：
  [固定版本等待逻辑](https://github.com/velopack/velopack/blob/f2edcbcafb81da5b3c884aaea330e225ad91d8b6/src/bins/src/shared/util_common.rs#L12-L23)、
  [Windows apply](https://github.com/velopack/velopack/blob/f2edcbcafb81da5b3c884aaea330e225ad91d8b6/src/bins/src/commands/apply_windows_impl.rs#L126-L137)。
  以上为源码核实，未在用户设备执行安装器超时演练。
