# 01 — 采集能力与原生会话隔离

**What to build:** 采集能力与原生会话隔离，覆盖已确认的运行期行为。

**Blocked by:** None — can start immediately

Status: ready-for-human

- [x] 快速配置变化最终收敛，启停不并发执行且不增加 debounce
- [x] 权限撤销隔离能力故障，旧回调不再产生新事实
- [x] 原生线程、句柄和清理结果归单次 session，停止未完成不得丢失所有权
- [x] Mac/Windows 自动回归通过
- [x] 配置提交不等待原生清理；能力状态与通知由单一后台写入者发布
- [x] 原生 session 区分 RequestStop 与包含失败信息的 Completion
- [ ] 原生权限与反复启停验收有证据

## Verification

2026-09-07：实现与自动验证完成；原生人工验收尚未执行，不标记 done。

- `dotnet test Heartbeat.slnx --no-restore`：1,124 项通过；最后一项 Mac 状态通知修复后补跑完整 Mac 测试，77 项通过（比全量运行增加 1 项）。Windows 38 项、System 83 项均通过。
- `dotnet build Heartbeat.slnx --no-restore` 通过（0 warnings / 0 errors），IDE1006 命名检查及 `git diff --check` 均通过。
- 先失败后修复的行为回归：启停重叠、撤权时迟到输入、AX 故障后切换应用立即恢复、失败通知完成前 session 不可替换、延迟失败 observer 不覆盖恢复状态、原生启动失败后的最后能力通知必须是 Unavailable。另验证停止完成不依赖调用者 UI 上下文。
- Standards / Spec 双轴审查完成，发现项已修复并复查关闭。审查基线为 00806ad。
- 日志：`/tmp/heartbeat-ticket01-final-tests.log`、`/tmp/heartbeat-ticket01-mac-final.log`、`/tmp/heartbeat-ticket01-final-build.log`、`/tmp/heartbeat-ticket01-final-naming.log`。

## Architecture tightening after a1b2699

用户授权的第二轮仍限于 01。已删除调用者线程执行的 ObservationReconciler，三个能力共用 ObservationCapability 的后台执行、状态发布、轮询与停止结果；平台 adapter 保留原生操作及观察转换。ObservationThread.RequestStop 只发停止信号，Completion 在会话清理与最后通知结束后返回失败信息（成功为 null）。

- 配置提交不等待原生清理：Mac 输入公开配置接口的阻塞清理回归先失败后通过。
- 共享能力测试覆盖最新目标收敛、单一有序状态通知、可用通知能读取已就绪观察、失败 Stop 结果保留且不能通过重复 Stop/Start 隐藏、显式故障恢复。
- 审查发现的无变化轮询丢输入、异步切换遗漏静态标题均先复现再修复。后者通过 DesktopObservationSource 与实际 MacAccessibilityEvents 组合验证，真实调用路径不等待后台转换。
- 删除旧 continuation 写状态路径及其专用测试，用共享能力的有序通知行为测试替代。
- 最终 `dotnet test Heartbeat.slnx --no-restore`：1,132 项通过（Mac 79、Windows 37、System 89）；构建 0 warnings / 0 errors，IDE1006 与 diff whitespace 检查通过。
- Standards 与 Spec 以 a1b2699 为基线，已复查关闭发现项。日志：`/tmp/heartbeat-tighten-final-tests.log`、`/tmp/heartbeat-tighten-final-build.log`、`/tmp/heartbeat-tighten-final-naming.log`。
- 相对 a1b2699 的 C# 生产代码新增 287 / 删除 433，净删 146 行；测试新增 258 / 删除 65，净增 193 行。不包含文档或用户的其他修改。

实现及自动验证完成，仍待下述原生验收；02–05 未实施。

## Remaining native acceptance

承接者：项目 owner，在使用本提交构建的测试应用、具备对应原生平台权限的 Mac/Windows 设备上验收。当前未替换已安装应用，也未操作用户的系统权限。

1. Mac 分别启用窗口活动、输入记录与点击辅助判断；撤销 Accessibility 或 Input Monitoring 权限并等待权限轮询。仅受影响能力暂停，配置仍启用，前台应用基线和其他 Collector 继续运行，暂停期间没有新标题/输入事实。重新授权后能力恢复。
2. Mac/Windows 连续快速切换可选能力，最后分别停在关闭和开启；确认实际输出符合最终配置，无重复 hook、旧 session 回调或清理新句柄现象。
3. Mac 在多个前台应用间切换并启停窗口活动采集；确认标题属于当前应用，失败恢复后的状态与输出一致。两平台退出后确认进程与原生资源结束，记录构建版本、平台、日志和结果。

未完成以上验收前，自动适配器测试不能视为真实 TCC/Win32 验收证据。超时恢复交互仍按 ADR-052 后置；当前保留未停止 session 的所有权并报告失败。
