# OTel 来源与解析

上级：[AGENTS.md](../../AGENTS.md) · 依据：`docs/Copilot Chat OpenTelemetry 监控信息文档.md`、`docs/Agent Host OpenTelemetry 监控信息文档.md`、`docs/三层关系总览.md`（含实测附录）。改解析规则前先读对应章节，**不要凭官方文档猜键名**。

相关：[结果指标](outcome-metrics.md)、[会话与 Agent 运行](sessions.md)、[存储](storage.md)

## 1. 来源归因

- 两条管线：Copilot Chat `github.copilot.chat.otel.*`、Agent Host `chat.agentHost.otel.*`；命令 `Token Tracker: 启用 OTel 上报` 同时写两套设置并保持 `captureContent=false`。
- Agent Host 设置需**完整退出并重开** VS Code 才生效，仅重载窗口会复用旧 host。
- 来源只用于接收端归因：路径优先（`/otlp/copilot-chat`、`/otlp/agent-host`），旧 `/v1/traces` 再按资源属性判定。Agent Host 带 `service.namespace=vscode.agent-host`，`service.name` 可能是 `github-copilot`、`claude-code`、`codex-app-server`、`vscode-agent-host`；Copilot Chat 通常无 namespace。
- Web 与扩展默认展示**综合**数据，不提供来源切换与来源标签。

## 2. 本地 span 库形态（实测，2026-11-06）

- 两份库的 `spans` / `span_attributes` / `span_events` DDL 逐字相同，主键是 `span_id` 单列；`turn_index` 恒 NULL。
- 扩展库不保存 resource attributes：`service.name` / `service.namespace` / `session.id` 都取不到，管线归因只能靠文件路径。
- `gen_ai.usage.cache_creation.input_tokens` 两条管线都不发；`cache_read` 只有扩展管线有（实测命中 99.03%）。Agent Host 库 `cached_tokens` 全 NULL，所以只用 Agent Host 的会话命中率必然为空——这是数据源缺失，不能渲染成 0%。
- `execute_hook` span 两条管线都是 0 条；hook 信号只在 span 的 **`events` 数组**（`github.copilot.hook.start/end` + `hook.type`/`duration`），本地库对应 `span_events` 表。
- Agent Host 库 `gen_ai.usage.reasoning.output_tokens` 属性存在但 `spans.reasoning_tokens` 列全 NULL，导入时列值为 0 必须回退到该属性（新旧键都试），否则推理 token 静默变 0。
- `github.copilot.tool.parameters.*` 一族（`edit_type` / `skill_name` / `mcp_server_name_hash` / `mcp_tool_name`）**实测均不存在**；实际键是 `github.copilot.external_tool.name`、`github.copilot.context.mcp_server_names`（哈希数组）、`github.copilot.context.skills`（明文清单）。按 `tool.parameters.*` 写查询一定查不到。
- 扩展管线 `input_tokens` **已含** cache_read（逐条 `cached ≤ input`），`TokenSemantics.UsesExclusiveInputTokens` 只认 anthropic/claude 是对的。
- `captureContent` 开启会让扩展库涨到 GB 级（实测 3.63 GB，`gen_ai.input.messages` 单键占 83%）且关闭后不回收；导入用 `DefaultTimeout = 5` 秒对这种库有超时风险。
- `localTraceDatabases()` 同时探测小写与大写两种 globalStorage 目录名（小写优先）。该目录一律是全小写 `<publisher>.<id>`（实测同目录有 `ms-python.python`、`redhat.java`、`jottingcn.tokentracker`）；Windows 大小写不敏感会掩盖这一点，Linux/macOS 按大写拼路径会找不到库。

## 3. 解析关键规则

