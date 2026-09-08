# 主链路自动验收设计

状态：Headless 与 Desktop 主链路已实施，2026-09-07 已有 macOS 源码及打包 `.app` 的真实链路证据。
覆盖 Reference → Headless/Desktop → Analytics；其他服务组合、服务内部检查与 CI 尚未接入。
Windows 原生、Setup/升级、自启动和操作系统权限仍待设备验收；长期无响应网络下的 Desktop
退出问题仍未解决，TCP reset 演练不覆盖该问题。历史制品证据与后续修复提交的验证分开记录。

## 已确认目标（2026-09-07）

- 首要目标是消除修改后人工启动、操作与检查主链路的重复工作；运行期可观测性为失败定位提供证据，界面与体验的探索性验收另外处理。
- 主链路验收需要自动启动所需服务，并验证真实服务间的数据传递：Collector → Desktop/Headless Hub，以及 Desktop/Headless Hub → Analytics。
- 第一版选择最小且覆盖面尽可能广的一条链路。后续能够接入服务内部检查与不同服务组合；实现时保持环境编排与场景检查分离。
- 固定回归由可重复的程序执行和断言。AI Agent 负责调用、分析失败和探索补充；探索发现的稳定场景可以转成固定回归。
- 第一版范围已确认：从已安装的 Reference ManagedProcess Collector 开始，启动真实 Headless Hub 与 Analytics，通过 Analytics 查询 API 核对指定 Fact。Dashboard 展示不在首版链路内。
- 当时将 Desktop 定为首版之后的接入项；现已独立实现并取得下文 macOS 验收证据，不由 Headless 通过推定。服务内部检查、其他 Collector 和服务组合仍未接入。
- 采用验证泳道：待测的 Heartbeat 服务及其数据在隔离环境内相互通信，外部 Auth 继续使用线上服务。Auth 自身不是待测对象，不建设测试 Auth，也不为本任务改造 Auth 地址配置。
- 从已安装状态开始；准备工作复用正式 Package Installation 与 Runtime API 建立隔离状态。Registry 下载、安装管理 API 与发布流程不进入首版验收范围。
- 默认每次运行创建独立泳道，结束后回收本次进程与数据，保留报告和日志；排障时可显式保留环境。
- 首版提供人和 AI Agent 均可调用的本地命令，自动完成全过程并返回明确退出码；后续 CI 调用同一入口，不另写一套验证逻辑。

本文中的 AI Agent 指开发助手；领域词汇中的 Agent 仍指 Desktop 采集宿主。

## 设计时核实的基础

- `collection/desktop/Heartbeat.Desktop.UI/Diagnostics/DesktopStartupSmoke.cs` 证明宿主起停和 System Runtime 注册，不拉起 UI，也不证明事实已经采集或上传。
- `scripts/smoke-local-data.mjs` 与 `docs/runbooks/local-data-smoke.md` 已有数据不变量、水位和质量信号检查，但客户端启动、窗口切换与输入仍靠操作者。水位推进尚不能关联到一次指定的测试刺激。
- `.github/workflows/collector-contracts.yml` 运行各层测试；本次新增的本地验证命令负责真实主链路编排，尚未接入 CI 自动触发。
- `collection/collectors/Heartbeat.Collector.Reference.ManagedProcess/Program.cs` 已有通过 Collector Protocol Client 发布固定 Segment 的参考采集器，声明 Account Subject；本次复用其现有 Package 和协议实现完成真实链路验收。
- `compose.local.yml` 使用持久化本地数据并依赖真实 Auth，不能直接将现有开发环境等同于一次独立的自动验收环境。
- `collection/hub/Heartbeat.Collection.Hub/Http/Endpoints.cs` 已支持 Analytics 地址覆盖，Auth 地址固定为线上服务，符合本轮已确认的泳道边界。
- Desktop 的 Marketplace 仅接受 Machine Subject，现有 Reference Package 是 Account-only，不能原样装进 Desktop；第一条 Headless 场景与后续 Desktop 场景必须区分。
- 当前 Compose 项目名、数据挂载与启动脚本探测端口固定；泳道编排需要显式分配资源，不能只改项目名就复用同一数据库目录。
- Analytics 的按用户名查询依赖本地 User 已存在，上传不会创建该行；新数据库需要准备与线上身份对应的本地 owner 前置数据。此准备不包含待断言的 Fact，不能让预置数据替代真实上传。

