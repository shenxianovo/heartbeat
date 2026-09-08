# 05 — 应用操作统一接纳与最终动作

**What to build:** 应用操作统一接纳与最终动作，覆盖已确认的运行期行为。

**Blocked by:** 01、02、03、04

Status: done

- [x] 应用退出意图固定后拒绝冲突变更
- [x] 各子树结果汇总后才进入最终动作，副作用不重复
- [x] 未决退出交互与更新提交策略作为明确承接项，不以本片完成冒充全产品验收

## Confirmed scope (2026-09-08)

以解耦与可维护性为目标：复用既有 owner，删除被替代的编排，不建设通用任务框架。
生产代码、测试与文档分别统计增删，不以净删除代替维护性判断。

- Q1：安全检查与停止完成后才调度更新；安装器调度失败记录错误并正常退出，不恢复原采集会话。
- Q2：无法确认数据持久化时，阻止安装器与原生退出，保留明确结果和关闭的接纳入口。
  本片不实现重试／强退交互；因此未知余量可阻止自动退出，不能宣称完整退出体验完成。
- 离线或未上传完本身不阻止退出；本地余量已确认持久化即可退出，清理错误独立记录。
- 沿用 PRD 已确认的应用退出、Host 管理命令、真实 Host 组合和 Fact/Upload 测试边界。
- 开始实施时 HEAD 为 `9919a40e5524cda4edd6462f5df2812beab690ba`。实施期间有独立变更合入；
  本次审查以 `c3b0f6b` 为固定点，仅覆盖本任务提交 `9058e9a`、`259b3a5`，排除交错的独立提交。

## Verification

实现、自动验证与双轴审查完成。本片的产品限制由 06 承接。

- `dotnet build Heartbeat.slnx --no-restore`：0 warning / 0 error。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- `dotnet test Heartbeat.slnx --no-restore --verbosity quiet`：1,188/1,188 通过。
- 回归覆盖相同／冲突意图、关闭接纳、未知数据阻止最终动作且不导致 UI 未处理异常、安全数据伴随
  清理失败仍退出、并发更新只调度一次、安装器失败正常退出、关闭输入记录的持久 backlog。
- 真实 Mac Host 组合使用隔离目录和可控原生 adapter，离线产生并保留末尾数据后正常退出。
  这不是安装到真实设备后的原生窗口／安装器验收，后者由 06 承接。
- 本次没有执行新的 solution-wide rename；旧 updater preparation 接口由唯一最终动作替代，无兼容路径。

- Standards：首次发现通知异常能跳过停机，先红后绿验证后修复；复审 0 项未解决。
- Spec：首次发现再次 Install 丢弃旧 Activation 未知证据，真实 ManagedProcess 回归复现后修复；
  Install／Retry 统一保留退休证据，复审 0 项未解决。
- 按审查建议将事件回调内的断言移到退出完成之后，避免断言被通知隔离吞掉；单测通过。
- 生产代码 +354 / -122（净 +232）；测试与文档分开统计，不以总 diff 行数冒充解耦指标。
  净新增用于原本缺失的接纳和安全结果边界；平台重复更新编排与旧 preparation 接口已删除。

## Lifecycle / friction closeout

- 05 的三个验收项完成，PRD 仍为 `ready-for-human`；01 原生验收和 06 恢复／安装验收尚未完成。
- 06 是剩余策略与真实验收的唯一承接项，没有另建重复 backlog。
- ADR-003/022/052 与 Collection glossary 已同步当前行为；未加入没有退出条件的兼容路径。
- 本任务只提交相关路径；工作区的 frontend、计数脚本和 release design 独立变更不属于本任务。
