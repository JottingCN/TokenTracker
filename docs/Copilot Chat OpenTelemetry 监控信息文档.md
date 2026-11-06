# Copilot Chat OpenTelemetry 监控信息文档

> 范围：本文档聚焦 **Copilot Chat 本地扩展管道**，即 `github.copilot.chat.otel.*` / `COPILOT_OTEL_*` / `OTEL_*`，服务名 `copilot-chat`。Agent Host 管道 `chat.agentHost.otel.*` 仅在对比处简要提及，不展开。  
> 依据：你提供的 VS Code 文档及前述讨论整理；标注“补充排查”的内容来自讨论，非文档原文。
> **实测校准（2026-11-06）**：以下结论已用本机 `agent-traces.db`（3285 span / 60 个属性键）与 `agent-host-traces.db`（12588 span / 229 个键）对照核对。标注 **“实测：”** 的句子来自落库数据，标注 **“官方文档：”** 的来自 VS Code 文档原文；两者冲突时以实测为准，并在第 20 节给出完整清单。

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

> **实测**：落库文件是 `<extensionGlobalStorage>/agent-traces.db`（Windows：`%APPDATA%\Code\User\globalStorage\github.copilot-chat\agent-traces.db`），
> **不在** `otel/` 子目录下，**也不叫** `spans.db`。官方文档与本项目旧文档写的 `<extensionGlobalStorage>/otel/spans.db` 是错的。
> 文件名与 `service.name` 大小写无关（目录名实测为 `github.copilot-chat`）。

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

> **实测：扩展管线的落库拓扑几乎不是树。** 本机 3285 个 span 里 **3275 个 `parent_span_id IS NULL`，且各自独占一个 `trace_id`**（distinct trace = 3275），
> 只有 10 个 span 有父级——全部来自同一条 `runSubagent` → `invoke_agent Explore` 的子代理链路。
> 也就是说上面那棵树只在 **Copilot CLI in-process（bridge）/ Claude / 子代理** 路径成立；前台 `chatMLFetcher` 的每次 LLM 调用都作为独立根 trace 落库，
> **不会**挂在 `invoke_agent` 下面。做拓扑或“按 trace 聚合一次交互”时不能假设父子关系存在，必须回落到 `conversation.id` + 时间窗口。
> 对比：Agent Host 管线的 `chat` span 全部有父级（2175 个 span 只分布在 15 个 trace），树很深。

### 6.1.1 本地 SQLite 结构（`dbSpanExporter`）

实测结构，供导入/离线查询直接使用：

```text
spans(span_id PK, trace_id, parent_span_id, name, start_time_ms, end_time_ms,
      status_code, status_message, operation_name, provider_name, agent_name,
      conversation_id, request_model, response_model, input_tokens, output_tokens,
      cached_tokens, reasoning_tokens, tool_name, tool_call_id, tool_type,
      chat_session_id, turn_index, ttft_ms)
span_attributes(span_id, key, value)          -- PK (span_id, key)
span_events(id, span_id, name, timestamp_ms, attributes)   -- attributes 是 JSON
schema_version(version)                       -- 实测 = 1
sessions VIEW                                 -- 见下
```

实测要点：

- **主键是 `span_id` 单列**，不是 `(trace_id, span_id)`；`trace_id` 只有索引。
- **没有 resource 属性表**：`service.name` / `service.version` / `service.namespace` / `session.id`（窗口级）都取不到，
  因此导入时无法从库内判定来源，只能靠**文件名/路径**归因；`spans` 表里也查不到 `service.namespace` 这一 attribute key。
- `status_code`：`0`=UNSET（绝大多数）、`1`=OK、`2`=ERROR；`status_message` 带原始文本（实测 `The operation was aborted` / `This operation was aborted`）。
- `turn_index` 实测**全为 NULL**（3285/3285），不要依赖它。
- `ttft_ms` 是 `REAL`，与属性 `copilot_chat.time_to_first_token` 同值（毫秒）。
- 内置 view `sessions` 的口径有坑：`GROUP BY COALESCE(conversation_id, chat_session_id)`，把子代理工具 span 的 `call_...` id 也当成一个独立会话；
  模型取 `response_model`；时长用 `MAX(end) - MIN(start)`；**不含** reasoning/cache-creation；实测只聚出 9 个“会话”。
- 体积：`captureContent` 开启时内容属性会占绝对多数（实测 `gen_ai.input.messages` 单键 3.04 GB，占全库 3.63 GB 的 83%），
  且关闭捕获后旧行不会自动收缩，`PRAGMA freelist_count` 实测为 0（不回收）。读取请走只读连接并预期 GB 级扫描。

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
| `gen_ai.usage.cache_creation.input_tokens` | 缓存创建输入 token（若可用）；**实测：扩展管线 0 次出现，当前 runtime 不发** |
| `github.copilot.agent.type` | `builtin`、`custom`、`plugin` |
| `github.copilot.git.repository` | Git 仓库远程 URL；**实测：本库 0 次** |
| `github.copilot.git.branch` | 活动分支 |
| `github.copilot.git.commit_sha` | 当前提交 |
| `github.copilot.github.org` | GitHub org owner，仅 GitHub remote；**实测：0 次** |
| `copilot_chat.repo.remote_url` | 旧版，优先 `github.copilot.git.repository`；**实测：0 次** |
| `copilot_chat.repo.head_branch_name` | 旧版，优先 `github.copilot.git.branch` |
| `copilot_chat.repo.head_commit_hash` | 旧版，优先 `github.copilot.git.commit_sha` |
| `copilot_chat.turn_count` | 会话内 LLM 往返次数；实测只在 `invoke_agent` 上出现 |
| `copilot_chat.debug_log_label` | **实测补充**：子代理调用标签，如 `runSubagent-Explore`、`title`；拓扑/归因时比 `agent_name` 更直白 |
| `user.name` | **实测补充**：需 `captureIdentity`，且只出现在 `invoke_agent`（Local harness），不依赖 `captureContent` |
| `error.type` | 失败时错误类 |
| `gen_ai.input.messages` | 完整提示消息（仅内容捕获） |
| `gen_ai.output.messages` | 完整响应消息（仅内容捕获） |
| `gen_ai.tool.definitions` | 工具 schema（仅内容捕获） |

### 6.3 `chat` span

每次 LLM API 调用一个 span。