## 执行与判定

一个命令负责准备已安装状态、启动本次所需服务、等待就绪、执行场景、断言结果、收集证据与回收资源。平台环境准备与场景检查分开，使后续增加 Desktop、不同服务组合或服务内部检查时能够复用编排与报告。

首版通过条件是：由本次启动的 Collector 发布的指定 Fact，经真实 Hub 上传后，可从本泳道 Analytics 的查询 API 读到，身份、Subject/Source、时间区间和内容与预期一致。仅端口可连、HTTP 200 或任意历史水位推进不足以通过；使用本次隔离数据与运行证据避免旧数据造成假通过。

每次运行保留可供程序读取的结果与简短摘要；失败附阶段、相关服务日志、预期与实际值。线上 Auth 不可用或凭据失效时如实报告依赖阻塞，不计为主链路通过，也不自动归因为 Heartbeat 的功能回归。超时预算与清理结果显式记录，失败不能被后台重试或资源回收掩盖。

### 第一条场景

1. 为本次运行分配标识、数据目录、网络与端口，构建或选择当前待测版本的 Analytics、Headless 与 Reference 制品。
2. 准备本泳道 PostgreSQL 和 owner 前置数据，使用正式模块建立 Reference Package Installation 与 Runtime Instance；不预置待验收的 Fact。
3. 启动真实 Analytics 与 Headless，Headless 从已安装状态恢复并启动 Reference 子进程。上传地址指向本泳道 Analytics，ApiKey 换取令牌与 JWT 校验使用线上 Auth。
4. Reference 经正式 Collector Protocol 发布已知 Segment，Headless 经正常投影、缓存和上传链路交付。
5. 在有界期限内通过 Analytics 查询 API 找到并核对该 Segment；查询与预期使用同一本泳道 owner、Subject 和时间窗口。
6. 保存判定与证据，再回收本次持有的服务和数据；显式保留环境时报告资源位置和清理方式。

所有启动、等待与收尾均有明确结束条件。断言失败、依赖阻塞或清理失败返回非零；报告分别保留主链路结果与清理结果，不让后者覆盖前者。主动保留环境属于显式运行选项，不计为清理失败。

### 最小扩展边界

- 泳道编排拥有资源分配、配置路由、启停与清理；新增服务组合复用这些能力。
- 场景声明所需服务、前置状态、刺激和断言；新增 Desktop 或内部检查不改已有场景的通过含义。
- 结果记录覆盖范围、阶段、耗时、预期与实际结果及证据位置；人和 AI Agent 使用同一份结果。

首版只实现这条场景实际需要的接缝，不预建插件系统、通用工作流引擎或全部服务组合。测试数据不写入线上 Analytics 或既有本地开发数据库；线上 Auth 凭据由执行环境提供，不写入场景源码或报告。

## 实施顺序与完成标准

1. 建立本地命令、临时泳道和已安装状态准备，验证启停与回收能够独立完成。
2. 接入真实 Collector → Headless → Analytics 数据链路及查询断言，生成统一报告。
3. 验证正常运行可重复通过，断链、依赖阻塞和超时不会假通过，失败仍保留证据并执行清理；记录一条可直接复跑的命令。

首版完成需要真实执行上述链路的证据，只有新增脚本或各层测试通过不算完成。Desktop 后续已作为第二条宿主场景实现，处理 Machine Subject、真实宿主隔离和原生能力边界，证据见下文。服务内部检查、其他组合与 CI 尚未接入。

## 运行代码实施方案

以下已实现；使用方式见 [`tools/Heartbeat.Verification/README.md`](../../tools/Heartbeat.Verification/README.md)。

新增 `tools/Heartbeat.Verification` .NET console 项目，作为独立的按需验证命令，不加入默认 `dotnet test` 的执行链。入口为 `dotnet run --project tools/Heartbeat.Verification -- run headless-main`，从已有 Headless 配置读取线上 Auth 凭据。验证器自身的 `Heartbeat.Verification.Tests` 已加入 solution，随普通测试执行且不访问线上 Auth。选择 .NET 是为了直接复用现有安装模块、Runtime API、DTO 与 EF 数据供给，避免再写一套状态文件或数据库 schema。

首版 PostgreSQL 运行在独立 Docker 容器中，Analytics 与 Headless 使用当前源码 publish 的独立本机进程，Reference 由真实 Headless 启动。每个进程显式设置工作目录、端口、配置和数据目录；Analytics 的内容目录指向其构建产物，Headless 的上传地址指向本泳道 Analytics。此组合也允许后续加入必须运行在本机的 Desktop。

