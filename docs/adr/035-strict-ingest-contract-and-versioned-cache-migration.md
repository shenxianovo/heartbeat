# ADR-035: 严格摄入契约与版本化本地缓存迁移

## Status: Accepted

## Date: 2026-08-11

## Context

AppIdentity 与跨平台 InputCode 会替换现有上传字段，但 Agent 的离线 JSON 缓存可能仍保存旧 `AppName` 和 Windows VK。服务端若永久拒收、客户端又把所有失败都视为可重试，单条旧数据会成为毒丸并无限阻塞整个缓存；直接丢弃则违反 Upload Stream 的“批次不蒸发”不变量。

## Decision

跨平台 App 身份与 InputCode 上线时，Analytics 只接受新摄入契约，不保留旧 `AppName` 等服务端兼容分支；旧客户端收到升级要求后必须更新。数据兼容责任位于新版 Agent：启动时在缓存加载前备份旧文件，以旧持久化 DTO 解析并原子迁移到带 schema version 的新缓存；旧 segment 的 AppName 转为 AppIdentityKey但保留原 IdentityKey，旧输入码显式标为 `windows-vk-v1`。只有转换与重新加载校验全部成功后才归档旧文件，失败必须保留原文件并在 UI 报错。

Upload Stream 区分可重试与永久失败：网络错误、408、429、5xx 与可恢复认证失败继续重试；400/422 通过拆批定位坏记录并移入 dead-letter JSON，其余记录继续上传；426 显示必须更新并暂停无意义的重复尝试。dead-letter 是“批次不蒸发”不变量的隔离出口，不得静默删除数据。

发布采用服务端先切换的严格序列：旧 Agent 会短暂收到升级要求并继续写本地缓存；新版已可立即从 GitHub Releases 获取，升级后由本地迁移器转换旧缓存并重传。此方案接受短时停止上传，不引入 capability negotiation；发布前必须验证缓存容量足以覆盖升级窗口，并在桌面 UI 明确显示“必须更新”。

## Consequences

> 2026-09-07 implementation note: 段的 24 小时限制约束 `EndTime - StartTime`，不限制上传时
> 的数据年龄。有效离线历史必须允许重传；仍校验最早年份、未来时钟偏差、起止顺序和身份。
> 旧实现误将超过 24 小时 10 分钟的历史判为 422。已隔离的数据保留在 dead-letter 文件中，
> 需要在 Analytics 更新后经重新校验恢复；客户端更新本身不会自动重传隔离文件。

- ✅ 服务端契约保持单一，不长期背负旧字段分支。
- ✅ 离线历史在客户端原子升级，失败时仍可从备份恢复。
- ✅ 永久坏记录被隔离，不能拖住同批及后续有效数据。
- ⚠️ 服务端先切换会产生短时停止上传，发布前必须验证缓存容量与更新可用性。
- ⚠️ dead-letter 需要 UI 可见性与人工检查路径，不能成为隐形垃圾桶。

## References

- [ADR-008](./008-local-cache-offline-retry.md) — 本地离线缓存
- [ADR-018](./018-stable-segment-identity-snapshot-upload.md) — IdentityKey 与幂等重传
- [ADR-022](./022-upload-stream-owns-reinjection.md) — “批次不蒸发”不变量
- [ADR-034](./034-app-as-cross-platform-product.md) — 新 AppIdentity 摄入契约
