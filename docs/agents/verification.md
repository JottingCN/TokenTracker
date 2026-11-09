# 验证与禁止事项

上级：[AGENTS.md](../../AGENTS.md) · 相关：[工程约束](engineering.md)

## 1. 按改动面选择验证

| 改动 | 必须执行 | 当前基线 |
| --- | --- | --- |
| Core 逻辑（解析、存储、定价、元数据） | `dotnet test tests/TokenTracker.Core.Tests/TokenTracker.Core.Tests.csproj -c Release` | 94 项 |
| 看板 `web/` | `cd web && npm run build`（内含 `tsc --noEmit`） | 构建通过 |
| 扩展 `vscode-extension/` | `npm run compile` + `npm test` | 18 项 |
| 服务端 / 路由 / 序列化 | `npm run sync-server`（AOT 成功）+ `npm run smoke` | 63 项 |
| 文档（本目录） | 检查链接与路径是否仍然存在 | — |

**路由/序列化层只能由冒烟覆盖**：单测跑不到 `MapPost` 绑定与 AOT 序列化问题。冒烟已覆盖 OTLP 入口返回码、`/api` 与 `/otlp` 未命中 404、`from`/`to` 半参数 400、trace 分块契约、审批节点、`turns` 为空、metric 入库后结果指标立即可见。

## 2. 看板文案

用临时实例切 `en` / `zh-CN` / `zh-TW` 各走一遍主页、模型页、会话页、调用明细与两个抽屉，确认没有回退到原始 key（页面不应出现 `a.b.c` 形式文本）。`navigator.language` 可用浏览器 locale 覆盖，或改 `localStorage['tokentracker.language']` 后刷新。

## 3. 隔离 VS Code 实例

```bash
Code.exe --new-window \
  --user-data-dir <temp-profile> \
  --extensions-dir <temp-exts> \
  --extensionDevelopmentPath <repo>/vscode-extension \
  <repo>
```

- 通过 `<temp-profile>/User/globalStorage/jottingcn.tokentracker/extension.log` 检查激活与快照日志。
- 用临时端口（如 4330+）与临时数据库副本，避免影响正在使用的 4318 服务。
- 检查状态栏文本或 Tip 结构时，用桩 `vscode` 模块（`MarkdownString` / `createStatusBarItem` / `getConfiguration`）配合 `NODE_PATH` 直接加载 `out/statusBar.js` 驱动真实 `StatusBarController`，打印 `item.tooltip.value` 断言，不必启动 GUI。
- 验证「健康实例不被重启 / 后台更新检查失败不影响使用」时，先用临时数据目录手动起一个 `TOKENTRACKER_BUILD_ID=legacy-build-1` 的服务再激活扩展，确认 `server ready startedHere=false` 早于 `update check:` 日志且进程未变。

## 4. 服务端负载与内存

临时库副本 + 独立端口，按固定矩阵对比：

1. 启动后 3 s 的私有/工作集内存与线程数。
2. 100 次会话/拓扑请求后的增量。
3. 50 轮「POST 一个 span + GET dashboard」的耗时与增量。
4. 空闲 20 s 是否回落。

改 GC / 合并 / 拓扑前先记基线，避免把 GC 时机波动当回归。

## 5. 禁止事项

- 不要修改或写入真实的 `~/.copilot/session-state`、`agent-host.db`、`state.vscdb` 或用户数据库；验证请复制到临时目录。
- 不要终止用户正在使用的 `TokenTracker.Server`（默认端口 4318）或关闭用户的 VS Code 窗口；只清理自己启动的测试实例。
- 不要上传任何数据或引入遥测。
- 不要删除、重建或 DROP 用户数据；迁移只能附加式（见[存储](storage.md)第 3 节）。
- 不要在未明确要求时提交 git；提交时只暂存相关文件。