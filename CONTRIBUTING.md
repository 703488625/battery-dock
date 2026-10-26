# 贡献指南

感谢你愿意帮 BatteryDock 适配更多设备。这个项目很小，维护重点是**可验证、可回退、只读优先**。

## 先开 Issue

在写代码前，建议先开一个 Issue，说明：

- 设备型号、VID/PID
- 连接方式：蓝牙 / 2.4G 接收器型号
- 已安装的厂商软件（G HUB、雷云、ATK HUB 等）
- `--verbose` 日志相关片段
- 你希望实现的是电量、DPI、回报率还是其他功能

协议适配尤其需要真实设备。没有硬件的型号可以提交协议资料，但会标记为“未实机验证”。

## 提交 Bug

请附上：

1. Windows 版本和 CPU 架构
2. 应用版本 / Release tag
3. 设备型号和 VID/PID
4. 复现步骤
5. `%LOCALAPPDATA%\BatteryDock\app.log` 的相关片段
6. 如果涉及 UI，附截图（注意遮挡个人信息）

日志可能包含设备名称和路径，提交前请检查。

## 提交协议适配

- 只提交你有权使用的协议资料；不要粘贴厂商闭源代码。
- 不要复制 GPL 项目的源码。可以记录协议事实，然后用本项目风格重新实现。
- 电量读取必须只读；如果协议包含写入命令，说明写入的影响范围。
- 读取失败返回 `null`，让上层回退到上次电量；不要返回 0%。
- 帧必须校验 report ID、命令、序列号回显和合理的百分比范围。
- 在 `docs/PROTOCOLS.md` 和 README 支持表中更新验证状态。
- 附上原始帧/日志时，先去掉序列号、地址等隐私信息。

## 代码风格

- C# 12 / .NET 8，WPF。
- 异步 I/O 优先；HID 读写必须有超时。
- UI 更新回到 Dispatcher 线程。
- 新增颜色/画刷放进 `Themes/Dark.xaml` 和 `Themes/Light.xaml`，使用 `DynamicResource`。
- 不要引入网络请求、遥测、后台服务或管理员权限。
- 保持改动聚焦；格式化/重排请单独提交。

## 本地验证

```powershell
dotnet build src\BatteryDock\BatteryDock.csproj -c Release
.\dist\BatteryDock.exe --demo --show
.\dist\BatteryDock.exe --show --verbose
```

手动检查清单见 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#手动测试清单)。

## Pull Request

PR 描述请包含：

- 解决了什么设备/问题
- 使用的协议和验证状态
- 测试过的硬件/系统
- 是否有写入操作
- 截图或日志（如适用）

提交信息建议：

```text
feat(logitech): add 0x0003 stable id
fix(ui): make segmented settings clickable
docs: add protocol matrix
```

## 许可证

提交代码即表示你同意以本项目的 MIT 许可证发布你的贡献。第三方协议资料请在 `THIRD-PARTY-NOTICES.md` 中注明来源。