代码按四项责任组织，先用普通类和函数表达：

| 责任 | 内容 |
| --- | --- |
| 命令入口 | 选择场景、加载配置、建立运行标识、处理取消并汇总退出码 |
| 泳道资源 | 构建产物、数据库容器、服务进程、地址与数据目录、启动等待、日志和清理 |
| 场景与准备 | 用正式模块建立前置状态，声明预期数据，通过真实进程和 HTTP 执行检查 |
| 结果输出 | 阶段耗时、通过/失败/阻塞、预期与实际值、证据位置和独立清理结果 |

Reference 通过现有 apphost 的 `--create-package` 创建当前平台 Package；场景准备调用 `CollectorPackageInstallations.Install` 与 `CollectorRuntime.CreateInstance` 建立已安装状态。Analytics 自身完成 migration 后，准备步骤用 `AppDbContext` 和 `UserService.ProvisionAsync` 建立 private owner；subject 与线上 token 对齐，用户名只是泳道本地别名。准备步骤不得调用上传或摄入服务替代待测链路。

验收调用正式 `GET /api/v1/users/{username}/segments`，携带同 owner 的线上会话 JWT，以 `source` 和 UTC `start/end` 查询。首版使用现有 Reference 的确定性 Segment，在空泳道中断言唯一匹配的 Source、IdentityKey、标题、起止时间及 300 秒时长，并关联查询到的 Device 身份。Analytics Segment Id 由 Hub 按 StreamId/FactId 投影产生，不等于原始 FactId；报告记录返回 Id，断言不复制内部哈希算法。

运行产物放在 Git 忽略的 `.local/verification/<run-id>/`，至少包括 JSON 结果、各服务日志和本次查询证据。私密配置与长期报告分开处理；默认回收私密配置及业务数据，只保留不含凭据的证据。日志能够证明的阶段如实记录，证据不足时报告最后已确认阶段，不猜测故障所属服务。

验证实现本身重点覆盖有风险的边界：历史数据不能造成假通过，服务提前退出和超时能够结束运行，取消与失败路径仍回收本次资源，失败证据不被清理覆盖。端到端验收需至少正常连续运行两次，以及一次受控断链运行确认得到非零结果；线上 Auth 不可用时保留阻塞证据。

## 首版验证证据（2026-09-07）

- `dotnet build Heartbeat.slnx --no-restore`：通过，0 warning / 0 error。
- `dotnet test tools/Heartbeat.Verification.Tests`：9 项通过，覆盖错误/重复结果拒绝、超时证据、凭据脱敏、命令取消，以及服务提前退出后的子进程回收。
- 真实线上 Auth + 临时 PostgreSQL + 真实 Analytics/Headless/Reference：连续两次通过，退出码 0，清理通过。运行标识为 `20260907t115535-b1917f96`、`20260907t115803-c6dd802f`。
- `--fault disconnect-upload --timeout-seconds 20`：查询保持空数组，delivery 阶段超时，退出码 1，清理通过。运行标识为 `20260907t115821-854515f8`。
- 缺失配置：configuration 阶段报告 blocked，退出码 2，清理通过。运行标识为 `20260907t120105-0279675a`。
- `--keep`：链路通过后报告 retained，发送 SIGTERM 后退出码 0、清理通过。运行标识为 `20260907t120103-f42172f2`。
- 收尾核查：上述泳道均无残留 `work/`，Docker 中无验证容器，保留报告与日志未发现配置中的 API key。

上述机器相关报告位于 `.local/verification/<run-id>/report.json`，不进入版本控制。首版真实运行平台是 macOS；其他操作系统与 Desktop 链路不由这些结果推定已验收。实现未新增生产服务自检端点，也未改变 Auth 或 Collector 的生产协议。

## Desktop 开发、验收与 Release（已实施）

见 [ADR-053](../adr/053-desktop-profile-and-installation-ownership.md)。两个平台入口复用
`DesktopBootstrap`，先解析运行目录，再取得独占权，之后才创建日志、配置和 Host。独占使用
实际文件锁而非 runId 派生 Mutex，目录符号链接和大小写别名不能绕过保护；默认目录同时保留
旧版 Mutex。Windows 原有“创建锁失败仍继续”的路径已经移除。竞争返回 3，锁创建错误以异常
停止；启动 smoke 竞争仍报告 inconclusive 并返回 1。

