# Collection

采集桌面设备的前台应用使用时长与各应用内活动，上传至服务端。作为常驻托盘或菜单栏应用运行。

## Language

**Agent**:
桌面采集宿主：承载 Collector Runtime、system Collector、通用 ExternalHost loopback 监听、集面读模型以及统一缓存/上传。它不再提供通用 source 级 loopback segment ingress，也不认识任何具名可选 Collector（ADR-049）。
_Avoid_: Service, Worker（这些是 Agent 内部的实现层）

**Collector（采集器）**:
一个观测特定应用内活动并通过 Collector Protocol 向 Runtime 发布 Fact 的组件（browser 扩展、VRChat 账号采集器等）。system 采集器内置于 Desktop，同样经 Runtime 汇入，特例性仅剩两点：进程内 binding、不可停用。非内置采集器代码位于 `collection/collectors/`。
_Avoid_: 插件/Plugin（口语别名，UI 与文档统一用"采集器"；ADR-017 等历史文档中的 plugin 即此概念）

**Host Composition（宿主组合）**:
宿主（Desktop platform head 或 Headless host）在组合根注册的服务集合。它只允许出现通用领域概念：Collector Package Installation、Collector Runtime、Collector Instance / Activation、Execution Driver、Collector Protocol 与通用 ExternalHost handler seam，加上唯一写死的 System BuiltIn。宿主 UI、主题、启动 smoke 与宿主测试同受这条边界约束。
_Avoid_: 在宿主里为某个具体 Collector 建 binding 扩展、状态类型、默认安装目录或 UI 卡片

**Named Optional Collector（具名可选 Collector）**:
除 System 以外、按名字可识别且不必存在的 Collector（browser、vrchat 等）。它不进入 Host composition：宿主不持有它的 binding、runtime、状态模型、安装目录、平台知识或 UI 条目，只通过通用 seam 接受它。"宿主允许它缺席"不等于已解耦——只要宿主代码里还写着它的名字，它仍是宿主特化（ADR-049）。
_Avoid_: Optional Plugin、把"缺席时降级"当成解耦完成

**Collector Package（采集器包）**:
Collector 的内容快照；精确候选由声明版本与 content hash 共同确定。正式发布物不可变；当前本地官方 Package 在发布安装 seam 建立前允许同一声明版本换内容。它不表示已经安装、配置或运行。
_Avoid_: Plugin Package、把 Collector Package 与 Collector 混称为“插件”

**Collector Runtime（采集器运行时）**:
宿主无关地管理采集器包的安装事实、Collector Instance 与实际 Activation，并承载统一 Collector Protocol
和 Execution Driver 的运行边界；Desktop Agent 与 Headless Hub 复用同一 Runtime 语义，通过宿主 adapter
提供各自的 Subject 投影、上传、管理与平台能力。它不等同于 Collector 内部可能使用的细粒度组件运行时。
_Avoid_: Plugin Runtime、Package Manager（后者只覆盖制品管理）

**Collector Protocol（采集器协议）**:
Collector Activation 与 Hub 交换身份、能力、配置、生命周期和 Fact 的统一语义契约；它独立于具体传输，也独立于 Package 发布版本。
_Avoid_: 把当前 loopback HTTP 路由集合当成完整协议

**Collector Protocol Client（采集器协议客户端）**:
Collector 侧持有协议会话与未确认交付责任的参与者；它统一承担 Activation 生命周期、消息关联、ACK/重试、Stream Gap、授权、Collector Secret 与 drain，使 Collector 只需表达观测事实。
_Avoid_: 每个 Collector 自行拼装协议状态机、把 Client 与某一种 Transport Binding 混称

**Collector Ingress Queue（采集器入口队列）**:
隔离观测回调与 Collector Protocol 交付背压的 Collector 内部边界。平台 UI、窗口事件和输入 hook 只入队；后台 delivery pump 才能等待持久化、ACK 或重试。
回调返回只承诺进入进程内易失队列，不承诺 durable acceptance；System InputEvent 的后台容量判定必须
在一个 durable journal mutation 中提交 Event 或覆盖它的 Stream Gap，不能用两个独立存储动作制造
“已拒绝但无 Gap”的崩溃窗口。
_Avoid_: 在原生回调或 UI 线程同步等待 Fact 发布

