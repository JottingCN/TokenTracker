# AGENTS.md

本文件面向在本仓库工作的 AI/自动化代理与开发者。面向使用者的说明见根目录 `README.md` 与 `vscode-extension/README.md`；开始修改前请先读完本文件的**工程约束**、**验证要求**与**禁止事项**。

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
| `vscode-extension` | VS Code 薄客户端；`out/` 与 `server/` 为构建产物（不跟踪） |
| `tests/TokenTracker.Core.Tests` | 核心单元测试 |
| `docs` | Copilot Chat / Agent Host 的 OTel 字段文档与来源识别方案 |

## 常用命令

```bash
# 核心测试
dotnet test tests/TokenTracker.Core.Tests/TokenTracker.Core.Tests.csproj -c Release

# 服务端
dotnet run --project src/TokenTracker.Server -c Release
dotnet build src/TokenTracker.Server/TokenTracker.Server.csproj -c Release
dotnet publish src/TokenTracker.Server -c Release -r win-x64 -o <dir>

# Web
cd web && npm install && npm run dev      # 5173，代理到 4318
cd web && npm run build                   # 输出到服务端 wwwroot

# VS Code 扩展
cd vscode-extension && npm install
npm run compile        # tsc → out/（不提交）
npm test               # node --test：状态栏工作区选择与 Tip 渲染（先 pretest 编译）
npm run sync-server    # AOT 发布服务端到 server/（需 vcvarsall.bat x64）
npm run build          # compile + sync-server（本地离线开发用）
npm run package        # 生成 tokentracker-*.vsix（排除 server/）
```

`npm run sync-server` 调用 `scripts/sync-server.mjs`，Windows 上需要 MSVC 环境；跨平台构建用
`TOKENTRACKER_TARGET_RID=<rid> node vscode-extension/scripts/sync-server.mjs`，必须在对应平台执行 AOT 发布。