取得目录锁后才判断是否需要旧 Mutex。路径相同时直接识别；路径不同时，在已锁定的 Profile
创建自动删除的随机临时文件，确认该文件是否也能从默认目录访问。该文件只用于核实实际目录
身份，互斥始终由 `.desktop.lock` 承担；不向无关默认目录写入，也不按操作系统猜测卷的大小写
规则。因此大小写敏感卷上的独立目录可并行，指向同一实际目录的别名仍保留旧版保护。

`--data-directory PATH` 是普通启动参数，既可用于持久开发目录，也可用于一次验收。目录内仍用
现有 config.json、Collector Package/Runtime、缓存和日志布局；Analytics 地址继续通过
`HEARTBEAT_API_BASE_URL` 提供，ExternalHost 监听沿用 config.json 的 `ingestPort`。首条
Desktop 场景设为 0，避免同日常客户端竞争 loopback 端口；将来的 ExternalHost 场景需显式指定
端口与连接目标。

`DesktopInstallation` 持有自启动端口和更新工厂。显式 Profile 和 smoke 只能使用 detached
adapter；即使执行文件来自真实安装也不授予写入能力。普通启动还需 Velopack 的安装识别才可
绑定安装。DesktopState 构造只建立状态和订阅，自启动注册协调由安装对象显式执行。独立运行
的自启动开关和更新入口显示不可用，底层拒绝自启动写入，更新返回 Skipped。

`DesktopProcessControl` 提供目录内的本地进程观察与退出入口：原生 UI 消息循环执行回调后才
写 `desktop-status.json`（PID、UI readiness、自启动/更新能力）；创建 `desktop-stop` 请求现有
`DesktopApplicationLifetime` 完成正常退出。SIGTERM/Ctrl+C 也进入同一退出事务。它没有另建
Host、Collector 生命周期或远程管理服务。运行期间只有该 Profile 的使用者应写这些文件。

验收器提供 `headless-main` 和 `desktop-main`，共用 `MainPathScenario` 的 Auth、空数据基线、
事实断言和 owner 验证。Desktop 场景先用该制品现有 startup smoke 获取真实 Machine UUID，
再通过正式 Installation/Runtime API 准备 Machine Instance，启动真实 Desktop 和原生 UI。
Reference 包可以生成 Account/Machine 两个版本，声明与协议客户端使用相同 Subject kind；固定
的 `reference.account` Source/标题保留为既有测试事实，不用它推断 Subject。身份断言单独验证
Desktop 的真实 Machine UUID，Headless 则验证 Account 的服务端映射。

验证器先独立 publish 到 `.local/verification-runner`；此准备步骤仍构建其源码依赖。制品执行
使用 `dotnet .local/verification-runner/Heartbeat.Verification.dll run ...`，不调用 MSBuild，
不要求已指定服务的源码能够编译。每个服务可用 `--artifact SERVICE=PATH` 指定已有制品，跳过对应 publish，支持 Desktop `.app`
或可执行文件，以及 Analytics/Headless DLL。`VerificationArtifact` 解析制品并记录版本和整个
目录的内容 hash，随后使用相同准备和运行阶段。指定制品不复制、不重新构建，也不安装到日常
使用位置。未指定的服务仍从源码 publish；完全使用制品时需指定全部三个服务。`dotnet run`
仅用于源码开发，不能作为跳过源码构建的制品执行入口。Release workflow 可在既有解包步骤后
调用预构建验证器，不改变独立发布单元；本次不接入 CI。

Desktop 场景确认 UI 已运行且安装能力均关闭，退出后单独断言 Desktop 返回 0，再回收进程组和
数据库；保留脱敏后的 Desktop 文件日志。清理失败会保留现场并返回非零。主链路通过不表示
Setup、自启动、权限授权或 vA→vB 更新已验收，仍需各平台真实安装设备证据。

## Desktop 验证证据（2026-09-07）