**Stream Gap（流缺口）**:
Collector 对某个 Fact Stream 已知丢失范围的持久事实；稳定 UUIDv7 GapId 是幂等身份，协议 messageId 只标识一次传输尝试。时间使用非空半开区间，同一范围可以有多个不同 GapId，不能仅按 range/reason 去重而吞掉独立丢失。
_Avoid_: 用 messageId 充当 Gap 身份、把相同时间范围自动视为同一丢失、ACK 前删除本地 Gap

**Durable InputEvent Projection（持久输入事件投影）**:
Hub 已提交 Event Fact 后、Analytics InputEvent 上传确认前的持久责任窗口；`InputEventBuffer` 是该窗口唯一容量 owner。满容量返回 backpressure 并保留原 FactId，底层 JSON 文件只做原子持久化、不自行裁剪。
出网每轮只交付有界批次，413 由 Upload Stream 继续拆批；Analytics 确认送达后先从 pending 投影移除、再把稳定 FactId 写入有界 Delivery Receipt，Runtime 重启回放时据此跳过已送达 Event。收据写失败或被裁剪最多造成服务端幂等去重的重复上行，不能导致未送达 Event 被跳过。
_Avoid_: 在 JsonFileCache 与投影层分别封顶、把 count clamp 当成成功、满容量丢 oldest 却不报告 Gap

**Collector Protocol Conformance Suite（采集器协议一致性套件）**:
跨语言共享的可执行协议行为语料，固定生命周期、ACK、重试、Gap 与 drain 结果；各语言实现通过同一组向量证明其 Binding 没有改变协议语义。它不是 wire-message schema，也不是完整请求/响应 transcript。
_Avoid_: 只共享 DTO、以某一种语言实现作为协议本身、把行为语料误当消息格式定义

**Collector Drain Outcome（采集器排空结果）**:
Collector 在同一个绝对 deadline 内停止观测、把 ingress tail 先交给本地 durable first stage / outbox、尝试交付并报告 remainder 的逻辑结果；`drained`、`deadline_exceeded`、`stop_failed`、`flush_cancelled`、`persistence_failed` 是跨 Execution Driver 的稳定 reason。逻辑结果与 Runtime 是否成功接收 completion 分层记录；只有逻辑 reason 为 `drained`、remainder durable、pending facts/gaps 均为零且 completion 成功时才是 fully drained。InProcess 到期 fence 并释放 writer，ManagedProcess 到期终止并释放，ExternalHost 保持 lease revoke 弱语义。
_Avoid_: 把未送达的 `activation.drained` 当成 completion 成功、用 pending=0 掩盖 non-durable/unknown tail、让 deadline 后的旧 Activation 继续写入

**Observation Declaration（观测声明）**:
Collector Package 携带的独立 JSON 声明，描述 Source 的有序观测深度、读数槽位和展示标签。Package loader 先验证其路径、hash、Source 与版本，再由 Hub 原文上行；它不属于 Fact payload，也不从 Fact Schema 或 Output Template 推导。
_Avoid_: Fact Schema、Output Template、运行时自报 declaration

**Artifact Descriptor（制品描述符）**:
Collector Package 中描述一个可验证执行制品的 `*.artifact.json`。它固定入口与内容；Browser ExternalHost 描述符还枚举整个 sideload payload 的路径、大小与 hash。它的内容 hash 由最终 manifest 引用。
_Avoid_: Collector Package manifest、浏览器内部 `manifest.json`、把 descriptor 当成可执行文件本身

**Transport Binding（传输绑定）**:
Collector Protocol 在某种执行边界上的承载方式；不同 Binding 不改变协议语义。
_Avoid_: 为每种执行方式发明不同协议

**Output Template（输出模板）**:
Collector Package 对一类可实例化 Fact Stream 的静态声明，限定其 Source、FactKind、schema、SubjectKind 与 identifying dimensions。
_Avoid_: 运行时任意注册 schema、把每个动态 dimension value 写成新清单项

