# Copilot Chat OpenTelemetry 监控信息文档

> 范围：本文档聚焦 **Copilot Chat 本地扩展管道**，即 `github.copilot.chat.otel.*` / `COPILOT_OTEL_*` / `OTEL_*`，服务名 `copilot-chat`。Agent Host 管道 `chat.agentHost.otel.*` 仅在对比处简要提及，不展开。  
> 依据：你提供的 VS Code 文档及前述讨论整理；标注“补充排查”的内容来自讨论，非文档原文。

---

## 1. 概述

Copilot Chat 可通过 OpenTelemetry（OTel）导出 **traces、metrics、events**，用于观察：

- 代理交互
- LLM 调用
- 工具执行
- Hook 执行
- Token 用量与缓存 token
- 编辑接受/拒绝、代码行数、用户反馈等结果指标

所有信号名称和属性遵循 **OTel GenAI Semantic Conventions**，可对接任意 OTLP 兼容后端。

### 属性命名空间

| 命名空间 | 来源 | 使用建议 |
|---|---|---|
| `gen_ai.*` | OTel GenAI 语义约定 | 存在标准键时优先使用 |
| `github.copilot.*` | Copilot 专用规范命名空间，与 GitHub Copilot CLI OTel 约定共享 | 推荐用于新仪表板、告警、查询 |
| `copilot_chat.*` | 原始 VS Code 扩展命名空间 | 旧版；部分键与 `github.copilot.*` 双重发出 |

> 旧版 `copilot_chat.*` 键会无限期继续发出，**没有终止日期**。现有收集器、仪表板和下游消费者无需修改。

---

## 2. 启用条件

满足任一条件即激活 OTel：

- `github.copilot.chat.otel.enabled` 为 `true`
- `github.copilot.chat.otel.dbSpanExporter.enabled` 为 `true`
- `COPILOT_OTEL_ENABLED=true`
- 设置了 `OTEL_EXPORTER_OTLP_ENDPOINT`

---

## 3. VS Code 设置

打开 Settings 搜索 `copilot otel`。

| 设置 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `github.copilot.chat.otel.enabled` | boolean | `false` | 启用 OTel 发出 |
| `github.copilot.chat.otel.exporterType` | string | `"otlp-http"` | `otlp-http`、`otlp-grpc`、`console`、`file` |
| `github.copilot.chat.otel.otlpEndpoint` | string | `"http://localhost:4318"` | OTLP 收集器端点 |
| `github.copilot.chat.otel.captureContent` | boolean | `false` | 捕获完整提示、响应、系统提示、工具 schema、工具参数、工具结果 |
| `github.copilot.chat.otel.captureIdentity` | boolean | `false` | 为 Local harness 会话捕获认证账户名、OS 用户名、机器主机名 |
| `github.copilot.chat.otel.maxAttributeSizeChars` | integer | `0` | 每个内容属性最大字符数；`0` 不截断 |
| `github.copilot.chat.otel.outfile` | string | `""` | JSON-lines 输出文件路径 |
| `github.copilot.chat.otel.dbSpanExporter.enabled` | boolean | `false` | 将 span 持久化到本地 SQLite，供导出命令使用；隐式启用 OTel |

---

## 4. 环境变量

环境变量优先于 VS Code 设置。企业托管设置会覆盖用户设置，但 Copilot Chat 扩展中 OTel 环境变量仍可覆盖托管值。

