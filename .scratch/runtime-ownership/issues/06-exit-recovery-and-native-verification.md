# 06 — Best-effort 退出与原生验收

Status: ready-for-human

**Remaining gate:** 真实 Mac/Windows 菜单／系统退出、01 的权限／session 验收，以及安装绑定更新验收。

## Problem and revised decision

05 建立接纳、数据证据与唯一最终动作；未知数据会阻止退出，但没有有效的恢复出口。
2026-09-08 用户改为：“不保证一定保存下来。但尽最大努力……努力策略先弄个最简单的”。
本片据此采用 best-effort：尝试保存，保留真实证据与风险，然后退出。此决定替代原来的未知阻塞、
重试／强退 UI 目标；不表示未知因超时或清理成功变成安全。权威决定见 ADR-052 Ticket 06。

## Implemented slice

- 固定首次退出意图并关闭接纳，沿用现有子树停产／保存／fence，不增加退出层重试。
- 周期上传先取消并归还保管责任，随后一轮末次交付共享 5 秒联网预算；Host cancellation 可提前
  结束交付。预算用完后仍给上传源本地持久化机会；关闭的 Input Recording 不恢复上传。
- 未知余量记录“部分数据尚未确认保存，退出可能丢失”、owner 证据与 System drain 诊断，然后
  继续清理和已固定的唯一最终动作。删除保留停止后空壳与缓存失败任务的旧阻塞分支。
- 正常退出／更新重启都只执行一次对应动作，安装器只在停止与清理完成后调度；调度失败记录
  错误并正常退出，不恢复采集会话。离线持久 backlog 始终允许退出。
- 不新增恢复框架、失败窗口、强退确认、存储格式或持久任务；下一次启动沿用已有重放路径。

## Recovery facts and limits

- System ingress／outbox 的未知尾部没有停机后保存 owner，旧 Activation writer 已 fenced；
  重复 Stop 只复用终态。ManagedProcess 未报告余量或进程已结束也不能凭新 Activation 解除未知。
- 正常 Segment 投影已有 Runtime durable 证据；缓存写失败本身不阻止退出。legacy `Accept`
  的内存缓存虽可再次落盘，不足以支持生产场景的通用“重试保存”。
- 保管证据只报告剩余副本与未确认数量，未知不等于已确认丢失，不声称具体丢失条数。
- 5 秒不是整体退出硬期限；不返回的原生／同步文件调用与 owner 归还责任仍可能延长退出。
  不另起超时旁路，不重新打开 writer。强杀／崩溃不保证退出流程执行或数据保存。
- 后续只有实际故障证明有收益时，再在本 issue 追加窄的持久化交接或整体期限切片；这些不是
  当前 best-effort 产品策略的必需项，不另建重复恢复 backlog。

## Acceptance

- [x] 明确最小努力策略、预算、剩余数据 owner 和不保证的边界。
- [x] 未知数据也正常退出，保留风险／原因；本地持久数据仍安全，唯一意图和最终动作不变。
- [x] 真实文件失败、网络预算耗尽、重复退出、未知时安装器失败有自动回归。
- [ ] 真实 Mac/Windows：菜单退出、系统退出、重复退出；保存未知仍记录风险并退出。
- [ ] 安装绑定下普通退出更新与更新重启；安装器失败正常退出，下次手动启动旧版本。
- [x] 更新 PRD／ADR／glossary，区分实现、自动验证、原生验收。

## Verification

审查固定点：`198e0b1e913ca3b1b753b75c910d55b2005fa042`。仅包含本任务路径，排除工作区独立改动。

- TDD：应用退出 seam 的两条未知余量回归先失败后通过；网络无响应测试先因 8 秒仍未停止而
  失败，加入 5 秒预算后通过，并确认后续 Segment 流仍保存本地余量。
- 真实 Mac Host 组合 + 可控原生 adapter：隔离 Collector 目录变成文件，稳定触发真实 IOException；
  System 报告 `PersistenceFailed` 和未知余量，接纳仍关闭、结果不改写、应用只退出一次。
- 应用退出 13 项、UploadWorker 停止 4 项及真实文件失败专项通过。
- `dotnet test Heartbeat.slnx --no-restore --verbosity quiet`：13 个测试项目共 1,192/1,192 通过。
- 构建：0 warning / 0 error；IDE1006 命名检查、`git diff --check` 和
  `node scripts/smoke-local-data.mjs check` 均通过。
- Standards／Spec 各发现同一处 glossary 更新调度前提漂移，已修正并复查；各 0 项未解决。
- 本轮证据目录：`.local/verification/runtime-ownership-06/`；其中运行数据与日志只用于本机验证。

## Native acceptance and handoff

当前 macOS 原生进程已在独立 Profile 启动，真实 UI readiness 已确认，离线 SIGTERM 正常退出码 0。
它使用当前 Debug 制品、无安装绑定，可选窗口／输入采集关闭。这个证据不替代菜单、TCC 权限、
Windows 或真实安装器验收。

另一次独立原生进程在 UI 就绪后，将自己的 Collector 数据目录移至 `.retained` 并在原路径放置
普通文件，制造真实保存失败；通过正式 `desktop-stop` 入口退出，退出码 0，约 0.124 秒，日志
同时包含 `PersistenceFailed` 与未知数据丢失风险。报告为 `native-mac-storage-failure.json`。
这两次运行都没有替换日常应用、调用安装器或修改系统权限；不会据此勾选跨平台完整验收。

剩余验收由项目 owner 在对应设备／测试安装中执行，并将版本、OS、日志、退出码与结果记回这里：

1. Mac 菜单退出／Cmd-Q 与 Windows 托盘退出／系统关闭，连续重复请求，确认进程、图标和锁结束。
2. 用独立 Profile 制造 Collector 存储不可写，确认日志有失败原因／未知风险且仍退出；恢复目录后
   重启只回放实际已保存数据，不据退出码宣称无损。不要修改日常 Profile 来注入故障。
3. 在测试安装绑定中准备已下载更新，分别普通退出和更新重启；确认停止清理先于安装器、只调度
   一次。安装器不可用时仍退出，下一次手动启动旧版。独立 Profile 无更新能力，不能代替本项。
4. 合并执行 01 的 TCC／Win32 权限与原生 session 验收，证据仍记回 01；不自动勾选其人工门禁。

## Lifecycle / friction closeout

实现与自动验证不等于全部原生／安装验收。06 和 PRD 保持 `ready-for-human`；05 保留历史完成状态，
其未知阻塞策略注明由本片替代。无新兼容路径或无移除条件的恢复框架。