**Fact Schema（事实模式）**:
可执行事实 payload 的版本化约束。唯一权威文件位于 `collection/contracts/facts/`；Package 内 schema 与最终 manifest 是 build staging 产物。Package 使用原始字节 hash 做完整性校验；baseline 与 Runtime 以解析后的 JSON 含义判断同一 `(SchemaId, Major, Revision)` 是否变化。当前 Collector Protocol v1 只执行 Segment 与 Event。
_Avoid_: 在 Collector C# / TypeScript 里复制 schema 字符串、把私有状态文件的 `schemaVersion` 混入 Fact Schema 版本体系

**Hub Instance（Hub 实例）**:
Collector Runtime 的一个持续运行宿主，可以是 Desktop Agent 内嵌 Hub，也可以是服务器上的无头 Hub。Hub Instance 是运维身份而非观测主体；一个无头 Hub 可以托管观测不同账号、身体或其他主体的多个 Collector Instance。
Desktop 与 Headless 不形成两套 Runtime 领域模型，差异只通过宿主 adapter 表达。
_Avoid_: Device、Subject、把无头 Hub 按某个 Collector 命名、Desktop Runtime 与 Headless Runtime

**Hub Management Surface（Hub 管理界面）**:
由 Hub Instance 自己拥有的用户管理边界，用于需要交互授权的 Collector Instance 设置与恢复。Dashboard 可以提供入口，但 Analytics 不代理第三方账号凭据、授权应答或管理命令。
_Avoid_: Analytics Collector Control Plane、要求用户通过服务器终端完成账号授权

**Host Management Operation（宿主管理操作）**:
Host 接纳并仅在本次 Host 生命周期内持有的一次显式管理变更；请求或 UI 生命周期不拥有已接纳操作，重启也不恢复操作历史。
_Avoid_: Request Task、UI Operation、跨重启持久任务

**Collector Installation（采集器安装）**:
本机完整持有某一精确 Collector Package 的事实；部分下载或未完成目录不是 Installation。安装不表示 Collector 已启用、已获授权、能够激活或正在运行，多个 Collector Instance 可以共享同一份已安装内容。
_Avoid_: Download、Staging、Registration、Active

**Collector Instance（采集器实例）**:
一个稳定、已配置的 Collector 身份；更换其 Collector Package 版本或重新激活时，实例身份保持不变，同样不会因为长期离线而自动删除。同一采集器包可以对应多个实例，但 App 或 Profile 只有在确实拥有独立启用、配置或卸载意图时才形成 Instance。Browser 的一次 Marketplace 安装只创建一个 Machine-scoped Instance；兼容浏览器和 Profile 是这个 Instance 下的 External Host，不形成额外 Instance（ADR-051）。删除身份与配置只能是显式用户操作。
_Avoid_: 用 Source、进程 ID 或包版本充当实例身份

**Collector Instance Key（采集器实例键）**:
Hub 为需要幂等发现的 Collector Instance 分配的不透明、不可变稳定槽位；它在同一 `PackageId + Subject` 内唯一，不替代 CollectorInstanceId。App 和 External Host Identity 不充当 Instance Key；它们属于 Instance 下的实际运行与 Stream 身份。
_Avoid_: 第二个 CollectorInstanceId、Instance Config、把 Source 当作 Instance Key

**Collector Activation（采集器激活）**:
Collector Instance 的一次协议会话和实际运行身份；重启、重连、成功或失败都不改写 Instance 身份。ExternalHost Instance 可以同时拥有多个 Activation，各自代表一份独立运行的外部宿主；同一 External Host Identity 的新 Activation 只接替该 Host 的旧会话，不影响同 Instance 下的其他 Host。
_Avoid_: Run（含义过泛）、Active（后者是按 Source 流量推断的既有状态）

**Collector Activation Lifetime（采集器激活生命周期）**:
从 Hub 接受一次 Activation 到其 writer/lease 最终释放的单一所有权事务；Hub 与 Collector Protocol Client
在协议两侧各自拥有生命周期，彼此交换结果而不形成跨进程 owner。
_Avoid_: 多个 caller 各自 Stop/释放、跨进程 Lifecycle Coordinator、Artifact Delivery