| 属性 | 说明 |
|---|---|
| `gen_ai.operation.name` | 始终 `chat` |
| `gen_ai.provider.name` | 提供程序名。**实测：前台与辅助调用一律是 `github`，即使 `server.address` 是 `api.deepseek.com`/`api.xiaomimimo.com`（BYOK）。`provider.name` 表示路由/协议，不代表厂商，不能用于定价或供应商图标归因** |
| `gen_ai.request.model` | 请求模型 |
| `gen_ai.response.model` | 解析模型；实测有 4 条 `chat` 为 NULL（请求失败/中断），归因必须回落 request |
| `gen_ai.response.finish_reasons` | 停止原因；实测取值只有 `["stop"]`（3243）与 `["error"]`（1），是 JSON 数组字符串 |
| `gen_ai.agent.name` | **实测：`chat` span 上是子系统标签而非代理名**，取值 `copilotLanguageModelWrapper`（前台主调用，2153）、`copilotLanguageModelWrapper/thinkingTitle`（1084）、`copilotLanguageModelWrapper/toolRiskAssessment`（7）、`progressMessages`（2）、`title`（1）。**这是判别辅助调用最直接的信号** |
| `copilot_chat.parent_chat_session_id` | 子代理/派生调用的父级会话 ID；子代理工具 span 的 `session_id`/`conversation.id` 可能是父工具 call id，应优先用该字段归入一级会话（实测 7 次） |
| `copilot_chat.session_id` / `gen_ai.conversation.id` | 实测各 2160 次，两者同值；余下 1094 条 `chat`（全是辅助调用）**两个都缺**，落库列 `conversation_id`/`chat_session_id` 同时为 NULL |
| `gen_ai.request.max_tokens` | 最大输出 token，实测 3247 |
| `gen_ai.request.temperature` / `top_p` | 实测恒在（3247） |
| `gen_ai.request.stream` | **实测补充**：布尔，观测到的 3243 条全为 `true` |
| `gen_ai.usage.input_tokens` | 本次调用输入 token |
| `gen_ai.usage.output_tokens` | 本次调用输出 token |
| `gen_ai.usage.cache_read.input_tokens` | 缓存读取输入 token，实测 3245 |
| `gen_ai.usage.cache_creation.input_tokens` | **实测：扩展管线一次都没出现（0/3245）**，不要按“若可用”当作常态；缓存写入价逻辑在本管线上不会被触发 |
| `gen_ai.usage.reasoning.output_tokens` | 推理 token；与旧键 `gen_ai.usage.reasoning_tokens` **同时发出**（实测 1097 / 1096），确认无限期双发 |
| `copilot_chat.time_to_first_token` | 首个 SSE token 时间（**毫秒**） |
| `gen_ai.response.time_to_first_chunk` | **实测补充**：同时存在，单位是**秒**（实测 `496` ms 对应 `0.498`），多数略大于 TTFT，个别明显更大；换算时必须 ×1000 |
| `gen_ai.response.id` | **实测补充**：响应 ID（3243），可用于与后端日志对账 |
| `copilot_chat.server_request_id` | **实测补充**：服务端请求 ID，实测只在 Copilot 托管模型（`gpt-4o-mini`，1093）上出现，BYOK 调用没有 |
| `copilot_chat.copilot_usage_nano_aiu` | 实际用量 nano-AIU；**实测 1093 次全为 `0`**，且只在 `gpt-4o-mini-*` 上出现，因此“实际 AIC”在本管线常态显示 0 |
| `copilot_chat.request.max_prompt_tokens` | **实测补充**：本次调用的上下文预算（观测值 `744000`、`872000`、`12285`）。实测 3247 条**无一超限**，`input_tokens / 预算` 最大 0.759 → 可直接算上下文占用率 |
| `copilot_chat.request.shape` | **实测补充**：JSON `{"api":"messages","messageCount":N}`，请求 API 形态与消息条数（实测 2~44） |
| `copilot_chat.request.options` | **实测补充**：JSON 请求选项，含 `reasoning_effort`（观测 `"max"`）、`stream`、`stream_options.include_usage` |
| `server.address` | API 主机名；实测 2153 次（`api.deepseek.com` 1919、`api.xiaomimimo.com` 234），**辅助调用的 1094 条不带该属性** |
| `error.type` | 失败时错误类；实测取值 `DOMException`(7)、`AbortError`(2)，是异常类名而非业务分类 |
| `gen_ai.input.messages` / `gen_ai.output.messages` / `gen_ai.system_instructions` / `gen_ai.tool.definitions` / `copilot_chat.user_request` / `copilot_chat.reasoning_content` | 仅内容捕获；见第 10 节 |

> **实测：`gen_ai.usage.input_tokens` 的口径已确认。** 本机 3247 条 `chat` 合计 input 631 891 312、cache_read 625 767 744，
> cached 恒 ≤ input（总体命中 99.03%），说明本管线（OpenAI 兼容 `messages` API）的 `input_tokens` **已包含**缓存读取部分，
> 命中率分母直接用它，**不要**再加一次 cache_read。仅 Anthropic/Claude 类 provider 是“不含缓存”的口径。

### 6.4 `execute_tool` span

每次工具调用一个 span。

| 属性 | 说明 |
|---|---|
| `gen_ai.operation.name` | 始终 `execute_tool` |
| `gen_ai.tool.name` | 工具名，如 `readFile`。**实测扩展宿主注册的“外部工具”名被发成 `SHA-256(工具名)`**（Agent Host 库里 167 条 `8e6ae5f8…` 就是这种），需要用公开内置工具清单现算哈希反查；本扩展库样本里出现的是明文内置名（`runSubagent`、`read_file`、`list_dir`、`file_search`、`manage_todo_list`，均下划线命名，**不是文档示例里的 `readFile`**） |
| `gen_ai.tool.type` | `function` 或 `extension`（MCP 工具）；实测本库 5 条全是 `function` |
| `gen_ai.tool.call.id` | 工具调用标识。**实测坑：存在空字符串 `''`（如 `manage_todo_list`），按 call id 去重必须把空串当缺失，否则不同调用会被折叠** |
| `gen_ai.conversation.id` / `copilot_chat.session_id` | 实测在子代理场景被写成**父级工具 call id**（`call_00_haIaowRucTY2nYf4fsq78158`），必须沿 `parent_span_id` 回溯归到根会话 |
| `copilot_chat.parent_chat_session_id` | 父会话真实 ID，实测存在 |
| `copilot_chat.debug_log_label` | **实测补充**：`runSubagent-Explore` 之类的标签，可直接标出子代理身份 |
| `github.copilot.tool.parameters.edit_type` | 编辑工具：`create`、`update`、`str_replace`、`insert`；**实测：两个本地库都没有这个键（0 次），文档写的 `github.copilot.tool.parameters.*` 一族在落库数据里均未观测** |
| `github.copilot.tool.parameters.skill_name` | 技能调用；**实测：0 次**。Agent Host 实际给的是 `github.copilot.context.skills`（**可用技能明文清单**，不是被调用的那一个） |
| `github.copilot.tool.parameters.mcp_server_name_hash` | MCP 工具：server 名 SHA-256；**实测：0 次**。Agent Host 实际键是 `github.copilot.context.mcp_server_names`（哈希 JSON 数组）与 `github.copilot.external_tool.name`（哈希） |
| `github.copilot.tool.parameters.mcp_tool_name` | MCP 工具：调用的工具名；**实测：0 次** |
| `github.copilot.tool.parameters.command` | shell 工具（仅内容捕获，截断）；**实测：0 次**。Agent Host 侧即便关闭内容捕获也会给 `process.executable.name`（`pwsh.exe`）与 `process.exit.code`（0/1/2），**命令退出码是免费的可靠性信号** |
| `github.copilot.tool.parameters.file_path` | 文件工具（仅内容捕获）；**实测：0 次** |
| `github.copilot.tool.parameters.mcp_server_name` | MCP 工具（仅内容捕获）；**实测：0 次** |
| `error.type` | 失败时错误类；实测 Agent Host 侧 `execute_tool` 取值 `ToolError`(55)、`ModelCallError`(39)、`query`(24)、`cancelled`(8)、`denied`(2)，**不都是类名** |
| `gen_ai.tool.call.arguments` | 工具输入参数（仅内容捕获） |
| `gen_ai.tool.call.result` | 工具输出（仅内容捕获） |
| `gen_ai.tool.description` | **实测补充**：工具描述文本，同样属内容门控（实测 5 次，avg 765 字符） |

