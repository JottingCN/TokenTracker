# Token Tracker

[![GitHub](https://img.shields.io/badge/GitHub-JottingCN%2FTokenTracker-181717?logo=github)](https://github.com/JottingCN/TokenTracker)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

本地 Copilot Token 用量与费用看板：接收 Copilot 的 OpenTelemetry 数据，写入 SQLite，并通过 REST / WebSocket 实时呈现缓存命中率与人民币估算费用。

支持三种客户端：

- **VS Code 扩展**：状态栏合并统计 + 多模型 tooltip，一键打开浏览器看板。
- **浏览器看板**：React + TypeScript + Vite + Ant Design。
- **WinUI 3 桌面端**（可选）：内置接收服务与看板。

> 服务端支持 Windows x64、Linux x64、macOS x64 / arm64；所有数据保存在本机，不采集对话内容。

## 功能

- **缓存命中率**：`cache_read_input_tokens / input_tokens`，按会话、模型、调用三级查看。
- **费用估算**：基于内置模型价格计算 CNY 费用，保留 6 位小数；支持缓存 / 未缓存输入 / 输出 / 思考四类费用拆分（缓存费用包含读取与写入）。
- **实际 AIC**：扩展管线的调用若带 `copilot_chat.copilot_usage_nano_aiu`，调用列表会在估算费用下方显示实际 AIC；Agent Host 暂无该字段。
- **历史导入**：命令 `Token Tracker: 导入本地 Copilot OTel 数据` 可从扩展宿主与 Agent Host 的本地 span 数据库只读导入历史调用，并按 `trace_id + span_id` 去重。
- **峰谷与节假日**：DeepSeek 按北京时间区分高峰 / 空闲价格；节假日优先读本地库，缺失时从 `api.apisbo.com` 拉取并按年缓存。
- **价格覆盖**：看板内可修改内置价格、恢复默认、添加 / 删除自定义模型；保存后自动重算历史费用。支持按单次调用输入 token 阈值设置第二档（长上下文）价格，超过阈值时整次调用按长上下文价计算；缓存写入可单独设置单价，未设置时回退到未缓存输入价。
- **时间范围**：今天 / 昨天 / 近 7 天 / 近 30 天 / 全部，Web 端支持自定义起止时间。
- **多模型会话**：同一会话跨模型的统计自动合并，下钻时按模型分开展示。
- **辅助调用**：辅助调用并入正常统计（顶部卡片与按模型列表）；会话统计仅包含可归属的主调用；“最近调用”作为独立菜单展示全部调用。
- **费用单位**：Web 看板可在价格设置中切换人民币、美元、积分；USD 汇率与积分兑换率可调，价格保存时统一换算为 CNY。状态栏仍按 CNY 显示。
- **会话标题**：来自 Agent Host AHP 实时标题（session id 可关联时）、`agent-host.db`、扩展宿主的 `ChatSessionStore` 索引（`state.vscdb`，只读）与 `~/.copilot/session-state`；AHP 不可用时自动回退。改名和工作区变化会推送给所有已连接客户端。
- **实时推送**：WebSocket 变更驱动推送（新 span、价格变更、重新订阅），空闲时服务按需退出。
- **AHP 状态**：页脚显示 AHP 已连接/连接中/未连接及会话数，连接状态变化会自动推送。
- **数据保留**：按保留设置清理过期 span（默认 90 天）。

## 架构

```
Copilot / VS Code
  │  OpenTelemetry (OTLP/HTTP JSON)
  ▼
TokenTracker.Server  (Kestrel · .NET 10 Native AOT · 127.0.0.1:4318)
  ├─ /v1/traces, /v1/metrics     接收 OTLP
  ├─ TokenTracker.Core           SQLite、定价、节假日、会话元数据
  ├─ /api/*, /ws                 REST + WebSocket
  └─ wwwroot                     React 看板（构建产物）
        │
        ├─ VS Code 扩展（状态栏 / tooltip）
        ├─ 浏览器（http://127.0.0.1:4318）
        └─ WinUI 3 桌面端（可选）
```

| 项目 | 说明 |
| --- | --- |
| `src/TokenTracker.Core` | OTLP 解析、SQLite 存储、定价与节假日、会话标题解析 |
| `src/TokenTracker.Server` | ASP.NET Core 服务：OTLP 接收、REST、WebSocket、静态看板 |
| `src/TokenTracker.App` | WinUI 3 桌面端（Windows App SDK 2.6.4，AOT） |
| `web` | React + Ant Design 看板，构建输出到 `src/TokenTracker.Server/wwwroot` |
| `vscode-extension` | 薄客户端：内置并托管共享服务、状态栏、命令 |
| `tests/TokenTracker.Core.Tests` | 核心逻辑单元测试 |

## 快速开始

### 1. 开启 Copilot OpenTelemetry

在命令面板运行 **`Token Tracker: 启用 OTel 上报`**，会同时写入扩展宿主与 Agent Host 两条管线；也可以手动在 `settings.json` 中加入：

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "otlp-http",
  "github.copilot.chat.otel.otlpEndpoint": "http://127.0.0.1:4318",
  "github.copilot.chat.otel.protocol": "http/json",
  "github.copilot.chat.otel.captureContent": false,

  "chat.agentHost.otel.enabled": true,
  "chat.agentHost.otel.exporterType": "otlp-http",
  "chat.agentHost.otel.otlpEndpoint": "http://127.0.0.1:4318",
  "chat.agentHost.otel.otlpProtocol": "http/json",
  "chat.agentHost.otel.captureContent": false
}
```

- 扩展宿主管线覆盖本地 Copilot Chat；Agent Host 管线覆盖 Agent Host 中的 Copilot / Claude / Codex。
- Agent Host 设置需要**完整退出并重新打开 VS Code** 才会生效；仅重载窗口可能复用旧 host。
- `captureContent` 保持 `false`，统计 token 不需要对话内容。

### 2. 启动 Token Tracker

安装通用 VSIX（Windows / Linux / macOS）。首次激活会通过 GitHub API 下载当前版本、匹配平台的服务端（支持 VS Code `http.proxy` / `HTTPS_PROXY`），校验 SHA-256 后缓存并启动：

```bash
code --install-extension vscode-extension/tokentracker-*.vsix --force
```

也可以不使用扩展，直接运行服务端（见 Release 中的独立服务端包）：

```bash
dotnet run --project src/TokenTracker.Server -c Release
```

数据库默认位置：

| 平台 | 路径 |
| --- | --- |
| Windows | `%LOCALAPPDATA%\TokenTracker` |
| Linux | `~/.local/share/TokenTracker` |
| macOS | `~/Library/Application Support/TokenTracker` |

可用 `TOKENTRACKER_DATA_DIR` 覆盖。

### 3. 打开看板

浏览器访问 <http://127.0.0.1:4318>，或使用命令 `Token Tracker: 在浏览器中打开看板`。

## HTTP / WebSocket

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/v1/traces` | OTLP/HTTP JSON traces |
| POST | `/v1/metrics` | OTLP/HTTP JSON metrics |
| GET | `/healthz` | 健康检查 |
| GET | `/api/version` | 名称、版本、buildId、能力列表 |
| GET | `/api/dashboard?range=0..4` | 汇总与按模型统计（含辅助调用）、会话（主调用）和全部调用 |
| GET | `/api/model/{model}` | 模型详情 |
| GET | `/api/session/{sessionId}` | 会话汇总与调用明细 |
| GET | `/api/prices` | 价格列表（内置 + 自定义） |
| POST | `/api/prices` | 新增或更新价格覆盖 |
| DELETE | `/api/prices?model=...` | 删除价格覆盖 |
| POST | `/api/import-local` | 从本地 Copilot / Agent Host span 数据库导入历史调用 |
| GET | `/api/settings` | 服务端设置（当前为数据保留天数） |
| POST | `/api/settings` | 更新数据保留天数；`0` 表示永久保留 |
| GET | `/ws` | WebSocket：`subscribe` 后接收变更驱动的 `snapshot` |

## 环境变量（服务端）

| 变量 | 默认值 | 说明 |
| --- | --- | --- |
| `TOKENTRACKER_PORT` | `4318` | 监听端口（仅 `127.0.0.1`） |
| `TOKENTRACKER_DATA_DIR` | 见上文（按平台） | 数据库目录 |
| `TOKENTRACKER_BUILD_ID` | 空 | `/api/version` 返回的构建标识 |
| `TOKENTRACKER_IDLE_EXIT_SECONDS` | `0`（不退出） | 无客户端连接后延迟退出；扩展默认传 60 |
| `TOKENTRACKER_RETENTION_DAYS` | `90` | 数据保留天数（`0` = 永久）；数据库中的设置优先于此默认值 |
| `TOKENTRACKER_SESSION_STATE_DIR` | `~/.copilot/session-state` | 会话 `workspace.yaml` 根目录 |
| `TOKENTRACKER_AGENT_HOST_DB` | 自动定位 | `agent-host.db` 路径（默认查找 stable / Insiders） |
| `TOKENTRACKER_HOLIDAY_API_URL` | `https://api.apisbo.com/holidays/year/{0}` | 节假日接口模板 |

## 会话标题与工作区

标题优先级（`SessionMetadataProvider`）：

1. `agent-host.db` 中 `titleSource=user` 的标题
2. `workspace.yaml` 中 `user_named: true` 的名称
3. Agent Host AHP `listSessions` / `session/titleChanged` 的实时标题（session id 可关联时）
4. 扩展宿主 `chat.ChatSessionStore.index`（`state.vscdb`，只读）中的有效标题
5. `agent-host.db` 的自动 / agent 摘要（与 VS Code 会话列表一致）
6. `workspace.yaml` 的自动名称

工作区路径优先取 `agent-host.db` 记录的工作目录，其次 AHP `workingDirectories`、`ChatSessionStore` 索引的 `workingDirectory`，缺失时回退到 `workspace.yaml` 的 `cwd`；状态栏据此匹配当前 VS Code 窗口。

## 发布

扩展与服务端独立发布：

```bash
# 扩展（须与 vscode-extension/package.json 版本一致）
git tag v0.1.2
git push origin v0.1.2

# 服务端
git tag server-v1.0.0
git push origin server-v1.0.0
```

- `Release` workflow：构建通用 VSIX，产物为 `tokentracker-<version>.vsix`。
- `Server Release` workflow：构建四个平台的服务端包与 `.sha256`。
- 扩展默认从**最新的服务端 Release** 下载匹配平台的服务端（缓存 30 分钟），因此服务端可以独立升级，无需重新发布扩展。
- 服务端标签应使用 `server-v<服务端版本>`；扩展标签使用 `v<扩展版本>`。

也可以通过 `workflow_dispatch` 手动触发对应 workflow。

## 构建与测试

```bash
# 核心测试（23 个用例）
dotnet test tests/TokenTracker.Core.Tests/TokenTracker.Core.Tests.csproj -c Release

# 服务端
dotnet build src/TokenTracker.Server/TokenTracker.Server.csproj -c Release

# Web 看板（输出到服务端 wwwroot）
cd web
npm install
npm run dev      # http://localhost:5173，代理到 127.0.0.1:4318
npm run build

# VS Code 扩展
cd vscode-extension
npm install
npm run build    # 本地开发：tsc + 内置 win-x64 服务端（离线运行）
npm run compile  # 仅编译 TypeScript（发布包使用，运行时下载服务端）
npm run package  # 生成通用 tokentracker-*.vsix（不含服务端）

# 生成其他平台的服务端包（需在对应平台执行 AOT 发布）
TOKENTRACKER_TARGET_RID=linux-x64 node vscode-extension/scripts/sync-server.mjs
```

## 跨平台路径

服务端按平台发布，包内目录结构一致，仅可执行文件名不同：

| 平台 | 可执行文件 |
| --- | --- |
| Windows | `TokenTracker.Server.exe` |
| Linux / macOS | `TokenTracker.Server` |

其余文件（`wwwroot/`、`build.json`、SQLite 原生库）布局一致。运行时路径：

| 内容 | Windows | Linux | macOS |
| --- | --- | --- | --- |
| 数据目录 | `%LOCALAPPDATA%\TokenTracker` | `~/.local/share/TokenTracker` | `~/Library/Application Support/TokenTracker` |
| 会话状态 | `%USERPROFILE%\.copilot\session-state` | `~/.copilot/session-state` | `~/.copilot/session-state` |
| agent-host.db | `%APPDATA%\Code\User\globalStorage\agent-host.db` | `~/.config/Code/User/globalStorage/agent-host.db` | `~/Library/Application Support/Code/User/globalStorage/agent-host.db` |
| 扩展 OTel span 库 | `%APPDATA%\Code\User\globalStorage\GitHub.copilot-chat\agent-traces.db` | `~/.config/Code/User/globalStorage/GitHub.copilot-chat/agent-traces.db` | `~/Library/Application Support/Code/User/globalStorage/GitHub.copilot-chat/agent-traces.db` |
| Agent Host OTel span 库 | `%APPDATA%\Code\agent-host\otel\agent-host-traces.db` | `~/.config/Code/agent-host/otel/agent-host-traces.db` | `~/Library/Application Support/Code/agent-host/otel/agent-host-traces.db` |

以上均可通过 `TOKENTRACKER_DATA_DIR`、`TOKENTRACKER_SESSION_STATE_DIR`、`TOKENTRACKER_AGENT_HOST_DB` 覆盖。两个 OTel span 库仅保留约 7 天 / 最近 100 个会话；长期统计以 Token Tracker 数据库为准，可用导入命令补录。VS Code 扩展为通用包，首次激活时按平台下载对应服务端。

## 数据与隐私

- 所有解析与统计均在本机完成，不上传任何数据。
- `captureContent: false`，只处理 usage / token 计数字段，不保存对话内容。
- 本地数据库默认保留 90 天，可在 Web“设置 → 数据”中修改（`0` = 永久）；保留天数、价格覆盖与节假日缓存均保存在本地 SQLite。
- 会话标题来自本机 Agent Host AHP、`agent-host.db`、扩展宿主 `ChatSessionStore` 与 `~/.copilot/session-state`，只读访问；AHP 连接 token 不写入日志，仅同步标题/工作区。

## 已知约束

- 服务端发布 `win-x64`、`linux-x64`、`osx-x64`、`osx-arm64` 自包含 AOT 构建；扩展与服务端版本独立。
- 扩展首次激活按平台解析最新服务端 Release；支持 VS Code `http.proxy` 与 `HTTPS_PROXY` / `HTTP_PROXY`（http:// CONNECT 代理），也可将 `tokentracker.serverDownloadBaseUrl` 设为镜像前缀。`github.com/releases/download` 在部分网络不可用。
- 计时与峰谷判断使用北京时间；时间范围按北京时间自然日划分。
- 未定价模型仍会统计 token，但费用显示为“未定价”，直到配置价格。

## License

[MIT](LICENSE) © 2026 JottingCN

## 文档

- [VS Code 扩展说明](vscode-extension/README.md)
- [AGENTS.md](AGENTS.md)（仓库自动化代理约定）
