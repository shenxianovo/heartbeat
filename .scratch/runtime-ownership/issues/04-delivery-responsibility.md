# 04 — 数据交付责任可判定

**What to build:** 数据交付责任可判定，覆盖已确认的运行期行为。

**Blocked by:** None — can start immediately

Status: done

- [x] 已有持久化及 drain 结果不在 hosted adapter 丢失
- [x] 结果区分已交付、本地保管与未知余量，不从异常形状推断安全
- [x] 正常上传、暂停、失败与终止时每个未交付批次都有责任证据

## Verification

2026-09-07 第一轮：实现与自动验证完成。

- `dotnet build Heartbeat.slnx --no-restore`：0 warning / 0 error。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- `dotnet test Heartbeat.slnx --no-restore --verbosity minimal`：13 个项目，1,182/1,182 通过。
- TDD 回归覆盖传输抛异常后保管、拆批部分成功与暂停、dead-letter 不算送达、超限拒写、未 drain 的
  InputEvent 余量、Segment 淘汰证据及同 Id 回流、hosted 结果保留、离线卸载与清理失败后重试。
- 既有周期/终态上传互斥与取消回归通过；Headless 复用同一调度实现，截止后仅完成本地保管。
- Standards / Spec 双轴审查以本片起点 `da70af8` 为基线，发现项均修复并复核，最终均为 0 项未解决。

本片只提供 owner 交接结果，不宣称应用退出安全策略已完成。跨子树汇总、最终动作和未知余量的
用户恢复仍由 05 承接；01 的原生设备人工验收保持原门禁。Input Recording 关闭时仍不上传既有输入缓存，
本轮未尝试交付时不沿用此前成功结果。

## Implementation scope

用户确认本片以解耦与可维护性为目标：责任判断归数据 owner，Host 只消费结果；不建设新的
队列、恢复 UI 或退出策略。优先替换重复路径，分别报告生产代码、测试和文档增删，不以行数
抵消必要的责任证据。未交付数据不能因卸载被删除；当前采用无法安全删除就失败并保留数据的
最小实现。验证沿用 PRD 已确认的 Fact/Upload 接口与真实 Host 组合。

## Friction closeout

ADR-022 的旧返回契约、ADR-052 的实施进度与领域词汇已同步。缓存容量不再静默删除未交付数据，
已送达收据的裁剪回归其 owner。第一轮的 Segment 淘汰账目已在下述深化中删除，不新增在线回放框架。

## 2026-09-07 bounded deepening

用户授权继续深化本片后再进入 05。退出条件：上传流不按持久源类型分支；源保管待确认快照；
删除旧重注入与补偿路径；Fact/Upload 及真实 Host 回归通过。Segment 合并现有缓存与实时快照，
InputEvent 沿用持久投影，历史输入缓存保留同契约读取支持。此轮不实施 05。

- [x] 统一保留读取与显式确认，删除 IDurableUploadSource
- [x] Segment owner 接管缓存，删除重注入版本戳与淘汰账目
- [x] 完成回归、双轴审查与最终验证记录

重命名 inventory 定位到 UploadDrainResult 的两处 FreshItems 引用。Roslynator dry-run 在
Workspace.TryApplyChanges 抛 NullReferenceException，未写入文件；按已枚举的两处引用手动改为
Items，再由构建与回归验证。接口不属于序列化 DTO 或原生 ABI。

### 深化验证与审查闭合

- 最终 `dotnet test Heartbeat.slnx --no-restore --verbosity minimal`：13 个项目，1,173/1,173 通过。
- Browser 真实 Host 跨语言集成回归：5/5 通过；命名 inventory 与 diff whitespace 检查通过。
- 最终 solution build：0 warning / 0 error。
- 本轮 diff：生产 +172/-372（净删 200）；测试 +434/-803（净删 369）；文档另计。
- 保留读取/旧快照确认、多个源的确认故障汇总均先确认失败再修复。
- Standards 与 Spec 分别指出 1 项相同的容量责任问题：Segment 投影在 Fact 提交后执行，投影异常
  不会返回 Collector Retry，不能在此以容量拒绝冒充背压。通过真实 Fact 接纳、历史缓存合并、重启
  replay 和 20,000 + 1 有界上传复现后修复。最终复核两个轴均为 0 项未解决。
- Source 保留全部已接收 backlog，只有上传请求限批；Runtime 的真实接纳背压不变。长期离线的
  内存/磁盘成本及未来容量策略应落在可重试接纳入口的约束已写入 ADR-022。

本轮退出条件已达到，04 完成；不进入 05。01 的原生人工验收及 05 的最终动作仍是 PRD 剩余工作。
