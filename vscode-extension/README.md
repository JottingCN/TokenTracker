# Token Tracker for VS Code

Thin VS Code client for the local Token Tracker stack:

- Runs the bundled `TokenTracker.Server` as one shared local instance (Windows, `127.0.0.1:4318` by default). No Windows service required: VS Code starts it on activation; start/stop commands are also provided.
- Shows cache hit rate and estimated CNY cost in the status bar (updates over WebSocket).
- Opens the full dashboard in your default browser.
- Exposes its server URL/API to other extensions through `extension.exports`.

Commands:

- `Token Tracker: 在浏览器中打开看板`
- `Token Tracker: 启动服务`
- `Token Tracker: 停止服务`
- `Token Tracker: 服务信息`
- `Token Tracker: 刷新状态栏`

The status bar automatically scopes to the current window's workspace folders (sessions are matched by their workspace path) and is hidden when that workspace has no token data; when no folder is open it falls back to all sessions. The tooltip shows the latest session's token/price breakdown.

Runtime and data live in the extension cache (`globalStorage`):

- `runtime/<buildId>/` — versioned copy of the bundled server. A new build gets a new directory, so updates never overwrite a running binary.
- `data/tokentracker.db` — SQLite database (migrated once from `%LOCALAPPDATA%\TokenTracker`).

If an older instance was started by the extension, an updated build stops it and starts the cached copy; externally managed instances are reused with a warning.

Settings: `tokentracker.port`, `tokentracker.autoStart`, `tokentracker.statsRange` (today / 7d / 30d / all), `tokentracker.idleExitSeconds` (default 60; server exits after all windows close and no clients remain).

Build:

```bash
npm install
npm run build        # tsc + dotnet publish into ./server
npm run package      # produces token tracer .vsix
```
