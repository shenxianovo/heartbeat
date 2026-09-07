# 07 — 修复 client disconnect 与 async enumerator Dispose 竞态

Status: done

Owner: Analytics / Recap Streaming

Priority: P2 — 全仓项目并行时 client disconnect cleanup 可对仍在途的 `MoveNextAsync` 并发执行
`DisposeAsync`，把正常断连偶发暴露为 `NotSupportedException`。

## Acceptance

- [x] `NextChunkAsync` 返回 `ClientGone` 时仍保留在途 `pending` 的 ownership，先取消并等待该
  `MoveNextAsync` 收敛，再执行 enumerator `DisposeAsync`。
- [x] client disconnect 不产生 error/done event、不落 recap cache，silence/overall timeout 仍保持可区分。
- [x] 回归不依赖 `Task.Delay(200)` 与调度先后，使用确定性 cancellation/MoveNext/Dispose barrier。
- [x] 精确回归、Server suite 与完整 solution 项目并行重复通过。

## Comments

### 2026-08-31 — discovered by Collector lifetime closeout gate

在固定 Collector diff 的完整 solution 第 2 轮中，
`Generate_ClientDisconnect_BeatsBothTimeouts_NoErrorEvent_NothingCached` 1/1 失败，堆栈来自
`FakeGenerator.GenerateStreamAsync(...).DisposeAsync()` 的 `NotSupportedException`；相同二进制随后精确运行
10/10 通过，证明不是 Collector 代码回归而是并行调度相关 flaky。

源码中循环在检查 `step.ClientGone` 前无条件执行 `pending = null`，导致 `finally` 无法取消/等待仍在途的
`MoveNextAsync`，随即并发 Dispose。该问题属于 Recap streaming，当前 Collector 任务只记录证据与承接者，
不越界修改 Server production/fixture。

### 2026-09-07 — CI failure reproduced and fixed

[CI run 34084388317](https://github.com/shenxianovo/Heartbeat/actions/runs/34084388317/job/101625524198)
再次在相同断连测试失败：454 passed / 1 failed，异常为 `NotSupportedException`。
精确重跑原测试通过，确认原计时 fixture 不能稳定暴露竞态。

用实际 async iterator 的 cancellation/cleanup barrier 替换断连测试的 `Task.Delay(200)`，新增静默/总时限
两种相同清理边界。旧实现三条全部失败；修复仅将 `pending = null` 移至失败/断连分支之后，让 finally
始终先取消、等待读取，再 Dispose。没有跳过或放宽失败测试，没有改变公开协议与缓存语义。

验证：RecapServiceTests 39 passed；`dotnet test Heartbeat.slnx -c Release --no-restore --nologo` 连续两轮
全部通过，每轮 Analytics 457 passed。本机日志 `.local/recap-dispose-verification/solution-{1,2}.log`。
`git diff --check` 通过。本 issue 的实现和自动验收完成；此记录不表示 Analytics 已部署生产。