## 接口

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/otlp/{source}`、`/otlp/{source}/v1/traces` | 按路径绑定来源：`copilot-chat` / `agent-host`（旧 `extension-host` 兼容） |
| POST | `/v1/traces` | 兼容旧配置，按资源属性推断来源，无法判断写入 `unknown` |
| POST | `/v1/metrics`、`/otlp/{source}/v1/metrics` | 只按 outcome allowlist 入库；诊断时 `TOKENTRACKER_CAPTURE_OTEL_METRICS=1` 会把 payload 存到 `otel-metrics/` |
| GET | `/healthz`、`/api/version` | 健康检查；名称、版本、buildId、features 列表 |
| GET | `/api/system` | 数据库路径/大小、表与行数、原始/综合 span、outcome 指标、监听与 AHP 状态 |
| GET | `/api/dashboard` | `range=0..4`、`source`；汇总、按模型（含辅助调用）、会话（主调用） |
| GET | `/api/model/{model}` | `range` 或 `from`/`to`、`source`；模型详情、TTFT、平均生成速度、会话列表 |
| GET | `/api/session/{sessionId}` | 会话汇总、调用、事件统计、Agent 运行、Hook、工具统计、outcome 指标 |
| GET | `/api/session/{sessionId}/trace` | Trace 拓扑节点（时间升序，最多 5000，超限保留最新） |
| GET | `/api/calls` | 服务端分页明细：`range/source/model/query/kind/priced/period/offset/limit` |
| GET/POST/DELETE | `/api/prices` | 价格列表、新增或更新（保存后重算历史）、按 model 删除 |
| GET/POST | `/api/settings` | 数据保留天数（`0` = 永久） |
| POST | `/api/metadata-changed` | 扩展写入元数据后请求广播新快照 |
| POST | `/api/import-local` | 从本地 span 库只读导入，body `{ paths }` |
| GET | `/ws` | WebSocket：`subscribe` 后接收变更驱动快照 |

`source=combined`（旧 `all` 为别名）是默认值；不带 `source` 即综合视图。

## 环境变量（服务端）

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

## 跨平台路径（只读访问，禁止写入）

| 内容 | Windows | Linux | macOS |
| --- | --- | --- | --- |
| 独立服务端数据 | `%LOCALAPPDATA%\TokenTracker` | `~/.local/share/TokenTracker` | `~/Library/Application Support/TokenTracker` |
| 扩展数据 | `globalStorage/jottingcn.tokentracker/data` | 同左 | 同左 |
| 会话状态 | `%USERPROFILE%\.copilot\session-state` | `~/.copilot/session-state` | `~/.copilot/session-state` |
| agent-host.db | `%APPDATA%\Code\User\globalStorage\agent-host.db` | `~/.config/Code/User/globalStorage/agent-host.db` | `~/Library/Application Support/Code/User/globalStorage/agent-host.db` |
| 扩展 span 库 | `%APPDATA%\Code\User\globalStorage\GitHub.copilot-chat\agent-traces.db` | `~/.config/.../agent-traces.db` | `~/Library/.../agent-traces.db` |
| Agent Host span 库 | `%APPDATA%\Code\agent-host\otel\agent-host-traces.db` | `~/.config/Code/agent-host/otel/...` | `~/Library/Application Support/Code/agent-host/otel/...` |

两个本地 span 库只保留约 7 天 / 100 会话，长期统计以 Token Tracker 数据库为准。

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
其次 AHP `workingDirectories` 与 `ChatSessionStore` 索引的 `workingDirectory`，缺失时回退到 `workspace.yaml` 的 `cwd`；
再缺失时回退 `session_workspaces` 缓存（由 `SessionWorkspaceCache` 在解析成功时批量写入，`DashboardService` 每次构建 payload 后 flush）。`agent-host.db` 里查不到子代理会话的工作目录时，需沿 `chats_v2` 的 `owner_session_uri` / `parent_chat` 回溯到父会话取，且两种存放形态（`chats_v2.working_directories` 与 `session_chat_state.*_working_directories`）都要接受。状态栏按该路径匹配当前 VS Code 工作区。

### 元数据刷新

- 扩展监听 `workspace.yaml`（500ms 去抖）和 `agent-host.db*`（500ms 去抖、15 秒节流）。
- Agent Host AHP 客户端后台读取 `local-endpoint/entries`，通过命名管道/UDS/TCP 连接，订阅根通道并处理 `listSessions`、`session/titleChanged`、`root/sessionSummaryChanged`；断线 30 秒重连，仅用于标题，不读取对话内容。
- AHP 协议层使用官方 `Microsoft.VisualStudioCode.AgentHostProtocol` SDK；自定义 transport 仅负责本地 named pipe/UDS 的 WebSocket 握手。
- DashboardPayload 的 `ahp` 字段返回 `state / connected / sessions / items`；状态变化触发实时推送，左侧停靠区展示 AHP 连接情况。
- 变化后调用 `POST /api/metadata-changed`，服务向所有 WS 客户端广播新快照。
- 旧服务不支持该接口时，扩展回退为本地重新订阅。

### 状态栏 / Tip

- 状态栏：当前工作区最新会话的命中率、平均 TTFT、平均生成速度与费用；默认模板 `$(pulse) {hit} · {ttft} · {speed} · {cost}`，可通过 `tokentracker.statusBarFormat` 自定义占位符与格式（如 `{hit:0.00%}`、`{ttft:0.0s}`、`{cost:0.000000}`），缺失的可选项自动省略。
- 状态栏按工作区过滤后若一条都不匹配，回退到**工作区未知**（`workspacePath` 为空）的最近会话，不要整条隐藏；此时 Tip 首行追加 `$(folder) 工作区未确认`。其他工作区的会话仍不显示。
- 状态栏与 Tip 的费用单位由 `tokentracker.costUnit` 决定（`cny` / `usd` / `credit`，按 `usdRate` / `creditRate` 换算）。
- Tip：标题为正文加粗 `Token Tracker · 会话名`（不使用大号标题）；时间范围与统计范围合并为一行；每个模型独立成节，节标题为 `模型名 · 最后使用时间`，展示按显示宽度对齐的四类 token 明细表格，以及次数、总用时、平均耗时、命中率、TTFT、速度与费用；多于一个模型时在末尾展示合计。工具调用节标题带 `N 次 · M 种工具`，Hook 节标题带 `N 次 · M 组`，各自只列 Top 5。不在 Tip 中展示 Agent 运行与“其他事件”统计（看板会话页仍完整展示）。
- 不在 Tip 中显示工作区信息。

### 辅助调用

- 当前实现把没有 `session_id` 的 span 视为辅助调用（`is_main = 0`）。该分类是推断，不是 Copilot 显式标记。
- 辅助调用并入 Dashboard 汇总与按模型统计；会话列表仅含主调用；“调用明细”独立菜单展示全部调用（含辅助调用），支持关键字、来源、主/辅助、定价状态与时段检索，并按来源分别计数。辅助调用通常无法可靠归属具体会话。
- 来源（Copilot Chat `github.copilot.chat.otel`、Agent Host `chat.agentHost.otel`）只用于接收端归因；Web 与扩展默认展示**综合**数据，不提供来源切换和来源标签。`source=combined`（旧 `source=all` 作为别名）表示跨来源同一会话/模型/输入输出且 span 时间区间重叠的调用合并为一条，优先保留有效 TTFT、缓存、推理等字段（费用按合并后的 token 重新计算），原始来源记录仍保留供合并与诊断使用。接收端优先用路径区分：`/otlp/copilot-chat`、`/otlp/agent-host`（同时接受 `/v1/traces` 后缀，旧路径 `/otlp/extension-host` 作为别名）；旧 `/v1/traces` 再按资源属性判定。Agent Host 统一带 `service.namespace=vscode.agent-host`，其 `service.name` 可能为 `github-copilot`、`claude-code`、`codex-app-server`、`vscode-agent-host`；Copilot Chat 默认为 `copilot-chat` 且通常无 namespace。无法判断时写入 `unknown`，不要默认归入 Copilot Chat。会话页事件统计来自 `session_events`（`execute_tool` / `invoke_agent` 等），工具统计按 `gen_ai.tool.name` 汇总；工具事件同时保存 `gen_ai.tool.type`、`github.copilot.tool.parameters.edit_type` 与 `...skill_name`。仅事件、没有 chat span 的会话也会进入会话列表（调用列显示 `0 (N 事件)`）。
- 调用明细使用服务端分页（`total` / `offset` / `limit`，单页上限 500），不要重新引入固定 2000 条截断；文本检索在服务端匹配会话标题（元数据）、模型与会话 ID 后直接分页。combined 行按“脏会话”增量重建，只在相关会话新增/变更时更新；无 `session_id` 的辅助调用按批次时间窗增量重建（窗口沿重叠区间扩展，超过 12 小时或 4 轮仍不收敛才回退全量重建），**不要恢复“辅助调用批次 → 全表重建”**：实测 2 万 span 时全量重建单次读取消耗 3~12 秒 CPU，窗口重建只需 0.16 秒。窗口查询必须写成可用 `ix_spans_start` / `ix_spans_end` 的 OR 形式，`COALESCE(end_time_ms, start_time_ms)` 会退化成全表扫描。
- 左侧“系统信息”抽屉读取 `GET /api/system`，展示数据库路径/大小、表与行数、原始/综合 span、outcome 指标、监听与读取路径、AHP 状态；页脚状态栏已移除。

### OpenTelemetry 数据源

- 同时支持 Copilot Chat `github.copilot.chat.otel.*` 与 Agent Host `chat.agentHost.otel.*`；命令 `Token Tracker: 启用 OTel 上报` 写入两套设置并保持 `captureContent=false`。默认 endpoint 为 `http://127.0.0.1:<port>/otlp/copilot-chat` 与 `/otlp/agent-host`。
- Agent Host 设置需完整退出并重新打开 VS Code 才生效；仅重载窗口可能复用旧 host。
- 模型归因优先 `gen_ai.response.model`（Auto 模式下 request model 可能是 `auto`）；会话 ID 优先 `copilot_chat.parent_chat_session_id`（子代理归到父级/根会话），其次 `copilot_chat.session_id`、`gen_ai.conversation.id`、`copilot_chat.chat_session_id`；`call_` 开头的是工具调用 ID，不是会话 ID，必须忽略。子代理的 `execute_tool` span 会把会话字段写成父 call id，需沿 `parent_span_id` 回溯归属根会话；启动时以 `subagent_sessions_v1` 修复历史行。
- 子代理 `invoke_agent` 写入 `agent_runs`（父 span 为对应 `execute_tool`），其工具调用计入根会话工具统计；子代理 chat span 是真实 LLM 调用，计入会话调用与费用，但 `agent_runs` 汇总 token 不重复计入 chat 汇总。
- Anthropic/Claude 的 `input_tokens` 不含缓存：解析时把 cache read + cache creation 合并为总输入；cache creation 按缓存写入价计费，未配置时回退到未缓存输入价。
- 扩展管线若带 `copilot_chat.copilot_usage_nano_aiu`，调用列表在估算费用下方显示实际 AIC（nano-AIU / 1e9）；Agent Host 无该字段。
- 首次响应时间优先 `copilot_chat.time_to_first_token`（毫秒），其次 `gen_ai.server.time_to_first_token` / `gen_ai.response.time_to_first_token`；`gen_ai.response.time_to_first_chunk` 仅在没有明确 TTFT 且明显小于总耗时时使用，≥90% 总耗时视为非流式/代理缓冲并丢弃。生成速度只在有有效 TTFT 时按“输出 token /（耗时 − TTFT）”计算；生成时间不足 50 ms、输出少于 5 token 或无 TTFT 显示 `-`。
- 模型名按来源规范化：Copilot Chat 的 `gen_ai.*.model` 是规范 ID（如 `gpt-4o-mini-2024-07-18`），只做小写与去注解，**不裁剪日期/版本/`-latest`**；Agent Host 模型名带上游前缀（实测 `DeepSeek/deepseek-flash`），只去最外层一段前缀，保留 `provider/model`。价格表键同样只去最外层前缀，查找时优先精确匹配。不做历史模型名回填。
- 去重只使用文档化标识：span 以 `(trace_id, span_id)` 为主键，工具事件同来源按 `gen_ai.tool.call.id` 去重；**不要**按会话/模型/token/时间窗口猜测合并同来源 span。两套管线的 `gen_ai.response.id`、`trace_id`、`span_id` 互不相通，跨管线没有共享唯一 ID，因此 combined 只能用“会话 + 规范化模型 + 输入/输出 token + 主/辅助 + span 时间区间重叠”匹配（派生行写入 `otel_source='combined'`），原始记录保持不变，原始 span 变化后懒重建。费用不继承任一来源估算值，按合并后 token 与配置价格重算。
- Trace 拓扑始终排除 combined 派生行；综合视图在时间区间重叠时合并跨来源重复的 chat 节点并重挂子节点。拓扑按开始时间排序、用 depth 缩进展示层级、节点显示 chat 输入/输出/缓存 token；不要引入 git graph；超过 5000 节点保留最新一段，前端虚拟滚动并阻止滚动链到页面，支持全屏查看。
- 结果指标（编辑接受率、LOC、存活率、反馈/操作、PR/cloud、Agent 编辑）从 `metric_points` 按查询时累计 delta 聚合。metrics 的 `session.id` 是 VS Code 窗口级，归属顺序：自带 `gen_ai.conversation.id` / `copilot_chat.session_id` 直接归属 → 带 `gen_ai.tool.call.id` 时按 `session_events.call_id` 归属 → 否则记录 `window_session_id`，仅当该窗口在 `window_session_links` 中只有一个 chat session 时归属（`attribution_method=window`，置信度 0.6），多会话窗口保留 `window-only`，不要强行分摊。Dashboard 全局指标只按原始 metric point 统计一次。
- 只统计 `gen_ai.operation.name=chat` 的 span，避免与 `invoke_agent` / `execute_tool` 重复计数。
- 隐私约束：trace/metric 解析只保存非内容元数据 allowlist；即使 `captureContent` 开启，也不写入 prompt/response、工具参数/结果、单次调用的 file path、command、raw MCP server name、session title 或身份字段。唯一例外是 `session_workspaces` 保存的**会话工作区根目录**，它来自本机 VS Code 元数据（AHP / agent-host.db / workspace.yaml），不来自遥测内容，仅用于工作区归属匹配。
- `POST /api/import-local` 只读导入 chat span、工具/Hook 事件与 `invoke_agent`，按 `(trace_id, span_id)` / tool call id 去重。导入 chat span 必须复用在线解析的同一套规则（`ParseSessionId`、`ParseTtftMs` + `HasExplicitTtft` 的首块 ≥90% 丢弃、`ErrorTypeFrom`、provider/agent/finish_reasons/service.namespace），字段来自 `spans` 表的 `ttft_ms`/`agent_name`/`status_code` 列与 `span_attributes` 批量读取；只按列拼 session_id 会丢掉子代理归属和 TTFT、生成速度。

