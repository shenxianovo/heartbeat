# Headless Collection Host

无头 Hub host：一个 Collector Runtime 托管多个 ManagedProcess Collector Instance，每个
Instance 独立拥有投影、状态、上传身份、缓存与 Secret。Collector 的安装与授权由 owner-only
管理 API 驱动，位于
`/hub/api/v1`；Dashboard 直连 Hub，Analytics 不代理凭据或授权应答。

## 目录

- `Program.cs`：host、管理 API 与认证组合入口。
- `HeadlessHostServiceCollectionExtensions.cs`：复用 Hub 的 Runtime/Marketplace Host 注册，仅提供 Headless Subject、投影与上传 adapter。
- `HeadlessManagementEndpoints.cs`：owner-only HTTP 到共享管理操作契约的转换。
- `HeadlessCollectorReadModel.cs`：共享 Marketplace 状态与账号当前活动的展示投影。
- `HeadlessInstancePipelines.cs`：per-Instance 投影、状态、上传、缓存与 drain。
- `HeadlessFleetOptions.cs`：配置校验。
- `heartbeat-headless.compose.example.json`：配置 shape 的权威示例。

## Collector Package 从哪来

`heartbeat-headless` 镜像里不含任何可选 Collector Package，Package 与 Hub 各自构建、各自发版。
登录管理页已改成通用 Hub 管理页：它从 `collectorRegistryUrl` 的 Catalog 展示当前运行目标可用的
最新版，用户点击“安装”后，Hub 下载精确 release、验证并建立 Installation，按 Package 自带的
`defaultInstance` 创建 Runtime Instance 并启动。用户不需要粘贴 JSON、URL 或创建 Instance。

Runtime state 是 Instance 的唯一持久权威。Hub 重启时不访问 Web，而是按 Runtime 中记录的
`packageId + version + contentHash` 打开已有 Installation 并恢复激活。基础设施配置里的旧
`instances` / `packageDirectory` 以及 `headless-instance-map.json` 已直接移除，不提供兼容迁移。

“卸载”会停止并 drain 当前 Activation，然后删除 Runtime Instance、Secret namespace、
per-Instance 数据和不再被引用的精确 Installation。Catalog 条目仍保留，之后可以重新安装。

## 管理操作与生命周期

写请求返回 `202 Accepted`、操作凭据和 `Location`；HTTP 断开不取消已接受操作。`GET /hub/api/v1/operations`
读取各 Package 当前或最近的进程期结果，`GET /hub/api/v1/operations/{operationId}` 精确查询，
`POST /hub/api/v1/operations/{operationId}/cancellation` 显式取消；不可取消返回 409，未知操作返回 404。
执行失败保留在操作结果中。所有路由复用同一 owner `sub` 与 web `client_id` 校验；重启不恢复操作历史。

Desktop 与 Headless 使用同一个 `CollectorMarketplaceHost` 和 hosted lifecycle worker。管理操作与 Activation
在 Host Stopping 阶段收尾，随后 Headless worker 完成上传；Host 异步释放共享 owner 和其依赖。
读模型与 HTTP 借用管理 interface，不创建、启动或释放 Runtime。真实 Host 与 HTTP 回归使用离线 Registry
和 reference Collector，不需要第三方账号。

## 运行、验证与归属

```bash
dotnet run --project collection/hub/Heartbeat.Collection.Headless -- <config.json>
dotnet test collection/hub/Heartbeat.Collection.Headless.Tests
```

本地 Compose 路径见 [Development Guide](../../../docs/development.md)。本项目构建为
`heartbeat-headless` 镜像。各 Collector Package 是独立制品，不进镜像。交互授权边界见
[ADR-043](../../../docs/adr/043-hub-local-interactive-collector-authorization.md)，交付单元划分见
[ADR-048](../../../docs/adr/048-shared-collector-host-runtime-and-independent-release-units.md)。
Catalog 与 Runtime-owned Instance 边界见
[ADR-050](../../../docs/adr/050-generic-collector-marketplace-and-runtime-owned-instances.md)。