- 当前源码：`20260907t125251-8550f246`，Reference → 原生 Desktop → Analytics 通过，正常退出与清理通过。同机已安装客户端 PID 38562 继续运行；对正在验收的同一 Profile 再次启动返回 3。
- Velopack 打包、解包后的 `.app`：`20260907t130105-eec14bba`、`20260907t130335-95d11874` 两次通过，未重新构建 Desktop。后一报告记录版本 `0.0.2-verification.1+da70af86dfd5aeb6db305737035a053503ee4e55`；两次制品树 hash 相同：`sha256:c612e5c946eddf0d70d4a76fcda12a8a700c544dd9e78fa1c6d784f3f1a5d15e`。UI readiness、安装能力关闭、真实 Machine 映射、正常退出码 0 均有报告证据。
- Headless 回归：`20260907t130105-e259538e` 通过，清理通过。
- 确定性断链（TCP reset）：`20260907t130540-1a4c1e65` 的 delivery 在 90 秒后按预期失败，退出码 1；Desktop 正常退出码 0，清理通过。
- `.app` 验收前后，日常 `config.json` 与 LaunchAgent plist 的 SHA-256 指纹相同；正常运行证据无 API key 泄露，除网络黑洞失败现场外均已删除 work。
- 全量 `dotnet test Heartbeat.slnx --no-restore`：13 个测试程序集共 1182 项通过，包括验证器 19 项、共享 UI 57 项、Mac 79 项、Windows 38 项。Windows 测试在 macOS 的 .NET 运行时执行，不等于 Windows 原生设备验收。
- 原故障端点（绑定但不 listen 的 Socket）在 macOS 上造成 TCP 连接长时间等待：`20260907t130149-efa1c830` 的 delivery 超时，正常退出超过 30 秒。验证器随后强制回收进程组和数据库，保留 work 和失败日志。这个结果不计为干净退出通过，也不因普通链路成功而关闭；后续退出责任验证需覆盖长期无响应请求及最终缓存完成。当前 `disconnect-upload` 改为独占 listener 接受后立即 reset，使故障语义确定为连接失败；它不覆盖网络黑洞。

上述真实链路平台均为 macOS。`.app` 使用现有发布参数在本机打包，仅在独立目录执行，未安装或发布。Setup、真实权限、自启动注册和 vA→vB 更新仍待各自设备验收。

## P2 修复后的干净提交验收（2026-09-08）

本轮验收来源是本地分支 `codex/profile-verification-closeout` 的代码提交
`c376315ae6e486ea9ddfa28c7421d1c528afdb6e`，Git tree 为
`23262b287a756f27e739ec69127b42faedf6a9c3`。代码先提交，再从干净工作树 publish
验证器、Analytics、Headless、Reference 和 macOS Desktop，并用现有 Velopack 参数打包、解包
到隔离目录。每条构建、打包及运行命令前检查 HEAD 和工作树，命令后记录工作树状态；全部为空。
本节属于验收完成后的独立、仅文档提交，不是被测制品的源码提交。版本与目录 hash 只是制品身份，
源码关联由上述 Git 状态和实际构建命令共同证明，不能仅从版本号推定。

机器证据根目录为本 worktree 的 `.local/p2-closeout/`，其中 `provenance.json` 保存完整命令、
工作目录、时间、退出码、前后 Git 状态及报告路径；每条命令另存同名 `.log`。
真实运行均使用独立 PostgreSQL、Analytics、Profile/数据目录和线上 Auth；没有安装、发布制品，
也没有修改主工作树或操作日常已安装客户端。

| 场景 | Run ID | 结果 / 验证器退出码 | 清理 |
| --- | --- | --- | --- |
| 当前提交源码 Desktop | `20260908t003940-57cb3593` | passed / 0 | passed |
| 打包 `.app` 第一次 | `20260908t004059-4109ad3a` | passed / 0 | passed |
| 同一 `.app` 第二次 | `20260908t004214-c9bdc6d0` | passed / 0 | passed |
| 同一 `.app` TCP reset | `20260908t004325-00487ab7` | delivery 90 秒超时，预期 failed / 1 | passed |
| 源码不可构建时使用全部已有制品运行 Headless | `20260908t004507-f36b490e` | passed / 0 | passed |

前四项报告位于 `.local/verification/<run-id>/report.json`；最后一项位于
`.local/p2-closeout/broken-source/.local/verification/<run-id>/report.json`。
四次 Desktop 均记录原生 UI ready、安装能力 detached、正常退出码 0；三次无故障运行验证
Machine subject 映射，Headless 验证 Account subject 映射。TCP reset 不表示 delivery 成功，
也不覆盖长期无响应的网络黑洞。