| 变量 | 默认值 | 说明 |
|---|---|---|
| `COPILOT_OTEL_ENABLED` | `false` | 启用 OTel；设置 `OTEL_EXPORTER_OTLP_ENDPOINT` 也会启用 |
| `COPILOT_OTEL_ENDPOINT` | — | OTLP 端点，优先于 `OTEL_EXPORTER_OTLP_ENDPOINT` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — | 标准 OTel OTLP 端点 |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` | 仅 `grpc` 改变行为 |
| `COPILOT_OTEL_PROTOCOL` | — | 覆盖协议：`grpc` 或 `http`，优先于上者 |
| `OTEL_SERVICE_NAME` | `copilot-chat` | 资源属性中的服务名 |
| `OTEL_RESOURCE_ATTRIBUTES` | — | 额外资源属性，如 `team.id=platform,department=engineering` |
| `COPILOT_OTEL_CAPTURE_CONTENT` | `false` | 捕获完整提示和响应内容 |
| `COPILOT_OTEL_CAPTURE_IDENTITY` | `false` | 为 Local harness 会话捕获开发者和机器身份 |
| `COPILOT_OTEL_MAX_ATTRIBUTE_SIZE_CHARS` | `0` | 覆盖内容属性最大字符数；优先于设置项；`0` 不截断 |
| `COPILOT_OTEL_LOG_LEVEL` | `info` | `trace`、`debug`、`info`、`warn`、`error` |
| `COPILOT_OTEL_FILE_EXPORTER_PATH` | — | 将所有信号以 JSON lines 写入文件 |
| `COPILOT_OTEL_HTTP_INSTRUMENTATION` | `false` | 启用 HTTP 级 OTel 插桩 |
| `OTEL_EXPORTER_OTLP_HEADERS` | — | 收集器静态 HTTP 头；托管 Agent Host 动态头另见 Agent Host 文档 |

---

## 5. 命令

当 `github.copilot.chat.otel.dbSpanExporter.enabled` 为 `true` 时：

| 命令 | 说明 |
|---|---|
| **Chat: Export Agent Traces DB**（`github.copilot.chat.otel.exportAgentTracesDB`） | 将本地 SQLite span 数据库导出为 `.db` 文件 |

---

## 6. 收集的信号

### 6.1 Traces：典型 span 树

```text
invoke_agent copilot                           [~15s]
  ├── chat gpt-4o                              [~3s]  (LLM 请求工具调用)
  ├── execute_tool readFile                    [~50ms]
  ├── execute_tool runCommand                  [~2s]
  ├── chat gpt-4o                              [~4s]  (LLM 生成最终响应)
  └── (span 结束)