> **实测补充：Agent Host 侧还存在成对的 `external_tool <sha256>` span**（167 条）与同名 `execute_tool <sha256>` span 一一对应，
> 导入/统计时按 `gen_ai.tool.call.id` 去重，不要计成两次。

### 6.5 Hook 执行信号

**官方文档描述的是 `execute_hook` span。实测：两个本地库都一条都没有（`operation_name='execute_hook'` 计 0），hook 信号实际以 `span_events` 形式挂在 `invoke_agent` span 上**：

| 事件名 | 实测次数 | 关键属性 |
|---|---|---|
| `github.copilot.hook.start` | 1988 | `github.copilot.hook.type`、`github.copilot.hook.invocation_id` |
| `github.copilot.hook.end` | 1976 | 同上 + `github.copilot.hook.duration`（**秒，浮点**，实测 0.0005381 量级） |

`github.copilot.hook.type` 实测取值：`postToolUse`(1034)、`preToolUse`(830)、`errorOccurred`(26)、`postToolUseFailure`(24)、`userPromptTransformed`(22)、`userPromptSubmitted`(22)、`sessionStart`(14)、`agentStop`(9)、`subagentStart`(7)。

注意 `start` 与 `end` 数量不等（1988 vs 1976），说明有 hook 未正常收尾，**统计耗时只能以 `end` 的 `hook.duration` 为准**，不要用两条事件的时间差强行配对。

官方文档列出的以下属性在实测中均未出现，保留仅作对照，不要据此写查询：

| 属性 | 官方说明 | 实测 |
|---|---|---|
| `gen_ai.operation.name = execute_hook` | 每次 hook 一个 span | 0 条 |
| `github.copilot.hook.decision` | `pass`、`block`、`non_blocking_error` | 未出现 |
| `github.copilot.hook.tool_names` | Hook 作用工具，JSON 数组 | 未出现 |
| `copilot_chat.hook_type` | Hook 事件 | 未出现（实际键名带 `github.copilot.` 前缀） |
| `copilot_chat.hook_result_kind` | `success`、`error`、`non_blocking_error` | 未出现 |
| `copilot_chat.hook_input` / `hook_output` | 截断的输入/输出 | 未出现 |

> 结论：若需要 Hook 次数/耗时/类型，查询入口是 `span_events`（离线库）或 OTLP 推送的 span event，
> **不是** `spans WHERE operation_name='execute_hook'`。本项目 `LocalTraceImporter` 除 `spans` 外还会读 `span_events` 的 `hook.end`
> （`hook.start` 不入库，避免次数翻倍），把它写成 `operation='execute_hook'` 的 `session_events` 行；
> 因为 `session_events` 主键是 `(trace_id, span_id)`，hook 行的 `span_id` 是 `{spanId}#hook#{invocation_id}` 合成值。

---

### 6.6 `embeddings` span（官方文档未列）

**实测**：`operation_name='embeddings'` 共 30 条，`provider_name='openai'`，模型 `metis-1024-I16-Binary`，span 名 `embeddings metis-1024-I16-Binary`。

| 字段 | 实测值 |
|---|---|
| `gen_ai.embeddings.input_count` | 恒 `1` |
| `gen_ai.request.model` | `metis-1024-I16-Binary` |
| `gen_ai.provider.name` | `openai` |
| `error.type` / `status_code` | 7 条 `status_code=2`，`error.type=DOMException`，`status_message=This operation was aborted` |
| token 列 | `input_tokens`/`output_tokens`/`cached_tokens` 全 NULL |

这类 span 会被“只统计 `operation_name='chat'`”的规则排除，但**它是真实的上游调用**（本地命令/设置嵌入索引）。
若要完整反映用量，可单独归入“辅助调用/嵌入”，不要混进 chat 命中率与费用统计。

### 6.7 `vscode.chat.user_perceived_time_to_first_progress` span（官方文档未列）

**实测**：`operation_name` 为 **NULL**，属性是一组 `vscode.chat.user_interaction.*`（`schemaVersion=1`），来自 VS Code 内核而非扩展：

| 属性 | 实测值/说明 |
|---|---|
| `vscode.chat.user_interaction.timeToFirstProgress` | **用户感知首响应毫秒**（实测 `2246.300000011921`，浮点），比 server TTFT 更贴近体验 |
| `vscode.chat.user_interaction.firstProgressKind` | `reasoning` / `text` / `tool`（Agent Host 侧实测 35/3/2） |
| `vscode.chat.user_interaction.requestPhase` | `first`（Agent Host 侧还有后续阶段） |
| `vscode.chat.user_interaction.result` | `success` |
| `vscode.chat.user_interaction.interactionOrdinal` | 会话内交互序号 |
| `vscode.chat.user_interaction.requestId` / `rendererId` | `request_<uuid>` / `<uuid>`，用于跨信号关联 |
| `vscode.chat.user_interaction.windowVisible` / `windowFocused` | 布尔，**可用于剔除后台窗口产生的“慢”样本** |
| `vscode.chat.user_interaction.timeToTermination` | 仅 Agent Host 侧观测到 |

同一套键在 Agent Host 库中出现在 **46 个 span** 上（40 个带 `timeToFirstProgress`，6 个带 `timeToTermination`；`firstProgressKind` 实测 `reasoning` 35 / `text` 3 / `tool` 2），
属于**用户感知延迟**的权威来源；它与 `copilot_chat.time_to_first_token`（服务端 TTFT）不是同一口径，不能互相替换。

## 7. Metrics

### GenAI 语义约定指标

| 指标 | 类型 | 说明 |
|---|---|---|
| `gen_ai.client.operation.duration` | Histogram | LLM API 调用时长（秒） |
| `gen_ai.client.token.usage` | Histogram | Token 计数（输入和输出） |

