# AGENTS.md

本文件面向在本仓库中工作的 AI/自动化代理。开始修改前先阅读根目录 `README.md` 与 `vscode-extension/README.md`。

## 项目概览

Token Tracker 是一个本地 Copilot Token 用量与费用看板：

```
Copilot OTLP → TokenTracker.Server (Kestrel, .NET 10 AOT) → SQLite
                                                   ├─ REST /api/*
                                                   ├─ WebSocket /ws
                                                   └─ React 看板 (wwwroot)
客户端：VS Code 扩展 / 浏览器 / 可选 WinUI 3 桌面端
```

- 服务端发布 Windows x64 / Linux x64 / macOS x64 / macOS arm64 自包含 AOT 构建；VS Code 扩展为通用包，首次激活按平台下载并校验对应服务端。
- 所有数据留在本机，不采集、不上传对话内容（`captureContent: false`）。
- 时间范围、峰谷价格均按北京时间（UTC+8）计算。

## 目录

| 路径 | 说明 |
| --- | --- |
| `src/TokenTracker.Core` | OTLP 解析、SQLite、定价、节假日、会话元数据 |
| `src/TokenTracker.Server` | ASP.NET Core：OTLP 接收、REST、WebSocket、静态看板 |
| `src/TokenTracker.App` | WinUI 3 桌面端（Windows App SDK 2.6.4，AOT） |
| `web` | React + Ant Design + Vite，构建输出到 `src/TokenTracker.Server/wwwroot` |
| `vscode-extension` | VS Code 薄客户端；`out/` 为编译产物（不跟踪，构建生成） |
| `tests/TokenTracker.Core.Tests` | 核心单元测试 |

## 常用命令

```bash
# 核心测试
dotnet test tests/TokenTracker.Core.Tests/TokenTracker.Core.Tests.csproj -c Release

# 服务端
dotnet run --project src/TokenTracker.Server -c Release
dotnet publish src/TokenTracker.Server -c Release -r win-x64 -o <dir>

# Web
cd web && npm install && npm run dev      # 5173，代理到 4318
cd web && npm run build                   # 输出到服务端 wwwroot

# VS Code 扩展
cd vscode-extension && npm install
npm run compile        # tsc → out/（不提交）
npm run build          # compile + dotnet publish 到 server/
npm run package        # 生成 tokentracker-*.vsix
```

## 关键行为（改动前确认）

### 会话标题优先级

1. `agent-host.db` 中 `titleSource=user`
2. `workspace.yaml` 中 `user_named: true`
3. Agent Host AHP `listSessions` / `session/titleChanged`（session id 可关联时）
4. 扩展宿主 `chat.ChatSessionStore.index`（`state.vscdb`，只读）
5. `agent-host.db` 自动 / agent 摘要
6. `workspace.yaml` 自动名称

### 工作区路径

优先使用 `agent-host.db` 的 `working_directories` / `launch_working_directories`，
其次 AHP `workingDirectories` 与 `ChatSessionStore` 索引的 `workingDirectory`，缺失时回退到 `workspace.yaml` 的 `cwd`。状态栏按该路径匹配当前 VS Code 工作区。

### 元数据刷新

- 扩展监听 `workspace.yaml`（500ms 去抖）和 `agent-host.db*`（500ms 去抖、15 秒节流）。
- Agent Host AHP 客户端后台读取 `local-endpoint/entries`，通过命名管道/UDS/TCP 连接，订阅根通道并处理 `listSessions`、`session/titleChanged`、`root/sessionSummaryChanged`；断线 30 秒重连，仅用于标题，不读取对话内容。
- DashboardPayload 的 `ahp` 字段返回 `state / connected / sessions`；状态变化触发实时推送，页脚展示 AHP 连接情况。
- 变化后调用 `POST /api/metadata-changed`，服务向所有 WS 客户端广播新快照。
- 旧服务不支持该接口时，扩展回退为本地重新订阅。

### 状态栏 / Tip

- 状态栏：当前工作区最新会话的合并命中率与费用（各 2 位小数）；无数据时隐藏。
- Tip：标题 `Token Tracker - 会话名`，显示时间范围与模型数，按模型分开展示四类 token 与费用（6 位小数）。
- 不在 Tip 中显示工作区信息。

### 辅助调用

- 当前实现把没有 `session_id` 的 span 视为辅助调用（`is_main = 0`）。该分类是推断，不是 Copilot 显式标记。
- 辅助调用并入 Dashboard 汇总与按模型统计；会话列表仅含主调用；“最近调用”独立菜单展示全部调用（含辅助调用）。辅助调用通常无法可靠归属具体会话。

### OpenTelemetry 数据源

