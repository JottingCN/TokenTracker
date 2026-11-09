# Agent Host OpenTelemetry 监控信息文档

> 范围：**Agent Host 管道**，即 `chat.agentHost.otel.*` / `COPILOT_OTEL_*`（Agent Host 进程侧）。  
> **可用性**：Insiders / 非 stable 构建。  
> **与 Copilot Chat 管道的区别**：Agent Host 是独立 utility 进程，运行在扩展宿主**之外**，拥有自己的 OTel 管道、配置前缀和持久化路径，**不要将两者合并或假设一个设置会配置另一个**。

> **实测校准（2026-11-06，`agent-host-traces.db`：12588 span / 160046 属性行 / 5962 事件行 / 229 个 distinct 键 / 94 会话 / 3.1 天；`service.version` 为 runtime `1.0.103-unstable.r41379698026`）**
> 本文档以下结论被本机数据**推翻或大幅补充**，标注 **“实测：”** 的以数据为准：
> 1. **第 13 节“cache token 属性可用”是错的**——`gen_ai.usage.cache_read.input_tokens` / `cache_creation.input_tokens` 在本库 **0 次**，`spans.cached_tokens` 与 `reasoning_tokens` 列**全 NULL**。
> 2. **第 8 节的 `execute_hook` span 不存在**（0 条）；hook 实际是挂在 `invoke_agent` 上的 `span_events`。
> 3. 存在大量**完全未文档化**的信号：`permission` span、`github.copilot.session.timing.*`（约 90 组阶段耗时）、`vscode.agent_host.provider_timing/turn_timing/first_response`、`session.usage_info`（token_limit/current_tokens）、`github.copilot.cost`、`github.copilot.server_duration`、`gen_ai.conversation.compacted`、`gen_ai.request.reasoning.level`、`github.copilot.initiator`、`enduser.pseudo.id`、资源属性以 **span attribute** 形式落库等。详见第 19 节。


## 1. 概述：Agent Host 是什么

Agent Host 是一个**独立的 utility 进程**（`src/vs/platform/agentHost/`），用于承载 **native Copilot、Claude 和 Codex 运行时**，而不是使用扩展宿主内的 in-process harness。它拥有自己的 OTel 管道，目的是让 **provider-native 追踪**可以导出到 collector，或本地持久化以供检查。

当前架构中，**Agent Host 是 Copilot、Claude 和 Codex 代理会话的执行架构**。它负责路由、可选的 trace 拦截与 SQLite 持久化、资源规范化，以及跨 provider 的 trace context。每个 provider 拥有自己的 native instrumentation。扩展宿主中的 Copilot CLI 源仅作为 Agent Host 不可用时的回退和旧会话迁移支持，**不是新工作的模型**。


## 2. 核心架构：两条独立管道

| 维度 | **Agent Host OTel** | **扩展 OTel（Copilot Chat）** |
|---|---|---|
| **进程** | 独立 utility 进程（`src/vs/platform/agentHost/node/`） | 扩展宿主 |
| **配置前缀** | `chat.agentHost.otel.*` | `github.copilot.chat.otel.*` |
| **服务接口** | `IAgentHostOTelService` | `IOTelService` |
| **SDK** | Copilot `TelemetryConfig`、Claude 环境、Codex `otel.*` launch overrides | `@opentelemetry/sdk-node` 直接使用 |
| **持久化** | `<userData>/agent-host/otel/agent-host-traces.db` | `<extensionGlobalStorage>/agent-traces.db`（实测；旧文档误写 `otel/spans.db`） |

**关键原则**：这是两条独立管道。Agent Host 管道**不**配置扩展管道，反之亦然。

**实测差异（两条管线的落库形态差别很大，跨管线聚合前必须知道）：**

| 维度 | Agent Host 库 | Copilot Chat 库 |
|---|---|---|
| span 数 / 属性键 | 12588 / 229 键 | 3285 / 60 键 |
| trace 形态 | **深树**：2175 个 `chat` 全部有父级，只分布在 15 个 trace | **几乎全平**：3275/3285 是独立根 trace |
| `spans` 表结构 | **完全相同的 DDL**（同一段 SQLite schema） | 同左 |
| 资源属性 | **以 span attribute 落库**：`service.name/namespace/version/instance.id`、`process.executable.name/exit.code`、`server.address/port`、`enduser.pseudo.id` | **不落库**（无 resource 表，也没有这些 key） |
| 缓存 token | **0 次**（cache 键不存在，`cached_tokens` 全 NULL） | `cache_read` 可用，命中率 99.03% |
| `gen_ai.agent.name` | 仅 `invoke_agent` 有（12588 里 9 条非空） | **每条 `chat` 都有**，且是子系统标签 |
| hook | `span_events`：`github.copilot.hook.start/end` 1988/1976 | 无 |
| `invoke_agent` 的 token | **与 `chat` 合计完全相等**（input 627 916 878 / output 1 135 135），是汇总而非新增 | 同样汇总在 root 上 |

> 最后一行很关键：两条管线的 `invoke_agent` span 都携带**会话级汇总 token**，与它下面的 `chat` span 数值重复。
> 任何 `SUM(input_tokens)` 都必须限定 `operation_name='chat'`，否则整体用量翻倍。


## 3. 两种运行模式

Agent Host OTel 有两种模式，配置前需明确选择。

### 3.1 Pass-through 模式

- **触发条件**：`chat.agentHost.otel.enabled` 为 `true`，且 `dbSpanExporter.enabled` 为 `false`。
- **行为**：SDK 直接导出到用户配置的 exporter（OTLP/HTTP、OTLP/gRPC、file 或 console）。SDK span **不被拦截**；host 产生的 session-title 元数据通过匹配的 JSON/file/console forwarder 发出。

### 3.2 DB 模式

- **触发条件**：`chat.agentHost.otel.dbSpanExporter.enabled` 为 `true`（**隐式启用 OTel**）。
- **行为**：SDK 被指向 Agent Host 内 `127.0.0.1` 上的 **loopback OTLP/HTTP receiver**。span 被解码并写入本地 SQLite 数据库。如果**还配置了** OTLP/HTTP JSON 外部端点，receiver 会将规范化后的 JSON body **fan-out** 到该端点，因此外部 collector 可以同时接收 trace。
- **协议限制**：私有 trace hop 始终使用 **OTLP/HTTP JSON**。对于 OTLP/HTTP protobuf 或 OTLP/gRPC 外部协议，**trace 保留在 SQLite 中**，但 native logs 和 metrics 仍直接导出。