```

子代理通过 `runSubagent` 等工具调用时，trace context 自动传播；子代理的 `invoke_agent` span 作为父代理 `execute_tool` span 的子项，形成跨异步边界的连接 trace。

### 6.2 `invoke_agent` span

包装整个代理编排，包括所有 LLM 调用和工具执行。

| 属性 | 说明 |
|---|---|
| `gen_ai.operation.name` | 始终 `invoke_agent` |
| `gen_ai.provider.name` | 提供程序，如 `github` |
| `gen_ai.agent.name` | 代理名，如 `copilot`、`copilotcli`、`claude` |
| `gen_ai.conversation.id` | 会话 ID |
| `gen_ai.request.model` | 请求模型 |
| `gen_ai.response.model` | 解析模型 |
| `gen_ai.usage.input_tokens` | 会话总输入 token |
| `gen_ai.usage.output_tokens` | 会话总输出 token |
| `gen_ai.usage.cache_read.input_tokens` | 缓存读取输入 token（若可用） |
| `gen_ai.usage.cache_creation.input_tokens` | 缓存创建输入 token（若可用） |
| `github.copilot.agent.type` | `builtin`、`custom`、`plugin` |
| `github.copilot.git.repository` | Git 仓库远程 URL |
| `github.copilot.git.branch` | 活动分支 |
| `github.copilot.git.commit_sha` | 当前提交 |
| `github.copilot.github.org` | GitHub org owner，仅 GitHub remote |
| `copilot_chat.repo.remote_url` | 旧版，优先 `github.copilot.git.repository` |
| `copilot_chat.repo.head_branch_name` | 旧版，优先 `github.copilot.git.branch` |
| `copilot_chat.repo.head_commit_hash` | 旧版，优先 `github.copilot.git.commit_sha` |
| `copilot_chat.turn_count` | 会话内 LLM 往返次数 |
| `error.type` | 失败时错误类 |
| `gen_ai.input.messages` | 完整提示消息（仅内容捕获） |
| `gen_ai.output.messages` | 完整响应消息（仅内容捕获） |
| `gen_ai.tool.definitions` | 工具 schema（仅内容捕获） |

### 6.3 `chat` span

每次 LLM API 调用一个 span。

| 属性 | 说明 |
|---|---|
| `gen_ai.operation.name` | 始终 `chat` |
| `gen_ai.provider.name` | 提供程序名 |
| `gen_ai.request.model` | 请求模型 |
| `gen_ai.response.model` | 解析模型 |
| `gen_ai.response.finish_reasons` | 停止原因，如 `["stop"]` |
| `copilot_chat.parent_chat_session_id` | 子代理/派生调用的父级会话 ID；子代理工具 span 的 `session_id`/`conversation.id` 可能是父工具 call id，应优先用该字段归入一级会话 |
| `gen_ai.request.max_tokens` | 最大输出 token |
| `gen_ai.request.temperature` | 温度，若设置 |
| `gen_ai.request.top_p` | Top-p，若设置 |
| `gen_ai.usage.input_tokens` | 本次调用输入 token |
| `gen_ai.usage.output_tokens` | 本次调用输出 token |
| `gen_ai.usage.cache_read.input_tokens` | 缓存读取输入 token（若可用） |
| `gen_ai.usage.cache_creation.input_tokens` | 缓存创建输入 token（若可用） |
| `gen_ai.usage.reasoning.output_tokens` | 推理 token（若可用） |
| `gen_ai.usage.reasoning_tokens` | 旧版，优先 `gen_ai.usage.reasoning.output_tokens` |
| `copilot_chat.time_to_first_token` | 首个 SSE token 时间（毫秒） |
| `server.address` | API 主机名 |
| `error.type` | 失败时错误类 |

### 6.4 `execute_tool` span

每次工具调用一个 span。

| 属性 | 说明 |
|---|---|
| `gen_ai.operation.name` | 始终 `execute_tool` |
| `gen_ai.tool.name` | 工具名，如 `readFile` |
| `gen_ai.tool.type` | `function` 或 `extension`（MCP 工具） |
| `gen_ai.tool.call.id` | 工具调用标识 |
| `github.copilot.tool.parameters.edit_type` | 编辑工具：`create`、`update`、`str_replace`、`insert` |
| `github.copilot.tool.parameters.skill_name` | 技能调用 |
| `github.copilot.tool.parameters.mcp_server_name_hash` | MCP 工具：server 名 SHA-256 |
| `github.copilot.tool.parameters.mcp_tool_name` | MCP 工具：调用的工具名 |
| `github.copilot.tool.parameters.command` | shell 工具（仅内容捕获，截断） |
| `github.copilot.tool.parameters.file_path` | 文件工具（仅内容捕获） |
| `github.copilot.tool.parameters.mcp_server_name` | MCP 工具（仅内容捕获） |
| `error.type` | 失败时错误类 |
| `gen_ai.tool.call.arguments` | 工具输入参数（仅内容捕获） |
| `gen_ai.tool.call.result` | 工具输出（仅内容捕获） |

### 6.5 `execute_hook` span

每次 hook 执行一个 span，例如 `PreToolUse`、`Stop`。

| 属性 | 说明 |
|---|---|
| `gen_ai.operation.name` | 始终 `execute_hook` |
| `github.copilot.hook.decision` | `pass`、`block`、`non_blocking_error` |
| `github.copilot.hook.duration` | Hook 执行时长（秒） |
| `github.copilot.hook.tool_names` | Hook 作用工具，JSON 数组 |
| `copilot_chat.hook_type` | Hook 事件，如 `PreToolUse` |
| `copilot_chat.hook_result_kind` | `success`、`error`、`non_blocking_error` |
| `copilot_chat.hook_input` | Hook 输入负载（截断） |
| `copilot_chat.hook_output` | Hook stdout（成功时，截断） |
| `error.type` | 失败时错误类 |

> 注意：`hook_input` / `hook_output` 未标注“仅内容捕获”，但为截断的输入/输出，合规上应按潜在内容处理，并实测后端是否收到。

---

## 7. Metrics

### GenAI 语义约定指标

| 指标 | 类型 | 说明 |
|---|---|---|
| `gen_ai.client.operation.duration` | Histogram | LLM API 调用时长（秒） |
| `gen_ai.client.token.usage` | Histogram | Token 计数（输入和输出） |

### 扩展特定指标

| 指标 | 类型 | 说明 |
|---|---|---|
| `copilot_chat.tool.call.count` | Counter | 按工具名和成功状态统计工具调用 |
| `copilot_chat.tool.call.duration` | Histogram | 工具执行延迟（毫秒） |
| `copilot_chat.agent.invocation.duration` | Histogram | 代理端到端时长（秒） |
| `copilot_chat.agent.turn.count` | Histogram | 每次代理调用的 LLM 往返数 |
| `copilot_chat.session.count` | Counter | 已开始的聊天会话数 |
| `copilot_chat.time_to_first_token` | Histogram | 首个 SSE token 时间（秒） |

### 代理活动与结果指标

| 指标 | 类型 | 说明 |
|---|---|---|
| `copilot_chat.edit.acceptance.count` | Counter | 编辑接受/拒绝决策（内联聊天、聊天编辑、hunk 级） |
| `copilot_chat.chat_edit.outcome.count` | Counter | 文件级聊天编辑会话结果（accepted/rejected/saved） |
| `copilot_chat.lines_of_code.count` | Counter | 已接受代理编辑新增/删除代码行数 |
| `copilot_chat.edit.survival.four_gram` | Histogram | 4-gram 文本相似度存活分数（0-1） |
| `copilot_chat.edit.survival.no_revert` | Histogram | 无回退存活分数（0-1） |
| `copilot_chat.user.action.count` | Counter | 用户参与：copy、insert、apply、followup |
| `copilot_chat.user.feedback.count` | Counter | 点赞/点踩 |
| `copilot_chat.agent.edit_response.count` | Counter | 代理编辑响应按成功/错误 |
| `copilot_chat.agent.summarization.count` | Counter | 上下文摘要结果（applied/failed） |
| `copilot_chat.pull_request.count` | Counter | 通过 Copilot agent 创建的 PR |
| `copilot_chat.cloud.session.count` | Counter | 云/远程代理会话，按 partner agent |
| `copilot_chat.cloud.operation.count` | Counter | 云操作次数（远端 agent 侧工具/任务调用） |
| `copilot_chat.cloud.operation.duration` | Histogram | 云操作耗时 |
| `copilot_chat.cloud.error.count` | Counter | 云操作失败次数 |

指标包含筛选属性，例如 `gen_ai.request.model`、`gen_ai.provider.name`、`gen_ai.tool.name`、`copilot_chat.edit.source`、`error.type`。

#### 记录位置与触发条件（实测核对 `extensions/copilot`）

所有结果指标都由 `extensions/copilot/src/platform/otel/common/genAiMetrics.ts` 的 `GenAiMetrics` 发出，
调用点全部在 **扩展宿主**，由 VS Code 的 UI 事件触发；Agent Host 只转 trace，不产生这些指标。

| 指标 | 记录位置（`extensions/copilot/src/`） | 触发条件 |
|---|---|---|
| `edit.acceptance.count` | `extension/conversation/vscode-node/userActions.ts` | `chatEditingSessionAction`（accepted/rejected，source `chat_editing`）、`chatEditingHunkAction`（`chat_editing_hunk`）、内联聊天完成（`inline_chat`） |
| `chat_edit.outcome.count` | 同上 | 文件级 chat editing 动作，含 `saved`，带 `has_remaining_edits` |
| `lines_of_code.count` | 同上 | **仅** `chatEditingHunkAction` 且 outcome=`accepted` 时累加 `linesAdded`/`linesRemoved`；内联聊天接受不计 LOC |
| `user.action.count` | 同上 | 复制 / 插入 / apply / followup 的用户动作 |
| `user.feedback.count` | 同上 | 响应点赞（`positive`）/ 点踩（`negative`） |
| `edit.survival.four_gram` / `no_revert` | 同上、`extension/prompts/node/codeMapper/codeMapperService.ts` | 接受后延迟测量（`time_delay_ms`），source 为 `inline_chat` / `code_mapper` |
| `agent.edit_response.count` | `extension/intents/node/editCodeIntent.ts` | 每次 edit-code 代理响应，`success` / `error` |
| `agent.summarization.count` | `extension/intents/node/agentIntent.ts` | 摘要应用/失败；另有 `skipped`（同日重复摘要被跳过） |
| `pull_request.count` | `extension/chatSessions/copilotcli/node/copilotcliSession.ts` | CLI 代理创建 PR |
| `cloud.session.count` | `extension/chatSessions/vscode-node/copilotCloudSessionsProvider.ts` | 云/远程会话启动，属性 `partner_agent` |
| `cloud.operation.count` / `.duration` / `.error.count` | `extension/chatSessions/vscode-node/cloudBackendTelemetry.ts` | 云任务操作（createSession / followUp / sessionActivated 等） |

导出链路：`GenAiMetrics` → `NodeOTelService.incrementCounter/recordMetric`
→ `PeriodicExportingMetricReader`（**10 秒**）→ OTLP/HTTP `<otlpEndpoint>/v1/metrics`（本项目的 endpoint
是 `/otlp/copilot-chat`，因此实际路径为 `/otlp/copilot-chat/v1/metrics`）。
`IdentityMetricExporter` 只过滤身份属性，**不丢弃指标**；因此“卡片为空”只能是没触发 UI 事件、
OTel 未启用，或指标不在本项目的 allowlist 内。

---

## 8. Events

| 事件 | 说明 |
|---|---|
| `gen_ai.client.inference.operation.details` | 完整 LLM 调用元数据：模型、token、finish reason |
| `copilot_chat.session.start` | 新聊天会话开始 |
| `copilot_chat.tool.call` | 每次工具调用及耗时、错误详情 |
| `copilot_chat.agent.turn` | 每轮 LLM 往返及 token 计数 |
| `copilot_chat.edit.feedback` | 用户接受/拒绝文件级代理编辑 |
| `copilot_chat.edit.hunk.action` | 用户接受/拒绝单个 hunk |
| `copilot_chat.inline.done` | 内联聊天编辑被接受/拒绝 |
| `copilot_chat.edit.survival` | 定期测量 AI 生成代码接受后的存活情况 |
| `copilot_chat.user.feedback` | 用户对聊天响应点赞/点踩 |
| `copilot_chat.cloud.session.invoke` | 云/远程代理会话启动 |

---

## 9. 资源属性与身份

所有信号携带：

| 属性 | 值 |
|---|---|
| `service.name` | `copilot-chat`（可由 `OTEL_SERVICE_NAME` 配置） |
| `service.version` | 扩展版本 |
| `session.id` | 每个 VS Code 窗口唯一 |

自定义资源属性：

```bash
export OTEL_RESOURCE_ATTRIBUTES="team.id=platform,department=engineering"
```

### 身份捕获

仅适用于 **Local harness 会话**。启用后遥测包含：

- `user.name`：代理调用 span，包括子代理和内联聊天 span
- `process.user.name`、`host.name`：资源属性

启用方式：`github.copilot.chat.otel.captureIdentity` 或 `COPILOT_OTEL_CAPTURE_IDENTITY=true`。  
**独立于内容捕获**。身份属性可识别开发者和机器，仅在组织有合适用途、访问控制和保留策略时启用。Agent Host 进程遥测不包含这些身份属性。

---

## 10. 内容捕获与 `captureContent: false`

默认不捕获任何提示内容、响应、工具参数。仅包含模型名、token 数、时长等元数据。

开启 `captureContent` 后会填充：

- 完整提示消息
- 响应消息
- 系统提示
- 工具 schema
- 工具参数
- 工具结果

> 内容捕获可能包含代码、文件内容、用户提示等敏感信息，仅在可信环境启用。

### 10.1 `captureContent: false` 时可分析维度

| 维度 | 可用数据/字段 | 能回答的问题 |
|---|---|---|
| 执行流程与追踪拓扑 | `invoke_agent`、`chat`、`execute_tool`、`execute_hook` span 及父子关系；子代理 trace context | 一次交互经过哪些 LLM、工具、hook、子代理；调用链是否连通 |
| 性能与延迟 | span 时长；`copilot_chat.time_to_first_token`；`copilot_chat.tool.call.duration`；`copilot_chat.agent.invocation.duration`；hook duration | 端到端耗时、TTFT、工具延迟、hook 耗时、瓶颈 |
| 模型与 token/成本 | provider、request/response model、input/output tokens、cache read/creation tokens、reasoning tokens、max tokens、temperature、top_p、finish reasons | 模型使用分布、token 趋势、缓存 token、推理 token、停止原因、参数配置 |
| 工具与 MCP 使用 | `gen_ai.tool.name`、`tool.type`、`tool.call.id`、`edit_type`、`skill_name`、`mcp_server_name_hash`、`mcp_tool_name`、`error.type`、工具调用计数/延迟 | 哪些工具常用、成功率/错误率、编辑类型、MCP 工具使用、工具延迟 |
| 代理/会话类型 | `gen_ai.agent.name`、`github.copilot.agent.type`、`conversation.id`、`turn_count`、`session.count`、`service.name` | 前台/CLI/Claude/子代理区分；会话轮次；不同 harness 对比 |
| 代码编辑与用户结果 | 编辑接受/拒绝、文件/hunk 结果、代码行数、存活分数、copy/insert/apply/followup、点赞/点踩、PR 数、云会话数、摘要结果、编辑响应成功/错误 | AI 编辑接受率、代码存活率、用户参与、PR/云产出、摘要失败 |
| Hook 执行 | hook decision、duration、tool_names、hook_type、result_kind、error.type | Hook 是否阻断、耗时、作用工具、成功/错误 |
| Git/组织上下文 | git repository、branch、commit_sha、github.org | 按仓库、分支、提交、组织分析 |
| 错误与可靠性 | 各 span `error.type`；工具成功/失败；编辑响应 success/error；摘要失败 | 错误类型分布、失败集中点 |
| 资源与组织维度 | `service.name`、`service.version`、`session.id`；自定义资源属性 | 按服务、版本、团队、部门、环境筛选聚合 |
| 身份维度（需单独启用） | `captureIdentity`：`user.name`、`process.user.name`、`host.name` | 归因到开发者/机器；独立于 `captureContent` |
| 事件维度 | 第 8 节事件 | 审计、时间线、反馈和结果分析 |

### 10.2 `captureContent: false` 时不可用

- 具体 prompt/response 文本、系统提示、完整消息
- 工具 schema、工具参数、工具结果
- `gen_ai.tool.call.arguments`、`gen_ai.tool.call.result`
- `github.copilot.tool.parameters.command`、`file_path`、`mcp_server_name`
- 基于代码/文件内容的语义分析、敏感信息检查、提示注入检测

---

## 11. 工具调用统计

**可以统计，且不需要开启 `captureContent`。**

### 可用信号

| 维度 | 字段/信号 |
|---|---|
| 调用次数 | `copilot_chat.tool.call.count`；按 `gen_ai.tool.name`、成功/失败聚合 |
| 延迟 | `copilot_chat.tool.call.duration`；`execute_tool` span 时长 |
| 工具名 | `gen_ai.tool.name` |
| 工具类型 | `gen_ai.tool.type`：`function` / `extension`（MCP） |
| 调用标识 | `gen_ai.tool.call.id` |
| 错误 | `error.type`；成功/失败维度 |
| 编辑工具细分 | `github.copilot.tool.parameters.edit_type` |
| 技能调用 | `github.copilot.tool.parameters.skill_name` |
| MCP 工具 | `mcp_server_name_hash`、`mcp_tool_name`（原始 server 名需内容捕获） |
| 调用拓扑 | `execute_tool` span 在 trace 中的父子关系 |
| 事件明细 | `copilot_chat.tool.call` |

### 不可用（除非内容捕获）

- `gen_ai.tool.call.arguments`
- `gen_ai.tool.call.result`
- `command`、`file_path`、`mcp_server_name`

**典型查询思路**：按 `gen_ai.tool.name` 聚合调用量；用 duration 看 P50/P95；用 `error.type` 看失败；用 `tool.type` 区分普通工具和 MCP；用 trace 结构看工具顺序和子代理关系。

---

## 12. 缓存命中统计

### 12.1 可用字段

`invoke_agent` 和 `chat` span 均包含：

- `gen_ai.usage.cache_read.input_tokens`
- `gen_ai.usage.cache_creation.input_tokens`

这些是元数据，**独立于 `captureContent`**。此外 `gen_ai.client.token.usage` histogram 记录输入/输出 token；缓存 token 建议以 span 属性为准。

### 12.2 计算方法

可按模型/会话聚合：

- 缓存读取 token：`sum(gen_ai.usage.cache_read.input_tokens)`
- 缓存创建 token：`sum(gen_ai.usage.cache_creation.input_tokens)`
- 输入 token：`sum(gen_ai.usage.input_tokens)`

可计算缓存读取占比，例如 `cache_read / input_tokens`。**分母语义需按提供商实际定义确认**：`input_tokens` 是否已包含缓存读取部分，不同后端可能不同。

### 12.3 显示为 0 的排查

`0` 是合法值，不一定代表“不支持”。排查方向：

1. **缓存确实未命中**：会话刚开始、上下文短、前缀未稳定时常见。
2. **提供商/模型未报告缓存指标**：不同模型和端点支持程度不同。
3. **遥测链路未拿到缓存字段**：提供商未上报或解析缺失时可能为 `0` 或缺失。
4. **版本/配置问题**：确认 OTel 已启用、exporter 正确、版本较新。
5. **BYOK 模式补充排查**：此前讨论提到 Copilot CLI 在 BYOK 模式下曾出现提示缓存完全禁用、`cache_read_input_tokens` 持续为 `0`，订阅模式正常。此为补充排查项，非本文档原文。

**交叉验证**：使用 **Cache Explorer** 直接查看提示缓存命中率。若 Cache Explorer 也显示 0%，更可能是缓存未命中或提供商未报告；若 Cache Explorer 非零而 OTel 为 0，则问题在遥测链路。

---

## 13. Copilot Chat 管道下的 harness 视图

在 `copilot-chat` 服务内，可通过 `gen_ai.agent.name` 区分代理类型：

| `gen_ai.agent.name` | 代理类型 |
|---|---|
| `GitHub Copilot Chat` | 前台代理（agent mode） |
| `copilotcli` | Copilot 包装器 span |
| `claude` | Claude 代理 |

按 `service.name` 筛选：

| `service.name` | 来源 |
|---|---|
| `copilot-chat` | 前台代理、CLI 包装器、Claude agent span（扩展发出） |
| `github-copilot` | 共享 Copilot runtime span 和 CLI 终端会话 |
| `claude-code` | Claude Code 子进程 SDK 遥测（当转发 `CLAUDE_CODE_ENABLE_TELEMETRY`） |

补充：
- Copilot harness 的扩展包装 span（`invoke_agent copilotcli`，服务 `copilot-chat`）是 runtime 原生 span（服务 `github-copilot`）的父项，二者出现在同一 trace。
- 终端 Copilot CLI 会话在独立进程中运行，trace 作为 `github-copilot` 下独立根 trace，不链接扩展 trace；CLI runtime 仅支持 `otlp-http`。
- Claude agent 产生 `copilot-chat` 下扩展级 span，`invoke_agent claude` 根 span 嵌套 `chat`、`execute_tool`、`execute_hook`；工具为 `Agent` 时子 span 嵌套其下。

---

## 14. 与 Agent Host 管道的区别（简表）

| 维度 | `github.copilot.chat.otel.*` | `chat.agentHost.otel.*` |
|---|---|---|
| 执行表面 | Local Copilot Chat：前台聊天、内联聊天、本地 CLI 包装器等 | Agent Host 会话：独立工具进程运行的 Copilot/Claude/Codex 代理会话 |
| 进程模型 | VS Code 扩展宿主内 | 独立工具进程，扩展宿主之外 |
| 配置前缀 | `github.copilot.chat.otel.*` | `chat.agentHost.otel.*` |
| 持久化 | `<extensionGlobalStorage>/otel/spans.db` | `<userData>/agent-host/otel/agent-host-traces.db` |
| 覆盖代理 | 主要是 Local Copilot Chat 前台代理 | Agent Host 会话中的 Copilot、Claude、Codex 等 |

**是否需要同时统计**：不是重复统计同一批数据，而是按执行表面互补。只使用 Local Chat 则只配前者；使用 Agent Host 会话则需后者；两者都用则两个都配，但后端按 `service.name` 或表面标签分开聚合。企业托管设置可同时作用于两个表面。

**是否会消失**：短期预计不会。Agent Host 是当前代理会话主要执行架构，但 Local Copilot Chat 扩展管道未被宣布弃用；被标记为遗留/回退的是扩展宿主中的旧版 Copilot CLI 源，而非 `github.copilot.chat.otel` 管道本身。

---

## 15. 安全与隐私

| 方面 | 详情 |
|---|---|
| 默认关闭 | 除非你或组织启用监控，否则不导出 OTel 数据 |
| 默认无内容 | 提示、响应、工具参数需 `captureContent` 选择加入 |
| 默认属性无 PII | 会话 ID、模型名、token 计数不可识别个人身份 |
| 已配置目标 | 数据发往你或组织配置的收集器端点或文件 |
| 身份捕获 | 可识别开发者和机器，需组织有合适用途、访问控制和保留策略 |
| 内容捕获 | 可能包含代码、文件内容、用户提示，仅在可信环境启用 |

企业托管遥测值会覆盖用户设置；托管身份捕获覆盖 `COPILOT_OTEL_CAPTURE_IDENTITY`；托管资源属性覆盖 `OTEL_RESOURCE_ATTRIBUTES`。应移除冲突的 OTel 环境变量以确保企业配置生效。

---

## 16. 常见后端与导出器示例

### Aspire Dashboard

```bash
aspire dashboard run
```

或：

```bash
docker run --rm -d -p 18888:18888 -p 4318:18890 --name aspire-dashboard \
  mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

