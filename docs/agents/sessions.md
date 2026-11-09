# 会话元数据与轮次

上级：[AGENTS.md](../../AGENTS.md) · 相关：[存储](storage.md)、[Trace 拓扑](trace-topology.md)、[扩展 UI](extension-ui.md)

## 1. 标题优先级

1. `agent-host.db` 中 `titleSource=user`
2. `workspace.yaml` 中 `user_named: true`
3. Agent Host AHP `listSessions` / `session/titleChanged`（session id 可关联时）
4. 扩展宿主 `chat.ChatSessionStore.index`（`state.vscdb`，只读）
5. `agent-host.db` 自动 / agent 摘要
6. `workspace.yaml` 自动名称

会话标题只在查询时解析，**不入库**。

## 2. 工作区路径

优先 `agent-host.db` 的 `working_directories` / `launch_working_directories`，其次 AHP `workingDirectories` 与 `ChatSessionStore` 索引的 `workingDirectory`，再回退 `workspace.yaml` 的 `cwd`，最后回退 `session_workspaces` 缓存（`SessionWorkspaceCache` 在解析成功时批量写入，`DashboardService` 每次构建 payload 后 flush）。

`agent-host.db` 查不到子代理会话工作目录时，沿 `chats_v2` 的 `owner_session_uri` / `parent_chat` 回溯父会话，且两种形态（`chats_v2.working_directories` 与 `session_chat_state.*_working_directories`）都要接受。状态栏按该路径匹配当前 VS Code 工作区。

## 3. 元数据刷新

- 扩展监听 `workspace.yaml`（500 ms 去抖）与 `agent-host.db*`（500 ms 去抖、15 秒节流）；AHP 已连接时暂停这些监控（标题与工作目录由 AHP 推送并触发广播），断开后自动恢复，状态写入 `extension.log` 的 `metadata watchers paused/resumed`。
- AHP 客户端后台读 `local-endpoint/entries`，经命名管道/UDS/TCP 连接，订阅根通道并处理 `listSessions`、`session/titleChanged`、`root/sessionSummaryChanged`；断线 30 秒重连，**仅用于标题，不读对话内容**。协议层用官方 `Microsoft.VisualStudioCode.AgentHostProtocol` SDK，自定义 transport 只负责本地 WebSocket 握手。
- `DashboardPayload.ahp` 返回 `state / connected / sessions / items`，状态变化触发实时推送。
- 变化后调用 `POST /api/metadata-changed` 广播新快照；旧服务不支持时扩展回退为本地重新订阅。

## 4. 对话轮次（turns）