**Collector Delivery Ownership（采集器交付所有权）**:
Collector Protocol Client 对未确认 Fact/Stream Gap 的保留与交付责任；drain 时它从 background 原子转移到
有绝对 deadline 的 drain owner，fenced 后不能再 ACK 或改变 durable responsibility。
_Avoid_: background pump task、用 CancellationToken 或异常消费顺序表示 ownership

**Interactive Authorization（交互授权）**:
Collector Activation 通过 Collector Protocol 请求用户完成的一次非阻塞授权交互。Collector 声明当前 challenge，Hub Management Surface 收集应答；敏感应答只在内存中传递，不成为配置或运行状态。
_Avoid_: Hub 内置第三方登录流程、把登录设为 Dashboard 的强制前置步骤

**Collector Secret（采集器秘密）**:
按 Collector Instance 隔离保存、供 Collector 恢复第三方会话的不透明秘密。它与 Collector Runtime State 分库存放，不进入 Manifest、Fact、普通日志或用户配置。
_Avoid_: Instance Config、Runtime State、在 Package 目录保存 cookie

**External Host Identity（外部宿主身份）**:
ExternalHost Collector 中一份独立宿主安装的内部稳定身份，例如某个浏览器 Profile 中加载的扩展；它由外部宿主首次运行时生成并本地持久化，区分并发 Activation，并使同一 Collector Instance 下的各 Host 拥有独立 Fact Stream、writer 与重传连续性。宿主数据被清理或重装后产生新的 External Host Identity 与 Stream，不根据 Profile 名、路径或进程猜回旧身份；旧 Stream 只变为不活跃。它不形成独立 Collector Instance、启用意图或主 UI 管理项。
_Avoid_: Collector Instance、Browser Profile 设置、把外部宿主身份暴露为用户必须管理的采集器

**Waiting for External Host（等待外部宿主）**:
ExternalHost Collector 的 Installation 与 Desired Instance 已存在，但当前没有连接中的 External Host Activation。它是 Instance 层的如实等待状态，不是 Activation 的启动阶段或运行故障；管理界面简写为“等待连接”。
_Avoid_: Starting、Failed、Not Installed

**Browser Window Activity（浏览器窗口活动）**:
browser Collector 对每个浏览器窗口当前所选 active tab 的如实观察，不判断该窗口或浏览器是否处于操作系统前台。多个窗口、浏览器或 Profile 的活动可以合法重叠；它们是对应 App 的内部细节，不是可求和的用户注意力或使用时长。
_Avoid_: 浏览器前台活动、把 active tab 解释为 OS 前台窗口、按 Host/Profile 拆成用户事实

**Ready（激活就绪）**:
Collector Activation 已完成协议协商并打开所需 Fact Stream，可以承担运行责任；Ready 不要求已经产生第一条 Fact。
_Avoid_: 进程存活、首次产生数据、Active

**Collector Desired State（采集器期望状态）**:
用户对 Collector Instance 的启用、配置与要运行的精确 Collector Package reference 的意图；暂时的安装或运行失败不会改写它。channel、版本范围与自动解析不属于当前范围。
_Avoid_: 把当前运行事实反写成用户意图

**Collector Runtime State（采集器运行状态）**:
Collector Runtime 对当前 Collector Activation 的阶段、健康结果与失败原因的观察事实。运行状态可随重试和宿主变化而改变，不是用户配置。
_Avoid_: Desired State、Active（后者只回答某 Source 最近是否有流量）

**Artifact Delivery（制品交付）**:
谁负责把 Collector Package 交付给运行位置并维持其版本；它与谁执行代码相互独立。
_Avoid_: Lifecycle Ownership（把交付与执行混成单轴）

**BuiltIn Delivery（内置交付）**:
Collector Package 随宿主应用一起构建和发布的 Artifact Delivery；System Collector 随 Desktop 升级，不进入 Web 更新候选或逐 Instance 包审批。
_Avoid_: 把 System 当作可远程替换的独立 Package、把 BuiltIn 等同于 InProcess

