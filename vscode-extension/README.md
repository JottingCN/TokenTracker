# Token Tracker for VS Code

[![GitHub](https://img.shields.io/badge/GitHub-JottingCN%2FTokenTracker-181717?logo=github)](https://github.com/JottingCN/TokenTracker)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/JottingCN/TokenTracker/blob/main/LICENSE)

在状态栏实时查看当前工作区的 Copilot Token 缓存命中率、首次响应时间、生成速度与估算费用，一键打开完整的本地看板。

扩展是薄客户端：首次激活时按操作系统下载对应平台的服务端，校验 SHA-256 后缓存并托管；多个 VS Code 窗口与浏览器共用同一个服务实例。所有数据留在本机，不采集对话内容。

## 你会看到什么

**状态栏**（右下角）：当前工作区最近更新会话的命中率、平均 TTFT、平均生成速度与费用，默认形如

```
$(pulse) 82.35% · 155 ms · 30.5 tok/s · ¥0.012345
```

工作区没有 token 数据时自动隐藏，出现新数据后自动显示；未打开文件夹时回退为“所有工作区”。状态栏严格按工作区匹配：打开文件夹的窗口只显示归属该文件夹（含子目录）的会话，其他工作区或无法归属的会话都不会显示。会话的工作区归属会被缓存在本地数据库，Agent Host 未连接时仍按上次记录匹配。

**悬停 Tooltip**：标题为 `Token Tracker · 会话名称`，随后按模型分节展示

- 加粗的 `模型名 · 最后使用时间`，其下是按显示宽度对齐的四类 token（输入 / 缓存命中 / 输出 / 思考）与费用表格
- 调用次数、总用时、平均耗时、命中率、TTFT、生成速度、费用
- 多于一个模型时在末尾给出合计
- 工具调用 Top 5（节标题给出总次数与工具种数，每条含次数、平均耗时、失败数）与 Hook 执行统计

**浏览器看板**：主页 / 调用明细 / 模型 / 会话 / 系统信息 / 设置，包含趋势图、Trace 拓扑与价格配置，详见 [项目说明](https://github.com/JottingCN/TokenTracker#看板导览)。

## 前置条件

- 平台：Windows x64、Linux x64、macOS x64 / arm64
- VS Code 1.90 或更高
- 已安装 GitHub Copilot；首次激活会从最新的服务端 Release 下载服务端包（Windows 约 12 MB，Linux / macOS 约 29 MB，随服务端版本变化，下载进度显示实际大小；自动读取 `http.proxy` / `HTTPS_PROXY`）

## 安装与启用

```bash
code --install-extension tokentracker-*.vsix --force
```

安装后重新加载窗口，然后：

1. 在命令面板运行 **`Token Tracker: 启用 OTel 上报`**，一次性配置扩展宿主与 Agent Host 两条管线（保持 `captureContent: false`）。
2. **完整退出并重新打开 VS Code**——Agent Host 的设置只在冷启动时读取，重载窗口无效。
3. 产生几次 Copilot / Agent 对话，状态栏即会出现；需要历史数据时运行 `Token Tracker: 导入本地 Copilot OTel 数据`。

等价的手动设置（`settings.json`）：

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "otlp-http",
  "github.copilot.chat.otel.otlpEndpoint": "http://127.0.0.1:4318/otlp/copilot-chat",
  "github.copilot.chat.otel.protocol": "http/json",
  "github.copilot.chat.otel.captureContent": false,

  "chat.agentHost.otel.enabled": true,
  "chat.agentHost.otel.exporterType": "otlp-http",
  "chat.agentHost.otel.otlpEndpoint": "http://127.0.0.1:4318/otlp/agent-host",
  "chat.agentHost.otel.otlpProtocol": "http/json",
  "chat.agentHost.otel.captureContent": false
}
```

扩展宿主管线覆盖本地 Copilot Chat；Agent Host 管线覆盖 Agent Host 中的 Copilot / Claude / Codex。

## 命令

| 命令 | 说明 |
| --- | --- |
| `Token Tracker: 在浏览器中打开看板` | 用默认浏览器打开 `http://127.0.0.1:<port>` |
| `Token Tracker: 服务信息` | 显示服务版本、地址、是否由本扩展启动 |
| `Token Tracker: 启用 OTel 上报` | 同时配置扩展宿主与 Agent Host 的 OTel，指向本机服务 |
| `Token Tracker: 导入本地 Copilot OTel 数据` | 从本地 span 库只读导入历史调用、工具与代理事件，自动去重 |
| `Token Tracker: 刷新状态栏` | 重新订阅并刷新数据 |
| `Token Tracker: 启动服务` | 启动或复用共享服务；有新版本时可按提示切换到已下载的构建 |
| `Token Tracker: 停止服务` | 停止由扩展启动的服务（外部实例不会被停止） |

## 自定义状态栏

`tokentracker.statusBarFormat` 默认 `$(pulse) {hit} · {ttft} · {speed} · {cost}`。

- 可用占位符：`{icon}`、`{hit}`、`{ttft}`、`{speed}`、`{cost}`、`{calls}`、`{duration}`、`{input}`、`{cache}`、`{output}`、`{reasoning}`、`{events}`、`{tools}`、`{toolerrors}`、`{agents}`、`{turns}`、`{hooks}`、`{range}`、`{model}`、`{session}`。
- 冒号后指定格式，例如 `{hit:0.00%}`、`{ttft:0.0s}`、`{ttft:ms}`、`{speed:0.0}`、`{cost:0.000000}`、`{calls:0}`。
- 缺失的可选指标（如无有效 TTFT）会自动省略，不会留下重复的分隔符。

示例：`$(robot) {hit} · {cost} · {model}` 或 `$(pulse) {calls} 次 · {ttft:0.0s} · {speed}`。

## 设置

**状态栏与显示**

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| `tokentracker.statusBarFormat` | `$(pulse) {hit} · {ttft} · {speed} · {cost}` | 状态栏模板 |
| `tokentracker.statsRange` | `7d` | 统计范围：`today` / `7d` / `30d` / `all` |
| `tokentracker.costUnit` | `cny` | 费用单位：`cny` / `usd` / `credit` |
| `tokentracker.creditRate` | `0.01` | 1 积分对应的人民币金额 |
| `tokentracker.usdRate` | `7.2` | 1 美元对应的人民币金额 |

**服务端**

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| `tokentracker.port` | `4318` | 本地服务端口（仅监听 `127.0.0.1`） |
| `tokentracker.autoStart` | `true` | 服务未运行时自动启动 |
| `tokentracker.idleExitSeconds` | `5` | 所有客户端断开后延迟退出；`0` = 不自动退出 |
| `tokentracker.serverDownloadBaseUrl` | 空 | 服务端镜像地址前缀；留空使用最新 GitHub 服务端 Release |

## 服务与更新

- 首次激活按平台从**最新的服务端 Release** 下载并校验 `.sha256`，之后同一构建会缓存在本地复用，Release 检查结果缓存 30 分钟。
- 端口上已有正常运行的服务时，扩展直接复用它：不会重新下载，也不会重启正在运行的实例。
- 更新检查在后台进行，无论成功或失败都不影响使用；发现新版本时会提示一次，由你决定何时执行“重启服务”，失败时只在日志中记录。
- 服务由所有窗口共享，关闭某个窗口不会影响其他窗口正在使用的服务；全部客户端断开后按 `idleExitSeconds` 退出。
- 数据保存在 `<VS Code globalStorage>/jottingcn.tokentracker/data/tokentracker.db`，默认保留 90 天，可在看板“设置 → 数据”调整。

## 排查

- **状态栏不显示**：确认当前工作区有 token 数据；用 `Token Tracker: 服务信息` 确认服务已运行；检查 Copilot OTel 配置是否指向 `http://127.0.0.1:4318`。
- **Agent Host 没有数据**：需要完整退出并重新打开 VS Code；本地 span 库只保留约 7 天 / 100 个会话，可用导入命令补录。
- **下载失败或 GitHub 限流**：稍后重试，或把 `tokentracker.serverDownloadBaseUrl` 指向镜像前缀（扩展会追加 `-<rid>.<zip|tar.gz>`）。代理支持 VS Code `http.proxy` 与 `HTTPS_PROXY` / `HTTP_PROXY`（`http://` CONNECT），暂不支持 SOCKS。
- **端口被占用**：修改 `tokentracker.port`，并同步修改 Copilot 的 `otlpEndpoint`。
- **版本不一致提示**：说明端口上的服务由外部启动，扩展不会替换它；如需切换，先停止外部进程再执行 `Token Tracker: 启动服务`。
- **改名未刷新**：确认 `agent-host.db` 或 `workspace.yaml` 发生变化；`agent-host.db` 触发的刷新最多延迟 15 秒。Agent Host 通过 AHP 连接时，标题与工作目录由 AHP 直接推送，此时扩展会暂停这两个文件的监控；看板“系统信息 → 读取与访问路径”会列出每个路径当前的实际用途。
- **诊断日志**：输出面板 `Token Tracker`，或文件 `<globalStorage>/jottingcn.tokentracker/extension.log`（Windows `%APPDATA%\Code\User\globalStorage`、Linux `~/.config/Code/User/globalStorage`、macOS `~/Library/Application Support/Code/User/globalStorage`）。

## 隐私

- 仅在本机运行，不向任何远端上传数据；除服务端包下载外不访问网络（节假日日历除外，可在看板中查看来源）。
- 只处理 token / usage 计数，不采集对话内容（`captureContent: false`）。
- 读取会话标题与工作区信息时使用只读连接，不写入 `agent-host.db`、`state.vscdb` 或 `~/.copilot/session-state`。
- 解析到的会话工作区根目录会缓存在本地数据库 `data/tokentracker.db` 里（仅用于状态栏的工作区匹配），不上传、不包含对话内容；删除该数据库文件即可清除。

## 相关文档

- [Token Tracker 总说明](https://github.com/JottingCN/TokenTracker)
- [开发者与自动化代理约定](https://github.com/JottingCN/TokenTracker/blob/main/AGENTS.md)
