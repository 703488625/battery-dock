# 协议与验证矩阵

这份文档记录 BatteryDock 使用的公开/逆向协议事实，方便排查设备兼容性和提交协议适配 PR。它不是厂商规范，也不包含任何厂商源代码。

> **只读优先**：电量查询全部是只读操作。本程序只在用户明确点击“应用 DPI / 应用回报率”时写入对应 HID 命令，不写固件、不修改板载配置。  
> **隐私**：日志可能包含设备名、VID/PID 和原始帧。提交 issue 前请先检查。

## 验证状态

| 标记 | 含义 |
|---|---|
| ✅ 实测 | 在开发者本机或维护者硬件上实际读取/写入成功 |
| ⚠️ 部分 | 协议已实现，但只在部分型号/部分命令上验证 |
| 🧪 未复测 | 代码已接入，当前没有对应硬件完成最终验证 |
| ❌ 不支持 | 已知型号/固件不开放该命令 |

## 总矩阵

| 设备族 | 传输 | 入口 | 状态 |
|---|---|---|---|
| BLE Battery Service | BLE GATT | `0x180F` / `0x2A19` | ✅ 通用路径 |
| Windows 设备电量属性 | Windows PnP | 系统属性 | ✅ 通用路径 |
| 经典蓝牙 HFP 电量 | 经典蓝牙 | `{104EA319-6EE2-4701-BD47-8DDBF425BBE5}` PID 2 | ⚠️ 依设备 |
| Razer 2.4G | HID feature report | 90 字节报告 | ✅ DeathAdder V2 X HyperSpeed |
| ATK Z87 / V75X | HID report 4 | Evision `1A` / legacy `87` | ✅ Z87；V75X 🧪 |
| ATK / VXE / VGN 鼠标 | HID | COMPX / HECHI / Nordic 52/54 | ⚠️ 部分型号 |
| Logitech HID++ 2.0 | HID report 0x10/0x11 | feature table | ✅ G PRO X SUPERLIGHT 电量；稳定 ID/热插拔 🧪 |
| ASUS ROG Omni | HID feature report | `0B05:1ACE` / `12 07` | 🧪 未实机 |
| MCHOSE L7 系列 | HID feature report 0x11 | command `0x06` | 🧪 未实机；G3 排除 |

---

## 蓝牙

### BLE GATT Battery Service

- Service：`0x180F`
- Characteristic：`0x2A19`（Battery Level，0–100）
- 适用于标准 BLE 键盘、鼠标、手柄、部分耳机。
- Windows 通过 `BluetoothLEDevice` / GATT 枚举读取；不需要厂商软件。

### Windows 设备电量属性

部分蓝牙设备不暴露 GATT Battery Service，但 Windows 自己会从驱动/固件取得电量。程序会读取系统设备属性作为补充来源。

### 经典蓝牙 HFP 电量

- 属性 GUID：`{104EA319-6EE2-4701-BD47-8DDBF425BBE5}`
- 属性 ID：`2`
- 主要用于部分耳机/经典蓝牙外设；不是所有设备都提供。

---

## Razer 2.4G

实现：`Services/WirelessHidBatteryService.cs` 中的 Razer 分支。

- 使用 **90 字节 feature report**。
- 帧头包含 transaction id、command class、command id、data size，最后一字节为 XOR 校验。
- 电量命令族：`0x07 / 0x80`
- 充电状态：`0x07 / 0x84`
- DPI 命令族：`0x04 / 0x85`、`0x04 / 0x05`、`0x04 / 0x86`、`0x04 / 0x06`
- 回报率：查询 `0x00 / 0xC0`，设置 `0x00 / 0x40`

实测：

- DeathAdder V2 X HyperSpeed：电量读取 ✅
- DPI 读取/写入 6400 ✅
- 回报率切换 ❌：该固件返回“不支持”，UI 会提示

---

## ATK / VXE / VGN

实现：`Services/WirelessHidBatteryService.cs`。

### Evision（ATK Z87 / V75X）

- 64 字节 HID report。
- 请求：`04 SeqLo SeqHi 1A`，其中 `Seq` 是非零 16 位序号。
- 回包必须回显相同的 `Seq` 和命令 `0x1A`。
- 电量：响应 byte 8；充电标志：byte 9。
- 旧版兼容：`04 87`；响应 byte 3 是原始电量。
- 旧协议在键盘休眠时可能返回 `0x00`，实现按“无读数/休眠”处理并回退到上次电量。