- **会话 ID**：优先 `copilot_chat.parent_chat_session_id`（子代理归到父级/根会话），其次 `copilot_chat.session_id`、`gen_ai.conversation.id`、`copilot_chat.chat_session_id`；`call_` 开头的是工具调用 ID，必须忽略。子代理 `execute_tool` 会把会话字段写成父 call id，需沿 `parent_span_id` 回溯；启动时以 `subagent_sessions_v1` 修复历史行。
- **模型名**：优先 `gen_ai.response.model`（Auto 模式 request 可能是 `auto`）。Copilot Chat 是规范 ID（如 `gpt-4o-mini-2024-07-18`），只做小写与去注解，**不裁剪日期/版本/`-latest`**；Agent Host 带上游前缀（`DeepSeek/deepseek-flash`），只去最外层一段，保留 `provider/model`。价格表键同样只去最外层前缀，查找精确匹配优先。不做历史模型名回填。
- **TTFT**：优先 `copilot_chat.time_to_first_token`（毫秒），其次 `gen_ai.server.time_to_first_token` / `gen_ai.response.time_to_first_token`，最后 `gen_ai.response.time_to_first_chunk`。**实测 chunk 键单位是秒**（同 span 上 `496` ms 对应 `0.498`），`ParseTtftMs` 的 ×1000 换算是对的，两条管线都只靠它拿 TTFT。chunk 仅在无明确 TTFT 时回退，且 ≥90% 总耗时视为非流式/代理缓冲丢弃（`HasExplicitTtft` 不含 chunk 键，回退值始终受该规则约束）。
- **生成速度**：仅在有效 TTFT 时按「输出 token /（耗时 − TTFT）」计算；生成时间 < 50 ms、输出 < 5 token 或无 TTFT 显示 `-`。
- **去重**只使用文档化标识：span 以 `(trace_id, span_id)` 为主键，工具事件同来源按 `gen_ai.tool.call.id` 去重；**不要**按会话/模型/token/时间窗猜合并同来源 span。`gen_ai.tool.call.id` 存在空串，必须当缺失处理。
- 跨管线没有共享唯一 ID（`gen_ai.response.id`、`trace_id`、`span_id` 互不相通），combined 只能用「会话 + 规范化模型 + 输入/输出 token + 主/辅助 + span 时间区间重叠」匹配，派生行写 `otel_source='combined'`，原始记录保持不变并懒重建。费用不继承任一来源估算值，按合并后 token 与配置价格重算。
- 只统计 `gen_ai.operation.name=chat` 的 span 作为调用，避免与 `invoke_agent` / `execute_tool` 重复计数。
- 子代理 `invoke_agent` 写 `agent_runs`（父 span 为对应 `execute_tool`），其工具调用计入根会话工具统计；子代理 chat span 是真实 LLM 调用，计入会话调用与费用，但 `agent_runs` 汇总 token 不重复计入 chat 汇总。
- **`agent_runs.is_root` 不能按“父 id 为空”判断**：根轮次的 `invoke_agent` 有父（宿主 session span，从不入库），实测 91 行里 85 行被判成子代理，会话页把每一轮都标成 subagent。`GetAgentRuns` 改为读取时按「父 span 是否在本地 `session_events` 中」重算，与拓扑同一口径（历史行自动纠正）。
- **agent 名称回退到 span 名**：Agent Host 只对部分 run 设置 `gen_ai.agent.name`，其余靠 span 名 `invoke_agent <name>`（如 `invoke_agent explore`）。解析时写入，读取时再从 `session_events.name` 兜底一次，历史行也能显示。
- **模型回退到同 trace 的主模型**：`invoke_agent` 本身不带 model，`GetAgentRuns` 用同 `trace_id` 下 chat span 的众数模型填充，否则整列显示 `-`。
- **token/cache 来自这个 run 自己的调用**：`invoke_agent` span 的 token 永远没有 cache（Agent Host 不发 cache token），所以直接用会把缓存列显示成 0。现在的规则是：
  - **根 run**：取时间窗内 `otel_source='combined'` 的 chat 汇总（combined 才有 cache，且它的行不带 parent、trace 也是另一条管线的，只能按「会话 + 时间窗」关联），结果与该 run 的整轮调用一致；
  - **子代理 run**：只取 `parent_span_id = 自己的 chat`，否则子代理那个极短的窗口会吞掉父轮尾部的调用；
  - 窗口内没有 combined（本地导入、单管线）时回退到「自己的直接子 span」。
- **不再有对话轮次**：会话页只保留 `agentRuns`（见[会话与 Agent 运行](sessions.md)）；原来的轮次分桶已删除，避免与客户端气泡口径互相矛盾。
- 只有 `gen_ai.operation.name=chat`（或 `name` 以 `chat ` 开头）且有 `gen_ai.usage.input_tokens` 的 span 才成为调用行；缺 token 的 chat span 会被丢弃。

## 4. 工具、审批、Hook

- 工具统计按 `gen_ai.tool.name` 汇总；工具事件同时保存 `gen_ai.tool.type`（**实测全为 `function`，不能用来区分 MCP**）与可选的 `edit_type` / `skill_name`。表格里因此**不再显示 MCP Server / MCP 工具 / Agent 三列**——实测这些属性不存在，显示出来永远是 `-`。
- 耗时列用**中位数**而不是平均值：`openBrowserPage` 这类工具会被保持打开数小时（实测单条 51 432 s），平均值被拉到 30 分钟，中位数仍是秒级。最大值保留，作为最坏情况。
- 工具名统一用 `ToolNameResolver`（Core）显示：上游把扩展宿主注册的外部工具名发成 `SHA-256(工具名)`，解析器用内置公开工具清单现算哈希反查（不维护哈希表），`ToolCallRow.ToolDisplayText` 与 Trace 节点 `label` 共用；扩展只读服务端返回的 `toolDisplayText`，**不要在客户端再维护一份哈希表**。MCP server 哈希保持不可解读。

