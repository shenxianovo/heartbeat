# 主链路自动验收设计

状态：首版已实施并完成 macOS 真实链路验收（2026-09-07）。覆盖 Reference → Headless → Analytics；Desktop、其他服务组合与服务内部检查仍是后续接入项。

## 已确认目标（2026-09-07）

- 首要目标是消除修改后人工启动、操作与检查主链路的重复工作；运行期可观测性为失败定位提供证据，界面与体验的探索性验收另外处理。
- 主链路验收需要自动启动所需服务，并验证真实服务间的数据传递：Collector → Desktop/Headless Hub，以及 Desktop/Headless Hub → Analytics。
- 第一版选择最小且覆盖面尽可能广的一条链路。后续能够接入服务内部检查与不同服务组合；实现时保持环境编排与场景检查分离。
- 固定回归由可重复的程序执行和断言。AI Agent 负责调用、分析失败和探索补充；探索发现的稳定场景可以转成固定回归。
- 第一版范围已确认：从已安装的 Reference ManagedProcess Collector 开始，启动真实 Headless Hub 与 Analytics，通过 Analytics 查询 API 核对指定 Fact。Dashboard 展示不在首版链路内。
- Desktop 是明确的后续接入项，不因 Headless 通过而视为完成。后续还需能够添加服务内部检查、其他 Collector 和服务组合。
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

首版完成需要真实执行上述链路的证据，只有新增脚本或各层测试通过不算完成。Desktop 为明确的下一条宿主场景；届时处理 Machine Subject、真实宿主隔离和原生能力边界。服务内部检查、其他组合与 CI 复用入口逐步接入。

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

## 现有架构约束

遵循 ADR-048/049：Desktop 与 Headless 共享 Runtime，但保留真实宿主差异；通用宿主不认识具名可选 Collector。验收场景不能为方便测试把参考采集器写死进宿主组合。

真实设备权限、浏览器扩展及发布更新属于各自独立的覆盖范围。第一条通信链路通过，不自动关闭其他 issue 的实机或发布验收门禁。
