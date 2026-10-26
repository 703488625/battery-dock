# 发布到 GitHub

这份清单把本地项目变成一个可下载、可构建、可提 Issue 的公开仓库。

## 1. 创建仓库

在 GitHub 上新建一个**空仓库**（不要勾选自动生成 README / .gitignore / LICENSE）：

- 推荐仓库名：`battery-dock`
- 显示名：`BatteryDock / 电量坞`
- 可见性：Public
- Description：

```text
A local-only Windows tray app for Bluetooth and 2.4 GHz peripheral battery levels. Liquid-glass UI, no vendor software.
```

推荐 Topics：

```text
windows  wpf  dotnet  tray  battery  bluetooth  hid  logitech  razer  atk  mchose
```

## 2. 仓库名与链接

当前文档按 `703488625/battery-dock` 配置。如果 fork、改名或迁移账号，需要同步更新：

- `README.md` 的下载链接和 CI / Release 徽章
- `CHANGELOG.md` 的 release 链接
- `.github/ISSUE_TEMPLATE/config.yml`
- 本文档里的 remote 示例

## 3. 初始化并推送

```powershell
cd E:\电量工具
git init -b main
git add .
git commit -m "Initial public release: BatteryDock 1.0.0"
git remote add origin https://github.com/703488625/battery-dock.git
git push -u origin main
```

如果 Git 提示没有身份：

```powershell
git config --global user.name "你的名字"
git config --global user.email "你的 GitHub noreply 邮箱"
```

## 4. 检查 GitHub Actions

推送后打开 Actions：

- `build` 工作流应在 push / PR 时运行。
- 检查 Windows runner 能否 restore / build / publish。
- 如果失败，先看 `global.json` 和 `setup-dotnet` 的版本。

## 5. 创建首个 Release

推荐让 `.github/workflows/release.yml` 自动完成：

```powershell
git tag v1.0.0
git push origin v1.0.0
```

工作流会：

1. 发布自包含单文件
2. 打包 `BatteryDock-v1.0.0-win-x64.zip`
3. 生成 SHA-256
4. 创建 Release 并上传资产

也可以在本地生成后手动上传：

```powershell
.\scripts\release.ps1 -Version 1.0.0
```

然后到 GitHub → Releases → Draft a new release，上传：

```text
release\BatteryDock-v1.0.0-win-x64.zip
release\BatteryDock-v1.0.0-win-x64.zip.sha256
```

Release notes 可以直接使用 `CHANGELOG.md` 的 `[1.0.0]` 段落。

## 6. 仓库设置建议

- **Issues**：开启；使用 `.github/ISSUE_TEMPLATE`。
- **Discussions**：可选，适合协议适配和硬件兼容性讨论。
- **Security**：开启 Security Advisories；`SECURITY.md` 已提供说明。
- **Actions**：允许 GitHub Actions；Release workflow 需要 `contents: write`。
- **Branches**：保护 `main`，要求 PR 通过 `build` 检查（可选）。
- **About**：填写 Description、Website（可留空）、Topics。
- **Social preview**：可以上传 `docs/screenshot.png` 或单独做 1280×640 图。

## 7. 发布后检查

- [ ] README 的 Release 链接可打开
- [ ] zip 下载后能解压，`BatteryDock.exe` 能运行
- [ ] `.sha256` 与 zip 匹配
- [ ] CI badge（如果已加）显示绿色
- [ ] Issue 模板能正常打开
- [ ] `SECURITY.md` 和 `LICENSE` 显示正常
- [ ] 仓库 Topics / Description 已填写

## 8. 不要提交

`.gitignore` 已排除：

```text
dist/
release/
artifacts/
.dotnet/
.nuget/
bin/
obj/
*.log
电量坞.lnk
```

发布包走 GitHub Releases，不进入 Git 历史。
