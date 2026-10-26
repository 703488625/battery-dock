# 开发与调试

## 环境

- Windows 10 19041+ / Windows 11，x64
- .NET 8 SDK
- Git
- 建议使用 Visual Studio 2022（.NET 桌面开发）或 Rider
- 运行设备工具时需要真实的蓝牙 / 2.4G 接收器；没有硬件时可用 `--demo`

项目只依赖 `HidSharp 2.1.0`。WPF、WinForms、Windows Bluetooth API 来自 .NET 8。

## 获取源码与构建

```powershell
git clone https://github.com/YOUR_GITHUB_USERNAME/battery-dock.git
cd battery-dock
dotnet restore src\BatteryDock\BatteryDock.csproj
dotnet build src\BatteryDock\BatteryDock.csproj -c Release
```

生成自包含单文件：

```powershell
.\scripts\build.ps1
```

或手动：

```powershell
dotnet publish src\BatteryDock\BatteryDock.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist
```

输出：`dist\BatteryDock.exe`（约 70 MB，不依赖已安装的 .NET 运行时）。

## 运行与调试

```text
BatteryDock.exe              正常启动，留在托盘
BatteryDock.exe --show       启动后直接打开面板
BatteryDock.exe --settings   启动后直接打开设置页
BatteryDock.exe --demo       演示模式（假数据，不读写真实设备）
BatteryDock.exe --verbose    输出详细日志
BatteryDock.exe --tray       静默启动（开机启动项使用）
```

调试时建议：

1. 先启动 `BatteryDock.exe --demo --show`，确认 UI 和主题没问题。
2. 再启动 `--verbose`，查看真实设备日志。
3. 日志文件：`%LOCALAPPDATA%\BatteryDock\app.log`。
4. 如果托盘图标在 `^` 里，把它拖到任务栏固定，方便反复打开。
5. 单实例：重复启动会通知已有实例，不会多开。

## 代码结构

```text
src/BatteryDock/
├─ Services/
│  ├─ BluetoothBatteryService.cs    BLE / Windows / HFP
│  ├─ WirelessHidBatteryService.cs  Razer / ATK / ROG / MCHOSE / COMPX
│  ├─ LogitechHidppService.cs       HID++ 2.0、稳定 ID、接收器通知
│  ├─ MouseDpiService.cs            DPI / 回报率路由
│  ├─ PeripheralBatteryService.cs   合并数据源
│  ├─ LastKnownStore.cs             上次电量
│  ├─ HidNativeDevice.cs            原生 HID 读写封装
│  └─ TrayIconManager.cs            托盘图标 / 菜单 / 通知
├─ ViewModels/
│  ├─ MainViewModel.cs              刷新、筛选、原地更新
│  ├─ DeviceItemViewModel.cs        设备卡片
│  └─ SettingsViewModel.cs          设置绑定
├─ Themes/                          Dark / Light / Styles
├─ Controls/                        BatteryBar / RoundedClipPanel
├─ TrayPopupWindow.xaml             主面板 + 设置页
├─ KeyboardTestWindow.xaml          按键测试
└─ MouseSettingsWindow.xaml         DPI / 回报率
```

更完整的数据流和线程模型见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 添加一个新 2.4G 协议

1. 确认接收器 VID/PID、HID 报告 ID、feature/output report 长度。
2. 先写一个**只读**探测函数，返回 `BatteryProbeResult?`：
   - 百分比 0–100
   - 是否充电
   - 是否充满
3. 在 `WirelessHidBatteryService` 的分发逻辑里按 VID/PID 加入。
4. 帧校验必须包含：
   - report ID
   - 命令/子命令
   - 序列号回显（如果协议有）
   - 合理的百分比范围
5. 读取失败时返回 `null`，让上层回退到上次电量；不要返回 0%。
6. 在 `docs/PROTOCOLS.md` 和 `README.md` 的支持表里标注验证状态。
7. 如果有写入命令，必须确认不会写板载配置/固件，并在 UI 中明确提示。

### HID 传输注意事项

- 每个 `HidNativeDevice` 的读写都有超时；不要让一个设备阻塞整个扫描。
- 某些国产接收器有多个 64 字节 vendor collection；需要尝试多个接口，但写入失败要跳过。
- 设备休眠时通常不返回数据；用“无读数”而不是 0% 表示。
- 不要假设所有设备的 feature report 长度相同；先读描述符。

## 添加蓝牙支持

蓝牙侧优先使用标准路径：

1. BLE GATT `0x180F/0x2A19`
2. Windows 设备电量属性
3. 经典蓝牙 HFP 属性

如果设备不提供这些，通常无法可靠读取电量。不要尝试绕过系统蓝牙栈。

## 手动测试清单

发布前至少验证：

- [ ] `--demo --show` 面板、设置、主题切换、键盘测试、鼠标设置窗口
- [ ] 浅色 / 深色 / 跟随系统切换，文字可读
- [ ] 刷新频率、低电量阈值、免打扰开关可点击并持久化
- [ ] 蓝牙设备出现 / 休眠 / 恢复
- [ ] 2.4G 设备休眠后上次电量回退
- [ ] 托盘左键 / 右键 / 退出
- [ ] 开机启动开关（可选，需注销验证）
- [ ] 单实例行为
- [ ] 无设备时的空状态
- [ ] `--verbose` 日志没有未处理异常

## 提交 Issue 时请附带

- Windows 版本、CPU 架构
- 设备型号、VID/PID、连接方式（蓝牙 / 接收器型号）
- 是否安装厂商软件（G HUB、雷云、ATK HUB 等）
- `--verbose` 日志相关片段（先删除个人信息）
- 如果能提供 HID 报告描述符或原始帧，请说明采集工具和权限

## 代码风格

- 使用 C# 12 / .NET 8 语法，但保持 WPF 兼容。
- 异步 I/O 优先；HID 读写必须带超时。
- UI 更新必须回到 UI 线程。
- 新增资源使用 `DynamicResource`，确保主题切换生效。
- 不要引入网络请求、遥测或后台服务。
- 不要复制 GPL 项目的源代码；只记录协议事实并重新实现。

## 相关文档

- [架构](ARCHITECTURE.md)
- [协议](PROTOCOLS.md)
- [发布](RELEASE.md)
- [贡献指南](../CONTRIBUTING.md)