**Official Collector Package Registry（官方采集器包注册源）**:
发布方为非 BuiltIn 官方 Collector Package 提供的 Catalog、版本目录与制品来源；Catalog Latest 只表示 Registry
当前推荐下载的精确 Release，不保存某个 Host 的 Desired State，也不承担 Installation 或 Activation。
_Avoid_: Collector Registry（旧 source 级配置账本）、Analytics 控制面、Update Offer、审批系统

**Collector Catalog（采集器目录）**:
Official Collector Package Registry 公开的可安装 Collector 清单，每个条目包含展示信息和当前最新版的精确
Release；它是 Web 发现事实，不是某个 Host 的已安装清单或运行配置。
_Avoid_: Installed Collectors、Runtime State、把 Catalog Latest 叫作当前运行版本

**Default Instance Blueprint（默认实例蓝图）**:
Collector Package 对“一键安装后创建哪种默认 Instance”的通用声明，包含 SubjectKind、configVersion 与默认
config；它属于 Package，不包含某个用户的 InstanceId、SubjectId、Secret 或运行状态。
_Avoid_: Headless Instance JSON、Hub 内的 PackageId 分支、用户填写内部 Instance 参数

**Collector Marketplace（采集器市场）**:
Host 用于浏览官方 Catalog，并把一个 Catalog Latest 安装成精确 Installation 与默认 Instance 的通用交互；
它不允许任意 URL 安装，也不负责已安装 Package 的版本更新。Desktop 与 Headless 共用同一个 Marketplace
Runtime；该 Runtime 拥有 Driver dispatch、Activation、恢复、状态、重试与卸载，宿主 adapter 只提供 Subject
与本地投影挂载。
_Avoid_: Update Manager、Package URL 输入框、具名 Collector 安装器

**Collector Package Release（采集器包发布）**:
某个非 BuiltIn Collector 通过自己的显式 tag 产生并在 Web 静态目录公开的不可变精确版本。Release metadata
绑定 PackageId、Version、目标平台、artifact URL、字节长度与 SHA-256；它不表示“当前版本”，也不表示 Host
已下载、安装或激活。当前第一条真实纵切是 VRChat `linux-x64`。
_Avoid_: Desktop Release、Update Offer、把 release.json 当作 mutable current pointer

**Execution Driver（执行驱动器）**:
Collector Runtime 协调 Collector Activation 的方式，可以是进程内、托管进程或外部宿主；它不表示 Runtime 一定持有对应制品。
_Avoid_: Lifecycle Driver（未区分制品交付）、假定 Hub 能直接停止所有 Collector

**External Host App Identity（外部宿主应用身份）**:
ExternalHost Collector 对承载自身的应用所作的稳定平台身份声明，以 `AppIdentityKey` 表达。Collector 自己拥有识别知识并随 `hello` 和 Stream dimension 直接提供该值；Host 不把产品 slug 转译成平台身份。它与 External Host Identity 一起确定 Stream 身份，但不形成 Collector Instance。Backend 暂时不认识某个 Key 时仍保留真实值，不阻断事实。
_Avoid_: App Hint、Host 侧产品映射、用显示名称或进程名代替稳定 AppIdentityKey

**Upload Stream（上传流）**:
泛化的出网流（ADR-020/022）：绑定一个出网源（IUploadSource），drain 一轮 = 先重传离线缓存，再取一个 adapter-defined 有界 fresh 批出网——送达，或落离线缓存，否则自动重注入源（重注入不回滚更新的快照）。413 会递归拆批，已成功子批不再重试；单项仍过大则保留重试。"批次不蒸发"是流自持的不变量。compact 为按流策略（segments 出网前压缩快照，input-events 不压缩）。segments 与 input-events 各一实例。
_Avoid_: UploadService（退役的三份同构模板）、Upload Channel（ADR-022 前的旧名，彼时退回项由调用方重注入）

**Segment Rotation Boundary（段轮换边界）**:
连续观察在摄入最大时长之前封口当前 Segment Fact、并从同一 instant 开始新 Fact 的共享边界；轮换前后 union 连续且不重叠。
_Avoid_: 等服务端拒绝后切段、各 Collector 自定阈值、复用旧 FactId