### 扩展特定指标

| 指标 | 类型 | 说明 |
|---|---|---|
| `copilot_chat.tool.call.count` | Counter | 按工具名和成功状态统计工具调用；**实测带 `gen_ai.tool.name` + `success`，本项目只用名字，`success` 未参与统计** |
| `copilot_chat.tool.call.duration` | Histogram | 工具执行延迟（毫秒） |
| `copilot_chat.agent.invocation.duration` | Histogram | 代理端到端时长（秒）；**实测：不在本项目 allowlist，不入库** |
| `copilot_chat.agent.turn.count` | Histogram | 每次代理调用的 LLM 往返数；**实测：不在 allowlist，不入库**（轮次请用 `copilot_chat.turn_count` span 属性） |
| `copilot_chat.session.count` | Counter | 已开始的聊天会话数；**实测：不在 allowlist，不入库** |
| `copilot_chat.time_to_first_token` | Histogram | 首个 SSE token 时间（秒）；**实测：不在 allowlist，不入库**（TTFT 请用 span 属性） |

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
OTel 未启用、指标不在本项目的 allowlist 内、**指标已入库但归属不到该会话（见 7.1）**，
或**数据是通过 `POST /api/import-local` 从离线 SQLite 导入的——离线库里根本没有 metrics**。

### 7.1 结果指标的实际携带属性与归属限制（实测核对 0.73.0 bundle）

`incrementCounter(name, value, attrs)` 只是 `counter.add(value, attrs)`，**不补任何公共维度**。
各指标 datapoint 携带的属性就是上游写死的那几个：

| 指标 | datapoint 属性（实测） | 本项目聚合读的键 |
|---|---|---|
| `edit.acceptance.count` | `copilot_chat.edit.source`、`copilot_chat.edit.outcome`、`copilot_chat.language_id`? | `copilot_chat.edit.outcome` ✅ 对齐 |
| `chat_edit.outcome.count` | 同上 + `copilot_chat.has_remaining_edits`? | `copilot_chat.edit.outcome` ✅ |
| `lines_of_code.count` | `type`、`copilot_chat.language_id`?（值 = 行数，只在 accepted hunk 上递增） | `type` ✅ |
| `user.action.count` / `user.feedback.count` | `action` / `rating` | ✅ |
| `agent.edit_response.count` / `agent.summarization.count` | `outcome`（后者另有 `skipped`，聚合忽略） | ✅ |
| `edit.survival.*`（histogram） | `copilot_chat.edit.source`、`copilot_chat.time_delay_ms` | 用 sum/count 求均值 ✅ |
| `tool.call.count` | `gen_ai.tool.name`、**`success`** | 只读 name，**`success` 未参与统计** → `toolMetricCalls` 把失败也算进去 |
| `cloud.session.count` / `cloud.operation.*` / `pull_request.count` | `partner_agent` 等 | ✅ |

**由此得到三条硬限制：**

1. **结果指标不带会话 ID，也不带模型/provider。** 第 6 节写的筛选属性（`gen_ai.request.model`、`gen_ai.provider.name`、`error.type`）
   只有 `gen_ai.client.*` 与 `tool.call.*` 系列才有，**outcome 指标一个都没有**。因此结果指标无法按模型/provider/错误维度拆分。
2. **每会话的结果卡片只剩一条归属路径。** AGENTS.md 的三级阶梯里，「自带 `gen_ai.conversation.id`/`copilot_chat.session_id`」和
   「按 `gen_ai.tool.call.id` 反查」对 outcome 指标**永远不会命中**；唯一可用的是 resource 上的**窗口级** `session.id`，
   且要求该窗口在 `window_session_links` 里**只对应一个** chat 会话（置信度 0.6）。
   同一 VS Code 窗口开出第二个会话后，这个窗口的编辑指标只会进 Dashboard，**永不进会话卡片**——这是数据源限制，不是 bug。
3. **上游会发但本项目 allowlist 未收录**（静默丢弃）：`copilot_chat.session.count`、`copilot_chat.agent.turn.count`、
   `copilot_chat.agent.invocation.duration`、`copilot_chat.time_to_first_token`，以及 `gen_ai.client.*` 系列
   （后两类与 span 重复，属有意排除；前四个见上面「扩展特定指标」表的标注）。

> 累计计数器每 10 秒重发同一个 series，本项目为控制行数只保留**每分钟一行**：区间内的新点会
> **覆盖**该行的 value/count/sum/min/max/end_time（不是丢弃）。早于已存点或时间戳重复的采样直接忽略。
> 若把新点丢弃，进程退出前最后 ≤60 秒的增量就永久丢失——低频的“接受编辑”尤其容易被吞掉。

---

## 8. Events

**必须区分两种“事件”**：官方文档的 Events 指 **OTLP Logs 信号**（只走 `/v1/logs` 推送，`dbSpanExporter` 不落库）；
本地 SQLite 里的 `span_events` 表是**挂在 span 上的事件记录**，两者名字和口径都不一样。

### 8.1 OTLP Logs 事件（官方文档，需后端接收，**实测不落本地库**）

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

### 8.2 本地库 `span_events` 实测事件

扩展库（21 条）与 Agent Host 库（5962 条）的实际事件名如下，**上面那张表里没有一个出现**：

| 管线 | 事件名 · 次数 | attributes 关键内容 |
|---|---|---|
| Copilot Chat | `exception` 9、`turn_start` 4、`turn_end` 4、`user_message` 2、`tools_available` 2 | `turn_start/turn_end`：`turnId`；`user_message`：`content`（**用户原文，内容门控**）；`tools_available`：`toolDefinitions`（**全量工具 schema，实测 90 KB，内容门控**）；`exception`：`exception.type/message/stacktrace` |
| Agent Host | `github.copilot.hook.start` 1988 / `hook.end` 1976、`sandbox.enforcement_state` 923、`session.usage_info` 874、`gen_ai.client.operation.exception` 124、`user.message` 45、`mcp.server.lifecycle` 21、`session.abort` 5、`system.notification` 3、`compaction_start` 2、`compaction_complete` 1 | 见 Agent Host 文档第 9 节；`session.usage_info` 带 `token_limit`/`current_tokens`/`messages_length` |

要点：

- `exception` 事件的 **`exception.stacktrace` 含本地绝对路径与 `extension.js` 行号**，属于潜在内容，即使 `captureContent: false` 也应确认后端是否收到。
- Agent Host 的 `gen_ai.client.operation.exception` 携带 `github.copilot.error_type`、`github.copilot.error_status_code`（实测 `400`）、`github.copilot.error_provider_call_id`、`github.copilot.error_service_request_id`——**这是免费的 HTTP 状态码与后端请求 ID 来源**，比 `error.type` 精细。
- 本项目 `session_events` 的 `execute_tool` / `invoke_agent` 统计来自在线 OTLP 推送；从本地库导入时**当前完全没读 `span_events` 表**，所以导入结果里不会有任何事件统计（见第 20 节）。

