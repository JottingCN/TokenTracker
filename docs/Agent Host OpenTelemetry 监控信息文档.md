# Agent Host OpenTelemetry 监控信息文档

> 范围：**Agent Host 管道**，即 `chat.agentHost.otel.*` / `COPILOT_OTEL_*`（Agent Host 进程侧）。  
> **可用性**：Insiders / 非 stable 构建。  
> **与 Copilot Chat 管道的区别**：Agent Host 是独立 utility 进程，运行在扩展宿主**之外**，拥有自己的 OTel 管道、配置前缀和持久化路径，**不要将两者合并或假设一个设置会配置另一个**。


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
| **持久化** | `<userData>/agent-host/otel/agent-host-traces.db` | `<extensionGlobalStorage>/otel/spans.db` |

**关键原则**：这是两条独立管道。Agent Host 管道**不**配置扩展管道，反之亦然。


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


## 7. 资源标识与 trace context

### 7.1 服务命名

Agent Host 是一个逻辑系统，包含多个 native OTel producer。Agent Host 拥有的 launch 和 ingest 边界会设置 `service.namespace=vscode.agent-host`，同时保持各组件 service name 不同。

| Producer | `service.name` |
|---|---|
| Host session/title metadata | `vscode-agent-host`（除非 host 有显式覆盖） |
| Copilot runtime | `github-copilot` |
| Claude runtime | `claude-code` |
| Codex app-server | `codex-app-server` |

### 7.2 分布式 trace context

Host 发出一个**零时长**的 `vscode.agent_host.session` 锚点，并将其 W3C `traceparent`/`tracestate` 传递给 native runtime。Copilot 通过 `CopilotClientOptions.onGetTraceContext` 读取，Claude 通过 session 子进程环境接收，Codex 通过 session-scoped JSON-RPC request envelope 接收。Provider-native trace 因此可以**共享同一个 trace id**，同时保留各自的 provider conversation 属性。

### 7.3 Session title 元数据

**仅当内容捕获启用时**，Agent Host 在 Copilot/Claude/Codex 的权威 session title 变化时发出零时长 `vscode.agent_host.session.title_changed` span。属性包括 `gen_ai.conversation.id`、`vscode.agent_host.session.title`（上限 200 字符）、`vscode.agent_host.session.uri`。Title 文本是用户派生内容，因此仅在 `captureContent` 启用时发出。


## 8. 追踪结构与 span 类型

Agent Host 的 trace 结构遵循 GenAI 语义约定，由 **provider-native instrumentation** 产生，Agent Host 负责路由、规范化、持久化和跨 provider context 传播。

### 典型 span 树

```text
invoke_agent {harness}                         (root span, per run / per sub-agent)
  ├── chat {model}                              (per LLM call)
  ├── execute_tool {tool}                       (per tool invocation)
  └── execute_hook {hook}                       (per hook execution, when applicable)
```

- **`invoke_agent`**：包装整个代理运行或委托的子代理运行。`gen_ai.operation.name` 为 `invoke_agent`，携带 `gen_ai.agent.name`、`gen_ai.provider.name`、token 用量、cache token 等。
- **`chat`**：每次 LLM API 调用一个 span。`gen_ai.operation.name` 为 `chat`，携带模型、token 用量、`gen_ai.usage.cache_read.input_tokens`、`gen_ai.usage.cache_creation.input_tokens` 等。
- **`execute_tool`**：每次工具调用一个 span。`gen_ai.operation.name` 为 `execute_tool`，携带 `gen_ai.tool.name`、调用参数/结果（仅内容捕获）等。
- **`execute_hook`**：每次 hook 执行一个 span（视 harness 而定）。

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


## 10. 事件（Events）

Agent Host 管道**不拦截或持久化 events**。`vscode.agent_host.session.title_changed` 是 host 产生的**零时长 span**（在 trace 中），不是独立 event 信号。Provider-native events（如 `copilot_chat.session.start`、`copilot_chat.tool.call`）是否可用，取决于 provider 是否发出以及你的外部端点配置。


## 11. 内容捕获：`captureContent`

默认**不捕获** prompt、response、tool arguments、tool results 等内容。`chat.agentHost.otel.captureContent` 为 `false` 时：

- Span 仅包含元数据：模型名、token 计数、时长、工具名、`error.type`、cache token 等。
- **不可用**：prompt/response 文本、tool arguments/results、file paths、commands、raw server names、session title 文本。
- **可用**：trace 拓扑、延迟、工具调用次数/类型/错误、token 用量、**cache read/creation token**、资源属性。

`captureContent` 启用后，prompt、response、tool 内容会进入 span 属性；**session title span 也才会发出**。隐私敏感，不应在 span 发往共享 sink 的环境中开启。