> 两种模式可以同时使用：DB 落盘 + OTLP 转发，既能在 collector 中实时查看，又能事后查询原始数据。


## 4. VS Code 设置

打开 Settings 搜索 `agentHost otel`。

| 设置 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `chat.agentHost.otel.enabled` | boolean | `false` | 启用 Agent Host OTel 发出 |
| `chat.agentHost.otel.exporterType` | string | `"otlp-http"` | `otlp-http`、`otlp-grpc`、`console`、`file` |
| `chat.agentHost.otel.otlpEndpoint` | string | — | OTLP 端点；裸 base URL 会自动追加 `/v1/traces`，完整信号级 URL 原样使用 |
| `chat.agentHost.otel.captureContent` | boolean | `false` | 在 span 属性中捕获 prompt/response 内容；隐私敏感，**不要**在 span 发往共享 sink 的环境中开启 |
| `chat.agentHost.otel.outfile` | string | `""` | `exporterType` 为 `file` 时的 JSON-lines 输出路径 |
| `chat.agentHost.otel.dbSpanExporter.enabled` | boolean | `false` | 将 span 持久化到 `<userData>/agent-host/otel/agent-host-traces.db`；隐式启用 OTel |

**设置生效时机**：环境变量在 Agent Host **spawn 时绑定**，`IAgentHostOTelService` 在构造时读取一次并缓存。**会话中途修改设置对已运行的 Agent Host 无效**，必须 reload window 或重启 VS Code。


## 5. 设置 → 环境变量映射

Agent Host 设置最终被翻译为 Agent Host 进程的环境变量（`buildAgentHostOTelEnv()`）。

| 设置 | 环境变量 |
|---|---|
| `chat.agentHost.otel.enabled` | `COPILOT_OTEL_ENABLED` |
| `chat.agentHost.otel.exporterType` | `COPILOT_OTEL_EXPORTER_TYPE` |
| `chat.agentHost.otel.otlpEndpoint` | `OTEL_EXPORTER_OTLP_ENDPOINT`（`COPILOT_OTEL_ENDPOINT` 也接受且优先） |
| `chat.agentHost.otel.captureContent` | `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` |
| `chat.agentHost.otel.outfile` | `COPILOT_OTEL_FILE_EXPORTER_PATH` |
| `chat.agentHost.otel.dbSpanExporter.enabled` | `COPILOT_OTEL_DB_SPAN_EXPORTER_ENABLED` |

**优先级**：企业 managed policy > 父进程环境变量 > 用户设置。


## 6. 信号路由：只有 trace 进入 Agent Host 管道

**只有 traces 进入 Agent Host loopback 和 SQLite 数据库**。当配置了外部 OTLP 端点时，provider-native **logs 和 metrics 绕过 Agent Host，直接从 SDK 导出**。

| Provider | Trace 配置 | 直接外部信号 |
|---|---|---|
| Copilot | SDK `TelemetryConfig` + trace-specific endpoint | Metrics |
| Claude | `OTEL_TRACES_EXPORTER` + trace-specific endpoint | Logs 和 metrics |
| Codex | `otel.trace_exporter` launch override | Logs 和 metrics |

Agent Host **不接收或持久化** `/v1/logs` 或 `/v1/metrics`。当 Agent Host OTel 启用时，其 launch 配置会覆盖该 Agent Host 进程中 standalone Claude/Codex exporter 的目标；禁用时，standalone provider 遥测不受影响。

**DB 模式的 Codex 临时过滤**：loopback receiver 会丢弃 Codex 中同时满足 `service.name=codex-app-server`、span 名 `auth`、`code.module.name=codex_login::auth::manager` 的 span（约每秒 2 次）。其他 Codex span 保留。

> **实测：本机库中没有任何 Codex/Claude span**（`service.name` 只有 `github-copilot` 11343 与 `vscode-agent-host` 1245），
> 该过滤与 Codex/Claude 的信号路由**均未经本机数据验证**，引用时请标注为“官方文档口径”。


## 7. 资源标识与 trace context

### 7.1 服务命名

Agent Host 是一个逻辑系统，包含多个 native OTel producer。Agent Host 拥有的 launch 和 ingest 边界会设置 `service.namespace=vscode.agent-host`，同时保持各组件 service name 不同。

| Producer | `service.name` | 实测（本库） |
|---|---|---|
| Host session/title metadata | `vscode-agent-host`（除非 host 有显式覆盖） | 1245 |
| Copilot runtime | `github-copilot` | 11343 |
| Claude runtime | `claude-code` | 0（未使用） |
| Codex app-server | `codex-app-server` | 0（未使用） |

**实测：Agent Host 库把资源属性写成 span attribute**，因此离线也能分层——这是与扩展库最大的结构差异：

| 键 | 实测取值 |
|---|---|
| `service.namespace` | `vscode.agent-host`，**12588/12588 全覆盖**（可用于判定来源管线） |
| `service.name` | `github-copilot` / `vscode-agent-host` |
| `service.version` | runtime 版本，如 `1.0.103-unstable.r41379698026.g3219c66` |
| `service.instance.id` | `copilot-otel-<pid>-<n>`（**进程内实例区分**，扩展库无） |
| `server.address` / `server.port` | `127.0.0.1` + 本地 provider 端口 |
| `process.executable.name` / `process.exit.code` | `pwsh.exe` + `0`/`1`/`2`（**shell 工具退出码，内容捕获关闭也有**） |
| `enduser.pseudo.id` | 32 位十六进制伪匿名 ID（45 次，见第 15 节） |

### 7.2 分布式 trace context

Host 发出一个**零时长**的 `vscode.agent_host.session` 锚点，并将其 W3C `traceparent`/`tracestate` 传递给 native runtime。Copilot 通过 `CopilotClientOptions.onGetTraceContext` 读取，Claude 通过 session 子进程环境接收，Codex 通过 session-scoped JSON-RPC request envelope 接收。Provider-native trace 因此可以**共享同一个 trace id**，同时保留各自的 provider conversation 属性。

