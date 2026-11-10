# Trace 拓扑

上级：[AGENTS.md](../../AGENTS.md) · 相关：[存储](storage.md)、[会话与轮次](sessions.md)

接口：`GET /api/session/{sessionId}/trace?budget=&cursor=&range=|from=&to=` → `{ nodes, hasMore, nextCursor, total }`。

## 1. 分块顺序（硬约束）

- **必须按 FIFO（开始时间正序）分块 + 时间游标**：首块最早，滚到底用 `nextCursor` 追加更新的块。
- **不要**按“整条 trace 不切开”切块：实测一条 trace 可跨 15.4 小时，它必然与其他 trace 交错，按 trace 切会让块之间时间倒流、观感错乱。
- **不要**恢复页码分页器（曾出现 96 页、每页 30 行的错误实现）。
- 分块在**全局排序 + 合并 + 深度计算之后**切片，切块不改变顺序与父子关系。
- 时间范围过滤（`fromMs`/`toMs`）同样在合并之后、切片之前做：窗口外的祖先变成 `isContext` 行，`total` 只数窗口内的行（`MaxContextAncestors=64`）。
- 块首缺失的祖先以 `kind=context` / `isContext=true` 重复带上（无 token、无费用、无耗时），`total` 不计它们，缩进才不会断。
- `budget` 默认 400（实测首块 172 KB / 44 ms，整会话一次要 1.86 MB / 179 ms）。

## 2. 节点构成

- 拓扑始终**排除 combined 派生行**；综合视图在时间区间重叠时合并跨来源重复的 chat 节点并重挂子节点。
- 按开始时间排序、用 depth 缩进展示层级；chat 节点显示输入/输出/缓存 token。不要引入 git graph。
- chat 节点的 label 用**规范化后的 `spans.model`**，不要用 OTel span 名：Agent Host 的 span 名是 `chat DeepSeek/deepseek-flash`（带供应商前缀），而树已经把 operation 单独渲染成标签，用 span 名会显示成 `chat chat DeepSeek/deepseek-flash`，且与状态栏/表格的模型名不一致。
- 事件节点包含 `execute_tool` / `invoke_agent` / `create_agent` / `execute_hook` / `permission`；permission 行的 label 为 `kind · result · tool`，hook 行的 label 只用 hook 类型（`preToolUse`），operation 标签本身已经写了「Hook 执行」（见 [OTel 解析](otel-parsing.md)）。
- **拓扑即「全信号树」**：工具、审批、子代理、hook 都在同一棵树里，只要它们在 `session_events` 里有行。工具/审批/子代理来自 OTLP trace，**hook 只能来自本地库导入**（OTLP 导出不含 span events，实测 0 条），没导入时不会有点；不要为它伪造节点。
- 深度由 parent 链计算并记忆化；父节点不在本块时靠 `isContext` 行补齐，不要用相邻行推断层级。
- **层级必须是 chat → execute_tool → permission**：Agent Host 把 `execute_tool` 报在轮次的 `invoke_agent` 下、与 `chat` 平级，原始父链是 `invoke_agent → chat` 与 `invoke_agent → execute_tool`，渲染出来工具像是模型调用的兄弟。`NestedToolsUnderChat` 把工具重挂到**同一轮内最近的前一个 chat 节点**。
  **不能用「同 trace 的 chat」判断**：两条管线的重复 span 合并时 chat 会被丢弃或 remap，实测存在「85 个工具的 trace 里 0 个 chat」，所以必须按整会话的起始时间顺序取最近的前一个 chat，并限制在最近一个**根** `invoke_agent` 之后。
  **父节点本身被合并丢弃时同样要重挂**：实测 77 个工具指向已不存在的 `invoke_agent` 行，旧逻辑把它们留在 depth 0 与模型调用并列。只保留「父节点存在且不是 `invoke_agent`」的情况（嵌套工具、其他 agent）；父节点读不到按 `invoke_agent` 处理。重挂后若目标 chat 落在更早的块，会被 `PrependContextAncestors` 补成 context 行，缩进才不会断。
  **子代理的工具不要重挂**：子代理的 `invoke_agent` 挂在 `execute_tool` 下，它的工具父节点就是这个 agent；若也按「父是 invoke_agent 就重挂」处理，会把子代理的工作拍平到上一层的 chat。判定方式是按 id 看父级 `invoke_agent` 的父节点是否为 `execute_tool`；同时子代理的 `invoke_agent` 不能重置「最近 chat」窗口。
- 工具名显示统一走 `ToolNameResolver`（服务端），前端不要再解析哈希。

## 3. 前端（`web/src/components/TraceTree.tsx`）

- 虚拟滚动、阻止滚动链到页面、支持全屏；接近底部才拉下一块，追加不移动视口，因此**不需要** scrollTop 补偿；块短于视口时自动继续取。
- 追加时**按节点 id 去重**（块首的上下文行会重复上一块的真实行），否则 antd Tree 拿到重复 key。
- 用 ref 同步守卫 `loadingRef` 防重复请求（`loadingMore` state 同一帧里拦不住两次 scroll）。
- 全部行加载完（`hasMore=false`）后的自动刷新**不能用「最后一个节点的游标」追加**：span 会晚于开始时间更晚的行到达（父 span 在子 span 之后导出，实测本库 2381 条 span、779 对父子如此），纯递增游标永远取不到它们，结果就是末尾几行缺父节点、缩进为 0。
- 正确做法是**尾部窗口重载**：保留前 `length - 240` 行不动，用 `节点[keep-1]` 的 `startTimeMs:id` 作游标重取尾巴，再按 id 合并（服务端会重新算这一段的 depth）。这样既不会把用户弹回顶部，也能补齐晚到的父节点。
- 合并时**真实行不能被 context 行覆盖**：尾巴响应会把窗口前的祖先作为 `isContext` 重复返回（计数清零），而那些 id 在保留的前缀里已经是真实行。
- 多日范围（`range>=2`、自定义跨天、`range=4`）显示 `MM-DD HH:mm:ss`，单日只显示 `HH:mm:ss.SSS`，跨年带年份；整列表统一格式，避免行宽跳动。
- 快照版本（调用数/最新开始时间/事件数/工具次数）驱动自动重取（500 ms 去抖）；换会话或换时间范围才显示 loading。
- 上下文行用灰色 `⋯` + Tooltip 提示，不显示耗时与 token。