**Active（采集器活跃）**:
旧 source 级读模型术语。统一 Runtime 后，Collector 的实际状态由各 Instance/Activation 的 Runtime State 与 lease 表达；ExternalHost lease 过期只结束对应 Host Activation，不改写 Desired State，也不删除 Package、Instance 或历史 Stream。

**Collector Registry（采集器注册表）**:
历史 source 级配置/声明账本；不再作为 browser 的安装、启用、发现或协议准入来源。新代码应依赖 Collector Installation、Instance Desired State、Runtime State 或更窄的声明存储 seam。
_Avoid_: 为新 Collector 恢复 source 级自动注册/config HTTP 协议

**Current Activity（当前活动）**:
集面读模型中"此刻在干什么"的条目：由 system 采集器在转场点（前台切换、进出 away）把 AppIdentityKey 推送进 hub，进程内事件驱动、零延迟；away 原样暴露为 `sys:away`，语义解释留给消费者。桌面 UI 与 Heartbeat 的唯一数据源。
_Avoid_: 从段流量派生（快照节律 + ≥1s 噪声闸门使派生值在转场后最长 30s 指向上一个 app，ADR-021 否决）

**Heartbeat（心跳）**:
presence 通道：周期 keepalive（活性，间隔为代码常量、不进配置）+ 变了就推（新鲜度），Current Activity 搭车上行。无缓存无重试（易逝信息，下一个心跳自然覆盖）。服务端在线窗口 ≥ 2× keepalive 间隔（ADR-021）。
_Avoid_: Status Upload（旧名，只描述了周期维度）

**Interaction Signal（交互信号）**:
只存在于本机内存中的最近点击信号，用于同窗标题变化的噪声门控；不持久化、不上传。它是缺少专用 Collector 时的可选 fallback，Windows 与 macOS 都由用户独立开关；UI 使用直白名称“点击辅助判断”并说明其 fallback 与本地瞬时性。它与 InputEvent Recording 可以共享底层平台 hook 或系统权限，但启用交互信号不代表允许保存输入事件。
_Avoid_: InputEvent（后者是持久化并上传的事实流）

**前台应用采集（Foreground App Collection）**:
system 采集器不可关闭的基线观测深度：记录当前前台 AppIdentity 与 away 转场。即使所有可选深度能力关闭，system 仍以该基线持续工作。
_Avoid_: 把它呈现为 system 总开关或可关闭的采集能力

**窗口活动采集（Window Activity Collection）**:
system 采集器的一项可选观测深度，统一包含 focused-window 切换与原始窗口标题观测。同窗标题变化是否切段由 Interaction Signal 的点击门控决定，不影响 AppIdentity 激活或 focused-window 切换。
_Avoid_: 把聚焦窗口与标题拆成两个用户能力开关

**采集能力（Collection Capability）**:
某个 Collector 拥有的一项用户可理解观测深度。固定基线没有开关；可选能力把用户的启用意图与实际运行状态分开，权限缺失、撤销或依赖未满足会暂停能力，但不改写用户意图。
_Avoid_: 用一个 bool 同时表示用户开关、权限与实际可用性

**Deactivate（停用采集器）**:
用户把某个 Collector Instance 的 Desired State 设为 `enabled=false`；Collector Runtime 负责停止或拒绝该 Instance 的 Activation，并保留 Installation、Instance 身份与配置。ExternalHost 的停用只约束对应 Instance，不以 Source 级全局开关代替；主卡上的“全部启用/停用”只是对多个 Instance 执行批量变更，不形成另一份 Desired State。

**采集器页（Collector page）**:
共享桌面 UI 中管理采集器的页面，并容纳采集器设置。System BuiltIn 不进入 Marketplace：system 采集器不可停用，前台应用采集作为无开关的固定基线，其他可选观测深度作为独立采集能力管理。每项能力的开关、实际状态、权限恢复动作与说明都归属 System 条目，不另建脱离所有者的全局“采集能力”区块。窗口活动采集是一个用户能力，不把 focused-window 切换与原始标题拆成两个开关。