VS Code：

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.captureContent": true
}
```

打开 `http://localhost:18888` → Traces。

### Jaeger

```bash
docker run -d --name jaeger -p 16686:16686 -p 4318:4318 jaegertracing/jaeger:latest
```

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.otlpEndpoint": "http://localhost:4318"
}
```

打开 `http://localhost:16686`，选择服务 `copilot-chat`。

### Langfuse

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.otlpEndpoint": "http://localhost:3000/api/public/otel",
  "github.copilot.chat.otel.captureContent": true
}
```

认证头用 `OTEL_EXPORTER_OTLP_HEADERS`。

### OTLP/gRPC

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "otlp-grpc",
  "github.copilot.chat.otel.otlpEndpoint": "http://localhost:4317"
}
```

### Console

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "console"
}
```

### File

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "file",
  "github.copilot.chat.otel.outfile": "/tmp/copilot-otel.jsonl"
}
```

> 组织托管的 Agent Host 文件导出使用 `telemetry.filePath`，与 Copilot Chat 扩展的 `outfile` 是不同目标。

---

## 17. 快速配置模板

### 仅元数据、不捕获内容（推荐默认）

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "otlp-http",
  "github.copilot.chat.otel.otlpEndpoint": "http://localhost:4318",
  "github.copilot.chat.otel.captureContent": false,
  "github.copilot.chat.otel.captureIdentity": false
}
```

### 需要调试内容时临时开启

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.captureContent": true,
  "github.copilot.chat.otel.maxAttributeSizeChars": 4096
}
```

