# 05 — 应用操作统一接纳与最终动作

**What to build:** 应用操作统一接纳与最终动作，覆盖已确认的运行期行为。

**Blocked by:** 01、02、03、04

Status: ready-for-agent

- [ ] 应用退出意图固定后拒绝冲突变更
- [ ] 各子树结果汇总后才进入最终动作，副作用不重复
- [ ] 未决退出交互与更新提交策略作为明确承接项，不以本片完成冒充全产品验收

## Confirmed scope (2026-09-08)

以解耦与可维护性为目标：复用既有 owner，删除被替代的编排，不建设通用任务框架。
生产代码、测试与文档分别统计增删，不以净删除代替维护性判断。

- Q1：安全检查与停止完成后才调度更新；安装器调度失败记录错误并正常退出，不恢复原采集会话。
- Q2：无法确认数据持久化时，阻止安装器与原生退出，保留明确结果和关闭的接纳入口。
  本片不实现重试／强退交互；因此未知余量可阻止自动退出，不能宣称完整退出体验完成。
- 离线或未上传完本身不阻止退出；本地余量已确认持久化即可退出，清理错误独立记录。
- 沿用 PRD 已确认的应用退出、Host 管理命令、真实 Host 组合和 Fact/Upload 测试边界。
- 本次审查基线为开始实施时的 `9919a40e5524cda4edd6462f5df2812beab690ba`。

## Verification

实现与自动验证完成，待双轴审查闭合。

- `dotnet build Heartbeat.slnx --no-restore`：0 warning / 0 error。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- `dotnet test Heartbeat.slnx --no-restore --verbosity quiet`：1,186/1,186 通过。
- 回归覆盖相同／冲突意图、关闭接纳、未知数据阻止最终动作且不导致 UI 未处理异常、安全数据伴随
  清理失败仍退出、并发更新只调度一次、安装器失败正常退出、关闭输入记录的持久 backlog。
- 真实 Mac Host 组合使用隔离目录和可控原生 adapter，离线产生并保留末尾数据后正常退出。
  这不是安装到真实设备后的原生窗口／安装器验收，后者由 06 承接。
- 本次没有执行新的 solution-wide rename；旧 updater preparation 接口由唯一最终动作替代，无兼容路径。