### 7.3 Session title 元数据

**仅当内容捕获启用时**，Agent Host 在 Copilot/Claude/Codex 的权威 session title 变化时发出零时长 `vscode.agent_host.session.title_changed` span。属性包括 `gen_ai.conversation.id`、`vscode.agent_host.session.title`（上限 200 字符）、`vscode.agent_host.session.uri`。Title 文本是用户派生内容，因此仅在 `captureContent` 启用时发出。

> **实测：该门控成立**——本机 `captureContent=false`，库内 0 条 `title_changed`，也 0 个内容属性。
> 同时确认存在**四类 host 合成 span**（官方文档只提到 anchor 与 title_changed）：
> `vscode.agent_host.session` 41、`vscode.agent_host.provider_timing` 1058、`vscode.agent_host.turn_timing` 57、`vscode.agent_host.first_response` 43。
> 这些 span 的 `operation_name` 为 **NULL**，第 8 节的四类 span 不覆盖它们。


## 8. 追踪结构与 span 类型

Agent Host 的 trace 结构遵循 GenAI 语义约定，由 **provider-native instrumentation** 产生，Agent Host 负责路由、规范化、持久化和跨 provider context 传播。

### 典型 span 树

```text
invoke_agent {harness}                         (root span, per run / per sub-agent)
  ├── chat {model}                              (per LLM call)
  ├── execute_tool {tool}                       (per tool invocation)
  ├── external_tool {sha256}                    (实测：与 execute_tool 成对)
  ├── permission                                (实测：审批等待，无 operation_name)
  ├── session.timing.{phase}                    (实测：阶段耗时，无 operation_name)
  └── execute_hook {hook}                       (per hook execution, when applicable)  ← 实测 0 条，见第 10 节
```

**实测的 span 构成（12588 条）**：

| span 类别 | 条数 | `operation_name` | 说明 |
|---|---|---|---|
| `chat {Vendor}/{model}` | 2175 | `chat` | 模型名**带厂商前缀**（`DeepSeek/deepseek-flash`、`MiMo/mimo-v2.6-pro`），`provider.name` 一律 `openai` |
| `execute_tool {tool}` | 2470 | `execute_tool` | 明文内置名（`powershell pwsh.exe` 923、`view` 449、`edit` 384、`grep` 302）+ **哈希名的外部工具**（`execute_tool 8e6ae5f8…` 167） |
| `external_tool {sha256}` | 167（+64+24…） | NULL | **与同名 `execute_tool` 一一成对**，按 `gen_ai.tool.call.id` 去重，不要计两次 |
| `permission` | 1780 | NULL | 审批 span，时长 1 ms ~ **51 424 907 ms**（等待人确认），见第 12.1 节 |
| `session.timing.{phase}` | ~4700 | NULL | 阶段耗时 span，`phase` 如 `session_pending_noop` 623、`workflow_validation` 89、`host_callback` 364 |
| `vscode.agent_host.provider_timing` | 1058 | NULL | **零时长**（实测 min=max=0） |
| `vscode.agent_host.session` / `turn_timing` / `first_response` | 41 / 57 / 43 | NULL | host 合成 anchor |
| `invoke_agent {harness}` | 59 | `invoke_agent` | 51 条名字**无后缀**（`gen_ai.agent.name` 缺失），8 条是子代理（`explore` 6、`task` 1、`general-purpose` 1） |

各 span 类型**实测可见的属性**（已排除内容门控；`chat` 26 键、`invoke_agent` 21 键、`execute_tool` 14 键）：

- **`chat`**：`gen_ai.operation.name`、`provider.name`（`openai`）、`request.model`、`response.model`、`response.id`、
  `response.finish_reasons`（实测 `["tool_calls"]` 2115 / `["error"]` 63 / `["stop"]` 56）、`request.stream`、
  **`gen_ai.request.reasoning.level`（实测恒 `max`）**、**`gen_ai.conversation.compacted`（`true` 1092 次，压缩标记）**、
  `usage.input_tokens`、`usage.output_tokens`、**`usage.reasoning.output_tokens`**、`conversation.id`、`error.type`、
  `server.address`（**实测恒 `127.0.0.1`，是本地 provider 代理而非厂商域名**）+ `server.port`、
  `github.copilot.cost` / `server_duration` / `initiator` / `interaction_id` / `turn_id`、`service.name/namespace/version/instance.id`。
  - **实测缺口**：`chat` 上**没有** `gen_ai.agent.name`、`request.max_tokens`、`temperature`、`top_p`，也**没有任何 cache 属性**。
  - `spans.ttft_ms` 列**有值**（826 ~ 332 841 ms，均值 7364），来源只能是 `gen_ai.response.time_to_first_chunk`（秒）换算。
  - ⚠ **`spans.reasoning_tokens` 列全 NULL，而属性 `gen_ai.usage.reasoning.output_tokens` 存在**——落库列投影丢了推理 token。
    本项目导入路径已在列值为 0 时回退读该属性（新旧键都试），否则 Agent Host 来源会整批记成 0（实测导入后汇总 508 179 token）。
- **`invoke_agent`**：`gen_ai.agent.name`、`agent.type`、`provider.name`、`conversation.id`、`response.finish_reasons`、
  `usage.input_tokens`、`usage.output_tokens`（**与 `chat` 合计完全相等：627 916 878 / 1 135 135 → 汇总而非新增**）、
  `github.copilot.turn_count`、`github.copilot.cost`、`enduser.pseudo.id`、
  **`github.copilot.context.skills` / `custom_agent_names` / `mcp_server_names`**（明文技能与代理清单；MCP 名为**哈希数组**）。
  - **实测缺口**：`invoke_agent` 上**没有** `request.model` / `response.model`，模型归因只能取它下面的 `chat`。
- **`execute_tool`**：`gen_ai.tool.name`（明文或 SHA-256）、`tool.type`（实测全 `function`）、`tool.call.id`、`agent.name`、
  `error.type`（`ToolError` 55 / `ModelCallError` 39 / `query` 24 / `cancelled` 8 / `denied` 2）、
  **`process.executable.name`（`pwsh.exe`）与 `process.exit.code`（`0` 829 / `1` 69 / `2` 3）**——**内容捕获关闭也能拿到命令退出码**。
  实测 `execute_tool` 错误率 64/2470 ≈ 2.6%。