- 轮次边界是 Agent Host 的**根 `invoke_agent`**：每条用户消息发一个根 invoke_agent；子代理的 invoke_agent 挂在 `execute_tool` 下，`GetSessionTurns` 用「父 span 不在本地 `session_events` 中」把它们排除。它同时返回每轮的 `span_id`，因为工具的 `parent_span_id` 指向它就是「这一轮」的唯一标识。
- 调用与工具事件按开始时间落桶；`EndTimeMs` 取该轮**最后一次调用结束或工具结束**的时间（`GetSessionToolRuns` 返回 start+end），**不是**下一轮开始时间，否则会把用户读消息的空闲算进耗时。
- `calls == 0 && toolCalls == 0` 的轮次会被丢弃并**重新编号**（只有用户消息、宿主没产生工作）。
- 纯 Copilot Chat 数据没有轮次标记，`turns` 为空是预期；首个轮次之前的调用不计入任何轮次。
- **进行中的轮次**：根 `invoke_agent` 只有在整轮结束时才导出，所以运行中的那一轮永远不会出现在边界列表里。`GetSessionTurns` 同时返回每轮的 `end_time_ms`，最后一个已知轮次用**自己的结束时间**封口；它之后的活动（有调用/工具但还没有边界）生成一条额外的 `InProgress=true` 行并排在最后。否则运行期间状态栏会一直显示上一轮，且本轮的调用被算进上一轮。会话页用「进行中」标签、Tip 用 `live` 标记、状态栏显示 `第N轮 M步（进行中）`。
- 汇总一致性：`summary.calls` 必须等于 `turns` 的 calls 之和（同一 `CallSample` 投影），`InProgress` 行也计入。
- **轮次尾巴按归属拆，不按时间猜**：每个 `execute_tool` 的 `parent_span_id` 就是它所属轮的根 `invoke_agent`。最后已知轮之后剩下的活动分两种：父 id 属于某个已知轮（或为空）→ 是该轮**自己的尾巴**（宿主先关 `invoke_agent` 再导出最后几个工具 span），必须并回该轮并延长 `EndTimeMs`，否则会把一轮拆成 52 + 6；父 id **不属于任何已知轮** → 是一轮还没导出的根（正在运行的那一轮），要在它第一个工具时就开 `InProgress` 行。旧实现只看「有没有新模型调用」，于是运行中的轮次要等首个 chat span 落地才出现，期间它的步数全算到上一轮——客户端在第一个工具调用时就开新一轮，所以两边的步数对不上。
- **steering 消息无法拆成独立轮**：运行中插话（客户端日志里 `delivery: "steering"`）不会产生新的根 `invoke_agent`，宿主把它并进当前轮继续跑。客户端按「每条用户消息」分气泡、各算步数，而 OTel 里它们同属一轮（实测 4 次插话，26 个工具并入前一轮）。同理，子代理的 `user.message` 在客户端也是一个气泡，但它下面挂的是子代理的 `invoke_agent`（父存在，被排除出轮次列表），所以 `turns` 条数会比客户端的气泡少。
- **入参顺序不可信**：调用明细走 `GetCalls` 的 `ORDER BY start_time_ms DESC`（分页需要最新在前），而样本投影是升序。`BuildTurns` 必须先归一化为升序再单向前进，否则所有调用会被塞进最后一轮——实测最后一个轮次显示 2690 次调用与整会话费用（回归用例在 `scripts/smoke-server.mjs` 的两轮场景）。
- 消费方：`SessionPayload.turns`（会话页「对话轮次」卡片）与状态栏 `{turn}` / `{step}` / `{turns}`（见[扩展 UI](extension-ui.md)）。
- **`turns` 与 `agentRuns` 不是一个东西，共用同一个边界 span**：一条用户消息对应一个根 `invoke_agent`，它既是轮次边界，也是「主代理运行」那一行。差别在三点：
  1. `agentRuns` 还包含**子代理**（`invoke_agent explore` 等，挂在 `execute_tool` 下），轮次明确排除它们；
  2. 轮次会丢弃 `calls == 0 && toolCalls == 0` 的零活动轮并重编号，`agentRuns` 保留（实测 64 轮 vs 68 行：65 主 + 3 子，另有 2 条零 token 的主代理行被轮次丢弃）；
  3. 轮次聚合调用/工具/token/费用，`agentRuns` 报的是这个 run 自己发出的调用（根 run 取轮内全部调用、子代理只取自己的），缓存来自 combined 视图。
  所以会话页两个计数**不应该相等**；轮次行上直接显示「主代理 N · 子代理 M」说明差额来源，Agent 运行卡片保留 per-run 明细。
- **轮次不落库、全部实时算**：没有 `turns` 表，也没有快照缓存，每次 `/api/session` 都重跑 `GetSessionTurns` + `GetSessionToolRuns` 并在内存里分桶。因此改算法对**全部历史会话立即生效**，不需要迁移或回填（实测功能上线前的 11-03 会话也能算出轮次）。代价随数据量线性增长：3100 次调用 / 3128 次工具 / 62 轮时，聚合路径 `includeCalls=false` 约 210 ms，带调用列表约 880 ms（瓶颈是序列化，不是分桶）。
- 反过来，**入库时就算好的字段改代码不会修历史行**：`spans` / `session_events` 是投影，OTLP 原始 attributes 不存（`metric_points.attrs` 除外），`permission_kind`、`tool_name` 归属、模型规范化、`call_id` 都是解析时写入的，历史行只能靠重新导入原始报文才能纠正。