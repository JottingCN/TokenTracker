# 结果指标（outcome metrics）

上级：[AGENTS.md](../../AGENTS.md) · 相关：[OTel 解析](otel-parsing.md)、[存储](storage.md)

从 `metric_points` 按查询时累计 delta 聚合，**只能来自在线 OTLP `/v1/metrics`**：两份离线 span 库里没有任何 metrics，所以 `import-local` 导入的会话结果卡片必然为空。

## 1. 唯一真实来源

Copilot Chat 扩展宿主 `extensions/copilot/src/platform/otel/common/genAiMetrics.ts` 的 `GenAiMetrics`，由 VS Code UI 事件触发：

| 触发点 | 指标 |
| --- | --- |
| `extension/conversation/vscode-node/userActions.ts` | 接受/拒绝、hunk action、copy/insert/apply、点赞 |
| `intents/node/editCodeIntent.ts`、`agentIntent.ts` | 编辑与代理意图 |
| `chatSessions/vscode-node/cloudBackendTelemetry.ts`、`copilotCloudSessionsProvider.ts`、`copilotcliSession.ts` | 云/CLI 会话 |
| `codeMapper/codeMapperService.ts` | 存活率等 |

Agent Host 只转 trace，provider-native metrics 直出，**不产生 `copilot_chat.*` 结果指标**——只用 Agent Host 时卡片为空是预期行为。指标名以 `extensions/copilot/docs/monitoring/agent_monitoring.md` 的 “Agent Activity & Outcome Metrics” 为准；`copilot_chat.cloud.pr_ready.count` 在上游代码与文档中都不存在，**不要重新加入 allowlist**。

## 2. 归属

顺序：

1. 自带 `gen_ai.conversation.id` / `copilot_chat.session_id` → 直接归属。
2. 带 `gen_ai.tool.call.id` → 按 `session_events.call_id` 归属。
3. 否则记 `window_session_id`，仅当该窗口在 `window_session_links` 中**只有一个** chat session 时归属（`attribution_method=window`，置信度 0.6）；多会话窗口保留 `window-only`，不要强行分摊。

Dashboard 全局指标只按原始 metric point 统计一次。

> **实测（0.73.0 bundle）**：`incrementCounter` 只做 `counter.add(value, attrs)`，编辑类指标（acceptance / chat_edit / LOC / action / feedback / summarization / survival）既不带 `gen_ai.conversation.id` 也不带 `gen_ai.tool.call.id`，前两级永不命中。每会话卡片只有“窗口唯一会话”一条路，多会话窗口下为空属预期，别去改匹配阈值。这些指标也不带模型/provider 维度，不能按模型拆。

## 3. 采样去重

**累计计数器每 10 秒重发同一 series**，`InsertMetricPoints` 为控行数只留每分钟一行：区间内新点必须 **UPDATE** 已有行的 value/count/sum/min/max/end_time，不能丢弃——丢一次会让进程退出前最后 ≤60 秒的增量永久失踪（接受编辑这种低频事件最先受害；回归用例 `SpanStoreTests.MergesCumulativeSamplesInsideTheSamplingInterval`）。早于已存点或时间戳重复的采样直接忽略。

## 4. 渲染

`OutcomeSummary` 里聚合的每个字段都必须在 `OutcomeCard` 渲染（或明确标注不展示）：`HasData=true` 却没有任何一行就是「空卡片」bug。当前覆盖：

编辑接受、file edit outcome、summarization、LOC、存活率、反馈、用户操作、PR/云会话、云操作、Agent 编辑、工具指标。

## 5. allowlist 边界

上游会发但 allowlist 未收录、当前静默丢弃的有：`copilot_chat.session.count`、`agent.turn.count`、`agent.invocation.duration`、`time_to_first_token`（轮次/TTFT 用 span 属性即可）。`tool.call.count` 带 `success` 布尔，聚合目前不区分成功失败。

口径细节：

- `lines_of_code.count` 只在 **accepted 的 hunk** 上累加（`inline_chat` 接受只计 `edit.acceptance.count`，不计 LOC）。
- `agent.summarization.count` 还有 `skipped` 结果，聚合时忽略（与文档的 applied/failed 一致）。