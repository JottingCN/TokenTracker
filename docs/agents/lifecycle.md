# 服务生命周期、更新与延迟

上级：[AGENTS.md](../../AGENTS.md) · 相关：[架构](architecture.md)、[存储迁移](storage.md)

## 1. 运行时目录

- 发布者 `JottingCN`，许可 MIT。扩展按 `buildId` 管理 `globalStorage/jottingcn.tokentracker/runtime/<buildId>`，同端口共享单实例；`server-runtime.json` 缓存平台/Release；`extension.log` 超过 1 MB 轮转为 `extension.log.1`。
- 首次激活按 RID 从**最新的服务端 Release** 下载并校验 `.sha256`（支持 `http.proxy` / `HTTPS_PROXY`），Release 检查缓存 30 分钟；本地开发目录存在 `server/` 时优先用内置副本。
- **激活不阻塞在下载上**：已有可用运行时（`server-runtime.json` + `.ready`）时先用缓存版本秒启动，更新检查与下载放到后台（`resolveCachedRuntime`）。这曾导致每次服务端发版后启动要卡 36–41 秒（Release 包约 13 MB）。
- **「首次运行需要下载」只在真正首次出现**：发布版 VSIX 不打包 `server/`，所以「没有 server 目录」不能当作首次运行判据；必须同时没有 `server-runtime.json`、也没有任何带 `.ready` 的运行时目录（`hasPreparedRuntime`）。
- 清理旧运行时前必须确认其可执行文件没有被占用（`isRuntimeInUse`：以写方式打开 exe，Windows 返回 EBUSY、Linux 返回 ETXTBSY）。只删除可执行文件动得了的目录；否则会先删掉 `wwwroot`、留下仍在运行的进程对外返回 404。被占用的目录记入 `skipped`，下次再清。
- 服务端支持以 Windows 服务方式运行（`AddWindowsService`），但扩展不安装服务，默认由扩展拉起子进程。

## 2. 启动与接管

- 端口上已有健康服务时：激活只探测 `/healthz` + `/api/version` 并立即接管（`attachOrStartServer`），**不下载、不重启**运行中的实例（即使 buildId 不一致）。
- 端口上没有服务、但本地已有运行时：用缓存版本启动，然后后台检查更新。激活路径的 `ensureServer` 传 `reuseOutdated: true`；即使缓存版本与运行中的实例 buildId 不同也不会 kill 它（实测：复用返回 `outdated: true` 且原进程仍在；显式「启动服务」才替换）。
- Release 检查与下载在 `checkServerUpdateInBackground` 后台执行，成功或失败都只写 `extension.log`；检测到新版本只提示一次并给「重启服务」动作，由用户决定是否替换。提示同时显示「新版本 + 当前运行版本」，避免被误读成首次安装。
- 只有 `Token Tracker: 启动服务` 才执行完整的「准备服务端 + 启动/替换过期自建实例」；替换失败（外部启动的实例）时提示不一致而不是假装已启动。
- 状态栏 WS 连续失败 3 次触发一次服务端自愈（`recoverServer`，先探测再 `ensureServer`），60 秒冷却、每次激活最多 5 次；WS 连上即清零。用户手动「停止服务」时状态栏已 dispose，不会触发自愈。

## 3. 空闲退出与保留期

- 所有客户端断开后按 `tokentracker.idleExitSeconds` 退出（扩展默认 5 秒，`0` = 不退出）。空闲判定同时看 WS 连接与近期 HTTP API 活动（窗口 `max(20s, 2×idleExitSeconds)`，`/healthz` 不计），避免纯轮询客户端被误判。
- 数据保留默认 90 天（`TOKENTRACKER_RETENTION_DAYS` 或 `POST /api/settings`，`0` = 永久），修改后立即清理，后台每 6 小时再执行。

## 4. 启动顺序

schema 迁移与一次性回填同步执行；**节假日拉取（最长 8 秒）与 `BackfillCostBreakdown` 在端口就绪后后台执行**——扩展只等 `/healthz` 25 秒，阻塞会把冷启动拖成启动超时。

## 5. 变更广播

`RealtimeHub.NotifyChanged` 有 **200 ms** 合并窗口：一批 OTLP/metrics 只触发一次快照重算。别再调大（500 ms 时「入库→推送」独占半秒，而单次快照查询只要 20–70 ms）。

## 6. 为什么聊天结束后要等几秒

**不是服务端问题**：上游 OTLP Batch Span Processor 默认 `OTEL_BSP_SCHEDULE_DELAY=5000`（metrics 为 10 秒周期且 Copilot Chat 写死，环境变量无效）。

实测分解：span 结束 → 入库中位 **5.0 秒**；入库 → WS 推送 ~0.3 秒；查询 20–70 ms。

只能让用户设置该环境变量加速，说明写在 `vscode-extension/README.md` 的「刷新延迟」。不要试图在服务端“补偿”这段延迟。