---

## 9. 资源属性与身份

所有信号携带：

| 属性 | 值 |
|---|---|
| `service.name` | `copilot-chat`（可由 `OTEL_SERVICE_NAME` 配置） |
| `service.version` | 扩展版本 |
| `session.id` | 每个 VS Code 窗口唯一 |

> **实测：以上资源属性只在 OTLP 推送里有；`dbSpanExporter` 的本地 SQLite 不保存任何 resource attributes。**
> 因此离线库/导入路径无法判定 `service.name`、无法用 `session.id` 做窗口级归属（`window_session_links` 只能来自在线 metrics），
> 也不能靠 `service.namespace` 区分管线——该键在 `span_attributes` 里根本不存在。管线归因只能依据**来源文件/接收入口**。

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

开启 `captureContent` 后会填充（**实测键名与体积**，本机开启时 `agent-traces.db` 达 3.63 GB，其中前两项占绝大部分）：

| 属性/事件键 | 内容 | 实测出现 · 平均/最大体积 · 合计 |
|---|---|---|
| `gen_ai.input.messages` | 完整提示消息 | 3249 · 960 KB · **3.04 GB（占全库 83%）** |
| `gen_ai.tool.definitions` | 工具 schema | 2145 · 72 KB · 152 MB |
| `gen_ai.system_instructions` | 系统提示 | 3247 · 19 KB · 120 MB |
| `copilot_chat.user_request` | **用户请求原文**（官方文档未列该键） | 3249 · 3.7 KB · 7.2 MB |
| `gen_ai.output.messages` | 响应消息，**含工具调用的完整 `arguments`** | 3245 · 344 B · 2.3 MB |
| `copilot_chat.reasoning_content` | **思考过程原文**（官方文档未列该键） | 1096 · 2 KB |
| `gen_ai.tool.call.result` / `.arguments` / `gen_ai.tool.description` | 工具输出/输入/描述 | 各 5 |
| `span_events`：`user_message.content` | 用户原文 | 2 |
| `span_events`：`tools_available.toolDefinitions` | 全量工具 schema | 2 · 90 MB 级 |

关闭 `captureContent` 后**上表 9 个属性键与 2 个事件字段全部消失**，实测影响是库体积从 GB 级降到 MB 级。

> **两个不受 `captureContent` 门控、但仍应按内容对待的实测例外：**
> 1. `span_events` 的 `exception.stacktrace` 带本地绝对路径（`c:\Users\<name>\AppData\...`）与 `extension.js` 行号。
> 2. `gen_ai.tool.call.id`、`copilot_chat.server_request_id`、`gen_ai.response.id` 是明文可关联标识，可跨信号串联同一请求。
>
> 本项目隐私约束仍成立：**解析层只写非内容 allowlist，不入库以上任何一项**；唯一例外是 `session_workspaces` 的会话工作区根目录（来源是本机 VS Code 元数据，不是遥测）。

### 10.1 `captureContent: false` 时可分析维度

| 维度 | 可用数据/字段 | 能回答的问题 | 实测备注 |
|---|---|---|---|
| 执行流程与追踪拓扑 | `invoke_agent`、`chat`、`execute_tool` span 及 `parent_span_id`；子代理 trace context | 一次交互经过哪些 LLM、工具、hook、子代理；调用链是否连通 | **扩展管线基本无树**（3275/3285 是独立根 trace），拓扑要靠会话 + 时间区间；hook 走 `span_events` |
| 性能与延迟 | span 时长；`copilot_chat.time_to_first_token`（ms）；`gen_ai.response.time_to_first_chunk`（s）；`vscode.chat.user_interaction.timeToFirstProgress`；`github.copilot.server_duration` | 端到端耗时、服务端 TTFT、**用户感知首响应**、网络/排队开销 | 服务端 TTFT 与感知 TTFT 口径不同，不能互换 |
| 模型与 token/成本 | provider、request/response model、input/output tokens、**cache read** token、reasoning token（新旧双键）、max_tokens、temperature、top_p、finish reasons、`request.options.reasoning_effort` | 模型分布、token 趋势、命中率、推理 token、推理档位、参数配置 | **`cache_creation.input_tokens` 实测不发**；`provider.name` 恒 `github`，厂商要看 `server.address` |
| 上下文压力 | `copilot_chat.request.max_prompt_tokens` + `input_tokens`；Agent Host 的 `session.usage_info` 事件（`token_limit`/`current_tokens`/`messages_length`）；`gen_ai.conversation.compacted` | 每次调用用了多少预算、是否接近上限、是否发生过压缩 | 实测最大占比 0.759，0 次超限；**当前看板完全没有这个维度** |
| 对话形态 | `copilot_chat.request.shape`（`api` + `messageCount`） | 消息条数与命中率/耗时的相关性 | 实测 messageCount 2~44 |
| 辅助调用归因 | `gen_ai.agent.name` 的子系统后缀（`/thinkingTitle`、`/toolRiskAssessment`、`progressMessages`、`title`） | 标题生成、思考标题、工具风险评估各花多少 | **比“没有 session_id 就算辅助”精确**：实测 1094 条无会话 span 全部由此归类 |
| 工具与 MCP 使用 | `gen_ai.tool.name`（外部工具为 SHA-256）、`tool.type`、`tool.call.id`、`error.type`、`process.executable.name`、`process.exit.code`、`context.skills`/`mcp_server_names`/`custom_agent_names` | 哪些工具常用、成功率、退出码、可用的技能与 MCP | **`tool.parameters.*` 一族实测均不存在**；`tool.call.id` 可能为空串 |
| 代理/会话类型 | `gen_ai.agent.name`、`github.copilot.agent.type`、`conversation.id`、`copilot_chat.turn_count`、`debug_log_label`、`github.copilot.initiator` | 前台/CLI/Claude/子代理区分；轮次；发起方 | `user.name` 需 `captureIdentity`（独立开关） |
| 代码编辑与用户结果 | 第 7 节 metrics（**仅 OTLP 推送**）；`vscode.chat.user_interaction.result` | 接受率、存活率、参与度、PR/云产出 | 本地库不含 metrics，必须在线接收 |
| Hook 执行 | `span_events`：`github.copilot.hook.type` + `hook.duration` + `invocation_id` | Hook 触发次数、类型分布、耗时 | **没有 `execute_hook` span，也没有 `hook.decision`/`tool_names`** |
| Git/组织上下文 | `github.copilot.git.branch`、`git.commit_sha`、`repo.head_branch_name`、`repo.head_commit_hash` | 按分支、提交分析 | **`git.repository` 与 `github.org` 实测不发** |
| 错误与可靠性 | 各 span `error.type`、`status_code`/`status_message`、`exception.*`、`gen_ai.client.operation.exception` 的 `error_status_code` | 错误分布、失败集中点、HTTP 状态码 | `error.type` 取值含 `DOMException`/`AbortError`/`ToolError`/`ModelCallError`/`query`/`cancelled`/`denied` |
| 资源与组织维度 | `service.name`/`service.version`/`session.id` | 按服务、版本、团队筛选 | **本地库取不到**，只在 OTLP 推送可用 |
| 身份维度（需单独启用） | `captureIdentity`：`user.name`；Agent Host 另有 `enduser.pseudo.id` | 归因到开发者/机器 | 实测 `user.name` 只在 `invoke_agent` |
| 事件维度 | 第 8.2 节 `span_events` 实测清单 | 时间线、异常、hook、上下文用量 | 官方第 8.1 节的 Logs 事件不落本地库 |

