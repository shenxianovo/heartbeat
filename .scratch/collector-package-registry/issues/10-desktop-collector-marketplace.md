# 10 — Desktop 通用 Collector Marketplace

Status: ready-for-human

Owner: Collection / Desktop Management

Priority: P1 — 让 Desktop 成为共享 Marketplace 的第二个真实调用者，同时保持宿主 UI 不认识任何具名可选
Collector。

## What to build

在 Windows 与 macOS Desktop 的共享原生管理界面中接入共享 `ICollectorMarketplaceRuntime`；该深模块内部
组合 `ICollectorPackageMarketplace` 与 `CollectorRuntime`：

1. 按当前 Desktop OS/architecture 浏览官方 Catalog，显示所有适配 target 的可选 Collector 最新版。
2. 点击安装后，由共享 Marketplace 完成下载与 Installation，再按 Default Instance Blueprint 自动创建一个
   Machine Subject 的默认 Instance；用户不输入 URL、JSON、SubjectId 或 InstanceId。
3. 已安装 ExternalHost Collector 没有 Activation 时显示“等待连接”；有 Ready Activation 时显示
   “运行中 · N 个连接”；稳定错误只用短标题，完整 failure/identity 放进展开诊断。
4. 完整卸载复用 Runtime 的通用停止与删除路径，并要求二次确认。System BuiltIn 不进入 Catalog，也没有
   Web 安装或卸载动作。

## Acceptance

- [x] Windows/macOS 使用同一个共享 presentation/ViewModel 行为；平台 head 只提供 Marketplace target、
      Machine Subject、数据目录和打开原生界面所需 adapter。
- [x] Desktop 直接调用与 Headless 相同的 Marketplace interface；不复制 Catalog HTTP、release 下载、hash、
      解包、Installation 或 Default Instance 创建逻辑。
- [x] UI、主题、Desktop state 与 platform head 的生产代码不含 Browser、VRChat、Chrome、Edge 分支或常量；
      卡片完全来自 Catalog/Runtime 通用 DTO。
- [x] 一键安装成功后只有一个 Runtime-owned 默认 Instance；重复点击不产生第二个 Instance。
- [x] ExternalHost 的无连接、N 个连接与身份冲突分别显示简短状态；随机 `externalHostIdentity` 不出现在主
      卡片，只进入诊断详情。
- [x] 安装/下载/Activation 失败不阻止 Desktop 或 System Collector 启动，也不伪造“运行中”；可以显式重试。
- [x] 卸载二次确认后停止全部 Activation，并只在 Runtime/Secret/data/Installation 都删除成功后显示未安装。
- [x] 不增加 `manualSetup`/`manualRemoval`、打开 Package 目录或执行 Package 命令的 UI。
- [x] System 继续只随 Desktop Release；Desktop publish/package 断言仍只有 System BuiltIn，不把 Web
      Collector 重新打进 Desktop 制品。
- [ ] 用 Reference ManagedProcess 与 Reference ExternalHost Catalog fixture 证明 UI/管理逻辑不依赖具体
      Collector；Windows/macOS composition 与 packaged-host startup smoke 保持通过。

## Non-goals

- 不实现安装后的 Package 更新、版本选择、自动轮询或通知。
- 不提供多个 Instance、Instance 编辑、原始 JSON config 或高级 Subject 管理。
- 不自动操作 Chrome/Edge 扩展管理页，也不提供 Browser 专属文案。
- 本轮不增加 Desktop 通用授权表单；需要交互授权的 ManagedProcess 当前只能显示“等待授权”，不能在
  Desktop 完成登录。Headless 既有授权入口保留。此限制经 owner 在 2026-09-07 收口时确认。

## Dependencies

- [issue 01](./01-static-registry-index.md)：共享 Marketplace module 与通用 Catalog。
- [issue 06](./06-browser-external-host-update.md)：通用 ExternalHost 状态、连接计数与可卸载生命周期。
- [ADR-051](../../../docs/adr/051-generic-external-host-identity-and-browser-delivery.md)：Desktop 与 ExternalHost 的
  产品语义。