### 4.1 审批（permission）

**审批没有“自动/人工”显式标记**。Agent Host 把每次审批写成独立 span `permission`（`operation_name` 为 NULL、只有 span 名 `permission`），属性只有：

| 属性 | 实测取值 |
| --- | --- |
| `github.copilot.permission.kind` | `read` / `shell` / `write` / `url` / `custom-tool` |
| `github.copilot.permission.result` | **只有 `approved` 与 `denied-interactively-by-user`** |
| `github.copilot.permission.tool_name` | 文档描述为“外部工具 SHA-256，内置工具明文”，**实测本机 47 条全部缺失**；工具名改由父 `execute_tool` 借用 |

因此只能读现象：

| 现象 | 解读 |
| --- | --- |
| 没有 permission span | 该工具没请求审批（策略放行） |
| 有 span 且耗时极短 | 瞬时通过 |
| 耗时到秒级或更长 | 在等人（实测最长 ≈ 14.3 h） |
| `denied-interactively-by-user` | 显式人工拒绝 |

不要从 `approved` 反推是谁点的通过。

实现约束：

- `InferOperation` **必须按 span 名优先判 `permission`**，否则 `permission.tool_name` 会让它被当成 `execute_tool`。
- `denied` 记为 `error_type=denied`。
- 存 `session_events.permission_kind` / `permission_result`（`SchemaVersion=3` 的附加迁移）。
- 拓扑 label 为 `kind · result · tool`（如 `shell · approved · powershell`），`denied` 显示红色；事件统计里独立成「审批」一行。
- 本地库导入时 permission span 的 `operation_name` 为 NULL，`LocalTraceImporter` 需要按 span 名 `permission` 提升为 operation。
- **`permission.tool_name` 实测全部为 NULL**（本机 47 条 permission 全都没有该属性，`tool_name` 列也是空），所以工具名必须从**父 `execute_tool`** 借用（`AttachApprovalTools`，在排序前按 id 索引父节点）。父 span 尚未入库时（宿主先导出审批、后导出工具）暂时只显示 `kind · result` 且 depth 为 0，父 span 到达后由拓扑的尾部窗口重载补齐。
- 给 permission 节点的 `Tool` 字段赋值用于去重：已有工具名时不再追加，避免出现 `… · powershell · powershell`。

### 4.2 Hook

- Hook 事件是对外 span 的 **OTel `events` 数组**（`github.copilot.hook.start/end`，父 span 是 `invoke_agent`），两条管线都没有 `execute_hook` span。旧实现只解析 span 本身、忽略 `events`，所以在线数据的 Hook 面板恒为 0——**必须读 `span.events`**（`ParseSpanEvents`）。
- 本地导入另有一条路径：Copilot Chat 库的 `span_events` 表里同样是这两类事件，`LocalTraceImporter` 读取它们。
- **只取 `end`**（`start` 不入库否则次数翻倍，实测 1988 start vs 1976 end）。
- `session_events` 主键是 `(trace_id, span_id)`，一条 span 只能占一行，所以 hook 行 `span_id` 用 `{spanId}#hook#{hook.invocation_id}` 合成（缺 id 用事件序号），**不要用真实 span_id**，否则同一条 invoke_agent 上多个 hook 会被 `INSERT OR IGNORE` 吃掉。
- 耗时只认 `end` 上的 `hook.duration`（单位**秒浮点**，解析时 ×1000），不要用 start/end 时间差配对（实测只差 0~1 ms）。
- 历史行补不回来：OTLP 原始报文不落库，`events` 在入库时被丢弃后就只剩聚合结果，修好解析只对之后收到的数据生效。

## 5. 本地导入（`POST /api/import-local`）

- 只读导入 chat span、工具/Hook 事件与 `invoke_agent`，按 `(trace_id, span_id)` / tool call id 去重。
- 导入 chat span 必须复用在线解析的同一套规则（`ParseSessionId`、`ParseTtftMs` + `HasExplicitTtft` 的 ≥90% 丢弃、`ErrorTypeFrom`、provider/agent/finish_reasons/service.namespace），字段来自 `spans` 表的 `ttft_ms`/`agent_name`/`status_code` 列与 `span_attributes` 批量读取；只按列拼 session_id 会丢掉子代理归属和 TTFT、生成速度。
- `gen_ai.usage.reasoning.output_tokens` / `cache_creation.input_tokens` 一律从属性读而不是只读列。
- 导入结果没有 metrics，因此会话结果卡片必然为空（见[结果指标](outcome-metrics.md)）。