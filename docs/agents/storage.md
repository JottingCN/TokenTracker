# 存储、聚合与迁移

上级：[AGENTS.md](../../AGENTS.md) · 相关：[OTel 解析](otel-parsing.md)、[Trace 拓扑](trace-topology.md)、[结果指标](outcome-metrics.md)

## 1. 存的是投影，不是原始报文

**`spans` 只有 34 个固定列，没有任何 attributes / JSON / payload 列**，上游属性解析完即丢弃：

```
trace_id, span_id, parent_span_id, name, start_time_ms, end_time_ms,
service_name, service_namespace, scope_name, otel_source,
model, session_id, is_main,
input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, reasoning_tokens,
ttft_ms, nano_aiu,
cache_cost_cny, uncached_cost_cny, output_cost_cny, reasoning_cost_cny, hit_rate, cost_cny, pricing_period,
error_type, provider_name, agent_name, subagent_name, window_session_id,
finish_reasons, received_at_ms
```

费用列是本地计算值，不是上游给的。系统信息里的「原始 / 综合」是 **`otel_source != 'combined'` 与 `= 'combined'` 的行数**，两者都是投影，不是原始报文。

| 上游信号 | 落到 | 形态 |
| --- | --- | --- |
| `chat` span | `spans`（`otel_source=copilot-chat` / `agent-host`） | 每行一次 LLM 调用 |
| 跨来源合并结果 | `spans`（`otel_source=combined`） | 派生行，可随时重建，拓扑与统计需按需排除 |
| `execute_tool` / `invoke_agent` / `create_agent` / `execute_hook` / `permission` | `session_events` | 事件投影 |
| `invoke_agent` 汇总 | `agent_runs` | 代理运行投影 |
| `copilot_chat.*` 结果指标 | `metric_points` | 数值 + `attrs`（见下） |
| 原始 traces / logs | **不保存** | — |
| 原始 metrics payload | 仅 `TOKENTRACKER_CAPTURE_OTEL_METRICS=1` 时写 `otel-metrics/*.json` | 诊断用，默认关闭 |

`metric_points.attrs` 是唯一保留部分原文的列：按 `key=value` 换行拼接，只留 `gen_ai.*` / `copilot_chat.*` / `github.copilot.*` / `server.*` / `error.*` / `service.*` 前缀，截断 4000 字符，并显式排除 `gen_ai.input.messages`、`gen_ai.output.messages`、`gen_ai.system_instructions`、`gen_ai.tool.definitions`、`gen_ai.tool.call.arguments`、`gen_ai.tool.call.result`、`copilot_chat.hook_input`/`hook_output`、`copilot_chat.repo.remote_url`、`github.copilot.tool.parameters.command`/`file_path`/`mcp_server_name`、`user.name`、`process.user.name`、`host.name`、`vscode.agent_host.session.title`/`uri`。

**直接后果**：新增统计维度必须先加列 + 提升 schema 版本（历史行补不回来）；原始属性只在两份本地库里存在约 7 天 / 100 会话，且 `import-local` 重导入仍写同一套投影列。不要设计“从库里读原始 attributes 回填”的方案。

隐私约束：即使 `captureContent: true`，也不写 prompt/response、工具参数与结果、单次调用的 file path、command、raw MCP server name、会话标题、身份字段。唯一例外是 `session_workspaces` 的**会话工作区根目录**——它来自本机 VS Code 元数据（AHP / `agent-host.db` / `workspace.yaml`），不来自遥测内容，仅用于工作区归属匹配。

## 2. 查询与聚合不变量

