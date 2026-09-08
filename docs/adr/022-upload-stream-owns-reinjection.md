# ADR-022: 上传源保管数据，Upload Stream 确认交付

## Status: Accepted

## Date: 2026-07-11; amended 2026-09-07

## Context

ADR-020 原来要求调用方把上传、缓存都失败的批次重注入 buffer。最初本 ADR 把这项义务收进
Upload Stream，但普通源的破坏性 Drain 与持久源的保留读取仍有两套语义：流必须区分源类型，
Segment 还需维护重注入版本戳与淘汰补偿账目。

Ticket 04 的用户确认目标是解耦与可维护性，采用便于演化的结构。此次修订替代原先的
Drain/Reinject 决定，不建设新的队列、恢复 UI 或完整退出策略。

## Decision

### 1. 上传源保留快照直到确认

`IUploadSource<T>` 统一为 `ReadBatch()` + `Confirm(items)`。读取不转移保管责任；只有 Analytics
接收或成功写入 dead-letter 的项才被确认。确认失败允许按稳定 Id 重传，不能丢失未确认项。
源同时提供当前 `Remainder` 与存储诊断，上传流无需知道源使用内存、文件还是持久投影。

SegmentIngestService 拥有 Segment 缓冲和现有 segments-cache 文件。启动压缩历史缓存后按 Id
合并实时快照；读取时尝试持久化，失败仍保留内存并允许发送；确认先提交剩余缓存，再释放对应
内存快照。确认通过对象身份只移除当时发送的版本，不删除发送期间到达的更高 Revision。
原来跨重注入保存的弱引用版本戳与淘汰账目删除。读取每批最多 20,000 项，缓存与内存保留全部
未确认 backlog。Fact 接纳限制仍在 Runtime 提交之前；Segment 投影发生在 Fact 提交之后，此时
抛出“背压”无法让 Collector 重试，因此不能再以独立的 20,000 项存储上限拒收历史缓存与
Runtime replay 的并集。Revision watermark 与重放协议保持原职责。

InputEventBuffer 的内存模式和持久模式都采用保留读取，每批最多 5,000 项，未读 backlog 仍由源
保管。持久投影先移除已确认项，再写有界 Delivery Receipt；收据故障或裁剪最多导致稳定 FactId
的幂等重传。现有投影 commit fence 不变。

历史 input-events-cache 文件由 CachedUploadSource 提供同一读取/确认接口，排在当前 InputEvent
投影之前。它只服务已有版本遗留的重试数据，新输入不再写该文件。移除条件是完成已有安装中
这类文件的交付或显式迁移，并保留启动重放、确认失败和重启不重复的验证；不能仅因新版本不写
它就删除读取支持。

### 2. Upload Stream 只编排传输与确认

每轮依次读取源、发送、确认已处理项。Segment 的历史和实时项已经在 owner 内合并，不再分成
缓存与 fresh 两次请求；InputEvent 历史文件仍先于当前投影。缓存读写、容量和保管证据属于源。
400/422 拆批定位坏记录，413 拆批缩小请求，单项仍过大则保留。426 暂停出网，但 Segment 源仍可
持久化新项。取消贯穿拆批和 HTTP，期限到达后停止发送，源保留未确认项。

`UploadDrainResult` 报告本轮读取项、Analytics 接收数量及各 owner 的当前余量，供 hosted adapter
和图标挂点消费。dead-letter 属于本地保管，不计作 Analytics 接收。余量包含未读取的 backlog，
无法枚举时保留未知；下一轮重新读取 owner 证据，不用永久置空的 latch 代替真实状态。
副本数不能当作跨存储唯一 Fact 数。`LastDrain` 在执行期间为空，末次结果也不能代替停产后的交接。

### 3. Worker 只调度

Desktop 与 Headless 复用 PeriodicUploadWorker，退出先取消并等待周期上传结束，再以退出期限执行
终态上传。Input Recording 关闭时仍按既有策略不读、不上传输入 backlog，不能沿用旧的成功结果。
Ticket 05 的应用退出汇总读取停产后各源的当前保管证据；关闭的 Input Recording 也参与安全判定，
但不会因此恢复上传。Ticket 06 将 Desktop 改为 best-effort：未知余量记录风险后也继续退出，
不改变余量证据；本地持久余量始终不阻止退出。Desktop/Headless 的终态交付轮次共享 5 秒联网
预算，取消后仍尝试本地持久化；不新增退出层重试，也不把该预算当作整个 Host 停止硬期限。

## Consequences

- 删除源类型分支、重注入、Segment 淘汰补偿和上传流中的独立缓存提交路径。
- 保管证据可随源恢复而重新判定；晚到确认不会覆盖或移除新快照。
- Segment 待确认项与磁盘缓存由同一 owner 维护；缓存提交失败可能重复发送已被 Analytics 接收的
  稳定 Id。投影阶段不能伪造接纳背压；长期离线 backlog 会占用对应内存和磁盘。未来容量策略需
  落在实际可重试的 Fact 接纳边界，本片不添加新队列或补投影调度。
- 验证围绕真实源的 Fact/Upload 边界、HTTP 契约及真实 Host 组合，不模拟两套保管策略。

## References

- [IUploadSource](../../collection/hub/Heartbeat.Collection.Hub/Upload/IUploadSource.cs)
- [UploadStream](../../collection/hub/Heartbeat.Collection.Hub/Upload/UploadStream.cs)
- [SegmentIngestService](../../collection/hub/Heartbeat.Collection.Hub/Segments/SegmentIngestService.cs)
- [InputEventBuffer](../../collection/desktop/Heartbeat.Collector.System/Input/InputEventBuffer.cs)
- [UploadStreamTests](../../collection/hub/Heartbeat.Collection.Hub.Tests/Upload/UploadStreamTests.cs)
- Amends [ADR-020](./020-system-collector-through-hub.md) §5 的破坏性读取/重注入约定
- [ADR-018](./018-stable-segment-identity-snapshot-upload.md) 的稳定 Segment Id
- [ADR-008](./008-local-cache-offline-retry.md) 的离线保管与重传