通用 Marketplace / ExternalHost UI 已实现（issues 06/10），由 Catalog、Installation、Instance 与 Runtime State 驱动通用条目：主卡只显示安装事实、“等待连接”、“运行中 · N 个连接”或简短错误，Activation、External Host Identity 与协议错误只进入展开诊断。Browser 仍没有已发布的 Desktop Package，所以当前 Catalog 不会凭空出现 Browser 卡片。Host 不提供 Package 人工加载说明、打开目录或具名 App 子项（ADR-051）。
_Avoid_: 采集器栏、Collector panel、为某个具体 Collector 在宿主 UI 写死卡片

**Setup**:
Velopack 生成的安装器（Setup.exe），用户首次安装时下载运行。
_Avoid_: Installer

**Release**:
一次发布产物的集合，包含 Setup、完整包（.nupkg）和元数据文件（RELEASES），托管在 GitHub Releases。
_Avoid_: Build, Package

**Update**:
应用检测到新版本后，下载增量/完整包并在用户确认重启后应用的过程。生命周期为 Idle → UpdateAvailable → Downloading → ReadyToApply，下载失败退回 UpdateAvailable（携带失败原因）；**只有 ReadyToApply 的更新才允许被应用**。"检查"是瞬时动作而非状态，结果三分：UpToDate / UpdateFound / CheckFailed——检查失败 ≠ 已是最新。
_Avoid_: Upgrade, Patch; Pending Update（旧名，混淆了"发现"与"已下载"）

**Desktop Exit Intent（桌面退出意图）**:
用户请求桌面 Agent 结束本次运行的目的：普通退出或更新重启。首次接受后固定；重复请求共享结果，
冲突意图不能触发第二次最终动作。关闭设置窗口仍只是隐藏 UI，不形成退出意图。
_Avoid_: 用窗口关闭表示 Agent 退出、把共享等待任务等同于安装器等副作用已经唯一化

## Relationships

- 一个 **Release** 包含一个 **Setup** 和一个完整包
- **Agent** 在应用生命周期内持续运行，**Update** 需重启应用才能生效
- `Heartbeat.Collection.Hub` 提供纯 .NET 的 hub 运行时（loopback ingest、Collector Registry/declaration、认证客户端、段缓冲、Current Activity、Upload Stream、presence 与缓存 seam），可由桌面或无头 host 组合，不依赖桌面采集、UI、平台 API 或发布供应商
- `Heartbeat.Collection.Headless` 是带 owner-only 管理 API 的无头 Web host：一个 Collector Runtime 从本地配置托管多个 ManagedProcess Collector Instance；深 `HeadlessInstancePipelines` module 按 Instance 吸收投影、当前状态、Analytics 上传身份、缓存与终态 drain，Fleet 不接触这些实现；服务器 Machine 身份不进入 Fact 归属
- `Heartbeat.Collector.System` 消费 App 激活、focused-window 切换、同窗标题变化与 away 等语义观察，产出 system ActivitySegment；平台 adapter 不把原生回调形状泄漏进状态机
- `Heartbeat.Desktop.Updater.Velopack` 统一承载 Windows/macOS 的 Velopack Update 生命周期（检查、下载、重试、ReadyToApply 门控与调度应用），并作为供应商依赖防火墙；platform head 只选择 Release channel，并在 updater 成功启动后停止 Agent、退出当前进程
- `Heartbeat.Desktop.Windows` 组合 Win32 观察、MachineGuid、图标、自启动、共享 Avalonia UI、托盘与 Velopack Update
- `Heartbeat.Desktop.Mac` 组合 NSWorkspace App/硬 away 观察、IOPlatformUUID、bundle 图标、共享 Avalonia UI、菜单栏 accessory 生命周期与逐用户 login start
- `Heartbeat.Desktop.UI` 是共享 Avalonia presentation module；ViewModel 只依赖 platform-head seam，可在不创建原生窗口时测试

## Flagged ambiguities

- "安装" 既指首次 Setup 安装，也指更新后的应用替换 — 统一用 **Setup**（首次）和 **Update**（后续）区分。
- "插件" 曾与 **Collector** 混用（口语、UI、ADR-017 中的 "plugin"）— 已统一：唯一规范术语是 **Collector（采集器）**，UI 栏与文档一律用"采集器"。
