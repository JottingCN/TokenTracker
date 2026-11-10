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

## 4. Agent 运行（agentRuns）

会话页与状态栏只有一张「Agent 运行」表，**不再提供对话轮次**：轮次口径与聊天客户端的气泡不一致（steering 插话会并入当前轮），继续维护两套数字只会互相矛盾。

- 一次运行 = 一个 invoke_agent span。根 invoke_agent（父 span 不入库）是主代理运行，每条用户消息一个；挂在 xecute_tool 下的 invoke_agent 是子代理运行。
- **is_root 读取时重算**：不能按「父 id 为空」判断，根轮的父是宿主 session span（不入库），旧实现把 85/91 判成子代理。规则是「父 span 不在本地 session_events 中即为根」，与拓扑同一口径。
- **名称回退 span 名**：Agent Host 只对部分 run 设置 gen_ai.agent.name，其余取 invoke_agent <name>；仍为空时界面显示「主代理」。
- **模型回退**：invoke_agent 不带 model，先取运行窗口内 chat 的众数模型，再退回整条 trace 的众数。
- **token / 缓存 / 费用**：invoke_agent 自身的 token 没有 cache（Agent Host 不发），直接用会让缓存列恒为 0。现在的规则：
  - 根 run：窗口内 otel_source='combined' 的 chat 汇总（combined 才有缓存；它的行不带 parent、trace 也是另一条管线的，只能按「会话 + 时间窗」关联），结果与整轮一致；
  - 子代理 run：只取 parent_span_id 等于自己的 chat，避免子代理的短窗口吞掉父轮尾部调用；
  - 窗口内没有 combined（本地导入 / 单管线）时回退到「自己的直接子 span」。
- **列表字段**：主代理/子代理标签、名称、模型、调用次数（窗口内 chat 数）、输入/缓存/命中率/输出/思考 tokens、耗时、开始时间、估算费用、错误。每列都可与调用明细的对应列对上。
- **不落库、实时算**：gent_runs 行是入库投影，名称/根标记/模型/token 都在读取时补齐，因此改算法对历史会话立即生效。
- 反过来，**入库时就算好的字段改代码不会修历史行**：spans / session_events 是投影，OTLP 原始 attributes 不存（metric_points.attrs 除外），permission_kind、	ool_name 归属、模型规范化、call_id 都是解析时写入的，历史行只能靠重新导入原始报文才能纠正。