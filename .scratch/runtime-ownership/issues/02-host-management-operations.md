# 02 — Host 持有已接受的管理操作

**What to build:** Host 持有已接受的管理操作，覆盖已确认的运行期行为。

**Blocked by:** None — can start immediately

Status: done

- [x] 已接受变更不随 UI/HTTP 生命周期取消
- [x] 同一 Host 中可查询操作结果，界面重新进入可恢复展示
- [x] 取消在安全阶段生效，不可撤销阶段明确返回不可取消
- [x] 重启恢复已保存事实，未提交操作可重新发起

## Verification

2026-09-07：实现与自动验证完成。

- `ICollectorMarketplace` 的写 interface 改为同步接纳并返回 Host Management Operation；调用方 token
  只用于可取消的结果等待和查询。Headless 请求断开不再把 request token 传给已接纳执行。
- operation owner 在 Host 生命周期内保存每个 Package 当前或最近结果；同目标重复命令共享 receipt，
  冲突命令拒绝。安全阶段取消、提交阶段不可取消以及 Host Stop 的取消/等待顺序有自动回归。
- Desktop presentation 在页面刷新时先读取 Host operation snapshot，恢复进行中或失败展示。
- 重启回归验证 operation history 清空，同时已提交的 Installation 与 Collector Instance 正常恢复；
  未提交操作没有持久占位，可重新发起。
- 精确命令查询/取消的 Headless HTTP 路由仍属于依赖本任务的 Ticket 03；本任务已经保证 HTTP request
  cancellation 只取消等待，不取消 Host execution。

验证证据：

- 基线与工具：`dotnet tool restore` 成功；修改前
  `dotnet build Heartbeat.slnx --no-restore` 为 0 warning / 0 error。
- 命名与调用点 inventory：
  `rg -n "ICollectorMarketplace|HostManagementOperation|OperationsSnapshot|WaitForOperationAsync|CancelOperation" collection --glob '*.cs'`
  覆盖 Hub contract/runtime、Host facade、Desktop presentation、Headless adapter 与所有测试替身；
  `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 通过。
- 受影响项目回归：
  `dotnet test collection/hub/Heartbeat.Collection.Hub.Tests/Heartbeat.Collection.Hub.Tests.csproj --no-restore`
  通过 322/322；
  `dotnet test collection/desktop/Heartbeat.Desktop.UI.Tests/Heartbeat.Desktop.UI.Tests.csproj --no-restore`
  通过 55/55；
  `dotnet test collection/hub/Heartbeat.Collection.Headless.Tests/Heartbeat.Collection.Headless.Tests.csproj --no-restore`
  通过 5/5。
- 最终构建与全量回归：`dotnet build Heartbeat.slnx --no-restore` 为 0 warning / 0 error；
  `dotnet test Heartbeat.slnx --no-restore` 通过 1,147/1,147；`git diff --cached --check` 通过。
