using System.Runtime.InteropServices;
using BatteryDock.Models;

namespace BatteryDock.Services;

/// <summary>
/// Reads Xbox-compatible controllers through the Windows XInput API. This is
/// the path used by the Xbox Wireless Adapter and USB-connected controllers;
/// Bluetooth controllers are read through the BLE Battery Service instead.
/// </summary>
internal sealed class XInputBatteryService
{
    private const uint ErrorSuccess = 0;
    private const byte BatteryDeviceTypeGamepad = 0;
    private const int SlotCount = 4;

    private readonly bool _demo;
    private bool _availabilityChecked;
    private bool _available;

    public XInputBatteryService(bool demo)
    {
        _demo = demo;
    }

    public IReadOnlyList<DeviceReading> Scan()
    {
        if (_demo || !EnsureAvailable()) return Array.Empty<DeviceReading>();

        var now = DateTimeOffset.Now;
        var states = new List<(int Slot, XInputBatteryType Type, XInputBatteryLevel Level)>();

        for (var slot = 0; slot < SlotCount; slot++)
        {
            try
            {
                if (Native.XInputGetState((uint)slot, out _) != ErrorSuccess) continue;

                var info = default(XInputBatteryInformation);
                var result = Native.XInputGetBatteryInformation(
                    (uint)slot, BatteryDeviceTypeGamepad, ref info);

                // A controller can be connected before it has reported battery
                // data. Keep it visible as a waiting device instead of hiding it.
                var type = result == ErrorSuccess
                    ? (XInputBatteryType)info.BatteryType
                    : XInputBatteryType.Disconnected;
                var level = result == ErrorSuccess
                    ? (XInputBatteryLevel)info.BatteryLevel
                    : XInputBatteryLevel.Empty;
                states.Add((slot, type, level));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                Logger.Warn("XInput 不可用：" + ex.Message);
                _available = false;
                return Array.Empty<DeviceReading>();
            }
        }

        var readings = new List<DeviceReading>(states.Count);
        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            var name = states.Count == 1 ? "Xbox 手柄" : $"Xbox 手柄 {index + 1}";
            readings.Add(CreateReading(state.Slot, state.Type, state.Level, name, now));
        }

        if (readings.Count > 0)
        {
            Logger.Info("XInput 扫描：" + string.Join(" | ", readings.Select(reading =>
                $"{reading.Name}={(reading.Battery?.ToString() ?? "?")}%/{reading.Transport}/{(reading.IsWired ? "有线" : "无线")}")));
        }

        return readings;
    }

    private bool EnsureAvailable()
    {
        if (_availabilityChecked) return _available;
        _availabilityChecked = true;

        try
        {
            // Any return value proves that xinput1_4.dll loaded; the slot itself
            // does not need to have a controller connected.
            Native.XInputGetState(0, out _);
            _available = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Logger.Warn("XInput 不可用，跳过 USB / 无线适配器手柄：" + ex.Message);
            _available = false;
        }

        return _available;
    }

    private static DeviceReading CreateReading(
        int slot,
        XInputBatteryType type,
        XInputBatteryLevel level,
        string name,
        DateTimeOffset now)
    {
        var isWired = type == XInputBatteryType.Wired;
        var battery = type is XInputBatteryType.Alkaline or XInputBatteryType.Nimh
            ? LevelToPercent(level)
            : null;
        var unavailableReason = type switch
        {
            XInputBatteryType.Wired => "USB 有线供电，无电池读数",
            XInputBatteryType.Disconnected => "手柄尚未上报电量，按一下手柄",
            XInputBatteryType.Unknown => "手柄暂未提供电量",
            _ => string.Empty
        };

        return new DeviceReading
        {
            Key = $"xinput:{slot}",
            Name = name,
            Address = $"XInput 槽位 {slot + 1}",
            Kind = DeviceKind.Gamepad,
            Transport = isWired ? DeviceTransport.Usb : DeviceTransport.Wireless24G,
            IsConnected = true,
            Battery = battery,
            IsEstimated = battery.HasValue,
            IsWired = isWired,
            UnavailableReason = unavailableReason,
            Origin = battery.HasValue ? BatteryOrigin.Current : BatteryOrigin.None,
            ObservedAt = battery.HasValue ? now : null
        };
    }

    private static int? LevelToPercent(XInputBatteryLevel level) => level switch
    {
        XInputBatteryLevel.Empty => 10,
        XInputBatteryLevel.Low => 40,
        XInputBatteryLevel.Medium => 70,
        XInputBatteryLevel.Full => 100,
        _ => null
    };

    private enum XInputBatteryType : byte
    {
        Disconnected = 0,
        Wired = 1,
        Alkaline = 2,
        Nimh = 3,
        Unknown = 255
    }

    private enum XInputBatteryLevel : byte
    {
        Empty = 0,
        Low = 1,
        Medium = 2,
        Full = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBatteryInformation
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    private static class Native
    {
        private const string Dll = "xinput1_4.dll";

        [DllImport(Dll, EntryPoint = "XInputGetState")]
        public static extern uint XInputGetState(uint dwUserIndex, out XInputState state);

        [DllImport(Dll, EntryPoint = "XInputGetBatteryInformation")]
        public static extern uint XInputGetBatteryInformation(
            uint dwUserIndex,
            byte devType,
            ref XInputBatteryInformation batteryInformation);
    }
}