### 本地 SQLite + 导出命令

```json
{
  "github.copilot.chat.otel.dbSpanExporter.enabled": true
}
```

然后执行 **Chat: Export Agent Traces DB**。

---

## 18. 排查清单

| 现象 | 检查 |
|---|---|
| 完全没有数据 | OTel 是否启用；endpoint/exporter 是否正确；后端是否接收 OTLP；协议是否匹配 |
| 有 trace 但无工具调用 | 是否使用了工具；`execute_tool` span 是否被采样；指标后端是否启用 |
| 工具调用有但无参数/结果 | `captureContent` 是否为 `false`；这是预期行为 |
| 缓存字段为 0 | 是否多轮稳定前缀；模型/端点是否报告缓存；Cache Explorer 交叉验证；版本；BYOK 补充排查 |
| 内容被截断 | 设置 `maxAttributeSizeChars` 或 `COPILOT_OTEL_MAX_ATTRIBUTE_SIZE_CHARS` |
| 身份字段缺失 | `captureIdentity` 是否启用；是否 Local harness；Agent Host 不包含 |
| CLI 终端 trace 不关联 | 终端 CLI 独立进程，trace 为独立根 trace，属预期 |
| gRPC 不生效 | CLI runtime 仅支持 `otlp-http`；确认后端协议 |

---

## 19. 相关链接

- [AI settings reference](/docs/agents/reference/ai-settings.md)
- [Manage AI settings in enterprise environments](/docs/enterprise/manage-ai-settings.md)
- [Troubleshoot AI in VS Code](/docs/agents/agent-troubleshooting/troubleshooting.md)
- [Diagnose prompt caching with the Cache Explorer](/docs/agents/agent-troubleshooting/cache-explorer.md)
- [OTel GenAI Semantic Conventions](https://github.com/open-telemetry/semantic-conventions/blob/main/docs/gen-ai/)
- [Aspire Dashboard standalone docs](https://aspire.dev/dashboard/standalone/)

---