# 发布与提交

上级：[AGENTS.md](../../AGENTS.md)

扩展与服务端独立发布：

```bash
git tag v0.1.9 && git push origin v0.1.9                 # 扩展（与 vscode-extension/package.json 版本一致）
git tag server-v1.0.0 && git push origin server-v1.0.0   # 服务端
```

- `Release` workflow 构建通用 VSIX（`tokentracker-<version>.vsix`）；`Server Release` 构建四平台服务端包与 `.sha256`（win-x64 / linux-x64 / osx-x64 / osx-arm64）。两者都支持 `workflow_dispatch`。
- 扩展默认从最新服务端 Release 下载，服务端可独立升级；也可用 `tokentracker.serverDownloadBaseUrl` 指向镜像前缀。
- 提交遵循现有风格 `type: summary`（`feat:` / `fix:` / `docs:` / `chore:`），正文说明「为什么」。不要添加与任务无关的格式化改动。