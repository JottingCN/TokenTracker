# AGENTS.md

本文件是仓库约定的**索引与红线**；细节按主题拆分在 `docs/agents/` 下，改哪块先读对应文档。
面向使用者的说明见 `README.md` 与 `vscode-extension/README.md`；OTel 字段实证见 `docs/` 根目录的三份文档。

## 项目速览

Token Tracker 是本地 Copilot Token 用量与费用看板。Copilot Chat / Agent Host 通过 OTLP 上报 → `TokenTracker.Server`（Kestrel、.NET 10 Native AOT）落 SQLite → REST `/api/*` + WebSocket `/ws` + React 看板（`wwwroot`）。VS Code 扩展负责状态栏、Tip、命令与 AHP 标题通道，并按平台下载/启动服务端进程。

- 可执行体只有两个：服务端（四平台自包含 AOT）与 VS Code 扩展（通用 VSIX）。
- **WinUI 桌面端已删除**（提交 `835671a 删除app`），不要按 `TokenTracker.App` / Windows App SDK 规划实现。
- 数据只留本机、不上传；默认 `captureContent: false`。
- 时间范围与峰谷价格一律按北京时间（UTC+8）。

## 文档地图

| 文档 | 什么时候读 |
| --- | --- |
| [架构与接口](docs/agents/architecture.md) | 改路由、payload、环境变量、数据流、路径定位 |
| [存储、聚合与迁移](docs/agents/storage.md) | 改 SQL、schema、聚合口径、`includeCalls` 路径 |
| [OTel 来源与解析](docs/agents/otel-parsing.md) | 改解析规则、来源归因、工具/审批/Hook、本地导入 |
| [结果指标](docs/agents/outcome-metrics.md) | 改 outcome allowlist、指标归属、卡片渲染 |
| [会话元数据与轮次](docs/agents/sessions.md) | 改标题优先级、工作区归属、AHP、`turns` |
| [Trace 拓扑](docs/agents/trace-topology.md) | 改拓扑分块、节点构成、前端虚拟滚动 |
| [费用与定价](docs/agents/pricing.md) | 改价格模型、峰谷、节假日、费用展示 |
| [服务生命周期](docs/agents/lifecycle.md) | 改启动/接管/更新/空闲退出/推送延迟 |
| [扩展 UI 与多语言](docs/agents/extension-ui.md) | 改状态栏、Tip、命令、i18n |
| [工程约束](docs/agents/engineering.md) | 写 C# / React 前必读（AOT、内存、UI 规范） |
| [Schema 代码生成计划](docs/agents/schema-codegen.md) | 改表结构前评估是否落地结构化代码生成（未开工，分四期） |
| [验证与禁止事项](docs/agents/verification.md) | 每个改动的收尾动作 |
| [发布与提交](docs/agents/release.md) | 发版、tag、commit 风格 |

## 十条红线

细节见对应文档；这里只写最容易被违反的。

1. **`spans` 是投影不是原始报文**（34 个固定列，无 attributes/payload）。新增统计维度必须加列 + 升 schema，历史行补不回来 → [存储](docs/agents/storage.md)
2. **数据库只能附加式迁移**：禁止 `DROP TABLE` / `DROP COLUMN` / 清库重建；`PRAGMA user_version` 是版本标记，读到更高版本只告警不改库 → [存储](docs/agents/storage.md)
3. **时间窗必须在 SQL 聚合之前下推**，不能按聚合后的 `lastSeenMs` 过滤（否则「今天」显示全部历史）→ [存储](docs/agents/storage.md)
4. **Trace 拓扑按 FIFO 时间游标分块**：首块最早、`cursor` 向后追加；不要页码分页器，也不要按整条 trace 切块 → [拓扑](docs/agents/trace-topology.md)
5. **拓扑窗口外的祖先用 `isContext` 行补齐**，不计数；实时刷新用**尾部窗口重载**（晚到的父 span 追不上递增游标），合并时真实行不得被 context 行覆盖 → [拓扑](docs/agents/trace-topology.md)
6. **AOT**：JSON 只走 `AppJsonContext`；`MapPost/MapGet` lambda 带 `HttpContext` 时必须再带 `CancellationToken`；错误用 `ErrorPayload` 不用 `Results.Problem` → [工程约束](docs/agents/engineering.md)
7. **审批没有“自动/人工”标记**：`permission.result` 只有 `approved` / `denied-interactively-by-user`，只能按有无 span 与耗时读现象；解析必须按 span 名优先判 `permission` → [OTel 解析](docs/agents/otel-parsing.md)
8. **轮次来自根 `invoke_agent`**（运行中的轮次没有边界，必须输出 `InProgress` 行，否则一直显示上一轮）；`EndTimeMs` 取工具结束时间，0 次数的轮次丢弃并重编号，`summary.calls` 必须等于 `turns` 之和 → [会话与轮次](docs/agents/sessions.md)
9. **跨请求缓存必须有上界**；WS 快照聚合不能靠客户端过滤（Tip 也传 `range`）→ [存储](docs/agents/storage.md)
10. **界面文案全部走 i18n**（`en` / `zh-CN` / `zh-TW` 三文件齐平；扩展另有三份 `package.nls`）→ [扩展 UI](docs/agents/extension-ui.md)

## 常用命令

```bash
# 核心测试（改任何 Core 逻辑都必须跑）
dotnet test tests/TokenTracker.Core.Tests/TokenTracker.Core.Tests.csproj -c Release

# 服务端
dotnet run --project src/TokenTracker.Server -c Release
dotnet build src/TokenTracker.Server/TokenTracker.Server.csproj -c Release
dotnet publish src/TokenTracker.Server -c Release -r win-x64 -o <dir>

# 看板（build 内含 tsc --noEmit）
cd web && npm install && npm run dev     # 5173，代理到 4318
cd web && npm run build                  # 输出到服务端 wwwroot

# VS Code 扩展
cd vscode-extension && npm install
npm run compile       # tsc → out/（不提交）
npm test              # pretest 自动 compile；node --test
npm run sync-server   # AOT 发布服务端到 server/（Windows 需 MSVC 环境）
npm run smoke         # 对 server/ 的 AOT 产物跑 HTTP 冒烟（随机端口 + 临时库）
npm run build         # compile + sync-server
npm run package       # 生成 tokentracker-*.vsix（排除 server/）
```

- `scripts/sync-server.mjs` 会先删 `server/` 再发布；**服务端进程正在运行时删除会 EPERM**，先停自己起的实例。
- 跨平台构建用 `TOKENTRACKER_TARGET_RID=<rid> node vscode-extension/scripts/sync-server.mjs`，AOT 必须在目标平台执行。
- 验证基线：Core 98 项、扩展 18 项、冒烟 80 项（见[验证](docs/agents/verification.md)）。

## 目录

| 路径 | 说明 |
| --- | --- |
| `src/TokenTracker.Core` | OTLP 解析、SQLite（`SpanStore*`）、定价、节假日、会话元数据缓存 |
| `src/TokenTracker.Server` | ASP.NET Core：OTLP 接收、REST、WebSocket、静态看板、系统信息 |
| `web` | React + Ant Design + Vite，构建到 `src/TokenTracker.Server/wwwroot` |
| `vscode-extension` | VS Code 薄客户端；`out/`、`server/` 为构建产物（不跟踪） |
| `tests/TokenTracker.Core.Tests` | 核心单元测试（xUnit） |
| `docs` | OTel 字段实证（三份）；`docs/agents/` 为工程约定 |