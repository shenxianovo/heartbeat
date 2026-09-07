# 03 — Desktop 与 Headless 共享运行所有权

**What to build:** Desktop 与 Headless 共享运行所有权，覆盖已确认的运行期行为。

**Blocked by:** 02 — Host 持有已接受的管理操作

Status: ready-for-agent

- [ ] Headless 管理入口复用操作契约
- [ ] 运行子树统一创建和释放，Fleet 不再同步阻塞清理异步资源
- [ ] Account Subject 投影与授权隔离保持，真实 Host 集成回归通过

## Verification

2026-09-07：实现与自动验证通过，待本片 Standards / Spec 审查收尾。

- Headless 使用共享 Runtime/Marketplace Host 注册和生命周期；删除 Fleet 自建、初始化、管理转发、
  同步 Dispose 以及重复资源列表。Subject/投影/上传留在 Headless adapter。
- 手动 Fleet 测试改为真实 Host；先复现旧 DI 双重捕获导致的重复 Dispose 异常，再验证修复。
- HTTP 重新连接查询/显式取消测试先以缺失路由的 404 失败，再通过；管理页恢复测试先以缺失
  操作展示失败，再通过。查询不依赖 Catalog 完成；所有新增路由验证匿名/错误 owner/错误 client 拒绝。
- Account Subject 恢复身份与不同 Account 的逐 Instance 上行投影验证通过。
- `dotnet build Heartbeat.slnx --no-restore`：0 warning / 0 error。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- `dotnet test Heartbeat.slnx --no-restore`：1,161/1,161；Headless 10/10，Hub 322/322。
- `npm run typecheck`、`npm test`、`npm run build`（frontend）：通过，256/256 tests。
- `git diff --check`：通过。Roslynator 对内部 Fleet 类型的 rename dry-run 在工具内部 NullReferenceException；
  已按完整引用清单限定替换为 `HeadlessCollectorReadModel`，构建/命名/全量回归验证引用闭合。
- 本片不含第三方账号或原生设备发布验收；既有 01 人工门禁与 04–05 后续工作保持原状态。

## Comments

2026-09-07：用户确认本片目标是解耦与提高可维护性，不是增加 feature。实施优先复用 Desktop/Headless
共享 Host 组合和生命周期，删除 Fleet 自建、转发与释放路径；HTTP 和管理页仅适配已有
Host Management Operation 契约。测试沿用 PRD 已确认的管理命令/结果查询、真实 Host 组合和投影交接边界。