- **`execute_hook`**：**实测 0 条**，见第 10 节——hook 走 `span_events`。

**重要**：Agent Host 的 span 属性权威来源是 **provider/runtime 仓库**（如 `github/copilot-agent-runtime`），不是 VS Code 的 TypeScript 常量。审计或修改集成时，应先读取 bundled `@github/copilot-sdk` / `@github/copilot` 版本，再解析对应的 immutable runtime tag 和其 `docs/developer-docs/monitor.md`。


## 9. 指标（Metrics）

**Agent Host 管道本身只处理 traces**。Metrics 由 provider-native SDK **直接导出到外部端点**，不经过 Agent Host loopback 或 SQLite。

因此，Agent Host 场景下的 metrics 可用性取决于 provider 是否发出以及是否配置了外部端点：

| 指标示例 | 来源 | 说明 |
|---|---|---|
| `gen_ai.client.token.usage` | provider-native | Token 用量（输入/输出） |
| `copilot_chat.tool.call.count` | 共享指标命名空间 | 按工具名和成功状态统计工具调用 |
| `copilot_chat.tool.call.duration` | 共享指标命名空间 | 工具执行延迟 |

指标包含筛选属性，如 `gen_ai.request.model`、`gen_ai.provider.name`、`gen_ai.tool.name`、`error.type`。**以你的 provider 实际发出的指标名为准**，不要假设 Agent Host 管道会转发或补全 metrics。

> **实测补充：**
> 1. 本地 SQLite **不含任何 metric**（receiver 丢弃 `/v1/metrics`），所以“Agent Host 有没有指标”只能用外部 collector 验证，不能靠离线库排查。
> 2. **不用 metrics 也能拿到费用与耗时**：`chat`/`invoke_agent` span 上有 **`github.copilot.cost`**（实测 2130 次，本环境恒 `0`，BYOK/免费模型）与
>    **`github.copilot.server_duration`**（实测 2129 次，825 ~ 332 855 ms），后者是 runtime 侧的服务端耗时，
>    与 `end_time_ms - start_time_ms` 的差值即可归因网络/排队开销。
> 3. `copilot_chat.*` 结果指标（接受率、LOC、存活率）**只由扩展宿主产生**，只用 Agent Host 时结果卡片为空是预期行为。


## 10. 事件（Events）

Agent Host 管道**不拦截或持久化 OTLP Logs 信号**（`/v1/logs` 被丢弃）。`vscode.agent_host.session.title_changed` 是 host 产生的**零时长 span**（在 trace 中），不是独立 event 信号。Provider-native events（如 `copilot_chat.session.start`、`copilot_chat.tool.call`）是否可用，取决于 provider 是否发出以及你的外部端点配置。

> **但必须区分：本地 SQLite 的 `span_events` 表会被持久化。** 实测本库有 5962 条，是 runtime 挂在 span 上的事件记录，
> 与上面说的 “Logs 信号” 不是一回事。第 8 节 `execute_hook`、上下文用量、异常详情、sandbox 决策**只能从这里取**。

实测事件清单（次数 / 关键 `attributes`）：

| 事件名 | 次数 | 挂载 span | 关键属性 |
|---|---|---|---|
| `github.copilot.hook.start` | 1988 | `invoke_agent` | `github.copilot.hook.type`、`hook.invocation_id` |
| `github.copilot.hook.end` | 1976 | `invoke_agent` | 同上 + **`hook.duration`（秒，浮点，实测 0.0004~0.0006 量级）** |
| `github.copilot.sandbox.enforcement_state` | 923 | `execute_tool` | `sandbox.decision_kind`、`control`(`process`)、`outcome`(`inactive`)、`enforcement_point`(`shell`)、`platform`(`windows`) |
| `github.copilot.session.usage_info` | 874 | — | **`github.copilot.token_limit`(恒 744000)、`current_tokens`(实测最大 597 224 ≈ 80% 预算)、`messages_length`** → 上下文占用率权威来源 |
| `gen_ai.client.operation.exception` | 124 | — | `exception.type`(`ModelCallError`) + **`github.copilot.error_type`(`query`)、`error_status_code`(400)、`error_provider_call_id`、`error_service_request_id`** |
| `github.copilot.user.message` | 45 | — | `message.source`(`user`)、`interaction_id`、`turn_id`（**不含正文**，captureContent=false 实测） |
| `github.copilot.mcp.server.lifecycle` | 21 | — | MCP server 生命周期 |
| `github.copilot.session.abort` / `system.notification` / `compaction_start` / `compaction_complete` | 5 / 3 / 2 / 1 | — | 中止与压缩时间线 |

`github.copilot.hook.type` 实测分布：`postToolUse` 1034、`preToolUse` 830、`errorOccurred` 26、`postToolUseFailure` 24、
`userPromptTransformed` 22、`userPromptSubmitted` 22、`sessionStart` 14、`agentStop` 9、`subagentStart` 7。

**Hook 统计口径**：`start`(1988) 与 `end`(1976) 数量不等，说明有 hook 未收尾；**耗时只认 `end` 上的 `hook.duration`**，
不要用两条事件的时间差配对（实测两者 `timestamp_ms` 只差 0~1 ms，根本量不出真实耗时）。


## 11. 内容捕获：`captureContent`

默认**不捕获** prompt、response、tool arguments、tool results 等内容。`chat.agentHost.otel.captureContent` 为 `false` 时：

- Span 仅包含元数据：模型名、token 计数、时长、工具名、`error.type` 等。
- **不可用**：prompt/response 文本、tool arguments/results、file paths、commands、raw server names、session title 文本。
  **实测还有一项与文档无关的不可用：cache read/creation token（见第 13 节，当前 runtime 不发，开不开内容捕获都没有）。**
- **可用**：trace 拓扑、延迟、工具调用次数/类型/错误、token 用量、**资源属性**、**上下文用量事件**、**审批事件**、**命令退出码**、
  **推理档位**、**压缩标记**、**server 端耗时**、**runtime 上报的费用字段**（详见第 12、19 节）。

