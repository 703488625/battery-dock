# 发布流程

## 版本约定

- 使用语义化版本：`MAJOR.MINOR.PATCH`，Git tag 为 `v1.1.0`。
- 当前版本：`v1.1.0`；首次公开版本：`v1.0.0`。
- 只在协议/功能/兼容性发生变化时升版本；文档修正可以只更新 README。

版本号写在 `src/BatteryDock/BatteryDock.csproj`：

```xml
<Version>1.1.0</Version>
<FileVersion>1.1.0.0</FileVersion>
```

## 发布前检查

- [ ] `--demo` 界面正常；浅色/深色切换正常
- [ ] 设置项可点击、可持久化
- [ ] 至少一台真实设备电量读取正常
- [ ] 没有未处理的异常日志
- [ ] README 支持表和 `docs/PROTOCOLS.md` 的验证状态是最新的
- [ ] `THIRD-PARTY-NOTICES.md` 已更新
- [ ] 版本号和 `CHANGELOG.md` 已更新
- [ ] 发布包不包含 `settings.json`、`last-known.json`、日志或个人路径

## 本地构建

```powershell
.\scripts\build.ps1
```

输出：

```text
dist\BatteryDock.exe
dist\BatteryDock.exe.sha256
```

## 生成发布压缩包

```powershell
.\scripts\release.ps1 -Version 1.1.0
```

输出：

```text
release\BatteryDock-v1.1.0-win-x64.zip
release\BatteryDock-v1.1.0-win-x64.zip.sha256
```

压缩包包含：

- `BatteryDock.exe`
- `README.md`
- `使用说明.txt`
- `LICENSE`
- `THIRD-PARTY-NOTICES.md`
- `CHANGELOG.md`

## 通过 GitHub Actions 发布

仓库包含 `.github/workflows/release.yml`。推送 `v*` tag 会自动：

1. `dotnet publish` 自包含单文件
2. 生成 zip
3. 生成 SHA-256
4. 创建 GitHub Release 并上传资产

```powershell
git tag v1.1.0
git push origin v1.1.0
```

工作流完成后，在 Releases 页面检查：

- zip 可以下载
- `.sha256` 内容与 zip 匹配
- Release notes 自动生成
- 文件名和版本号正确

## 手动发布（没有 Actions 时）

1. 本地执行 `.\scripts\release.ps1 -Version 1.1.0`
2. 打开 GitHub → Releases → Draft a new release
3. Tag 选择 `v1.1.0`（或新建）
4. 上传 `release\BatteryDock-v1.1.0-win-x64.zip` 和 `.sha256`
5. 粘贴 `CHANGELOG.md` 对应段落
6. 发布

## SmartScreen / 代码签名

当前构建**没有代码签名**，首次运行可能出现 Windows SmartScreen 提示：

> 更多信息 → 仍要运行

如果后续要减少提示，可以购买代码签名证书，在 CI 中用 `signtool` 签名。不要把证书或密码提交到仓库；使用 GitHub Secrets。

## 校验下载

用户可以用 PowerShell 验证：

```powershell
Get-FileHash .\BatteryDock-v1.1.0-win-x64.zip -Algorithm SHA256
Get-Content .\BatteryDock-v1.1.0-win-x64.zip.sha256
```

或直接验证 exe：

```powershell
Get-FileHash .\BatteryDock.exe -Algorithm SHA256
```

## 回滚

如果新版本出现严重问题：

1. 在 Releases 里把旧版本标记为 Latest（GitHub 支持编辑 release）。
2. 发布一个修复版本（例如 `v1.0.1`），不要删除旧 tag。
3. 在 Release notes 里说明问题和临时规避方式。

## 不提交的内容

`.gitignore` 已排除：

- `dist/`、`release/`、`artifacts/`
- `.dotnet/`、`.nuget/`、`bin/`、`obj/`
- 本地日志、截图临时文件、`电量坞.lnk`

GitHub Release 资产由 CI 或手动上传，不进入 Git 历史。


## 相关文档

- [发布到 GitHub](GITHUB-PUBLISHING.md)
- [架构](ARCHITECTURE.md)
- [开发与调试](DEVELOPMENT.md)