实测：

- ATK Z87：Evision 读取真实电量 ✅
- ATK V75X（`320F:5088`）：同一协议实现，🧪 未在 V75X 实机验证

### COMPX / HECHI / Nordic

- 2.4G 私有协议因型号而异，程序会依次尝试常见帧。
- COMPX：17 字节 report，report id 8，含校验字节。
- HECHI：report id 11。
- Nordic：常见 52/54 帧。
- 如果某型号仍读不到，通常是使用了尚未适配的私有协议，不代表接收器损坏。

---

## Logitech HID++ 2.0

实现：`Services/LogitechHidppService.cs`。

### 报文格式

- 短报文：report ID `0x10`，7 字节。
- 长报文：report ID `0x11`，20 字节。
- Software ID：`0x0B`。
- 接收器设备槽：`1..6`；接收器自身：`0xFF`。
- HID++ 2.0 请求头：`report[0]=reportId`、`report[1]=deviceIndex`、`report[2]=featureIndex`、`report[3]=(function << 4) | softwareId`，之后是参数。
- 实现会校验回复的 device index、software ID、feature index 和 function nibble，避免把迟到帧当成当前结果。

### 特性发现

- `0x0000` root：
  - function 0：按 feature ID 查询 feature index。
  - function 1：读取协议版本（要求 ≥ 2.0）。

### 稳定身份：`0x0003 deviceInformation`

function 0 的关键字节：

| 偏移 | 字段 |
|---|---|
| 1–4 | unit ID |
| 6 | transport flags |
| 7–12 | model ID（最多 3 个 PID，BE） |
| 13 | extended model ID |
| 14 | capabilities；bit 0 = 有序列号 |

function 2：读取序列号（最多 12 个 UTF-8 字节）。

程序优先使用 `sn:<serial>`；没有序列号时使用 `uid:<unitId>:<modelId>`；都没有时回退到型号/名称。

### 类型：`0x0005 deviceTypeAndName`

- function 1：设备名（长报文）。
- function 2：设备类型。

| 值 | 类型 |
|---|---|
| 0 | Keyboard |
| 2 | Numpad |
| 3 | Mouse |
| 4 | Trackpad |
| 5 | Trackball |
| 8 | Headset |
| 10/11/12/17/18 | Steering wheel / Joystick / Gamepad / Programmable controller / Car-sim pedals |
| 14/15 | Speaker / Microphone |

接收器设备优先使用 `0x41` 通知里的类型；直连设备才读取 `0x0005` function 2。这样避免在部分接收器上读取 function 2 导致超时。

### 电量特性优先级

实现顺序：

1. `0x1004` UnifiedBattery
2. `0x1000` BatteryLevelStatus
3. `0x0104` CenturionBatterySOC
4. `0x1001` BatteryVoltage
5. `0x1F20` ADC measurement

#### `0x1004` UnifiedBattery

function 0（capabilities）：

- byte 0：支持的档位 bit：`0x01` critical、`0x02` low、`0x04` good、`0x08` full。
- byte 1：bit 0 rechargeable、bit 1 state-of-charge（直接百分比）。

function 1（status）：

- byte 0：百分比（仅 capabilities 声明 SOC 时有效）。
- byte 1：档位 bit。
- byte 2：状态码。

状态码：

| 值 | 含义 | 当前 UI |
|---|---|---|
| 0 | discharging | 放电 |
| 1 | charging | 充电中 |
| 2 | charging nearly full | 充电中 |
| 3 | full | 已充满 |
| 4 | slow charging / firmware-specific | 不标记充电（按固件差异处理） |
| 5–7 | invalid / thermal / charging error | 不标记充电 |

如果设备只报档位，程序按 `full=90 / good=50 / low=20 / critical=5` 近似，并标记 `≈`。

#### `0x1000` BatteryLevelStatus

function 0 返回：byte 0 百分比、byte 2 状态码。

| 状态码 | 含义 |
|---|---|
| 0 | discharging |
| 1 / 2 / 4 | charging |
| 3 | full |