### 10.2 `captureContent: false` 时不可用

- 具体 prompt/response 文本、系统提示、完整消息、思考过程
- 工具 schema、工具参数、工具结果、工具描述
- 实测键名清单（9 个，全部消失）：`gen_ai.input.messages`、`gen_ai.output.messages`、`gen_ai.system_instructions`、
  `gen_ai.tool.definitions`、`gen_ai.tool.call.arguments`、`gen_ai.tool.call.result`、`gen_ai.tool.description`、
  `copilot_chat.user_request`、`copilot_chat.reasoning_content`
- 事件里的 `user_message.content` 与 `tools_available.toolDefinitions`；Agent Host 侧 `vscode.agent_host.session.title_changed` span 也不再发出
- `github.copilot.tool.parameters.command` / `file_path` / `mcp_server_name`（**这些键实测在开启时也不存在**，列在这里只为对齐官方文档）
- 基于代码/文件内容的语义分析、敏感信息检查、提示注入检测

> **仍然可用、但容易被误当成内容门控的：** `server.address`、`process.executable.name` + `process.exit.code`（命令退出码）、
> `github.copilot.cost`、`github.copilot.server_duration`、`gen_ai.conversation.compacted`、`gen_ai.request.reasoning.level`、
> `github.copilot.permission.kind/result`（审批结果）、`session.usage_info` 的 `token_limit`/`current_tokens`、
> `vscode.chat.user_interaction.*`、`github.copilot.context.skills` / `custom_agent_names`（明文清单）。
> **仍然可用的缓存指标只有 `cache_read`**：`cache_creation.input_tokens` 实测两条管线都不发（不是内容门控，是真的不发）。

---

## 11. 工具调用统计

**可以统计，且不需要开启 `captureContent`。**

### 可用信号

| 维度 | 字段/信号 | 实测 |
|---|---|---|
| 调用次数 | `copilot_chat.tool.call.count`；按 `gen_ai.tool.name`、成功/失败聚合 | metrics 只走 OTLP；本地库用 `spans WHERE operation_name='execute_tool'` |
| 延迟 | `copilot_chat.tool.call.duration`；`execute_tool` span 时长 | span 时长可用 |
| 工具名 | `gen_ai.tool.name` | 外部工具是 **SHA-256**，需内置清单现算反查 |
| 工具类型 | `gen_ai.tool.type`：`function` / `extension`（MCP） | 实测全为 `function` |
| 调用标识 | `gen_ai.tool.call.id` | **存在空串**，去重时要当缺失 |
| 错误 | `error.type`；成功/失败维度 | 另有 `process.exit.code`（Agent Host，明文退出码） |
| 编辑类型 | `github.copilot.tool.parameters.edit_type` | **实测不存在**；只能从 `gen_ai.tool.name`（`edit`/`create_file`/`str_replace`）推断 |
| 技能 | `github.copilot.tool.parameters.skill_name` | **实测不存在**；可用的是 `github.copilot.context.skills`（本次会话可用技能**清单**，明文） |
| MCP 工具 | `mcp_server_name_hash`、`mcp_tool_name` | **实测键名不存在**；实际是 `github.copilot.context.mcp_server_names`（**哈希数组**，非明文，与 `captureContent` 无关）与 `github.copilot.external_tool.name`（哈希） |
| 审批 | `permission` span：`github.copilot.permission.kind`/`result`/`tool_name` | **官方文档未列**；实测 1780 条，`read`/`shell`/`write`/`url`/`custom-tool`，`approved`/`denied-interactively-by-user` |
| 调用拓扑 | `execute_tool` span 在 trace 中的父子关系 | Agent Host 有树；扩展管线基本没有 |
| 事件明细 | `copilot_chat.tool.call` | 属 OTLP Logs，不落本地库；本地是 `github.copilot.hook.*` 等 |

### 不可用（除非内容捕获）

- `gen_ai.tool.call.arguments`
- `gen_ai.tool.call.result`
- `gen_ai.tool.description`
- `command`、`file_path`、`mcp_server_name`（**实测这些键在开启捕获时也不存在，见 6.4**）
- **仍然可用**：`process.executable.name` 与 `process.exit.code`（Agent Host 侧的命令退出码）

**典型查询思路（实测校正后）**：按 `gen_ai.tool.name` 聚合调用量（外部工具先过 `ToolNameResolver` 反查哈希）；用 `end-start` 看 P50/P95；
用 `error.type` + `status_code` 看失败；**不要用 `gen_ai.tool.type` 区分 MCP——实测全为 `function`**，MCP 只能靠 `context.mcp_server_names`（哈希数组）或哈希工具名判定；
**扩展管线不要用 trace 结构看工具顺序**（无父子关系），要按 `conversation.id` + `start_time_ms` 排序还原；Agent Host 侧才可以用树。

---

## 12. 缓存命中统计

### 12.1 可用字段

`invoke_agent` 和 `chat` span 上：

| 字段 | 独立于 captureContent | 实测 |
|---|---|---|
| `gen_ai.usage.cache_read.input_tokens` | 是 | **Copilot Chat 管线可用**（chat 3245 次、invoke_agent 也有）；**Agent Host 管线 0 次** |
| `gen_ai.usage.cache_creation.input_tokens` | 是 | **两条管线都 0 次**，当前 runtime 不发。缓存写入价分支不会被触发 |

这些是元数据，**独立于 `captureContent`**。此外 `gen_ai.client.token.usage` histogram 记录输入/输出 token；缓存 token 建议以 span 属性为准。

> **实测结论：缓存命中率目前只有 Copilot Chat（`github.copilot.chat.otel`）管线能算出来。**
> Agent Host 侧 `cached_tokens` 列全 NULL、cache 属性 0 次，所以 combined 视图里“只有 Agent Host 的会话”必然显示 `-` 或 0%，
> 这是数据源缺失而非未命中。若一定要一个 Agent Host 侧的缓存信号，只能用
> `github.copilot.session.timing.cacheReadCallCount`（实测出现过 `0`）和 `session.usage_info` 的 `current_tokens` 变化间接推断。

