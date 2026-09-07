# 主链路自动验收

在仓库根目录运行：

```bash
dotnet run --project tools/Heartbeat.Verification -- run headless-main
```

需要 .NET 10、正在运行的 Docker，以及已填写的 `.local/heartbeat-headless.json`；配置准备见
[开发指南](../../docs/development.md)。不需要先启动本地开发栈。

命令只读取该配置的 `apiKey` 和 `management`，不会使用其中的数据目录、端口、上传周期或安装
状态。API key 必须属于 `management.ownerSubject`。Auth 继续使用线上服务，用户名仅在临时
数据库中供给为本次运行的 private owner 别名，不修改线上账号。

## 执行内容

1. 为本次运行创建目录，构建当前源码的 Analytics、Headless 和 Reference Collector。
2. 启动独立 PostgreSQL 容器及真实 Analytics 进程，由 Analytics 正常执行 migration。
3. 用正式 Installation/Runtime 模块准备已安装 Reference 和 Account Instance，供给本地 owner；
   确认数据库与查询 API 均没有待测 Segment。
4. 启动真实 Headless，由它恢复并启动 Reference 子进程。Collector Protocol、缓存、上传与
   Analytics JWT 校验使用正常运行路径。
5. 查询正式 Segment API，核对唯一记录的 Source、IdentityKey、标题、起止和时长；额外核对
   持久化 Device 的 owner 与 Subject 映射。原始 FactId 与投影后的 Segment Id 分别理解。
6. 保存证据并回收本次进程、容器、数据库与私密配置。

## 参数与结果

```bash
# 使用另一份已有身份配置
dotnet run --project tools/Heartbeat.Verification -- run headless-main --config /absolute/path/headless.json

# 得到结果后保持泳道运行，排障结束按 Ctrl+C 清理
dotnet run --project tools/Heartbeat.Verification -- run headless-main --keep

# 检查验证器是否能发现上传断链：本命令预期返回 1
dotnet run --project tools/Heartbeat.Verification -- run headless-main --fault disconnect-upload --timeout-seconds 20
```

`--timeout-seconds` 是每个运行阶段的期限，默认 120 秒；build 固定最多 600 秒，线上 Auth 交换
最多 30 秒，单次 HTTP 查询最多 10 秒。准备过程中使用现有模块的同步文件操作；它们不是可抢占的
操作，期限在这些操作返回后的取消检查生效。服务停止先尝试收尾，超出监督期限再结束进程树。
清理成功证明本次资源已回收，不代表 Desktop 退出或更新语义已经验收。

退出码：`0` 通过，`1` 失败或清理失败，`2` 前置依赖阻塞，`130` 运行中取消。故障演练不会把
“预期失败”转换为通过；若返回 0，必须检查实际链路为何仍然可达。Auth 交换不可用或凭据失效
报告为阻塞；已完成交换而本地 Analytics 查询拒绝令牌时，报告实际失败阶段与 HTTP 状态供排查。

每次运行的 `.local/verification/<run-id>/` 包含：

- `report.json`：场景、阶段与耗时、最终结果、失败原因、独立清理结果、服务地址和身份证据。
- `expected.json`、`baseline.json`、`actual.json`：预期、空基线和最后一次查询结果；尚未执行到
  对应阶段时不会凭空生成。
- 各服务、构建与 Docker 操作的日志。已知凭据与 JWT 在写入证据前脱敏。

临时制品、业务数据和私密配置位于该目录下的 `work/`，正常或失败收尾后删除。若清理失败，
报告列出错误并保留现场，不能把这种运行当作通过。`--keep` 保留环境时命令继续运行；按 Ctrl+C
或发送 SIGTERM 后进入清理。不要把强制杀死整个验证命令当作正常停止入口。

## 扩展与维护

`VerificationCommand` 负责阶段顺序和最终结果；`VerificationLane` 负责本次资源；
`HeadlessMainScenario` 负责前置数据与断言；`RunReport` 负责证据。新增服务组合沿用资源和报告，
新增检查独立表达自己的预期。当前只提供 `headless-main`；Desktop 是后续明确接入项，Registry
下载安装、Dashboard 与服务内部检查也不计入当前覆盖范围。

POSIX 服务由独立 session 的监督进程持有，服务提前退出后仍可回收其遗留子进程；父验证命令
的 stdin 管道关闭也会触发服务组停止。Windows 使用进程树终止；目前真实验收证据来自 macOS，
Windows/Linux 完整泳道尚未运行验收。

验证器自身的测试不使用 Docker 或线上 Auth，包含断言、超时、凭据脱敏与进程清理边界：

```bash
dotnet test tools/Heartbeat.Verification.Tests
```

它们随 solution 的 `dotnet test` 执行。真实泳道单独按需调用，后续 CI 可以复用同一命令。
设计边界见[主链路自动验收设计](../../docs/architecture/automated-verification-design.md)。