> **实测：本机 `captureContent=false` 时，库内 0 个内容属性键**（`gen_ai.input.messages`、`gen_ai.output.messages`、
> `gen_ai.system_instructions`、`gen_ai.tool.definitions`、`gen_ai.tool.call.arguments/result`、`vscode.agent_host.session.title_changed` 全不存在），
> 库体积仅 26 MB / 12588 span；同期扩展库因开启捕获而达 3.63 GB。门控行为与文档一致。

`captureContent` 启用后，prompt、response、tool 内容会进入 span 属性；**session title span 也才会发出**。隐私敏感，不应在 span 发往共享 sink 的环境中开启。

企业 managed 设置支持 `captureContent` 配合 `lockCaptureContent: true`，在组织层面关闭内容捕获并阻止开发者本地重新开启。


## 12. 工具调用统计（`captureContent: false` 下）

**可以统计**。工具调用的元数据与内容正文在 OTel 设计中分离。

| 维度 | 可用信号 | 实测 |
|---|---|---|
| 调用次数 | `copilot_chat.tool.call.count`；按 `gen_ai.tool.name`、成功/失败聚合 | metrics **不经 Agent Host**，本地库请用 `spans WHERE operation_name='execute_tool'`（2470 条） |
| 延迟 | `copilot_chat.tool.call.duration`；`execute_tool` span 时长 | span 时长可用；另有 `github.copilot.server_duration` |
| 工具名 | `gen_ai.tool.name` | 内置名明文（`powershell`/`view`/`edit`/`grep`/`glob`/`task`）；**外部工具是 SHA-256**（167/64/24…），需现算哈希反查 |
| 工具类型 | `gen_ai.tool.type`（`function` / `extension` / MCP） | **实测全为 `function`**，包括 MCP 外部工具，不能用它区分 MCP |
| 调用标识 | `gen_ai.tool.call.id` | 存在；另需处理 `execute_tool`/`external_tool` 成对重复 |
| 错误 | `error.type` | `ToolError` 55、`ModelCallError` 39、`query` 24、`cancelled` 8、`denied` 2；**取值不都是类名** |
| 调用拓扑 | `execute_tool` span 在 trace 中的父子关系（是否由子代理触发） | **可用且质量好**：Agent Host 是真深树 |
| **命令退出码** | `process.executable.name` + `process.exit.code` | **实测无需内容捕获**：`pwsh.exe` 923 条，退出码 0/1/2 = 829/69/3 |
| **MCP 归属** | `github.copilot.context.mcp_server_names`（**哈希 JSON 数组**）、`github.copilot.external_tool.name`（哈希） | 文档写的 `mcp_server_name_hash` / `mcp_tool_name` / `edit_type` / `skill_name` **键名实测都不存在** |
| **技能/自定义代理清单** | `github.copilot.context.skills`、`github.copilot.context.custom_agent_names`（**明文**） | 是“本次会话可用清单”，不是“被调用的那一个” |

**不可用**（除非 `captureContent`）：tool arguments、tool results、具体 command、file path、raw MCP server name。
（**实测补充：`github.copilot.tool.parameters.*` 一族在两个本地库里都完全不存在**，不要按文档里的键名写查询。）

### 12.1 权限审批（官方文档未列，实测 1780 条）

Agent Host 把每次工具审批写成独立 span `permission`（**`operation_name` 为 NULL**，1 ms ~ 51 424 907 ms）：

| 属性 | 实测取值 |
|---|---|
| `github.copilot.permission.kind` | `read` 598、`shell` 573、`write` 265、`url` 185、`custom-tool` 159 |
| `github.copilot.permission.result` | `approved` 1776、`denied-interactively-by-user` 2 |
| `github.copilot.permission.tool_name` | 外部工具为 **SHA-256**，内置工具明文 |

这是**唯一能算“人审等待时长”和“拒绝率”的来源**，也是端到端延迟里最大的可归因项（最长一条 ≈ 14.3 小时的挂起审批）。
`denied` 同时会以 `error.type=denied` 出现在对应 `execute_tool` 上（实测 2 条），两者可对齐校验。

> **关于“人工还是自动审批”**：`permission.result` 没有自动/人工标记，只有 `approved` 与 `denied-interactively-by-user`。
> 因此只能从现象判断：**没有 permission span** = 该工具未请求审批（策略放行）；**有 span 且耗时极短** = 瞬时通过；
> **耗时达到秒级/更长** = 在等人确认；只有 `denied-interactively-by-user` 是显式的人工拒绝。
> 本项目把 permission span 存成 `operation='permission'` 的事件（`permission_kind` / `permission_result` 两列），
> 在 Trace 拓扑里显示为 `permission` 节点，label 形如 `shell · approved · powershell`，耗时即审批等待时长；
> 「事件统计」里独立成一行「审批」。不要试图从 `approved` 反推是谁点的通过。


## 13. 缓存命中统计

> **实测更正（2026-11-06）：本节原先的结论被本机数据推翻。**
> **Agent Host 管线目前拿不到缓存 token。** `agent-host-traces.db`（12588 span / 160046 属性行 / 229 个键）里：
> - `gen_ai.usage.cache_read.input_tokens`：**0 次**
> - `gen_ai.usage.cache_creation.input_tokens`：**0 次**
> - `spans.cached_tokens` 列：**全 NULL**（0/12588）
> - `chat` span 的 usage 属性只有 `input_tokens` / `output_tokens` / `reasoning.output_tokens` 三个
>
> 原先写的“cache token 属性可用，且独立于 `captureContent`”“在 root span 和 per-chat span 上都存在，适用于所有 agent 类型”
> **“present whenever upstream field exists, including 0”均与实测不符**——当前 runtime 版本（`1.0.103-unstable`）根本不落这两个键，
> 与是否命中、是否开启内容捕获都无关。

### 13.1 对本项目的直接后果

1. **缓存命中率只有 Copilot Chat 管线能算**（实测总体 99.03%，逐条 `cached ≤ input`）。只用 Agent Host 的会话在看板上必然显示 `-` 或 0%，
   这是**数据源缺失，不是未命中**，UI 不能把它渲染成 0%。
2. **combined 视图的分母/命中率**：跨管线合并后命中率会被 Agent Host 侧的“无缓存数据”稀释；
   展示时应说明口径（例如只按有 cache 数据的来源计算，或明确标注“来源不含缓存 token”）。
