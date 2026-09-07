# ADR-050: 通用 Collector Marketplace 与 Runtime-owned Instance

## Status: Accepted

## Date: 2026-09-03

## Context

ADR-048/049 已把可选 Collector 从 Host 构建与组合中移除，并完成 VRChat 精确 Web Release；但用户仍需理解
Release URL、手写 Headless Instance JSON、SubjectId 和 Package source。仅增加一个 URL downloader 会把
Registry/Runtime 内部概念泄漏给用户，也无法形成插件市场式安装体验。另一方面，重新在 Hub 或 Frontend 写死
VRChat 会破坏刚建立的宿主解耦。

## Decision

Official Collector Package Registry 增加通用 Catalog，只列出全部托管 Collector 在各 Host target 上当前最新版的
精确 Release；同一 Package 的 `latest` 是按 OS/architecture 唯一的集合，不能让 Linux、macOS、Windows
条目互相覆盖。精确 Release 也使用 `versions/<version>/<os>-<arch>/` 目录，允许同一个 Package version
为多个 Host target 发布不同 metadata 与 artifact。
Catalog Latest 只属于 Web 发现，不表示任何 Host 的 Desired State、Installation 或 Runtime State。每个
Collector 的独立 release workflow 只更新自己的 Catalog entry；Registry 可以认识 Collector，Host 不可以。

Collector Package 增加 Presentation 与 Default Instance Blueprint。Blueprint 以通用字段声明自动安装后要创建
的 SubjectKind、configVersion 和默认 config；Collector 特有默认值留在 Package 内。`Heartbeat.Collection.Hub`
中的共享 Marketplace module 以小 interface 隐藏 Catalog HTTP、Release 下载、校验、解包与 Installation。
Headless 是首个调用者，Desktop 未来复用同一个 module。

用户点击安装时，Host 通过通用路径安装 Catalog Latest、创建一个默认 Collector Instance 并激活；登录界面
完全由 Collector Protocol Authorization Challenge 驱动。`CollectorRuntime` 是动态 Instance 的唯一持久权威，
Hub 重启只从精确 Installation 恢复，不访问 Web。手写 `instances`、`packageDirectory` 和 Headless mapping
账本直接退役，不做兼容迁移。

完整卸载停止并 drain Instance，再删除 Runtime state、Secret、per-Instance data 和 Installation。第一版不做
版本更新、多个 Instance、实例编辑、签名或自动轮询。

## Consequences

- ✅ Hub、Desktop 与 Frontend 不含 VRChat/Browser 分支；增加 Collector 只改变其 Package、Release 与 Registry
  数据。
- ✅ 用户只看到 Catalog 条目和“安装”，不接触 URL、GUID 或 JSON config。
- ✅ Web Latest、Installation、Instance 与 Activation 各自只有一个权威，Latest 不反写运行事实。
- ✅ 同一共享 Marketplace module 可由 Headless 与未来 Desktop 调用。
- ✅ Catalog 的 latest 按 target 独立选择，未来 Desktop target 不需要改变 Host API 或覆盖 Headless release。
- ⚠️ Registry 获得一个有意为之的 mutable Catalog Latest；它只用于首次发现，本轮不承诺更新已安装 Package。
- ⚠️ 旧 Headless 配置不再启动，owner 需要备份后清理旧 data/config 并通过管理页重新安装、登录。

## Implementation update: 2026-09-04

Desktop 已成为第二个真实调用者。共享模块进一步收敛为 `ICollectorMarketplaceRuntime`：除了 Web 获取和
Installation，它还统一拥有默认 Instance、Driver dispatch、Activation、恢复、状态、重试与卸载。Desktop 与
Headless 只实现 Subject/本地投影 adapter；这是对本 ADR“同一共享 Marketplace module”边界的落地，不改变
Catalog Latest 与 Runtime State 分权的原决策。

## References

- [ADR-048](./048-shared-collector-host-runtime-and-independent-release-units.md)
- [ADR-049](./049-named-optional-collectors-outside-host-composition.md)
- [ADR-051](./051-generic-external-host-identity-and-browser-delivery.md) — Desktop 调用者与 ExternalHost 默认 Instance 语义
- [Collector Marketplace issue](../../.scratch/collector-package-registry/issues/01-static-registry-index.md)