- 同时支持扩展宿主 `github.copilot.chat.otel.*` 与 Agent Host `chat.agentHost.otel.*`；命令 `Token Tracker: 启用 OTel 上报` 写入两套设置并保持 `captureContent=false`。
- Agent Host 设置需完整退出并重新打开 VS Code 才生效；仅重载窗口可能复用旧 host。
- 模型归因优先 `gen_ai.response.model`（Auto 模式下 request model 可能是 `auto`）；会话 ID 依次取 `copilot_chat.session_id`、`copilot_chat.chat_session_id`、`gen_ai.conversation.id`。
- Anthropic/Claude 的 `input_tokens` 不含缓存：解析时把 cache read + cache creation 合并为总输入；cache creation 按缓存写入价计费，未配置时回退到未缓存输入价。
- 扩展管线若带 `copilot_chat.copilot_usage_nano_aiu`，调用列表在估算费用下方显示实际 AIC（nano-AIU / 1e9）；Agent Host 无该字段。
- 两个本地 span 库（扩展 `agent-traces.db`、Agent Host `agent-host-traces.db`）只保留约 7 天/100 会话；`POST /api/import-local` 只读导入并按 `(trace_id, span_id)` 去重，扩展命令为 `Token Tracker: 导入本地 Copilot OTel 数据`。
- 只统计 `gen_ai.operation.name=chat` 的 span，避免与 `invoke_agent` / `execute_tool` 重复计数。

### 费用与定价

- 内置价格可覆盖、恢复默认、删除自定义模型；保存后必须重算历史 span。
- 支持按单次调用输入 token 阈值（含缓存命中）设置第二档长上下文价格：未超过阈值用默认档，超过阈值整次调用按长上下文档计价；峰谷与分档可同时启用。
- DeepSeek 区分高峰 / 空闲价；节假日优先读 SQLite，缺失时按年拉取并缓存。
- 费用展示统一 6 位小数（状态栏摘要可用 2 位）。
- Web 看板支持人民币 / 美元 / 积分显示单位（浏览器 localStorage），价格输入按当前单位换算后仍以 CNY 保存；状态栏固定 CNY。
- 未定价模型不计算峰谷时段，调用列表显示“未定价”。

### 服务生命周期

- 发布者为 `JottingCN`，许可为 MIT；扩展按 `buildId` 管理 `globalStorage/jottingcn.tokentracker/runtime/<buildId>`，共享单实例；`server-runtime.json` 缓存平台/Release。
- 首次激活按 RID 从**最新的服务端 Release** 下载并校验 `.sha256`（支持 `http.proxy` / `HTTPS_PROXY`），Release 检查缓存 30 分钟；本地开发目录存在 `server/` 时优先使用内置副本。
- 数据位于 `globalStorage/jottingcn.tokentracker/data/tokentracker.db`，首次启动会从旧发布者目录迁移。
- 数据保留默认 90 天，可通过 `TOKENTRACKER_RETENTION_DAYS` 或 `POST /api/settings` 修改（`0` = 永久）；修改后立即清理，后台每 6 小时再执行。
- 无 Windows 服务；所有客户端断开后按 `tokentracker.idleExitSeconds` 退出。
- 扩展与服务端独立发布：扩展标签 `v*`（`Release`），服务端标签 `server-v*`（`Server Release`）。
- `vscode-extension/server/` 由 `npm run sync-server` 生成，不要手改；发布 VSIX 通过 `.vscodeignore` 排除该目录。
- 日志 `extension.log` 超过 1 MB 轮转为 `extension.log.1`。

## 工程约束

- .NET：`Nullable` + `ImplicitUsings` 开启；服务端 AOT，JSON 必须使用 `AppJsonContext` 源生成，禁止引入反射序列化。
- TypeScript：`strict`、`noUnusedLocals`、`noUnusedParameters`；提交前运行 `npm run compile`。
- SQLite schema 变更使用幂等迁移（`EnsureColumn` / `CREATE TABLE IF NOT EXISTS`），不要破坏已有数据库。
- 不要重新引入 TableView（Windows App SDK 2.6.4 不支持）；WinUI 列表使用现有 TableRowFactory 方案。
- 不添加 Webview；看板在浏览器打开。
- 不新增依赖或抽象，除非任务确实需要。
- 代码注释只写非显而易见的“为什么”，不写“做了什么”。
- 界面文案保持中文；费用、时间格式与现有实现一致。

## 验证要求

1. 修改核心逻辑：运行全部单元测试。
2. 修改扩展 TypeScript：`npm run compile`（`out/` 不提交）；验证下载链路时可用隔离 profile，并将 `tokentracker.serverDownloadBaseUrl` 指向本地静态目录。
3. 修改服务端：至少 `npm run sync-server` 生成新构建；确认 AOT publish 成功。跨平台包使用 `TOKENTRACKER_TARGET_RID=<rid>` 在对应平台发布。
4. 涉及扩展行为时，用**隔离** VS Code 实例验证：

```bash
Code.exe --new-window \
  --user-data-dir <temp-profile> \
  --extensions-dir <temp-exts> \
  --extensionDevelopmentPath <repo>/vscode-extension \
  <repo>
```

- 通过 `<temp-profile>/User/globalStorage/jottingcn.tokentracker/extension.log` 检查激活与快照日志。
- 可用临时端口（如 4330+）与临时数据库副本，避免影响正在使用的 4318 服务。

## 禁止事项

- 不要修改或写入真实的 `~/.copilot/session-state`、`agent-host.db` 或用户数据库；验证请复制到临时目录。
- 不要终止用户正在使用的 `TokenTracker.Server`（默认端口 4318）或关闭用户的 VS Code 窗口。
- 不要上传任何数据或引入遥测。
- 不要在未明确要求时提交 git；提交时遵循现有 commit 风格，只暂存相关文件。

## 提交风格

已有提交使用 `type: summary` 形式（`feat: ...`、`docs: ...`、`chore: ...`），主体说明“为什么”。不要添加与任务无关的格式化改动。
