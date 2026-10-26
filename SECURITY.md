# 安全说明

BatteryDock 是一个本地 Windows 托盘工具，设计目标是**不联网、不上传、不安装驱动、不要求管理员权限**。

## 支持版本

| 版本 | 支持 |
|---|---|
| 1.0.x | ✅ 当前公开版本 |
| < 1.0 | ❌ 未公开的早期构建，不提供安全更新 |

## 报告安全问题

请不要在公开 Issue 中贴出可利用细节、个人信息或完整日志。

优先使用 GitHub 的 **Security → Report a vulnerability**（Security Advisories）私下报告。请包含：

- 受影响版本
- 复现步骤
- 影响范围（例如：任意文件写入、权限提升、数据泄露）
- 你的系统版本

如果仓库没有启用 Security Advisories，请先开一个不含敏感细节的 Issue，请维护者提供私下渠道。

## 威胁模型

### 在本项目范围内

- 程序代码执行、路径处理、设置文件读写
- HID 设备访问和写入命令的边界
- 日志/设置中可能出现的隐私信息
- 更新/发布资产被替换（校验和、Release 流程）
- 依赖包（HidSharp、.NET）的已知漏洞

### 不在本项目范围内

- 厂商固件、接收器固件或驱动本身的漏洞
- 需要物理接触设备的攻击
- 用户主动把 `%LOCALAPPDATA%\BatteryDock` 或日志发给第三方
- Windows SmartScreen 对未签名 exe 的提示（这是预期行为）

## 设计约束

- 没有 HTTP 客户端、遥测、自动更新或后台网络服务。
- 不加载厂商 DLL，不执行厂商安装器。
- 电量查询为只读；DPI/回报率写入只在用户点击“应用”时发生。
- 不需要管理员权限；开机启动写入当前用户 `HKCU` 启动项。
- 发布包提供 SHA-256 校验文件；建议从 GitHub Releases 下载。

## 依赖

- [HidSharp 2.1.0](https://www.zer7.com/software/hidsharp)（HID 枚举）
- .NET 8 / WPF / Windows Forms（Microsoft）

第三方协议参考和许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