- **时间窗必须在 SQL 聚合之前下推**：`GetSessionEvents` / `GetSessionTools` / `GetHookSummaries` 都接受 `fromMs`/`toMs`。按聚合后的 `lastSeenMs`（组内最大值）过滤会让“今天用过的工具”显示全部历史计数（实测 `range=0` 报 1881 次而当天真实 64 次）。
- 会话页所有统计都遵循标题栏范围：调用与 Agent 运行按 `startTimeMs`，事件/工具/Hook 按上述 SQL 窗口，outcome 指标同样传区间。VS Code Tip 也按 `tokentracker.statsRange` 传 `range` 给 `/api/session`——**聚合不能靠客户端过滤**。
- `includeCalls=false` 时必须走 `SpanStore.GetSessionCallSamples`（`CallSample` 投影），不要 materialize 数千个 `CallRow`；`SummaryAccumulator` 的算术只保留一份实现（`Add(CallRow)` 转调 `Add(CallSample)`）。会话快照从约 880 KB 降到约 36 KB。
- combined 行按「脏会话 + 会话级时间窗」增量重建，窗口沿重叠区间扩展，超过 12 小时或 4 轮不收敛才回退该会话全量；**不要**恢复「按会话整库重算」（单个新 span 曾要重算整个 5k span 会话，实测 208 ms/次）。无 `session_id` 的辅助调用按批次时间窗增量重建，**不要**恢复「辅助调用批次 → 全表重建」（实测 2 万 span 全量读取 3~12 秒 CPU，窗口重建 0.16 秒）。窗口查询写成可用 `ix_spans_start` / `ix_spans_end` 的 OR 形式，`COALESCE(end_time_ms, start_time_ms)` 会退化成全表扫描。
- 调用明细必须服务端分页（`total`/`offset`/`limit`，单页上限 500），不要重新引入固定 2000 条截断；文本检索在服务端匹配会话标题（元数据）、模型与会话 ID 后直接分页。
- 跨请求缓存必须有上界：`ToolNameResolver.Cache`（4096 后清空）、`SessionMetadataProvider._cache`（2048）、`DashboardService._outcomeCache`（32）。WS 接收缓冲上限 256 KB，超过即断开。
- `SessionMetadataProvider` 的 `workspace.yaml` 缓存被 HTTP 与 WS 快照并发读写，**必须持锁**（普通 `Dictionary` 并发写入会抛 `concurrent update ... corrupted its state` 并 500）。`AgentHostTitleReader`、`CopilotChatTitleReader`、`AhpTitleClient` 同样内置锁，新增缓存保持该约定。
- 辅助调用（无 `session_id` → `is_main=0`）是**推断**，不是 Copilot 显式标记。扩展管线可用 `gen_ai.agent.name` 后缀增强（实测 `copilotLanguageModelWrapper/thinkingTitle` 1084、`/toolRiskAssessment` 7、`progressMessages` 2、`title` 1，主调用为 `copilotLanguageModelWrapper`）；Agent Host 的 `chat` span **没有**该属性，增强只能按来源分别生效。辅助调用并入 Dashboard 汇总与按模型统计，会话列表只含主调用，调用明细展示全部并按来源分别计数。
- 仅事件、没有 chat span 的会话也会进入会话列表（调用列显示 `0 (N 事件)`）。
- 最值口径：TTFT「最快」取 `MinTtftMs`，生成速度「最快」取 `MaxTokensPerSecond`，两者只统计 `error_type IS NULL` 的调用。平均值右上/右下角标注用 `flex` + `align-items: stretch` + `justify-content: space-between`，两侧用原生 `span`（antd `Typography` 会重置所需 margin）。

## 3. 迁移与版本

- 本地库只存需要的数据：span 投影、`session_events`、`agent_runs`、outcome `metric_points`、`window_session_links`、`session_workspaces`、价格、节假日、设置。不保存原始 attributes、日志事件、可由 span 推导的指标；会话标题只查询时解析，不入库。
- schema 变更使用幂等迁移（`EnsureColumn` / `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS`）。**建索引必须放在列迁移之后**：索引引用尚未添加的列会让 DDL 批次中断、后续表缺失并在启动时崩溃。
- **数据库必须向前兼容**：升级只能附加式迁移，**禁止** `DROP TABLE` / `DROP COLUMN` / 清库重建 / 版本不匹配时重建或清空。旧库原地补齐，任何一行用户数据都不能因版本变化被删除。
- 版本标记用 `PRAGMA user_version`（`SpanStore.SchemaVersion`，当前 **3**）。新增列/表时把常量 +1 并在 `Initialize` 追加幂等语句；启动时写当前值。读到 `user_version > SchemaVersion`（旧程序打开新库）进入兼容模式：只告警、不改库、不删数据，系统信息里显示 `schemaFromNewerBuild`。
- 唯一允许的删除路径是数据保留期（`DeleteOlderThan`，由 `retention_days` 触发）与按模型删除设置。**不要恢复 `SlimStorage`**（它会删 `metric_points`、DROP 列与 `otel_log_events` 表）。
- 工作目录来源（AHP `workingDirectories` / `agent-host.db` / `workspace.yaml`）大多要求 Agent Host 在线。服务端解析成功时把 `session_id → workspace_path` 写 `session_workspaces`，解析不到回退读表，因此 AHP 掉线后归属不丢；`DeleteOlderThan` 会清掉已无 span 的会话缓存。

### 迁移检查清单

1. `CREATE TABLE IF NOT EXISTS` / `EnsureColumn` 只加不删。
2. 所有索引写在列迁移之后。
3. `SchemaVersion` +1；`Initialize` 追加语句；启动写 `user_version`。
4. 用旧库副本验证：`user_version` 变更、行数不变、新旧接口都能读。