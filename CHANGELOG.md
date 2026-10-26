# Changelog

All notable changes to BatteryDock are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-26

First public release.

### Added

- Windows tray panel for Bluetooth and 2.4G keyboard / mouse / gamepad battery levels.
- Liquid-glass UI with ambient light field, gradient specular edges, glass cards,
  and matching light / dark / system themes.
- BLE GATT Battery Service (`0x180F` / `0x2A19`), Windows battery properties,
  and classic Bluetooth HFP battery fallback.
- 2.4G HID protocols:
  - Razer (90-byte feature report, battery / charging / DPI / polling-rate commands)
  - ATK Z87 and V75X Evision (`0x1A`) with legacy `0x87` fallback
  - ATK / VXE / VGN COMPX, HECHI, and Nordic 52/54 probes
  - Logitech HID++ 2.0 (`0x1004`, `0x1000`, `0x0104`, `0x1001`, `0x1F20`)
  - ASUS ROG Omni (`0B05:1ACE`, `12 07`)
  - MCHOSE L7 family (`0x5253` / `0x3837`, feature `0x11` / command `0x06`)
- Logitech stable identity via `0x0003` (serial, or unit ID + model ID).
- Logitech receiver `0x41` arrival/removal notifications and `0x02` arrival trigger.
- Charging / full badges, green charging tray icon, and estimated `≈` percentages.
- Adaptive refresh: 30 seconds while the panel is open, slower while hidden.
- Quiet hours and low-battery notifications; charging devices are suppressed.
- 104-key keyboard tester.
- Razer / Logitech DPI and polling-rate settings where the firmware supports it.
- Remember-last-known battery for sleeping devices; in-place UI refresh without
  an empty-state flash.
- Single instance, current-user startup entry, and local-only storage.
- Self-contained single-file `win-x64` build.

### Fixed

- Corrected Logitech `0x1000` charging status mapping.
- Recognized G PRO X SUPERLIGHT’s internal “PRO X Wireless” name as a mouse
  instead of a headset.
- Fixed mouse hit-testing in the glass segmented settings controls.
- Preserved the panel’s position when returning from keyboard / mouse tools.
- Avoided clearing the device list during partial scan previews.

### Known limitations

- Logitech stable-ID and `0x41` hotplug paths were not re-verified with the
  receiver present in the final test pass.
- ROG Omni and MCHOSE protocol implementations are untested on hardware.
- Some Razer firmware does not expose HID polling-rate switching.
- 2.4G private protocols vary by model and firmware; unsupported models need
  a dedicated adapter.
- WPF cannot provide real-time background refraction; the liquid-glass look is
  implemented with layered translucency, ambient gradients, and highlights.
- The executable is unsigned, so Windows SmartScreen may warn on first launch.

[1.0.0]: https://github.com/703488625/battery-dock/releases/tag/v1.0.0