3. **缓存写入价分支不会触发**：`cache_creation` 两条管线都不发，所以“缓存写入按缓存写入价计费”的逻辑目前只在未来 runtime 才有效，
   不要拿它验证费用回归。

### 13.2 Agent Host 侧可用的替代信号

| 信号 | 位置 | 用途 |
|---|---|---|
| `github.copilot.session.timing.cacheReadCallCount` | `session.timing.*` span 属性 | 会话内命中缓存的调用**次数**（实测出现值 `0`），只能定性 |
| `github.copilot.session.usage_info` 事件：`token_limit` / `current_tokens` / `messages_length` | `span_events.attributes` | 上下文压力与前缀增长，间接推断缓存是否生效 |
| `gen_ai.conversation.compacted` = `true`（1092 次） | `chat` span 属性 | 压缩后前缀变化，命中率骤降的可解释因素 |
| `gen_ai.response.time_to_first_chunk` / `spans.ttft_ms` | `chat` span | TTFT 越低通常说明命中，只能作旁证 |

### 13.3 计算方式（仅在 `cache_read` 存在时适用）

```text
cache_read_tokens = sum(gen_ai.usage.cache_read.input_tokens)
input_tokens      = sum(gen_ai.usage.input_tokens)
命中率            = cache_read_tokens / input_tokens      -- 不要重复加 cache_read
```

**注意**：`gen_ai.usage.input_tokens` 的语义因 provider 而异。**实测确认**：Copilot Chat 管线（OpenAI 兼容 `messages` API）
的 `input_tokens` **已包含**缓存读取部分，分母直接用它；Anthropic/Claude 类 provider 才是排他口径（需 `input + cache_read + cache_creation`）。
不要把不同 provider 的命中率放在一起比较。


## 14. 与 Copilot Chat 管道的分工与是否同时统计

| 维度 | `github.copilot.chat.otel.*` | `chat.agentHost.otel.*` |
|---|---|---|
| 执行表面 | Local Copilot Chat：前台聊天、内联聊天、本地 CLI 包装器等 | Agent Host 会话：Copilot、Claude、Codex 通过独立 utility 进程运行 |
| 进程 | 扩展宿主内 | 独立 utility 进程，扩展宿主之外 |
| Trace 路由 | 扩展直接导出 | Pass-through 或 DB 模式；只有 trace 进入 loopback/SQLite |
| Metrics/Logs | 扩展侧处理 | Provider-native 直接导出，**绕过 Agent Host** |
| 覆盖代理 | 主要是 Local Copilot Chat | Agent Host 中的 Copilot、Claude、Codex |
| **实测：trace 形态** | 几乎全平（3275/3285 独立根 trace），拓扑需按会话+时间重建 | 深树（2175 个 chat 只分布在 15 个 trace），层级可直接用 |
| **实测：缓存 token** | `cache_read` 可用（命中 99.03%） | **两者都不发**，`cached_tokens` 全 NULL |
| **实测：资源属性** | 不落库，管线归因只能靠文件路径 | 以 span attribute 落库（`service.namespace` 全覆盖） |
| **实测：hook** | 无 `execute_hook`、无 hook 事件 | 有 `github.copilot.hook.start/end` 事件 |
| **实测：模型名** | 规范 ID，无厂商前缀（`deepseek-flash`） | **带厂商前缀**（`DeepSeek/deepseek-flash`），`provider.name` 一律 `openai` |

**是否需要同时统计**：不是重复统计同一批数据，而是按执行表面互补。只使用 Local Chat 则只配扩展管道；使用 Agent Host 会话则需 `chat.agentHost.otel.*`；两者都用则两个都配，但后端应按 `service.namespace` / `service.name` 或执行表面标签分开聚合。

**是否会消失**：短期预计不会。Agent Host 是当前代理会话的主要执行架构，但 Local Copilot Chat 扩展管道**未被宣布弃用**；被标记为回退/遗留的是扩展宿主中的旧版 Copilot CLI 源，而非 `github.copilot.chat.otel` 管道本身。


## 15. 安全与隐私

| 方面 | Agent Host 管道 |
|---|---|
| 默认关闭 | `chat.agentHost.otel.enabled` 默认 `false` |
| 默认无内容 | prompt/response/tool 内容需 `captureContent` 显式开启 |
| Session title | **仅内容捕获启用时**发出，因为 title 是用户派生内容 |
| Metrics/Logs 路由 | 绕过 Agent Host，直接发往外部端点；Agent Host 不接收/持久化 |
| 企业控制 | `captureContent` + `lockCaptureContent: true` 可在组织层面锁定关闭内容捕获 |
| 敏感内容范围 | titles、prompts、responses、tool arguments、file paths、commands、raw server names 均应视为敏感内容，按有效内容捕获策略进行门控并施加边界 |

> **实测更正：Agent Host 确实带身份/环境类属性，只是不带用户名。** 本库捕获到（**均在 `captureContent=false` 下**）：
>
> | 属性 | 实测 | 敏感性 |
> | --- | --- | --- |
> | `enduser.pseudo.id` | 32 位十六进制，45 次 | **伪匿名稳定 ID**，可跨会话关联同一开发者，但不可直接反查身份 |
> | `service.instance.id` | `copilot-otel-<pid>-<n>` | **泄露本机 PID**，可关联同机其他信号 |
> | `github.copilot.context.skills` / `custom_agent_names` | 明文技能名与自定义代理名清单 | **用户自建资源名**（组织内命名习惯可识别），官方文档未列为敏感项 |
> | `github.copilot.context.mcp_server_names` | SHA-256 数组 | 已哈希，与“raw server name 需内容捕获”一致 |
> | `process.executable.name` | `pwsh.exe` | 环境信息（不含命令正文） |
> | `github.copilot.interaction_id` / `turn_id` / `gen_ai.response.id` / `error_service_request_id` | UUID / 请求 ID | **可跨信号与服务端日志关联**，属可识别标识 |
>
> 结论修正：**扩展侧文档“Agent Host 不包含身份属性”只适用于 `user.name` / `process.user.name` / `host.name` 这类明文身份键**；
> `enduser.pseudo.id` 这类伪匿名键由 runtime 自行发出，不受 `captureIdentity` 控制，组织评估时应按可关联标识对待。
> 本项目仍按既有约束处理：**解析层不入库以上任何一项**（allowlist 只保留模型、token、时长、工具名、`error.type`、会话与工作目录元数据）。