#### `0x0104` CenturionBatterySOC

function 0 返回：

- byte 0：百分比
- byte 1：重复百分比
- byte 2：1/2 充电中，3 已充满

#### `0x1001` BatteryVoltage

function 0 返回：

- byte 0–1：电压，BE，mV
- byte 2：flags
  - bit 7：外部供电
  - bit 0–2：0 charging、1 full、2 not charging
  - bit 3：快充
  - bit 4：慢充/涓流
  - bit 5：critical

程序使用 Solaar 风格的放电曲线把 3500–4186 mV 映射到 0–100%，并标记 `≈`。

#### `0x1F20` ADC

- byte 0–1：电压，BE，mV
- byte 2：bit 0 = 有充电信息，bit 1 = 正在充电
- 用于部分耳机/ADC 设备，百分比同样是推算值。

### DPI / 回报率

- DPI：`0x2201` AdjustableDPI、`0x2202` ExtendedAdjustableDPI。
- 回报率：`0x8060` ReportRate、`0x8061` ExtendedReportRate。
- 当前版本写当前档位，不写板载配置；侧键映射未开放。
- 本机未完成罗技 DPI/回报率实机验证。

### 接收器通知（热插拔）

- 短报文 register 读：`0x10, 0xFF, 0x81, register, 0, 0, 0`
- 短报文 register 写：`0x10, 0xFF, 0x80, register, v0, v1, v2`
- register `0x00`：24 位 BE 通知 flags；`WIRELESS` = `0x000100`。
- register `0x02` 写 `[02, 00, 00]`：让接收器为每个配对槽重新发送 `0x41`。
- `0x41` 短报告布局：

```text
[0] 0x10
[1] slot (1..6)
[2] 0x41
[3] protocol byte
[4] deviceInfo: low nibble = kind, bit 5 = encrypted, bit 6 = link not established
[5] WPID low
[6] WPID high
```

`online = (deviceInfo & 0x40) == 0`。接收器 kind 表与 `0x0005` 类型表不同：1 keyboard、2 mouse、3 numpad、4 presenter、5 remote、6 trackball、7 touchpad。

启用通知时会先读 register `0x00`，只置 `WIRELESS` 位再写回，保留其他位。

---

## ASUS ROG Omni

实现：`WirelessHidBatteryService.TryReadRog`。

- 接收器：`0B05:1ACE`
- Report ID：`0x03`
- 命令：`12 07`
- 电量：响应 byte 5
- 充电：响应 byte 10
- 有线形态使用 `00` 占位前缀，偏移相同
- 查询重试 3 次，校验报告 ID 和命令字，避免把休眠/键盘通道误认成鼠标

状态：🧪 本机无 ROG 设备，未实机验证。

---

## MCHOSE

实现：`WirelessHidBatteryService.TryReadMchose`。

- 接收器 VID/PID：`0x5253` / `0x3837`
- Feature Report：`0x11`
- 命令：`0x06`
- payload 整段 `^ 0xFF` 取反
- 逻辑 byte 9：电量
- 逻辑 byte 10：充电状态
- 首次空闲回包可能全零，程序会自动重试
- **G3 系列 `3837:4245` 使用另一套协议，明确排除**

状态：🧪 本机无 MCHOSE 鼠标，未实机验证。

---

## 公开参考

- OpenRazer：https://github.com/openrazer/openrazer
- HaloBattery：https://github.com/HeyOkay/HaloBattery
- ATK / VXE 协议说明：https://github.com/Ferris-echo/FerrisPulse/issues/31
- atktool：https://github.com/naseaoi/atktool
- logi-batter-display：https://github.com/Kamio-m/logi-batter-display
- logi-tray：https://github.com/yixing233/logi-tray
- Logitech HID++ 文档与 Solaar：https://github.com/pwr-Solaar/Solaar
- Linux `hid-logitech-hidpp`：https://github.com/torvalds/linux/blob/master/drivers/hid/hid-logitech-hidpp.c
- BatteryViewer：https://github.com/eamonwey/BatteryViewer
- mchose-linux：https://github.com/alexfrih/mchose-linux
- mchose-control-linux：https://github.com/zzxzzk115/mchose-control-linux

完整许可与参考说明见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。