源码 Desktop 版本为 `1.0.0+c376315ae6e486ea9ddfa28c7421d1c528afdb6e`，目录 hash 为
`sha256:d67bd6c731c5d3d6c113613a720cd37f207f019badfacbab3d5ccc0fb21696f6`。
三次打包制品运行使用 `.local/p2-closeout/artifacts/app/Heartbeat.app`，版本均为
`0.0.2-p2.1+c376315ae6e486ea9ddfa28c7421d1c528afdb6e`，目录 hash 均为
`sha256:78c0ce5c1346c186d53caa30d43598512431032af9c5c1319ace288f4d70e835`。
Analytics、Headless、Reference 的路径、版本及目录 hash 同样保存在 provenance 和对应报告。
源码泳道 publish 的临时制品随 work 清理；打包制品和预构建验证器保留在隔离 artifacts 目录。

最后一项从独立临时仓库启动预构建 `Heartbeat.Verification.dll`，同时显式指定
Analytics、Headless、Reference 三个制品。该仓库两个服务项目均为故意损坏的 XML；分别执行
真实 `dotnet build` 得到 `MSB4025` 和退出码 1，之后真实 Headless 链路仍通过。全部已有制品的
四次运行都没有服务 `build-*` 日志，证明制品执行入口未触发服务源码构建；验证器准备步骤仍会
构建自身源码依赖。

回归与独立审查：

- 大小写敏感 APFS 临时卷先复现原实现误判（预期 acquired，实际 occupied），修复后 Profile
  测试 9/9；普通卷最终验证器测试 22/22，含跨进程别名/旧 Mutex 保护及真实 CLI 入口边界。
  日志分别为 `profile-red.log`、`profile-sensitive-green.log`、`verification-tests.log`。
  敏感卷通过 `HEARTBEAT_PROFILE_TEST_ROOT` 指向隔离挂载点；临时卷已卸载，镜像已删除。
- 最终 solution build 为 0 warning / 0 error；IDE1006 命名检查通过，见 `final-build.log`、
  `naming.log`。Standards 与 Spec 两个独立审查均无新增 actionable finding。
- 全量测试运行时为 1182 passed / 2 failed（当时验证器 21 项；随后新增旧 Mutex 边界测试并
  单独重跑验证器至 22/22），不能标记全量通过。见 `full-tests-docker.log`：两个失败均在
  `VRChatPackageBuildScriptTests` 查找仓库根时抛出 `DirectoryNotFoundException`；其
  `Directory.Exists(".git")` 无法识别 worktree 的 `.git` 文件。该既有、范围外测试入口问题
  未在本轮修改；后续修复与回归结果见下方补记。
- `evidence-check.json` 记录对本轮 98 份日志/JSON 的程序内扫描：未发现所用 API key 或 JWT；
  五条泳道的 work、容器及记录的进程均无残留。检查不输出凭据内容。

本轮只关闭上述三个 P2：Profile 身份判断、预构建入口说明与验证、Desktop 实施状态文档。
长期无响应网络下的退出问题仍未解决；Windows 原生设备、Setup、真实权限、自启动注册、
vA→vB 更新及 CI 接入均不由本轮结果推定完成，也未扩展 P3 抽象重构。

### Worktree 测试入口修复补记（2026-09-08）

在 `9058e9a0eb2ddbe18572151663a495ddcba08951` 的独立临时 worktree 中，执行
`dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests --filter FullyQualifiedName~VRChatPackageBuildScriptTests --nologo -v minimal`，
稳定复现上述两项 `DirectoryNotFoundException`。随后仅修改测试的仓库根识别，同时接受
`.git` 目录和文件，保留构建脚本存在检查及原有安全断言。

应用修复后，在真实 worktree 和普通 checkout 分别执行
`dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests --nologo -v minimal`，
均为 21 passed / 0 failed / 0 skipped，包含原先失败的两项。临时 worktree 已清理。
此次仅回归 VRChat 测试项目，不改写上述历史全量测试结果，也不表示当前全量测试已重跑通过。

## 现有架构约束

遵循 ADR-048/049：Desktop 与 Headless 共享 Runtime，但保留真实宿主差异；通用宿主不认识具名可选 Collector。验收场景不能为方便测试把参考采集器写死进宿主组合。

真实设备权限、浏览器扩展及发布更新属于各自独立的覆盖范围。第一条通信链路通过，不自动关闭其他 issue 的实机或发布验收门禁。