## 16. 快速配置模板

### 仅 OTLP 导出（Pass-through，不捕获内容）

```json
{
  "chat.agentHost.otel.enabled": true,
  "chat.agentHost.otel.exporterType": "otlp-http",
  "chat.agentHost.otel.otlpEndpoint": "http://localhost:4318",
  "chat.agentHost.otel.captureContent": false
}
```

### DB 落盘 + OTLP 转发

```json
{
  "chat.agentHost.otel.enabled": true,
  "chat.agentHost.otel.dbSpanExporter.enabled": true,
  "chat.agentHost.otel.otlpEndpoint": "http://localhost:4318",
  "chat.agentHost.otel.captureContent": true
}
```

> 上例的 `captureContent: true` 仅供**临时本机调试**：实测开启后 prompt/response/tool 正文会进入本地 SQLite 并可能把库撑到 GB 级
> （同期扩展库因开启捕获达到 3.63 GB），且关闭后旧行不会自动回收。任何连到共享 sink 的环境请保持 `false`。

> 改动设置后必须 **Reload Window** 或重启 VS Code，因为环境变量只在 Agent Host spawn 时绑定。

### DB 模式 + 本地 SQLite

```json
{
  "chat.agentHost.otel.dbSpanExporter.enabled": true
}
```

Span 写入 `<userData>/agent-host/otel/agent-host-traces.db`。


## 17. 排查清单

| 现象 | 检查 |
|---|---|
| 完全没有数据 | `chat.agentHost.otel.enabled` 是否为 `true`；是否在 Insiders/非 stable 构建；endpoint/exporter 是否正确；**是否已 reload window/重启**（设置只在 spawn 时生效） |
| 有 trace 但无 metrics | Agent Host 管道**只处理 trace**；metrics 由 provider 直接导出，确认外部端点和 provider 是否发出 |
| DB 模式下外部 collector 收不到 trace | 外部端点需为 **OTLP/HTTP JSON**；protobuf/gRPC 外部协议下 trace 只留在 SQLite |
| 工具调用有但无参数/结果 | `captureContent` 为 `false`，这是预期行为 |
| 缓存字段为 `0` 或缺失 | 是否多轮稳定前缀；provider 是否报告；`input_tokens` 语义按 provider 确认；Cache Explorer 交叉验证 |
| 中途改设置不生效 | 必须 reload window 或重启 VS Code；Agent Host 构造时缓存环境变量 |
| Claude/Codex standalone exporter 行为异常 | Agent Host OTel 启用时其 launch 配置会覆盖 standalone 目标；禁用时恢复 |
| Codex 有 `auth` 噪声 span 被过滤 | DB 模式下的临时 Codex 0.142 兼容过滤，属预期行为 |
| 缓存命中率是空/0 | **实测当前 runtime 不发 cache token（数据源缺失）**，不是未命中；见第 13 节 |
| 找不到 `execute_hook` span | **实测 0 条**；hook 在 `span_events`（`github.copilot.hook.start/end`），见第 10 节 |
| `gen_ai.agent.name` 在 `chat` 上为空 | **实测如此**（该键只在 `invoke_agent`），跨管线统一分组会漏 |
| 推理 token 统计为 0，但属性里明明有 | `spans.reasoning_tokens` 列全 NULL 而 `gen_ai.usage.reasoning.output_tokens` 存在——**任何只读列的消费方都会丢**，本项目导入已改为属性回退 |
| 工具被算成两次 | `execute_tool {sha256}` 与 `external_tool {sha256}` 成对出现，按 `gen_ai.tool.call.id` 去重 |
| `gen_ai.tool.type` 分不出 MCP | **实测全为 `function`**，MCP 要靠 `context.mcp_server_names`（哈希）/`external_tool.*` 判定 |
| 端到端耗时莫名巨大 | 先查 `permission` span：实测最长一条挂起 14.3 小时（等人审批），与模型耗时无关 |
| 厂商/供应商判断错 | `provider.name` 一律 `openai`、`server.address` 一律 `127.0.0.1`；**厂商在模型名前缀里**（`DeepSeek/…`、`MiMo/…`） |
| 用 `service.name` 过滤不到 | 本地库里它是 **span attribute** 而非资源属性；`service.namespace` 恒 `vscode.agent-host` |
| 汇总 token 翻倍 | `invoke_agent` 的 usage 是**会话汇总**，与 `chat` 合计完全相等；聚合必须限定 `operation_name='chat'` |


## 18. 参考来源

- Agent Host OTel Pipeline：`src/vs/platform/agentHost/OTEL.md`
- Agent Host 设置与环境变量映射：`common/agentService.ts` / `agentHostStarter.config.contribution.ts`
- Agent Host 信号路由与资源标识：OTEL.md 的 Native Provider Signal Routing / Resource Identity / Distributed Trace Context
- OTel 在 VS Code 中的管道划分与架构说明
- Copilot OTel Metrics（指标命名与查询示例，跨管道参考）


## 19. 实测附录（2026-11-06）

### 19.1 官方文档列出但本机 0 次的键/结构

```text
gen_ai.usage.cache_read.input_tokens        gen_ai.usage.cache_creation.input_tokens
（operation_name='execute_hook' 的 span）   github.copilot.hook.decision / tool_names / duration(span attr)
github.copilot.tool.parameters.edit_type / skill_name / mcp_server_name_hash / mcp_tool_name / command / file_path / mcp_server_name
spans.cached_tokens 列                       spans.reasoning_tokens 列      spans.turn_index 列
```

### 19.2 未文档化但可取的信号（`captureContent=false` 实测可用）

**会话/调用级**

