# ADR-022: Upload Stream 自持退回重注入

## Status: Accepted

## Date: 2026-07-11

> 2026-09-04 implementation note: `Drain()` 返回下一个 adapter-defined 出网批，不再承诺清空整个 backlog。InputEvent durable projection 每批最多 5,000 条并保留其余项；Upload Stream 对 413 递归拆批，只保留未送达项。Analytics 确认送达的 InputEvent FactId 另存有界 Delivery Receipt，阻止 Collector Runtime 启动回放把最近的已送达 Event 再塞回 pending；提交顺序保证收据故障最多导致幂等重传，不会吞掉未送达数据。

## Context

ADR-020 §5 的上传通道契约是"送达，或落离线缓存，否则原样退回——退回项由调用方重注入源 buffer"。2026-07 的架构评审发现这把"drain 后的批不静默蒸发"不变量摊在三个文件里：通道退回（`UploadChannel`）、编排者重注入（`UsageUploadWorker`）、两个 buffer 各自的幂等重入。理解"上传和缓存都失败会怎样"要同时读三处；而编排这一切的 `UsageUploadWorker` 构造依赖 6 个具体类（含拖着输入钩子的 `InputEventCollector`），防丢分支与 StopAsync 终态 drain 恰好是零覆盖区——ADR-020 §6 用注释钉住的"托管服务注册顺序翻转防丢尾巴"没有任何测试保护。

两条流的 buffer 还各说各话：hub 是 `GetAndClearSegments`/`Accept`，输入侧是 `DrainAll`/`Requeue`（且藏在 collector 的两个纯转发方法后面）。worker 是唯一同时认识两套词汇的地方。

顺带一个现存缺陷：上传失败的退回批经 `Accept` 重注入是 last-write-wins——批次在外面失败期间，hub 若已收到同 Id 的**更新**快照，退回的旧快照会把它覆盖（本地最长回滚 30s，下个快照追平；服务端有单调生长门兜底，ADR-018）。

## Decision

### 1. `IUploadSource<T>` seam：出网源的统一词汇

`Drain()`（破坏性取走）+ `Reinject(items)`（双失败退回）。hub 与 `InputEventBuffer` 以显式接口实现挂载，公开方法名不动（hub 词汇统一属 ADR-021 实施范围）——两个生产 adapter，真 seam。`InputEventBuffer` 升格 DI 单例：collector 只剩钩子生命周期（写入方），出网侧直接 drain buffer，两跳变一跳。

### 2. `UploadChannel` 演进为 `UploadStream`：不变量收进一个类

流绑定自己的源。`DrainAsync()` 一轮 = 先重传离线缓存（成功清空，失败保留，ADR-008），再取 fresh 出网——送达，或落离线缓存，否则**流自己重注入源**。调用方从"必须记得重注入"（跨文件义务）变为"调用即安全"。`UploadAsync`/`UploadCachedAsync` 降为私有——接口收窄到一个方法。

`DrainAsync` 返回本轮从源取走的 fresh 批（无论结局，调用方只读）：图标挂点从中提 AppName，行为与 ADR-020 §6 一致（上传失败也触发）。图标不迁入 hub 事件——网络 I/O 不进 `Accept` 的订阅链。

### 3. 重注入不回滚

hub 的 `Reinject`：缺席才插入，在位者保留 EndTime 更晚的快照——与服务端单调生长门同一条规则（ADR-018），客户端侧补齐对称性，消除 30s 回滚窗口。退回批**不再过门卫**：已过一次校验，重过滤可能丢数据，违背不蒸发不变量。输入事件无快照概念，保 Id 回队不变。

### 4. cached-first 从跨流全局变为每流局部

原顺序（input-cached → segment-cached → input-fresh → segment-fresh）变为每流内部 cached → fresh。时序有序性只需流内保证，两条流写不同的表，跨流顺序无意义。

### 5. worker 退化为调度器

循环体收敛为 `DrainOnceAsync()`（两条流各一轮 + 图标挂点），StopAsync 终态 drain 复用同一入口。更名（"usage"化石）与 worker 测试属后续切片。

## Consequences

- ✅ "批次不蒸发"从三处未测的跨文件义务收敛为一个类 + 一份契约测试（双失败重注入、cached-first、compact 只作用于 cached 首次全部钉死）。
- ✅ 出网侧统一词汇；collector 纯转发层消失；30s 本地回滚窗口消除。
- ✅ worker 依赖从 6 个具体类降到 4 个，可测性前提就位（注册顺序脆弱点将由 worker 测试钉住）。
- ⚠️ 跨流 cached 顺序变化（无关正确性，两条流不同表）。
- ⚠️ 改名 churn：`UploadChannel` → `UploadStream`（类、测试、词条），Upload Channel 旧词标 _Avoid_。

## References

- [`collection/hub/Heartbeat.Collection.Hub/Upload/IUploadSource.cs`](../../collection/hub/Heartbeat.Collection.Hub/Upload/IUploadSource.cs) — 出网源 seam（§1）
- [`collection/hub/Heartbeat.Collection.Hub/Upload/UploadStream.cs`](../../collection/hub/Heartbeat.Collection.Hub/Upload/UploadStream.cs) — 上传流：drain 一轮自持不变量（§2）
- [`collection/hub/Heartbeat.Collection.Hub/Segments/SegmentIngestService.cs`](../../collection/hub/Heartbeat.Collection.Hub/Segments/SegmentIngestService.cs) — hub 的 Drain/Reinject adapter，不回滚规则（§3）
- [`collection/desktop/Heartbeat.Collector.System/Input/InputEventBuffer.cs`](../../collection/desktop/Heartbeat.Collector.System/Input/InputEventBuffer.cs) — 输入侧 adapter，共享单例（§1）
- [`collection/hub/Heartbeat.Collection.Hub/Runtime/UploadWorker.cs`](../../collection/hub/Heartbeat.Collection.Hub/Runtime/UploadWorker.cs) — 调度器 + 图标挂点（§5，更名自 UsageUploadWorker）
- [`collection/hub/Heartbeat.Collection.Hub.Tests/Upload/UploadStreamTests.cs`](../../collection/hub/Heartbeat.Collection.Hub.Tests/Upload/UploadStreamTests.cs) — 流契约测试（§2）
- [`collection/desktop/Heartbeat.Desktop.Windows.Tests/Hosting/AgentHostExtensionsTests.cs`](../../collection/desktop/Heartbeat.Desktop.Windows.Tests/Hosting/AgentHostExtensionsTests.cs) — 注册顺序不变量测试（ADR-020 §6 脆弱点）
- Amends [ADR-020](./020-system-collector-through-hub.md) §5 —— "退回项由调用方重注入"收进流自持；通道其余契约（送达/缓存/compact 按流策略）不变
- [ADR-018](./018-stable-segment-identity-snapshot-upload.md) —— 不回滚规则是其单调生长门在客户端的对称
- [ADR-008](./008-local-cache-offline-retry.md) —— 缓存重传语义不变
