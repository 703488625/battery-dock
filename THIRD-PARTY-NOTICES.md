# Third-party notices

BatteryDock is an independent implementation. The following projects provided
protocol documentation, ideas or libraries. No vendor software is required or
bundled.

## Libraries

- **HidSharp 2.1.0** — © James F. Bellinger, used for HID device enumeration.
  License: <http://www.zer7.com/files/oss/hidsharp/LICENSE.txt>
- **.NET 8 / WPF / Windows Forms** — Microsoft, MIT.

## UI / material design references

- **tantan1hao/wechat-apple-liquid-glass** — optical layering ideas for liquid
  glass (ambient light field, non-uniform specular rim, inner caustic band,
  layered content). Only visual principles were used; no code was copied.
  <https://github.com/tantan1hao/wechat-apple-liquid-glass>
- **Wei-Zhang09/LiquidGlassUI** (MIT) — material tokens and design guidance:
  radius scale, blur levels, tint/surface/highlight/shadow values, and the
  “readability before material fidelity” principle. Only design parameters
  were used; no source code was copied.
  <https://github.com/Wei-Zhang09/LiquidGlassUI>

## Bluetooth battery references

- **JPITSG/BluetoothBatteryMonitor** (MIT) — HFP battery property lookup and
  general Windows Bluetooth device handling ideas.
  <https://github.com/JPITSG/BluetoothBatteryMonitor>

## 2.4 GHz protocol references

- **OpenRazer** (GPL-2.0) — Razer HID report layout, battery command class
  `0x07` / command `0x80`, transaction ids and response handling.
  <https://github.com/openrazer/openrazer>
- **HaloBattery** (MIT) — Razer raw-level scaling and protocol notes for
  multiple vendor families.
  <https://github.com/HeyOkay/HaloBattery>
- **mouse_tray** — Nordic 52/54 Compx/ATK mouse protocol details.
  <https://github.com/Fan4Metal/mouse_tray>
- **2.4G-battery-viewer** — ATK/VXE wireless protocol notes.
  <https://github.com/yszyszysz233/2.4G-battery-viewer>
- **FerrisPulse issue #31** — ATK V75X Evision battery protocol: report `0x04`,
  non-zero 16-bit request sequence, command `0x1A`, percentage at byte 8 and
  charging flag at byte 9; Razer DPI command mapping `0x04/0x85`, `0x04/0x05`,
  `0x04/0x86`, `0x04/0x06`; Razer charging `0x07/0x84` and polling-rate
  commands `0x00/0xC0` / `0x00/0x40`.
  <https://github.com/Ferris-echo/FerrisPulse/issues/31>
- **FerrisPulse** (GPL-3.0) — additional Logitech HID++ / headset and device
  adaptation notes, including Logitech DPI features `0x2201` / `0x2202` and
  report-rate features `0x8060` / `0x8061`.
  Only protocol facts were used; no source code from this GPL project is
  included in BatteryDock.
  <https://github.com/Ferris-echo/FerrisPulse>
- **naseaoi/atktool** (ISC) — COMPX / HECHI ATK-VXE mouse battery protocols,
  report ids and charge-flag interpretation.
  <https://github.com/naseaoi/atktool>
- **Kamio-m/logi-batter-display** (MIT) — Logitech HID++ 2.0 report framing,
  feature ids `0x1000` / `0x1004` / `0x1001`, receiver device slots and device
  name lookup.
  <https://github.com/Kamio-m/logi-batter-display>
- **eamonwey/BatteryViewer** — ASUS ROG Omni receiver `0x12/0x07` battery query,
  report offsets and retry behavior; MCHOSE CZ/J protocol notes.
  <https://github.com/eamonwey/BatteryViewer>
- **yixing233/logi-tray** (GPL-3.0) — Logitech HID++ 2.0 battery feature order
  (`0x1004`, `0x1000`, `0x0104`, `0x1001`, `0x1F20`), capability-bit and
  voltage-curve semantics. Only protocol facts were used; no source code from
  this GPL project is included in BatteryDock.
  <https://github.com/yixing233/logi-tray>
- **andyvorld/LGSTrayBattery** (GPL-3.0) — HID++ 2.0 device type/name semantics,
  `0x0003` stable identity reads, receiver `0x41` arrival/removal notifications,
  and the “PRO X Wireless” naming ambiguity for the G PRO X SUPERLIGHT
  (`046D:C547`). Only protocol facts and design ideas were used; no source code
  from this GPL project is included in BatteryDock.
  <https://github.com/andyvorld/LGSTrayBattery>
- **Linux kernel `hid-logitech-hidpp` / Solaar** — upstream documentation for
  Logitech battery status codes and voltage-to-percentage approximation.
  <https://github.com/torvalds/linux/blob/master/drivers/hid/hid-logitech-hidpp.c>
  <https://github.com/pwr-Solaar/Solaar>
- **FerrisPulse issue #37** — ROG Omni / Harpe Ace Mini raw-frame and offline
  response (`0xAAFF`) semantics, plus the wired `0x00` placeholder format.
  <https://github.com/Ferris-echo/FerrisPulse/issues/37>
- **evren-oct/vigil-watt PR #58** — zero-access HID Feature handle idea for
  vendor collections that reject read/write access.
  <https://github.com/evren-oct/vigil-watt/pull/58>
- **alexfrih/mchose-linux** (MIT) — MCHOSE L7-family feature-report protocol:
  report `0x11`, command `0x06`, inverted payload, battery/charge offsets and
  the G3-family exclusion.
  <https://github.com/alexfrih/mchose-linux>
- **zzxzzk115/mchose-control-linux** (MIT) — independent MCHOSE verification
  (A7 Pro) and desktop/CLI integration ideas.
  <https://github.com/zzxzzk115/mchose-control-linux>
- **Lofree Hyzen web-driver protocol notes** as documented by HaloBattery —
  usage page `0xFF1C` / usage `0x92` keyboard collection background.

These notices are informational; the protocol facts were reimplemented in C#
for this app.