### 12.2 计算方法

可按模型/会话聚合：

```text
cache_read_tokens = sum(gen_ai.usage.cache_read.input_tokens)
input_tokens      = sum(gen_ai.usage.input_tokens)
命中率            = cache_read_tokens / input_tokens
```

**分母语义已实测确认（扩展管线，OpenAI 兼容 `messages` API）**：`gen_ai.usage.input_tokens` **已包含**缓存读取部分。
本机 3247 条 `chat`：input 合计 631 891 312、cache_read 合计 625 767 744，且逐条都满足 `cached ≤ input`（总体命中 99.03%），
因此**直接用 `input_tokens` 作分母，不要再加 cache_read**；只有 Anthropic/Claude 类 provider 才是“不含缓存”的排他口径（需 `input + cache_read + cache_creation`）。

缓存创建 token：`sum(gen_ai.usage.cache_creation.input_tokens)` —— 实测恒为 0/缺失，保留公式仅为兼容未来 runtime。

### 12.3 显示为 0 的排查

`0` 是合法值，不一定代表“不支持”。排查方向：

1. **缓存确实未命中**：会话刚开始、上下文短、前缀未稳定时常见。
2. **提供商/模型未报告缓存指标**：不同模型和端点支持程度不同。
3. **遥测链路未拿到缓存字段**：提供商未上报或解析缺失时可能为 `0` 或缺失。
4. **版本/配置问题**：确认 OTel 已启用、exporter 正确、版本较新。
5. **BYOK 模式补充排查**：此前讨论提到 Copilot CLI 在 BYOK 模式下曾出现提示缓存完全禁用、`cache_read_input_tokens` 持续为 `0`，订阅模式正常。此为补充排查项，非本文档原文。
   > **实测反驳（本机即 BYOK 场景）**：走 `api.deepseek.com` 的 `deepseek-flash` 命中率 **99.18%**、
   > 走 `api.xiaomimimo.com` 的 `mimo-v2.6-flash`/`mimo-v2.6-pro` 为 **97.86% / 97.19%**，
   > Copilot 托管的 `gpt-4o-mini-2024-07-18` 为 **76.39%**。**BYOK 并未禁用提示缓存**，该项至少在当前版本不成立。
6. **来源判定优先于以上所有排查**：**Agent Host 管线根本不报 cache 字段**（0 次），遇到“缓存为 0”先看数据来自哪条管线，再谈未命中。

**交叉验证**：使用 **Cache Explorer** 直接查看提示缓存命中率。若 Cache Explorer 也显示 0%，更可能是缓存未命中或提供商未报告；若 Cache Explorer 非零而 OTel 为 0，则问题在遥测链路。

---

## 13. Copilot Chat 管道下的 harness 视图

在 `copilot-chat` 服务内，可通过 `gen_ai.agent.name` 区分代理类型：

| `gen_ai.agent.name` | 代理类型 | 实测 |
|---|---|---|
| `GitHub Copilot Chat` | 前台代理（agent mode） | 1（`invoke_agent`） |
| `copilotcli` | Copilot 包装器 span | 本机未出现 |
| `claude` | Claude 代理 | 本机未出现 |
| `Explore` / 其他自定义名 | **子代理**：实测出现在 `invoke_agent {子代理名}`，配合 `copilot_chat.debug_log_label = runSubagent-Explore` | 1 |
| `copilotLanguageModelWrapper` | **不是代理**：前台主 LLM 调用（`chat` span） | 2153 |
| `copilotLanguageModelWrapper/thinkingTitle` | 思考标题生成（辅助调用） | 1084 |
| `copilotLanguageModelWrapper/toolRiskAssessment` | 工具风险评估（辅助调用） | 7 |
| `progressMessages` | 进度消息生成（辅助调用） | 2 |
| `title` | 会话标题生成（辅助调用） | 1 |

> 实测 Agent Host 侧 `chat`/`execute_tool` span **不带 `gen_ai.agent.name`**（12588 条里只有 9 条非空，全在 `invoke_agent`），
> 所以不能拿它跨管线做统一分组；子代理名在 Agent Host 是 `explore`/`general-purpose`/`task`（小写）。

按 `service.name` 筛选：

| `service.name` | 来源 |
|---|---|
| `copilot-chat` | 前台代理、CLI 包装器、Claude agent span（扩展发出） |
| `github-copilot` | 共享 Copilot runtime span 和 CLI 终端会话 |
| `claude-code` | Claude Code 子进程 SDK 遥测（当转发 `CLAUDE_CODE_ENABLE_TELEMETRY`） |

> **实测补充：`service.name` 只在 OTLP 推送可见，本地 SQLite 不存 resource attributes。**
> 离线分析要区分管线，只能靠**来源文件路径**（`agent-traces.db` vs `agent-host-traces.db`）或接收入口路径。
> 同理，`gen_ai.provider.name`（`github`/`openai`）与真实厂商无关：模型 `deepseek-flash` 走 `api.deepseek.com`、
> `mimo-v2.6-*` 走 `api.xiaomimimo.com`，provider 都写 `github`；Agent Host 侧 DeepSeek/MiMo 的 provider 写 `openai`。
> **厂商归因请用 `server.address` + 模型名，不要用 `provider.name`。**

补充：
- Copilot harness 的扩展包装 span（`invoke_agent copilotcli`，服务 `copilot-chat`）是 runtime 原生 span（服务 `github-copilot`）的父项，二者出现在同一 trace。
- 终端 Copilot CLI 会话在独立进程中运行，trace 作为 `github-copilot` 下独立根 trace，不链接扩展 trace；CLI runtime 仅支持 `otlp-http`。
- Claude agent 产生 `copilot-chat` 下扩展级 span，`invoke_agent claude` 根 span 嵌套 `chat`、`execute_tool`、`execute_hook`；工具为 `Agent` 时子 span 嵌套其下。**（实测本机未使用 Claude，该嵌套结构未经数据验证；同机扩展库里 `execute_hook` 为 0 条，引用时标注为官方口径。）**

---

## 14. 与 Agent Host 管道的区别（简表）

| 维度 | `github.copilot.chat.otel.*` | `chat.agentHost.otel.*` |
|---|---|---|
| 执行表面 | Local Copilot Chat：前台聊天、内联聊天、本地 CLI 包装器等 | Agent Host 会话：独立工具进程运行的 Copilot/Claude/Codex 代理会话 |
| 进程模型 | VS Code 扩展宿主内 | 独立工具进程，扩展宿主之外 |
| 配置前缀 | `github.copilot.chat.otel.*` | `chat.agentHost.otel.*` |
| 持久化 | `<extensionGlobalStorage>/agent-traces.db`（实测；旧文档误写为 `otel/spans.db`） | `<userData>/agent-host/otel/agent-host-traces.db` |
| 覆盖代理 | 主要是 Local Copilot Chat 前台代理 | Agent Host 会话中的 Copilot、Claude、Codex 等 |
| Trace 形态 | **实测：几乎全平**（3275/3285 为独立根 trace），仅子代理链路成树 | **实测：深树**（2175 个 `chat` span 只分布在 15 个 trace） |
| 缓存 token | **实测：`cache_read` 可用**（命中率 99.03%），`cache_creation` 不发 | **实测：两者都不发**，`cached_tokens` 全 NULL |
| 内容属性（实测本机） | `captureContent=true`，3.63 GB | `captureContent=false`，26 MB |

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