### 费用与定价

- 不内置任何模型价格；价格必须按模型配置。未配置价格的模型显示“未定价”；保存后必须重算历史 span。
- 支持按单次调用输入 token 阈值（含缓存命中）设置长上下文第二档价格：超过阈值整次调用按长上下文档计价；峰谷与分档可同时启用。
- DeepSeek 区分高峰 / 空闲价；节假日优先读 SQLite，缺失时按年拉取并缓存。
- 费用展示统一 6 位小数；Web 支持人民币 / 美元 / 积分显示单位（浏览器 localStorage），价格输入按当前单位换算后仍以 CNY 保存。
- 未定价模型不计算峰谷时段。

### 存储与迁移

- 本地库只存需要的数据：span 元数据、`session_events`、`agent_runs`、outcome `metric_points`、`window_session_links`、`session_workspaces`（会话工作目录缓存）、价格、节假日、设置。不保存原始 `attributes`、日志事件、可由 span 推导的 token/TTFT/duration 指标；会话标题仍只在查询时解析，不入库。
- 工作目录来源（AHP `workingDirectories` / `agent-host.db` / `workspace.yaml`）大多要求 Agent Host 在线。服务端在解析成功时把 `session_id → workspace_path` 写入 `session_workspaces`，解析不到时回退读该表，因此 AHP 掉线后工作区归属不会丢；`DeleteOlderThan` 会清掉已无 span 的会话缓存。
- SQLite schema 变更使用幂等迁移（`EnsureColumn` / `CREATE TABLE IF NOT EXISTS`）。**建索引必须放在列迁移之后**：索引引用尚未添加的列会让 DDL 批次中断、后续表缺失并在启动时崩溃。
- 插件仍在开发期，不考虑历史数据兼容：不要新增旧库迁移、旧字段回填或版本兼容分支（扩展侧已移除旧数据库复制逻辑）。