| 键 | 位置 | 实测 | 用途 |
|---|---|---|---|
| `github.copilot.cost` | chat / invoke_agent | 2130 次，恒 `0` | runtime 侧费用，可对账估算 |
| `github.copilot.server_duration` | chat | 2129 次，825~332 855 ms | 服务端耗时，与 span 时长作差 |
| `gen_ai.request.reasoning.level` | chat | 恒 `max` | 推理档位分组 |
| `gen_ai.conversation.compacted` | chat | `true` 1092 次 | 压缩标记 |
| `github.copilot.initiator` | chat | `agent` 2083 / `user` 45 / `sub-agent` 1 | 发起方区分 |
| `github.copilot.turn_id` / `interaction_id` | chat | 2165 次 | 轮次/交互关联（比 turn_index 可靠） |
| `github.copilot.turn_count` | invoke_agent | 51 次，取值 **1 ~ 155** | 会话轮次 |
| `gen_ai.response.finish_reasons` | chat | `tool_calls` 2115 / `error` 63 / `stop` 56 | 结束原因分布 |
| `gen_ai.conversation.compacted` + `session.usage_info` | chat / events | `token_limit` 744000 | **上下文压力** |

**审批与沙箱**

| 键/事件 | 实测 |
|---|---|
| `permission` span + `permission.kind/result/tool_name` | 1780 条；read/shell/write/url/custom-tool；approved/denied-interactively-by-user |
| `sandbox.enforcement_state` 事件 | 923 条；`control=process`、`outcome=inactive`、`enforcement_point=shell`、`platform=windows` |

**分阶段耗时（体量最大的一类）**

| 前缀 | 键数 | 说明 |
|---|---|---|
| `github.copilot.session.timing.{Stage}DurationMs` / `{Stage}StartOffsetMs` | 约 90 组 | 阶段名如 `sessionConstruction`、`contextAssembly`、`toolDescriptors`、`mcpCatalog`、`systemPrompt`、`promptEnvironment`、`powershellPrompt`、`responseAdoption`、`turnPreparation`、`hostCallback`、`attemptCacheControl`、`attemptMessageAssembly`、`firstModelCall` … |
| `github.copilot.session.timing.phase/ordinal/segment/duration_ms/start_offset_ms/totalMs/outcome/phaseCount/stageStartUptimeMs` | 9 | 阶段 span 的通用维度（实测 4464 条 stage span；`outcome=success` 101） |
| `github.copilot.session.timing.modelCallCount / modelCallErrorCount / modelCallAbortedCount / modelCallsTruncated / modelCallToolRequestCount / modelCallOutputCount / interCallCount / cacheReadCallCount / sessionRenameCallCount` | 9 | 会话内计数（`outcome` 覆盖 101 个会话，与 94 会话量级一致） |
| `github.copilot.session.timing.firstModelCallTimeToFirstTokenMs` / `outputModelCallTimeToFirstTokenMs` / `firstModelCallOutputTtftMs` | 3 | **首轮/输出轮 TTFT 拆分** |

**host 侧延迟分解（`vscode.agent_host.*`，挂在 `provider_timing`/`turn_timing`/`first_response` span）**

| 键 | 实测 |
|---|---|
| `timeToFirstProgress` / `timeToFirstSubstantiveProgress` | 与 `vscode.chat.user_interaction.*` 同名键呼应；**用户感知首响应** |
| `firstResponseTextMs` | 浮点毫秒，实测最大 808143 |
| `hasResponseText` / `result` | `success` 925 / `cancelled` 128 / `error` 62 → **取消率是免费信号** |
| `queueMs` / `executionMs` / `queueBeforeProgressMs` / `executionBeforeProgressMs` / `timeToProviderDispatch` / `totalElapsedMs` | host 侧排队 vs 执行拆分 |
| `providerStageQueueMs/ConfigMs/CreateMs/SnapshotMs/RefreshMs/FinalizeMs/PersistMs/TurnPrepareMs/ModelResponseMs/ClientMs` | 10 段 provider 阶段耗时 |
| `sendStageAttachmentsMs/ContributionsMs/ModelSelectionMs/WorkingDirectoryMs/CheckpointMs` | 5 段发送阶段耗时 |
| `isSubagentSession` / `invocationKind`(`newTurn`/`existingTurn`) / `sessionTurnKind` / `hostRootTurnOrdinal` / `rendererRootInvocationOrdinal` / `rootToolCallsBeforeFirstText` | 子代理与首字前的工具调用数 |
| `provider`(`copilotcli`) / `connectionKind` / `source`(`host`/`renderer`) / `titleGenerationStrategy`(`utility`) / `trustInteractionRequired` / `hostProcessAgeMs` | 会话来源与 trust 交互 |
| `vscode.chat.user_interaction.timeToTermination` | 仅本管线出现 |

**事件属性**

| 事件 | 属性 |
|---|---|
| `session.usage_info` | `github.copilot.token_limit`、`current_tokens`、`messages_length` |
| `gen_ai.client.operation.exception` | `github.copilot.error_type`、`error_status_code`、`error_provider_call_id`、`error_service_request_id` |
| `hook.start/end` | `github.copilot.hook.type`、`hook.invocation_id`、`hook.duration`(秒) |
| `sandbox.enforcement_state` | `sandbox.decision_kind`、`control`、`outcome`、`enforcement_point`、`platform` |

### 19.3 库结构事实

- `spans` / `span_attributes` / `span_events` / `schema_version` / view `sessions` 的 DDL **与扩展库逐字相同**（同一份导出代码）。
- 主键 `span_id` 单列；`turn_index` 全 NULL；`status_code` 0/1/2。
- 会话列：`conversation_id` 覆盖几乎全部 span，`chat_session_id` **全 NULL**——所以本管线不需要 `parent_chat_session_id` 回溯，
  子代理归属靠 `parent_span_id` 树即可。
- 229 个 distinct 键中约 **196 个** 落在 `operation_name IS NULL` 的 span 上（permission / session.timing / agent_host 合成 span），
  只看 `chat`/`execute_tool`/`invoke_agent` 三类会漏掉绝大多数信号。
- 时间窗实测 3.1 天、94 会话，与“只保留约 7 天 / 100 会话”的量级一致；库体积 26 MB（因 `captureContent=false`）。

---

以上为 **Agent Host 管道**的完整信息文档，结构与 Copilot Chat 文档对齐。核心记忆点：**独立进程、独立配置、只有 trace 进入 Agent Host 管道；metrics/logs 走 provider 直出，但 SQLite 会落 `span_events`；`captureContent: false` 下工具调用、审批、命令退出码、阶段耗时、上下文用量都可统计，但内容正文不可见，且当前 runtime 不发任何 cache token；设置改动必须重启生效。**