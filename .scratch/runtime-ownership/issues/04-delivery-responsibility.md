# 04 — 数据交付责任可判定

**What to build:** 数据交付责任可判定，覆盖已确认的运行期行为。

**Blocked by:** None — can start immediately

Status: done

- [x] 已有持久化及 drain 结果不在 hosted adapter 丢失
- [x] 结果区分已交付、本地保管与未知余量，不从异常形状推断安全
- [x] 正常上传、暂停、失败与终止时每个未交付批次都有责任证据

## Verification

2026-09-07：实现与自动验证完成。

- `dotnet build Heartbeat.slnx --no-restore`：0 warning / 0 error。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- `dotnet test Heartbeat.slnx --no-restore --verbosity minimal`：13 个项目，1,182/1,182 通过。
- TDD 回归覆盖传输抛异常后保管、拆批部分成功与暂停、dead-letter 不算送达、超限拒写、未 drain 的
  InputEvent 余量、Segment 淘汰证据及同 Id 回流、hosted 结果保留、离线卸载与清理失败后重试。
- 既有周期/终态上传互斥与取消回归通过；Headless 复用同一调度实现，截止后仅完成本地保管。
- Standards / Spec 双轴审查以本片起点 `da70af8` 为基线，发现项均修复并复核，最终均为 0 项未解决。

本片只提供 owner 交接结果，不宣称应用退出安全策略已完成。跨子树汇总、最终动作和未知余量的
用户恢复仍由 05 承接；01 的原生设备人工验收保持原门禁。Input Recording 关闭时仍不上传既有输入缓存，
本轮未尝试交付时不沿用此前成功结果。

## Implementation scope

用户确认本片以解耦与可维护性为目标：责任判断归数据 owner，Host 只消费结果；不建设新的
队列、恢复 UI 或退出策略。优先替换重复路径，分别报告生产代码、测试和文档增删，不以行数
抵消必要的责任证据。未交付数据不能因卸载被删除；当前采用无法安全删除就失败并保留数据的
最小实现。验证沿用 PRD 已确认的 Fact/Upload 接口与真实 Host 组合。

## Friction closeout

ADR-022 的旧返回契约、ADR-052 的实施进度与领域词汇已同步。缓存容量不再静默删除未交付数据，
已送达收据的裁剪回归其 owner；Segment 容量淘汰保留按 Id 的未解决证据，不新增在线回放框架。
