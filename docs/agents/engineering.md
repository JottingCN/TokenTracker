# 工程约束

上级：[AGENTS.md](../../AGENTS.md) · 相关：[验证](verification.md)、[扩展 UI](extension-ui.md)

## 1. .NET 与 Native AOT

- `Nullable` + `ImplicitUsings` 开启；服务端 `PublishAot=true`、`JsonSerializerIsReflectionEnabledByDefault=false`，JSON **必须**走 `AppJsonContext` 源生成，禁止反射序列化。
- 新增 payload / 消息 record 必须同时加 `[JsonSerializable(typeof(...))]`，否则 AOT 下运行期抛异常。
- 带 `HttpContext` 的 `MapPost` / `MapGet` lambda **必须额外带 `CancellationToken`**（或其它参数），否则重载解析会绑定到 `RequestDelegate` 并丢弃返回的 `IResult`（所有请求都变 200）。
- `Results.Problem` 在 AOT 下因缺少 `ProblemDetails` 元数据抛 `NotSupportedException`，返回错误用自建 `ErrorPayload`。

## 2. 内存与 SQLite

- 服务端是**内存受限**而非 CPU 受限的本地单用户服务：固定 `<ServerGarbageCollection>false</ServerGarbageCollection>`（默认 Server GC 会按逻辑核建堆 + 专用 GC 线程，实测 12 核下私有内存 53.6 MB / 27 线程 → Workstation 28.6 MB / 16 线程），并用 `RuntimeHostConfigurationOption` 设 `System.GC.ConserveMemory=7` 与 `System.GC.HighMemoryPercent=60`（默认 90% 在大内存机器上等于永不压缩）。**不要为了吞吐改回去。**
- SQLite 用 `Cache=Private` + 打开连接时 `PRAGMA cache_size = -1024`：shared-cache 与 WAL 混用不受官方推荐，而连接池下私有缓存必须限容（否则每个池化连接按默认 2 MB 预留）。
- 跨请求缓存必须有上界（见[存储](storage.md)第 2 节），新增缓存照此处理。

## 3. Web / UI

- 不添加 Webview；看板在浏览器打开。WinUI 已删除，不要再引入桌面端代码。
- 不新增依赖或抽象，除非任务确实需要。代码注释只写非显而易见的“为什么”。
- 表格高度统一走 `utils/tableMetrics.ts` 的 `clampTableHeight`：可用高度来自 `useAutoTableHeight`，按行数（单行 41px / 双行 48px）收敛，并把同一个值同时给 `scroll.y` 与 `--tt-table-body-height`，否则 `.tt-fill-table` 的 `min-height` 会让一两行数据撑满视口。
- 卡片标题必须放 `Card.title`、右侧操作放 `Card.extra`（折叠开关、全页查看等），不要塞进标题文本。会话页折叠区块收起时用 `collapsedBodyStyle` 把 body 一起隐藏。
- 列表分页放卡片标题栏右侧，条数写在标题后 `标题(N)`，单页时隐藏分页。
- 主页「按模型」表与会话表口径一致：模型 / 次数 / 输入（token + 未命中 + 缓存费用）/ 缓存（token + 费用）/ 命中 / 输出（token + 费用）/ 思考（token + 费用，0 时费用 `-`）/ 首次响应 / 生成速度 / 估算费用。`ModelRow` 必须带四类费用与 `reasoningTokens`；新增列后同步 `scroll.x` 与双行行高。
- 会话页有多个模型时显示模型 Tab（`全部模型(N)` + 各模型），切换后摘要卡片与「响应与速度」按该模型统计（`SessionPayload.models`，服务端与总会话用同一 `BuildSummary`）。
- 系统信息抽屉读 `GET /api/system`，路径表每行带用途标记（`扩展监控中` / `AHP 在线，暂停监控` / `查询时读取` / `AHP 端点轮询中` / `仅导入时读取` / `未使用`），由服务端 AHP 状态推导，与扩展监控开关同源，**不要写成静态清单**。
- 界面文案全部走 i18n（见[扩展 UI](extension-ui.md)），不写死中文或英文。

## 4. TypeScript

- `strict`、`noUnusedLocals`、`noUnusedParameters`；提交前运行 `npm run compile`（扩展）或 `npm run build`（看板）。
- `out/`、`server/`、`web/dist` 等构建产物不提交。