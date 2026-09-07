# PRD: Recap 流式生成

**决定文档**：[ADR-042](../../docs/adr/042-recap-streaming-generation.md)（amends
[ADR-023](../../docs/adr/023-recap-cloud-llm-projection.md) §4/§5）

## 问题

线上 `GET /api/v1/recaps/daily?force=true` 稳定 504：nginx `proxy_read_timeout` 默认 60s，而线上
LLM 生成常常更久；后端 `HttpClient` 默认 100s 大于它，于是代理先断，ADR-023 §4 设计的"502 +
可读原因"永远到不了前端。本地只是恰好更快（约 26s），不是配置不同。

## 目标

1. **可用性**：60 秒不再是功能上限——生成多久都不该被代理掐断，失败必须以可读原因抵达前端。
2. **体验**：等待变成阅读，叙事逐段浮现（"日记与档案"的调性，不做打字机动画）。
3. **语义清理**：生成的触发口收敛为一个（POST）；GET 可断言不烧 token、不写库。

## 非目标

- **不做异步任务**：生成寿命绑定连接，关页面即作废（单用户前提下的显式取舍，见 ADR-042 §1）。
- **不引入持久的"生成中"状态**，`CONTEXT.md` 不动。
- **不改发问/整理两条 LLM 出口的形状**（它们在止血步骤免费受益；流式化见 issue 06）。
- 不为 Cloudflare 那一层做上线前的阻断式验证（缓冲只影响体验、不会造成 504，改为上线后眼球
  观察，判据在 runbook）。

## 交付顺序

| # | Issue | 依赖 | 可独立发布 |
|---|---|---|---|
| 01 | [反向代理与超时止血](issues/01-proxy-timeout-stopgap.md) | — | ✅ 是（先上，立刻缓解） |
| 02 | [传输层与生成层改流式](issues/02-streaming-transport-and-generator.md) | — | ❌ |
| 03 | [GET 退成纯读 + 三态 DTO](issues/03-read-only-get-and-stale-hints.md) | — | ❌（与 05 同发） |
| 04 | [POST 流式生成端点](issues/04-sse-generate-endpoint.md) | 02 | ❌ |
| 05 | [前端流式消费与渲染](issues/05-frontend-stream-consumption.md) | 03, 04 | ❌ |
| 06 | [发问/整理流式化](issues/06-asking-proposal-streaming.md) | 02 | 待命，本 PRD 不承诺 |
| 07 | [断连与异步迭代器释放竞态](issues/07-client-disconnect-async-enumerator-dispose-race.md) | 02, 04 | ✅ 修复与自动验收完成，可独立部署 Analytics |

02–05 必须一起上线：03 拿掉 GET 的生成能力，前端就必须已经会走 POST。

## 验收

- 一次耗时 >60s 的强制生成能完整成功，前端逐段看到文本。
- 生成失败时前端显示可读原因（来自流内 `error` 事件），不是 HTML 504。
- 未认证/访客路径仍然永不触发 LLM。
- 同一天并发生成第二个请求拿到 409。
- 生成中途关闭页面：不写缓存，上次成功的叙事保留。
- 生成完成瞬间断开：叙事已落库，重新打开即命中缓存。

## 2026-09-07 — Reliability closeout

issue 07 已完成：断连和两种超时保留在途读取的所有权，等待生成器清理后再释放；确定性 regression
在旧实现 3/3 失败，修复后 Recap 39 条和连续两轮 solution（每轮 Analytics 457 条）全部通过。
这完成了该竞态的自动验收，不代表已部署 Analytics，也不替代原有线上流式体验验收。
