# 架构与接口

上级：[AGENTS.md](../../AGENTS.md) · 相关：[存储与聚合](storage.md)、[OTel 解析](otel-parsing.md)、[服务生命周期](lifecycle.md)

## 1. 数据流

```
Copilot Chat / Agent Host 扩展宿主
        │ OTLP/HTTP（traces、metrics）
        ▼
TokenTracker.Server  (Kestrel, .NET 10 Native AOT)
        ├─ SQLite   tokentracker.db
        ├─ REST     /api/*
        ├─ WS       /ws
        └─ 静态看板  wwwroot（React 构建产物）
        ▲
        │ /healthz + /api/version 探测；按平台下载/启动/替换服务端进程
VS Code 扩展（状态栏、Tip、命令、AHP 标题通道）  浏览器 / AHP
```

- 可执行体只有两个：`TokenTracker.Server`（四平台自包含 AOT）与 VS Code 扩展（通用 VSIX，首次激活按 RID 下载服务端并校验 `.sha256`）。
- **WinUI 桌面端已删除**（提交 `835671a 删除app`）。不要按 `TokenTracker.App`、Windows App SDK、`TableRowFactory` 或 TableView 约束写代码或做规划。
- 所有数据留在本机，不采集、不上传对话内容（默认 `captureContent: false`）。
- 时间范围、峰谷价格一律按北京时间（UTC+8）。

## 2. 目录

| 路径 | 说明 |
| --- | --- |
| `src/TokenTracker.Core` | OTLP 解析、SQLite（`SpanStore*`）、定价、节假日、会话元数据缓存 |
| `src/TokenTracker.Server` | ASP.NET Core：OTLP 接收、REST、WebSocket、静态看板、系统信息 |
| `web` | React + Ant Design + Vite；`npm run build` 输出到 `src/TokenTracker.Server/wwwroot` |
| `vscode-extension` | VS Code 薄客户端；`out/`、`server/` 是构建产物（不跟踪） |
| `tests/TokenTracker.Core.Tests` | 核心单元测试（xUnit） |
| `docs` | 两条 OTel 管线的字段实证、三层关系总览、来源识别 |
| `TokenTracker.slnx` | 解决方案（Core / Server / Tests） |