> 以下带 `captureContent: true` 的示例仅供**临时本机调试**：实测开启后单次会话即可让本地 SQLite 涨到 GB 级
> （见第 10 节），且关闭后旧行不会自动回收。任何要连到共享 sink / 云端后端的环境，请保持 `false`。

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
| 找不到 `execute_hook` span | **实测本管线 0 条**；hook 在 `span_events`（`github.copilot.hook.start/end`），见 6.5 |
| 第 8 节的 `copilot_chat.*` 事件一个都没有 | 那些是 **OTLP Logs**，`dbSpanExporter` 不落库；本地只有 8.2 的事件名 |
| 缓存命中率在 Agent Host 数据里是空/0 | **实测数据源不发 cache token**，不是未命中；见 12.1 |
| `provider.name` 与模型厂商不一致 | `provider.name` 表示路由/协议；厂商看 `server.address` + 模型名，见 13 |
| 拓扑只有一层、看不到父子 | 扩展管线每次 LLM 调用都是独立根 trace（实测），用会话 + 时间区间重建层级，见 6.1 |
| 同一会话冒出“陌生会话” | 子代理工具 span 的会话列被写成父 `call_...` id，需沿 `parent_span_id` 回溯，见 6.4 |

---

## 19. 相关链接

- [AI settings reference](/docs/agents/reference/ai-settings.md)
- [Manage AI settings in enterprise environments](/docs/enterprise/manage-ai-settings.md)
- [Troubleshoot AI in VS Code](/docs/agents/agent-troubleshooting/troubleshooting.md)
- [Diagnose prompt caching with the Cache Explorer](/docs/agents/agent-troubleshooting/cache-explorer.md)
- [OTel GenAI Semantic Conventions](https://github.com/open-telemetry/semantic-conventions/blob/main/docs/gen-ai/)
- [Aspire Dashboard standalone docs](https://aspire.dev/dashboard/standalone/)

---

## 20. 实测附录（2026-11-06）

样本：`agent-traces.db` 3285 span / 88970 属性行 / 21 事件行 / 60 个 distinct 键；
`agent-host-traces.db` 12588 span / 160046 属性行 / 5962 事件行 / 229 个键（`service.version` 为 runtime `1.0.103-unstable`）。
两边时间窗各约 2.3 / 3.1 天。以下清单已剔除内容门控键，即 **`captureContent: false` 时扩展管线实际可取的 51 个键**。

### 20.1 扩展管线 `chat` span（30 键，captureContent=false）

```text
copilot_chat.chat_session_id            copilot_chat.copilot_usage_nano_aiu
copilot_chat.debug_log_label            copilot_chat.parent_chat_session_id
copilot_chat.request.max_prompt_tokens  copilot_chat.request.options
copilot_chat.request.shape              copilot_chat.server_request_id
copilot_chat.session_id                 copilot_chat.time_to_first_token
error.type                              gen_ai.agent.name
gen_ai.conversation.id                  gen_ai.operation.name
gen_ai.provider.name                    gen_ai.request.max_tokens
gen_ai.request.model                    gen_ai.request.stream
gen_ai.request.temperature              gen_ai.request.top_p
gen_ai.response.finish_reasons          gen_ai.response.id
gen_ai.response.model                   gen_ai.response.time_to_first_chunk
gen_ai.usage.cache_read.input_tokens    gen_ai.usage.input_tokens
gen_ai.usage.output_tokens              gen_ai.usage.reasoning.output_tokens
gen_ai.usage.reasoning_tokens           server.address
```

### 20.2 其他 span 类型

| span 类型 | 键数 | 独有键 |
|---|---|---|
| `invoke_agent` | 21 | `copilot_chat.turn_count`、`github.copilot.agent.type`、`github.copilot.git.branch/commit_sha`、`copilot_chat.repo.head_branch_name/head_commit_hash`、`user.name` |
| `execute_tool` | 9 | `gen_ai.tool.name/type/call.id` |
| `embeddings` | 5 | `gen_ai.embeddings.input_count` |
| `vscode.chat.user_perceived_time_to_first_progress`（op=NULL） | 10 | 全部 `vscode.chat.user_interaction.*` |

### 20.3 官方文档列出但本机 0 次的键（不要写查询）

```text
gen_ai.usage.cache_creation.input_tokens
github.copilot.tool.parameters.edit_type / skill_name / mcp_server_name_hash / mcp_tool_name / command / file_path / mcp_server_name
github.copilot.hook.decision / tool_names
github.copilot.git.repository
github.copilot.github.org
copilot_chat.repo.remote_url / hook_type / hook_result_kind / hook_input / hook_output
copilot_chat.turn_count（仅 invoke_agent 有，chat 上没有）
```

### 20.4 对 TokenTracker 的直接含义

1. `LocalTraceImporter` 已补读 `span_events` 的 `github.copilot.hook.end`（实测导入 1976 条 hook，会话页从 0 组变 9 组）；
   `turn_start`/`turn_end`/`user_message`/`tools_available`/`exception`/`sandbox.*`/`session.usage_info` **仍未导入**——
   前两类含内容或已由 span 表达，后三类需要 `session_events` 之外的新表才能干净承载。
2. 导入路径拿不到 resource attributes，`ServiceNamespace` 恒 NULL，管线归因只能靠文件名——与现有实现一致，但不要指望它区分 BYOK 厂商。
3. `cache_creation` 恒 0，`TokenSemantics.UsesExclusiveInputTokens` 只认 anthropic/claude 是对的，本管线直接用 `input_tokens` 作分母。
4. 扩展库体积可达 GB 级，`DefaultTimeout = 5` 秒存在超时风险；建议按 `start_time_ms` 分批读，或先复制再导入。
5. 值得新增的字段（全部 `captureContent=false` 可用）：`request.max_prompt_tokens`（上下文占用率）、`request.options.reasoning_effort`、
   `request.shape.messageCount`、`gen_ai.conversation.compacted`、`permission.kind/result`、`session.usage_info` 的 token_limit/current_tokens、
   `vscode.chat.user_interaction.timeToFirstProgress`、`github.copilot.server_duration`、`process.exit.code`、`gen_ai.embeddings.*`、
   `copilot_chat.debug_log_label`、`gen_ai.agent.name` 的子系统后缀（辅助调用精确归类）。

---