### 服务生命周期与更新

- 发布者为 `JottingCN`，许可 MIT；扩展按 `buildId` 管理 `globalStorage/jottingcn.tokentracker/runtime/<buildId>`，同端口共享单实例；`server-runtime.json` 缓存平台/Release，`extension.log` 超过 1 MB 轮转为 `extension.log.1`。
- 首次激活按 RID 从**最新的服务端 Release** 下载并校验 `.sha256`（支持 `http.proxy` / `HTTPS_PROXY`），Release 检查缓存 30 分钟；本地开发目录存在 `server/` 时优先使用内置副本。
- 端口上已有健康服务时：激活只探测 `/healthz` + `/api/version` 并立即接管（`attachOrStartServer`），**不下载、不重启**运行中的实例（即使是扩展自己启动且 buildId 不一致的）。Release 检查与下载在 `checkServerUpdateInBackground` 后台执行，成功或失败都只写 `extension.log`；检测到新版本只提示一次并给“重启服务”动作，由用户决定是否替换。
- 只有 `Token Tracker: 启动服务` 命令才执行完整的“准备服务端 + 启动/替换过期自建实例”。
- 无 Windows 服务；所有客户端断开后按 `tokentracker.idleExitSeconds` 退出（扩展默认 5 秒，`0` = 不退出）。
- 数据保留默认 90 天（`TOKENTRACKER_RETENTION_DAYS` 或 `POST /api/settings`，`0` = 永久），修改后立即清理，后台每 6 小时再执行。

