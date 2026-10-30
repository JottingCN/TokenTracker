# Token Tracker for VS Code

[![GitHub](https://img.shields.io/badge/GitHub-JottingCN%2FTokenTracker-181717?logo=github)](https://github.com/JottingCN/TokenTracker)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/JottingCN/TokenTracker/blob/main/LICENSE)

在状态栏实时查看 Copilot Token 缓存命中率与人民币估算费用，并一键打开完整的本地看板。

扩展本身是薄客户端：首次激活时按操作系统下载对应平台的 `TokenTracker.Server` 自包含包，校验 SHA-256 后缓存并托管，多个 VS Code 窗口共享同一实例。

## 功能

- **状态栏合并统计**：显示当前工作区最新会话的命中率与费用，格式 `$(pulse) 00.00% · ¥0.00`；没有可用费用时省略费用。
- **多模型 tooltip**：
  - 标题为 `Token Tracker - 会话名称`
  - 显示时间范围与模型数量
  - 每个模型独立展示调用次数、命中率、费用，以及输入 / 缓存 / 输出 / 思考四类 token 与费用
- **工作区自动匹配**：按会话工作区过滤，当前窗口没有 token 数据时自动隐藏，有数据后自动出现。
- **名称自动刷新**：监听 `agent-host.db` 与 `workspace.yaml`，会话改名或工作区变化后自动更新。
- **浏览器看板**：完整的主页、模型、会话和设置页面（价格 / 数据）；费用可切换人民币、美元或积分显示，数据保留天数可配置，价格支持按输入 token 阈值分档。状态栏仍按人民币显示。
- **共享服务**：同一端口只保留一个服务实例，VS Code 窗口和浏览器共用。

## 前置条件

- 支持平台：Windows x64、Linux x64、macOS x64 / arm64
- VS Code 1.90 或更高版本
- 首次激活从最新的服务端 Release 下载匹配平台的服务端（约 8 MB）；自动读取 VS Code `http.proxy` 或 `HTTPS_PROXY` / `HTTP_PROXY`
- 已安装 GitHub Copilot 并在 VS Code 中开启 OpenTelemetry 导出

在 VS Code `settings.json` 中加入：

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "otlp-http",
  "github.copilot.chat.otel.otlpEndpoint": "http://127.0.0.1:4318",
  "github.copilot.chat.otel.protocol": "http/json",
  "github.copilot.chat.otel.captureContent": false
}
```

## 安装

```bash
code --install-extension tokentracker-*.vsix --force
```

安装后重新加载 VS Code 窗口。首次激活会下载并解压对应平台的服务端，随后自动启动；状态栏出现在右下角。

## 命令

| 命令 | 说明 |
| --- | --- |
| `Token Tracker: 在浏览器中打开看板` | 用默认浏览器打开 `http://127.0.0.1:<port>` |
| `Token Tracker: 服务信息` | 显示服务版本、地址、是否由本扩展启动 |
| `Token Tracker: 刷新状态栏` | 重新订阅并刷新数据 |
| `Token Tracker: 启动服务` | 启动共享服务 |
| `Token Tracker: 停止服务` | 停止由扩展启动的服务（外部实例不会停止） |

## 状态栏

- 统计对象：当前窗口工作区内最近更新的会话；汇总该会话下所有模型的数据。
- 显示内容：命中率（2 位小数）+ 估算费用（2 位小数，无法估算时隐藏）。
- 工作区没有 token 数据时状态栏隐藏，出现新数据后自动显示。
- 未打开文件夹时回退为“所有工作区”。

### 会话名称与工作区

标题优先级：

1. `agent-host.db` 中 `titleSource=user`（VS Code 内手动改名）
2. `workspace.yaml` 中 `user_named: true`
3. `agent-host.db` 的自动摘要（与 VS Code 会话列表一致）
4. `workspace.yaml` 的自动名称

工作区路径优先取 `agent-host.db` 记录的工作目录，缺失时回退到 `workspace.yaml` 的 `cwd`。

刷新机制：

- `workspace.yaml` 变化：500ms 去抖后立即刷新。
- `agent-host.db` 变化：500ms 去抖，且最多每 15 秒刷新一次（聊天过程中的草稿 / 轮次写入不会造成频繁刷新）。
- 变化会通知本地服务，向所有已连接客户端（VS Code 与浏览器看板）推送最新快照。
- VS Code 工作区文件夹变化：立即重新匹配。

## 设置

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| `tokentracker.port` | `4318` | 本地服务端口（`127.0.0.1`） |
| `tokentracker.serverDownloadBaseUrl` | 空 | 服务端镜像前缀（如 `https://mirror/tokentracker-server-1.0.0`）；空时使用最新 GitHub 服务端 Release |
| `tokentracker.autoStart` | `true` | 服务未运行时自动启动 |
| `tokentracker.statsRange` | `7d` | 状态栏统计范围：`today` / `7d` / `30d` / `all` |
| `tokentracker.idleExitSeconds` | `60` | 所有客户端（VS Code 窗口或浏览器）断开后延迟退出；`0` 表示不自动退出 |

## 服务生命周期

- 首次激活按平台映射到 `win-x64` / `linux-x64` / `osx-x64` / `osx-arm64`，从**最新的服务端 Release** 下载
  `tokentracker-server-<version>-<rid>.<zip|tar.gz>`，并校验同名 `.sha256`。
- 解压到 `runtime/<buildId>` 并写入 `server-runtime.json`；Release 检查结果缓存 30 分钟，后续激活优先复用，不重复下载。
- 扩展与服务端版本独立；服务端发布新 Release 后，扩展会在一小时内感知并升级。
- 扩展激活时先检查端口上的现有服务：
  - 构建一致：直接复用。
  - 由扩展启动且构建不一致：停止旧实例并启动当前构建。
  - 由外部启动：复用，构建不一致时提示。
- 缓存目录（VS Code globalStorage）：

```
jottingcn.tokentracker/
├─ runtime/<buildId>/        # 解压后的服务端（可执行文件 + wwwroot）
├─ data/tokentracker.db      # SQLite 数据库
├─ server-runtime.json       # 版本 / 平台 / 构建缓存
└─ extension.log             # 诊断日志
```

- Windows 首次使用时会尝试迁移 `%LOCALAPPDATA%\TokenTracker` 下的旧数据库。
- 所有 VS Code 窗口和浏览器都断开后，服务按 `idleExitSeconds` 退出；关闭窗口不会影响其他窗口正在使用的服务。
- 本地开发时若扩展目录存在 `server/`，直接使用内置副本，不联网下载。

## 对外 API

其他扩展可通过 `extension.exports` 访问：

```ts
interface TokenTrackerApi {
  getServerUrl(): string | undefined;
  getVersion(): unknown;
  openDashboard(): Promise<void>;
  refresh(): void;
  startServer(): Promise<void>;
  stopServer(): Promise<void>;
}
```

## 开发

```bash
npm install
npm run build     # 本地开发：tsc + 内置 win-x64 服务端（离线运行）
npm run compile   # 仅编译 TypeScript（发布包使用，运行时下载服务端）
npm run package   # 生成通用 tokentracker-*.vsix（server/ 不打包）
```

调试日志：

- 输出面板：`Token Tracker`
- 文件：`<globalStorage>/jottingcn.tokentracker/extension.log`
  - Windows `%APPDATA%\Code\User\globalStorage`
  - Linux `~/.config/Code/User/globalStorage`
  - macOS `~/Library/Application Support/Code/User/globalStorage`
- 超过 1 MB 自动轮转为 `extension.log.1`（保留一份），磁盘占用上限约 2 MB

## 排查

- **首次激活下载失败**：确认对应版本的 Release 已发布且包含当前平台的 `tokentracker-server-*` 与 `.sha256`；若提示 GitHub API 限流，稍后重试或设置 `tokentracker.serverDownloadBaseUrl` 指向镜像。
- **代理**：自动读取 VS Code `http.proxy` 与 `HTTPS_PROXY` / `HTTP_PROXY`，支持 `http://` CONNECT 代理并遵循 `NO_PROXY`；暂不支持 SOCKS。`github.com/releases/download` 在部分网络不可用，扩展默认走 GitHub API。
- **镜像**：`tokentracker.serverDownloadBaseUrl` 是 URL 前缀，扩展会追加 `-<rid>.<zip|tar.gz>`；例如设置为 `https://mirror/tokentracker-server-1.0.0`。
- **从旧发布者升级**：首次启动会将 `globalStorage/jotting.tokentracker/data` 中的数据库迁移到 `jottingcn.tokentracker`。
- **状态栏不显示**：确认当前工作区有会话数据；打开“服务信息”确认服务已运行；检查 Copilot OTel 配置是否指向 `http://127.0.0.1:4318`。
- **端口被占用**：修改 `tokentracker.port`，并同步修改 Copilot 的 `otlpEndpoint`。
- **服务版本不一致**：若提示外部实例被复用，先停止外部进程，再执行 `Token Tracker: 启动服务`。
- **改名未刷新**：确认 `agent-host.db` 或 `workspace.yaml` 发生变化；agent-host 触发的刷新最多延迟 15 秒。

## 隐私

- 仅在本机运行，不向后台上传数据。
- 只处理 token / usage 计数，不采集对话内容（`captureContent: false`）。
- 会话标题读取 `agent-host.db` 与 `workspace.yaml` 时使用只读连接。