企业 managed 设置支持 `captureContent` 配合 `lockCaptureContent: true`，在组织层面关闭内容捕获并阻止开发者本地重新开启。


## 12. 工具调用统计（`captureContent: false` 下）

**可以统计**。工具调用的元数据与内容正文在 OTel 设计中分离。

| 维度 | 可用信号 |
|---|---|
| 调用次数 | `copilot_chat.tool.call.count`；按 `gen_ai.tool.name`、成功/失败聚合 |
| 延迟 | `copilot_chat.tool.call.duration`；`execute_tool` span 时长 |
| 工具名 | `gen_ai.tool.name` |
| 工具类型 | `gen_ai.tool.type`（`function` / `extension` / MCP） |
| 调用标识 | `gen_ai.tool.call.id` |
| 错误 | `error.type` |
| 调用拓扑 | `execute_tool` span 在 trace 中的父子关系（是否由子代理触发） |
| MCP 工具 | `mcp_server_name_hash`、`mcp_tool_name`（原始 server 名需内容捕获） |

**不可用**（除非 `captureContent`）：tool arguments、tool results、具体 command、file path、raw MCP server name。


## 13. 缓存命中统计

**Agent Host 场景下，cache token 属性可用**，且独立于 `captureContent`。

- `chat` 和 `invoke_agent` span 携带 `gen_ai.usage.cache_read.input_tokens` 和 `gen_ai.usage.cache_creation.input_tokens`。
- 这些属性在 root span 和 per-chat span 上都存在，适用于所有 agent 类型（Copilot、Claude 等）。
- 缓存读取 token 从 provider 的上游字段映射（Anthropic `cache_read_input_tokens`、OpenAI `prompt_tokens_details.cached_tokens` 等）；**present whenever upstream field exists, including `0`; omitted only when absent**。

**计算方式**：在 trace 查询中按 `invoke_agent` 或 `chat` span 聚合：

```text
cache_read_tokens  = sum(gen_ai.usage.cache_read.input_tokens)
cache_creation_tokens = sum(gen_ai.usage.cache_creation.input_tokens)
input_tokens = sum(gen_ai.usage.input_tokens)
```

**注意**：`gen_ai.usage.input_tokens` 的语义因 provider 而异。例如 Anthropic 的 `input_tokens` **不包含** cache reads/writes；OpenAI 的 `prompt_tokens` 可能包含 cached tokens。计算命中率时分母需按 provider 实际定义确认，不要跨 provider 直接比较。

**显示为 0 的排查**：与 Copilot Chat 管道一致——可能是缓存未命中、provider 未报告、遥测链路未拿到、或 BYOK 端点未启用提示缓存。Cache Explorer 可作为交叉验证工具。


## 14. 与 Copilot Chat 管道的分工与是否同时统计

| 维度 | `github.copilot.chat.otel.*` | `chat.agentHost.otel.*` |
|---|---|---|
| 执行表面 | Local Copilot Chat：前台聊天、内联聊天、本地 CLI 包装器等 | Agent Host 会话：Copilot、Claude、Codex 通过独立 utility 进程运行 |
| 进程 | 扩展宿主内 | 独立 utility 进程，扩展宿主之外 |
| Trace 路由 | 扩展直接导出 | Pass-through 或 DB 模式；只有 trace 进入 loopback/SQLite |
| Metrics/Logs | 扩展侧处理 | Provider-native 直接导出，**绕过 Agent Host** |
| 覆盖代理 | 主要是 Local Copilot Chat | Agent Host 中的 Copilot、Claude、Codex |

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

> 注意：此前讨论提到“Agent Host 进程遥测不包含身份属性”来自扩展侧文档；Agent Host 自身的身份属性支持情况应以 provider runtime 的权威文档为准，不宜直接套用扩展侧结论。


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


## 18. 参考来源

- Agent Host OTel Pipeline：`src/vs/platform/agentHost/OTEL.md`
- Agent Host 设置与环境变量映射：`common/agentService.ts` / `agentHostStarter.config.contribution.ts`
- Agent Host 信号路由与资源标识：OTEL.md 的 Native Provider Signal Routing / Resource Identity / Distributed Trace Context
- OTel 在 VS Code 中的管道划分与架构说明
- Copilot OTel Metrics（指标命名与查询示例，跨管道参考）


以上为 **Agent Host 管道**的完整信息文档，结构与 Copilot Chat 文档对齐。核心记忆点：**独立进程、独立配置、只有 trace 进入 Agent Host 管道；metrics/logs 由 provider 直出；`captureContent: false` 下工具调用和缓存 token 仍可统计，但内容正文不可见；设置改动必须重启生效。**