## 发布

扩展与服务端独立发布：

```bash
git tag v0.1.2 && git push origin v0.1.2              # 扩展（与 package.json 版本一致）
git tag server-v1.0.0 && git push origin server-v1.0.0 # 服务端
```

- `Release` workflow 构建通用 VSIX（`tokentracker-<version>.vsix`）；`Server Release` workflow 构建四平台服务端包与 `.sha256`。
- 扩展默认从最新服务端 Release 下载，因此服务端可独立升级而无需重发扩展；也可用 `tokentracker.serverDownloadBaseUrl` 指向镜像前缀。
- 也可通过 `workflow_dispatch` 手动触发。

## 工程约束

- .NET：`Nullable` + `ImplicitUsings` 开启；服务端 AOT，JSON 必须使用 `AppJsonContext` 源生成，禁止引入反射序列化。
- TypeScript：`strict`、`noUnusedLocals`、`noUnusedParameters`；提交前运行 `npm run compile`。
- 不要重新引入 TableView（Windows App SDK 2.6.4 不支持）；WinUI 列表使用现有 TableRowFactory 方案。
- 不添加 Webview；看板在浏览器打开。
- 不新增依赖或抽象，除非任务确实需要。
- 代码注释只写非显而易见的“为什么”，不写“做了什么”。
- 界面文案保持中文；费用、时间格式与现有实现一致。列表分页放在卡片标题栏右侧，条数写在标题后 `标题(N)`，单页时隐藏分页。

