# Dashboard

Vue SPA：展示 Subject 状态、报表、Timeline/Replay 与 Recap，并承载叙事知识写回和
Hub-local 交互授权入口。

## 目录

- `src/main.ts`、`src/router/`、`src/views/`：应用入口、路由与页面。
- `src/api/`：OpenAPI client、SSE 与 Hub API adapter；不在 README 复制 endpoint。
- `src/composables/`：设备、状态与报表等数据域协调。
- `src/timeline/`、`src/segmentAdapters.ts`：Timeline/Replay 纯模型与 Source adapter。
- `src/knowledge/`、`src/teaching/`：Strand、Episode、Matcher 与确认流。
- `src/components/`：展示组件。

## 本地开发

先按[开发指南](../docs/development.md)启动本地栈，再运行：

```bash
npm --prefix frontend run dev
```

打开 <http://localhost:3000>，Vite 将 `/api` 和 `/hub` 请求统一代理到本地栈
`http://127.0.0.1:8080`，直接读取本地数据库与 Hub。前端保留热更新；后端改动需重新构建本地服务。
需要历史数据时按[数据刷新 runbook](../docs/runbooks/refresh-local-data.md)导入。
鉴权沿用本地栈的真实 Auth 配置。

## 验证与归属

```bash
npm --prefix frontend test
npm --prefix frontend run build
```

Vite `dist/` 进入 `heartbeat-frontend` nginx 镜像。领域边界见
[Frontend Context](CONTEXT.md)，API 约定见 [API 导读](../docs/api.md)。