## Comments

### 2026-09-04 — Grill closeout

Owner 选择 Desktop 原生界面直接复用共享 Marketplace/Runtime，不把管理动作代理到 Analytics；状态必须简短，
详细诊断可展开。当前是单用户自用，不建设 Package 声明的人工加载说明或打开目录按钮。

### 2026-09-04 — Implementation closeout

新增共享 `CollectorMarketplaceRuntime`，统一拥有 Catalog/Installation、默认 Instance、Driver dispatch、
Activation、恢复、重试、状态与卸载。Desktop 与 Headless 都只提供 Subject/投影 adapter；Headless 原有的安装、
恢复、Driver 与 Activation 编排已删除。Desktop 的 Windows/macOS head 使用同一 presentation/ViewModel，主 UI
只显示短状态，完整 failure/External Host Identity 只在展开详情中出现；System 仍是唯一随 Desktop 发布的
Package。

自动证据：Release build 0 warning；`dotnet test Heartbeat.slnx -c Release --no-build --no-restore` 共 1084 条
通过；Reference ManagedProcess 与改成 ExternalHost driver 的 Reference Package 都经过共享 Runtime 测试；
macOS `dotnet publish` 与 Velopack Portable 实包只包含 `CollectorPackages/System`。本机已安装 Heartbeat 正占用
单实例锁，退出请求被用户取消，因此本次新打包 `.app` 的 `--verify-startup` 未执行；Windows 最终包也只能由
CI runner 验证。最后一项保持未勾选，本 issue 为 `ready-for-human`，不能标 `done`。

### 2026-09-07 — Bounded handoff

Owner 要求停止扩展审查，完成已有修复的定向验证，将实机验收交回。没有增加授权表单、自动刷新或 Browser
发布。当前 UI 在进入采集器页、手动刷新及管理动作之后读取状态，不承诺连接变化自动刷新。

最后修复覆盖：失败重试清除旧错误、External Host 冲突按 identity 保留、离线 Catalog 不虚构 Latest、授权提交
参与停机操作等待、卸载在 Runtime 的围栏内清理 Host/Installation。“等待授权时卸载”的回归测试还复现了
ManagedProcess 取消等待早于其底层启动清理完成的问题；现已等待启动任务释放所有权后再返回。

人工验收由 owner 承接：用本轮最终源码重新构建的 macOS Portable/Windows Portable 验证启动、System 正常、
采集器页可打开、Catalog 失败不影响 System。此前 `/tmp/heartbeat-issue10-verify.Yy4hLO` 的包早于最后修复，
只能作为包内仅含 System 的历史证据，不能用于验收最终代码。macOS 当时的 AppleScript 退出请求返回
`-128`，没有强退已安装客户端；这不证明 owner 实际点击了取消。

最终定向验证：Hub Marketplace/Installation/ExternalHost/ManagedProcess transcript 69 passed，Headless 5 passed，
Desktop UI 42 passed，共 116 passed / 0 failed；Collector contracts 与 `git diff --check` 通过。此前 1084 条
全量结果只属于当时版本，最后修复后执行的是这里列出的定向回归。当前未提交、未 push、未发布。

### 2026-09-07 — Final Desktop package published

包含最终修复的 `6a0a4b08f51f22dfef9865c073ad24bceb570078` 已通过 `v4.2.0` 发布：
[Release](https://github.com/shenxianovo/Heartbeat/releases/tag/v4.2.0)、
[workflow](https://github.com/shenxianovo/Heartbeat/actions/runs/34083843184)。
Windows x64/macOS ARM64 的最终 packaged-host 启动检查在 CI 通过；Windows ARM64 只验证产物，未原生运行。
三种 Portable 均从公开 Release 下载并验证长度、SHA-256、zip CRC 与 System-only Package 内容。

最终包构建与自动启动验证已完成；owner 仍需验收原生采集器页、System 正常工作与 Catalog 离线隔离。
此记录不代替 UI 实机验收，issue 保持 `ready-for-human`，不重复创建后续 issue。
