# 扩展 UI 与多语言

上级：[AGENTS.md](../../AGENTS.md) · 相关：[会话与轮次](sessions.md)、[生命周期](lifecycle.md)

## 1. 状态栏

- 展示当前工作区最新会话的命中率、平均 TTFT、平均生成速度与费用。默认模板 `$(pulse) {turn} · {hit} · {ttft} · {speed} · {cost}`，可用 `tokentracker.statusBarFormat` 自定义占位符与格式（`{hit:0.00%}`、`{ttft:0.0s}`、`{cost:0.000000}`）。
- `{turn}` = **最新对话轮次与已执行步数**（`第3轮 12步`，步数 = 该轮已完成的 `execute_tool` 次数）；`{step}` 只显示步数；`{turns}` 是本范围轮次总数。数据来自 `SessionPayload.turns`，纯 Copilot Chat 数据没有轮次标记时整段必须自动省略（不能渲染成 `第0轮`）。
- 模板省略逻辑用占位符标记实现：空值先替换成 `\u0000`，再删掉相邻的 `·`，否则 `$(pulse) {turn} · {hit}` 在没有轮次时会渲染成 `$(pulse) · 25.00%`（回归断言见 `test/statusBar.test.js`）。
- 除 `{cost}` 外的指标取**最新主 agent 模型**的值（子代理/辅助调用模型不参与），`{model}` 同样显示该模型；`{cost}` 口径由 `tokentracker.statusBarCost` 决定：`session`（默认，整会话）/ `model`（仅最新模型）。breakdown 未加载时回退会话汇总。
- **严格按工作区匹配**：只显示归属当前文件夹（或其子目录）的会话；工作区未知的会话与其他工作区的会话都不显示，Tip 里也不写工作区信息。归属缺失应由 `session_workspaces` 缓存回补，而不是放宽匹配。
- 费用单位由 `tokentracker.costUnit` 决定（`cny` / `usd` / `credit`，按 `usdRate` / `creditRate` 换算）。
- `tokentracker.statsRange` 修改后即时生效（`StatusBarController.setRange` 清空 breakdown 缓存并重新订阅），无需重载窗口。订阅请求必须带 `range`，聚合不能靠客户端过滤（见[存储](storage.md)）。

## 2. Tip（悬停详情）

- 正文加粗标题 `Token Tracker · 会话名`（不用大号标题）；时间范围与统计范围合并一行。
- 每个模型独立成节（`模型名 · 最后使用时间`），展示按显示宽度对齐的四类 token 明细与次数、总用时、平均耗时、命中率、TTFT、速度、费用；多于一个模型时末尾给合计。
- 有轮次时在工具调用之前展示「对话轮次 · N轮 · M步 · 开始时间 · 耗时」小节，第二行是「模型调用 · 工具调用 · 费用」。轮数用总数（轮次已按出现顺序重编号，最新一轮的序号恒等于总数），不写 `N / N`；开始时间不带「开始」字样；进行中的轮次在末尾加 `· 进行中`。
- 工具调用节标题带 `N 次 · M 种工具`，Hook 节标题带 `N 次 · M 组`，各只列 Top 5。
- 不在 Tip 展示 Agent 运行与“其他事件”统计（会话页仍完整展示），也不显示工作区信息。
- Tip 工具名按 18 显示列截断（宽字符计 2 列），使用服务端 `toolDisplayText`；不要在客户端再维护哈希表。

## 3. package.json 与命令

- 设置项：`statusBarFormat`、`statusBarCost`、`statsRange`、`costUnit`、`creditRate`、`usdRate`、`port`、`autoStart`、`idleExitSeconds`、`serverDownloadBaseUrl`。
- 命令：`openDashboard`、`showServerInfo`、`configureOtel`、`importLocalTraces`、`refreshStatus`、`startServer`、`stopServer`。
- 新增设置/命令必须在 `package.nls.json`、`package.nls.zh-cn.json`、`package.nls.zh-tw.json` 三个文件同时补 `%key%`。

## 4. 多语言（i18n）

- 三种语言：`en`、`zh-CN`、`zh-TW`，标识与 VS Code `env.language` 一致。默认 `auto`：看板按 `navigator.language`、扩展按 `vscode.env.language` 解析，无法识别回退 `en`。
- 看板：`web/src/i18n/en.ts` 是唯一真源（`as const` 导出 `MessageKey`），`zh-CN.ts` / `zh-TW.ts` 用 `Record<MessageKey, string>` 保证键完整，**缺键即 tsc 报错**。新增文案必须同时补三个文件。
- 组件内用 `const { t } = useUi()`；列定义、label 表等模块级代码用 `web/src/i18n` 导出的 `t()`（依赖 provider 每次渲染写入 `setActiveLocale`）。插值写 `{name}` 由 `t('key', { name })` 传入，不要用模板字符串拼中英文。
- `utils/money.tsx`、`utils/model.ts`、`utils/labels.ts` 走同一套 `t()`；`Intl` 格式化用 `intlLocale(locale)`，不要写死 `zh-CN` / `en-US`。
- 标签与数值之间的冒号用 `t('label.separator')`（英文 `": "`、中文全角 `"："`），不要在 JSX 硬编码全角冒号。
- 扩展：运行期文案用 `src/i18n.ts` 的 `tr(zh, en, zhTW?)`——`zh-TW` 缺省时由 `src/zhConvert.ts` 从简体转换（术语表在文件顶部 `TERMS`）。