## 验证要求

1. 修改核心逻辑：运行全部单元测试。工作区缓存的回写/回退/批量阈值由 `SessionWorkspaceCacheTests` 覆盖，`session_workspaces` 与 span 一起清理由 `SpanStoreTests.SessionWorkspaceCacheSurvivesAndIsPrunedWithSpans` 覆盖。
2. 修改扩展 TypeScript：`npm run compile`（`out/` 不提交）；状态栏的工作区选择与 Tip 标注由 `vscode-extension/test/statusBar.test.js`（`npm test`，Node 内置 runner + `vscode` 桩模块，不引入测试依赖）覆盖；验证下载链路时可用隔离 profile，并将 `tokentracker.serverDownloadBaseUrl` 指向本地静态目录。
3. 修改服务端：至少 `npm run sync-server` 生成新构建；确认 AOT publish 成功。
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
- 需要检查状态栏文本或 Tip 结构时，可用桩 `vscode` 模块（`MarkdownString` / `createStatusBarItem` / `getConfiguration`）配合 `NODE_PATH` 直接加载 `out/statusBar.js` 驱动真实 `StatusBarController`，打印 `item.tooltip.value` 即可断言渲染结果，不必启动 GUI 窗口。
- 需要对比服务端负载时，先向临时库灌入合成 span，再按“辅助调用批次 → 综合读取”的顺序计时；换 `TOKENTRACKER_PORT` 与 `TOKENTRACKER_DATA_DIR` 起独立实例，验证完只清理自己启动的进程。
- 验证“健康实例不被重启 / 后台更新检查失败不影响使用”时，先用临时数据目录手动起一个 `TOKENTRACKER_BUILD_ID=legacy-build-1` 的服务再激活扩展，确认 `server ready startedHere=false` 早于 `update check:` 日志且进程未变。

## 禁止事项

- 不要修改或写入真实的 `~/.copilot/session-state`、`agent-host.db`、`state.vscdb` 或用户数据库；验证请复制到临时目录。
- 不要终止用户正在使用的 `TokenTracker.Server`（默认端口 4318）或关闭用户的 VS Code 窗口；只清理自己启动的测试实例。
- 不要上传任何数据或引入遥测。
- 不要在未明确要求时提交 git；提交时遵循现有 commit 风格，只暂存相关文件。

## 提交风格

已有提交使用 `type: summary` 形式（`feat: ...`、`docs: ...`、`chore: ...`），正文说明“为什么”。不要添加与任务无关的格式化改动。