## 3. 接口

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/otlp/{source}`、`/otlp/{source}/v1/traces` | 按路径绑定来源：`copilot-chat`（`extension-host` 为别名）/ `agent-host`；其他 source 404 |
| POST | `/otlp/{source}/v1/metrics`、`/v1/metrics` | 只按 outcome allowlist 入库；`TOKENTRACKER_CAPTURE_OTEL_METRICS=1` 时额外把原始 payload 存到 `otel-metrics/` |
| POST | `/v1/traces` | 兼容旧配置，按资源属性推断来源；无法判断写 `unknown`，不要默认归入 Copilot Chat |
| GET | `/healthz`、`/api/version` | 健康检查；名称、版本、buildId、features 列表 |
| GET | `/api/system` | 数据库路径/大小/表行数、原始与综合 span、outcome 指标、schema 版本、监听与 AHP 状态 |
| GET | `/api/dashboard` | `range=0..4` 或 `from`/`to`、`source`；汇总、按模型（含辅助调用）、会话（主调用） |
| GET | `/api/model/{model}` | `range` 或 `from`/`to`、`source`；模型详情、TTFT、平均生成速度、会话列表 |
| GET | `/api/session/{sessionId}` | 汇总、`models`（按模型同口径）、`calls`、`events`、`tools`、`turns`、`agentRuns`、`hooks`、`signalHealth`、`outcomes`；`range` 或 `from`/`to`、`source`、`includeCalls`（默认 true；**Web 会话页传 false**） |
| GET | `/api/session/{sessionId}/trace` | 拓扑节点：时间游标 FIFO 分块，`budget` 默认 400，`cursor` 向后取更新块；`range` 或 `from`/`to` 与其它会话接口同口径；响应 `nodes/hasMore/nextCursor/total` |
| GET | `/api/calls` | 服务端分页：`range/source/model/query/kind/priced/period/hasError/agent/offset/limit`（单页上限 500） |
| GET/POST/DELETE | `/api/models` | 模型设置：列表、新增或更新（保存后重算历史）、`?model=` 删除 |
| GET/POST | `/api/settings` | 数据保留天数（`0` = 永久） |
| POST | `/api/metadata-changed` | 扩展写入元数据后请求广播新快照 |
| POST | `/api/import-local` | 从本地 span 库只读导入，body `{ paths }` |
| GET | `/ws` | WebSocket：`subscribe` 后接收变更驱动快照 |

- `source=combined`（旧 `all` 为别名）是默认值；不带 `source` 即综合视图。
- `range=0..4` 与 `from`/`to` 二选一；`from`/`to` **必须同时提供且 `to > from`**，只给一个返回 400（此前会被静默忽略）。
- 无匹配的 `/api/*`、`/otlp/*` 一律 404，不再回落到 SPA 的 `index.html`。
- 服务端默认 dashboard 范围 `2`（近 7 天），会话默认 `4`（全部）——会话不能默认收窄，否则 VS Code Tip 会丢历史调用。
- 消息形状与序列化统一在 `src/TokenTracker.Server/Contracts.cs`；新增 payload 必须同时加 `[JsonSerializable]`（AOT，见[工程约束](engineering.md)）。

## 4. 环境变量（服务端）

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `TOKENTRACKER_PORT` | `4318` | 监听端口，只绑定 `127.0.0.1` |
| `TOKENTRACKER_DATA_DIR` | 按平台 | 数据库目录 |
| `TOKENTRACKER_BUILD_ID` | 空 | `/api/version` 返回的构建标识，扩展据此判断是否同版本 |
| `TOKENTRACKER_IDLE_EXIT_SECONDS` | `0`（不退出） | 无客户端后延迟退出；扩展默认传 5 |
| `TOKENTRACKER_RETENTION_DAYS` | `90` | 保留天数默认值，数据库设置优先 |
| `TOKENTRACKER_SESSION_STATE_DIR` | `~/.copilot/session-state` | `workspace.yaml` 根目录 |
| `TOKENTRACKER_AGENT_HOST_DB` | 自动定位 | `agent-host.db` 路径 |
| `TOKENTRACKER_HOLIDAY_API_URL` | `https://api.apisbo.com/holidays/year/{0}` | 节假日模板 |
| `TOKENTRACKER_CAPTURE_OTEL_METRICS` | 空 | `1` 时保存原始 metrics payload 用于诊断 |

## 5. 本机数据位置（只读访问，禁止写入）

| 内容 | Windows | Linux | macOS |
| --- | --- | --- | --- |
| 独立服务端数据 | `%LOCALAPPDATA%\TokenTracker` | `~/.local/share/TokenTracker` | `~/Library/Application Support/TokenTracker` |
| 扩展数据 | `globalStorage/jottingcn.tokentracker/data` | 同左 | 同左 |
| 会话状态 | `%USERPROFILE%\.copilot\session-state` | `~/.copilot/session-state` | `~/.copilot/session-state` |
| `agent-host.db` | `%APPDATA%\Code\User\globalStorage\agent-host.db` | `~/.config/Code/User/globalStorage/agent-host.db` | `~/Library/Application Support/Code/User/globalStorage/agent-host.db` |
| 扩展 span 库 | `%APPDATA%\Code\User\globalStorage\github.copilot-chat\agent-traces.db` | `~/.config/Code/User/globalStorage/github.copilot-chat/agent-traces.db` | `~/Library/Application Support/Code/User/globalStorage/github.copilot-chat/agent-traces.db` |
| Agent Host span 库 | `%APPDATA%\Code\agent-host\otel\agent-host-traces.db` | `~/.config/Code/agent-host/otel/...` | `~/Library/Application Support/Code/agent-host/otel/...` |
| AHP 端点 | `%APPDATA%\Code\agent-host\local-endpoint` | 同左（XDG） | 同左 |

两个本地 span 库只保留约 7 天 / 100 会话，长期统计以 Token Tracker 数据库为准。`globalStorage` 的扩展目录一律是全小写 `<publisher>.<id>`（见 [OTel 解析](otel-parsing.md)）。