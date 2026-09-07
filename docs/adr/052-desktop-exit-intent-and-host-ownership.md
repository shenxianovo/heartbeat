---
status: proposed
---

# ADR-052: 桌面退出意图与 Host 生命周期所有权

2026-09-07 的设计访谈已确认以下四项决定。用户随后要求先实施架构解耦，策略细节后续再定；
本文区分已实施的所有权调整与尚未实现的目标行为。延续 ADR-003 的“资源异步清理完成前保持 UI 循环”，
修正其初版把清理异常一律解释为禁止退出的策略。

## 已确认

1. 未上传数据已确认持久化时，资源清理失败应记录错误并结束进程；分别报告数据安全性与清理
   结果，不能留下 Host 已销毁但 UI 和单实例锁仍常驻的空壳。
2. 无法确认数据持久化时，不因期限到达自动强退。进入明确的失败交互，说明风险，允许用户选择
   重试或强制退出；超时不是持久化成功证据。
3. 首次接受的 Desktop Exit Intent 固定。立即关闭新变更入口，重复请求共享结果，冲突操作明确
   拒绝；包括安装器调度在内的最终副作用只能执行一次。
4. Collector 的运行、管理操作和资源释放完整归 Host。UI 提交命令并展示状态，不拥有 Collector
   运行子树。应用退出 owner 管整体阶段，Collector 子树和原生观察 session 各自拥有生命周期，
   不把所有对象塞进一个跨层总协调器，也不改变 ADR-046 的协议两侧各自所有权。

## 本次架构实施范围

- Windows/macOS 共用应用退出 owner；Host 异步停止和释放完成前保持 UI 循环，平台只负责桌面资源与原生退出。
- Collector 管理运行子树转由 Host 持有；UI 只借用管理命令和读模型，不再创建或停止 Marketplace。
- 通用 Collector Runtime 的组合入口从 System BuiltIn 中移入 Hub；System 只注册自己的 binding。
- Marketplace 通过 Host 的 Stopping 阶段关闭管理操作并等待结束，随后才进入最终上传；内部资源释放共享结果，失败不会跳过同层其他资源。

这部分不引入新的强退期限、失败交互或更新提交协议。当前仍保留“清理失败不自动退出”的临时行为；
它不是第 1、2 项目标的完成证明。更新安装器抢先启动、退出时所有写入口冻结、原生 session 的停止/重启
隔离和数据安全结果汇总仍需后续实施，不能由本次 Collector 所有权迁移推定已解决。

验证：`dotnet build Heartbeat.slnx --no-restore`、IDE1006 命名检查及全量 1,113 个 .NET 测试通过。
回归覆盖运行中的单线程 UI 上下文、无 Desktop/System 的 Host 组合、停止阶段与最终上传的顺序，
以及取消失败和并发释放的结果保留。打包应用的原生退出与更新安装尚未人工验收，未替换已安装应用。

## 尚待裁决

- 退出期限与末次联网交付的预算；达到期限后的 UI 行为。
- 更新安装器的提交时机，以及提交失败时能否继续原采集会话。
- 无法确认持久化时的恢复能力、交互入口与系统强制结束场景。
- 已接受操作的取消/完成责任、原生 session 停止失败与重新启用规则。
- 各子树的数据安全证据如何汇总，以及后续实施切片和验收边界。

这些问题尚未确定，不从当前代码的异常形状或 Dispose 调用顺序推导产品决定。

## 已核实的设计约束

- `Host.StopAsync` 正常返回不能证明数据安全：System hosted binding 没有向上返回已有的 Drain
  Outcome，UploadWorker 会捕获末次上传错误；需要汇总显式的持久责任结果。
- Collector Activation 终态会 fence 旧 writer。未知余量时只保留进程或重复 Stop，并不能恢复落盘
  能力；恢复必须保留/转交本地保存责任，不能重新开放已 fence 的协议 writer（ADR-046）。
- 固定的 Velopack 1.2.0 调度立即启动外部安装器，没有提交/撤销握手。其 XML 的“60 秒后放弃”
  与对应官方源码不一致：等待结束或报错后继续 apply，Windows 路径包含强制停止。因此在数据
  安全确认前启动安装器，会破坏第二项已确认决定。证据：
  [固定版本等待逻辑](https://github.com/velopack/velopack/blob/f2edcbcafb81da5b3c884aaea330e225ad91d8b6/src/bins/src/shared/util_common.rs#L12-L23)、
  [Windows apply](https://github.com/velopack/velopack/blob/f2edcbcafb81da5b3c884aaea330e225ad91d8b6/src/bins/src/commands/apply_windows_impl.rs#L126-L137)。
  以上为源码核实，未在用户设备执行安装器超时演练。
