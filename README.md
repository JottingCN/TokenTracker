# Token Tracker

[![GitHub](https://img.shields.io/badge/GitHub-JottingCN%2FTokenTracker-181717?logo=github)](https://github.com/JottingCN/TokenTracker)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

本地 Copilot Token 用量与费用看板：接收 VS Code Copilot 的 OpenTelemetry 数据，写入本机 SQLite，在浏览器看板和 VS Code 状态栏实时展示缓存命中率、首次响应时间、生成速度与估算费用。

- 数据全部留在本机，只统计 token 计数，不采集对话内容。
- 支持 Windows x64、Linux x64、macOS x64 / arm64。
- 时间范围与峰谷计价按北京时间（UTC+8）。

## 使用方式

| 客户端 | 适合做什么 |
| --- | --- |
| **VS Code 扩展** | 日常：状态栏看当前工作区，Tooltip 看模型明细，一键打开看板。详见 [扩展说明](vscode-extension/README.md) |
| **浏览器看板** | 完整分析：主页 / 调用明细 / 模型 / 会话 / 系统信息 / 设置 |
| **WinUI 3 桌面端**（可选） | Windows 原生窗口，自带接收服务与看板（与扩展/独立服务端二选一，默认使用同一端口） |

扩展与浏览器看板共用同一个本地服务（默认 `http://127.0.0.1:4318`），多个 VS Code 窗口和浏览器标签共用一个实例，不会互相抢占端口。

## 快速开始

### 1. 安装

推荐安装 VSIX 扩展。首次激活会按平台从最新的服务端 Release 下载自包含包并校验 SHA-256（约 8 MB，自动读取 VS Code `http.proxy` / `HTTPS_PROXY`）：

```bash
code --install-extension tokentracker-*.vsix --force
```

不想装扩展，可以直接运行 Release 中的独立服务端包：

```bash
TokenTracker.Server.exe      # Windows
./TokenTracker.Server        # Linux / macOS
```

### 2. 让 Copilot 上报数据

在命令面板运行 **`Token Tracker: 启用 OTel 上报`**，会同时配置扩展宿主（本地 Copilot Chat）与 Agent Host（Copilot / Claude / Codex）两条管线。等价的手动配置：

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

- Agent Host 的设置需要**完整退出并重新打开 VS Code** 才生效，仅重载窗口可能复用旧进程。
- 保持 `captureContent: false`，统计 token 不需要对话内容。

### 3. 打开看板

浏览器访问 <http://127.0.0.1:4318>，或运行 `Token Tracker: 在浏览器中打开看板`。产生几次对话后即可看到数据；工作区没有数据时状态栏会自动隐藏。

### 4. 配置模型价格

项目**不内置任何模型价格**。未配置的模型只统计 token，费用标注“未定价”。在“设置 → 价格”为模型填入缓存命中 / 缓存写入 / 未命中输入 / 输出单价即可，保存后历史费用立即重算。

## 看板导览

| 页面 | 内容 |
| --- | --- |
| **主页** | 所选时间范围的汇总卡片、按模型统计、会话列表、编辑与结果指标 |
| **调用明细** | 全部调用（含辅助调用），按标题 / 会话 / 模型关键字、主辅类型、定价状态、峰谷时段检索，服务端分页可翻完所有记录 |
| **模型**（按供应方分组） | 单模型的趋势、命中率、平均 TTFT 与生成速度，以及该模型下的会话 |
| **会话** | 逐次调用的四类 token 与费用、事件与工具统计、Agent 运行与 Hook、可全屏的 Trace 拓扑、结果指标 |
| **系统信息** | 数据库路径与大小、表与行数、原始/综合 span、监听与读取路径、AHP 连接状态 |
| **设置** | 价格（峰谷、长上下文分档、自定义模型）、费用单位与汇率、数据保留天数 |

顶栏可切换时间范围（今天 / 昨天 / 近 7 天 / 近 30 天 / 全部 / 自定义）、深浅主题与中英文界面；左侧停靠区底部显示实时连接与 AHP 状态。

## 指标说明

- **缓存命中率**：`cache_read_input_tokens / input_tokens`，按会话、模型、单次调用三级查看。
- **费用估算**：按你配置的单价计算，保留 6 位小数，拆分缓存读取 / 缓存写入 / 未命中输入 / 输出 / 思考；Anthropic 系模型的缓存读写会正确计入总输入。
- **首次响应与生成速度**：平均 TTFT，以及 `输出 token /（调用耗时 − TTFT）`。没有有效 TTFT 的调用显示 `-`，不会用总耗时凑速度。
- **实际 AIC**：扩展管线调用若带实际用量字段，会在估算费用下方显示实际 AIC；Agent Host 暂无该字段。
- **事件与工具**：工具调用次数、平均 / 最长耗时、失败数、工具类型、编辑 / 技能细分、MCP 工具名与错误类型；Agent 运行按父子层级展示，子代理的工具调用与 LLM 调用都归到根会话统计。
- **编辑与结果指标**：编辑接受率、代码行数、存活率、用户反馈与操作、PR / 云会话等，来自 OTel 指标。只有 VS Code 实际上报时才会出现；无法唯一归属到具体会话的会保留为窗口级，不会强行分摊。
- **两条管线自动合并**：同一次调用被扩展宿主和 Agent Host 同时上报时，按会话、模型、token 与时间区间重叠合并为一条，费用按合并后的 token 重算；页面默认展示综合数据，不需要切换来源。
- **多模型会话**：同一会话跨模型的统计自动合并，下钻时按模型分开展示。
- **辅助调用**：无法归属会话的调用计入主页与模型统计，会话页只统计可归属的主调用，“调用明细”保留全部。

## 费用单位

看板与状态栏都可选人民币、美元或积分显示；美元汇率与积分兑换率可调，价格始终以人民币保存。

## 数据在哪里

| 场景 | 位置 |
| --- | --- |
| VS Code 扩展 | `<VS Code globalStorage>/jottingcn.tokentracker/data/tokentracker.db` |
| 独立服务端（Windows） | `%LOCALAPPDATA%\TokenTracker` |
| 独立服务端（Linux） | `~/.local/share/TokenTracker` |
| 独立服务端（macOS） | `~/Library/Application Support/TokenTracker` |

可用 `TOKENTRACKER_DATA_DIR` 覆盖。默认保留 90 天，可在“设置 → 数据”调整（`0` = 永久）。

为补全会话标题与工作区，只读访问本机已有文件：Agent Host 的 `agent-host.db`、`~/.copilot/session-state/*/workspace.yaml`、扩展宿主 `ChatSessionStore` 索引，以及 Agent Host AHP 本地端点。“系统信息”页面会列出这些路径及用途。

## 隐私

- 解析与统计全部在本机完成，不上传数据、不引入遥测。
- 只保存 token 计数与非内容元数据；即使开启 `captureContent`，也不会写入 prompt、回复、工具参数/结果、单次调用的文件路径或命令内容。会话的工作区根目录会作为归属信息缓存在本地，用于 Agent Host 未连接时仍能匹配当前文件夹。
- 所有客户端断开后，服务按 `idleExitSeconds` 自动退出，不常驻后台。

## 常见问题

| 现象 | 处理 |
| --- | --- |
| 状态栏没有内容 | 当前工作区还没有 token 数据时属正常；用 `Token Tracker: 服务信息` 确认服务在运行，并检查 Copilot OTel 设置是否指向 `http://127.0.0.1:4318` |
| Agent Host 数据没进来 | 完整退出并重新打开 VS Code（重载窗口无效）；仍无数据可运行 `Token Tracker: 导入本地 Copilot OTel 数据` 补录 |
| 费用显示“未定价” | 在“设置 → 价格”为该模型配置单价，保存后自动重算历史费用 |
| 首次激活下载失败 | 确认对应平台的服务端包已发布；GitHub API 限流时稍后重试，或把 `tokentracker.serverDownloadBaseUrl` 设为镜像前缀 |
| 端口被占用 | 修改 `tokentracker.port`，并同步修改 Copilot 的 `otlpEndpoint` |
| 想看更早的数据 | Copilot 本地 span 库只保留约 7 天 / 最近 100 个会话，用导入命令补录；长期统计以 Token Tracker 数据库为准 |
| 服务有新版本 | 扩展会在后台下载并提示，正在运行的实例不受影响；确认后再执行“重启服务”切换 |

## 已知说明

- 计时、时间范围与峰谷判断均按北京时间，自然日也按北京时间划分。
- 扩展与服务端独立发布，服务端可在不更新扩展的情况下升级。
- WinUI 3 桌面端为可选组件，功能以浏览器看板为主。

## 文档

- [VS Code 扩展说明](vscode-extension/README.md)
- [AGENTS.md](AGENTS.md)：构建命令、接口、环境变量与实现约定（面向开发者与自动化代理）
- [docs 目录](docs)：Copilot Chat 与 Agent Host 的 OpenTelemetry 字段说明

## License

[MIT](LICENSE) © 2026 JottingCN
