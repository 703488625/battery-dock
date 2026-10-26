namespace BatteryDock.Models;

public enum DeviceKind
{
    Keyboard,
    Mouse,
    Gamepad,
    Headset,
    Phone,
    Other
}

public enum BatteryOrigin
{
    None = 0,
    Current = 1,
    LastKnown = 2
}

public enum DeviceTransport
{
    Bluetooth = 0,
    Wireless24G = 1
}

public sealed class DeviceReading
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string Address { get; init; } = string.Empty;
    public DeviceKind Kind { get; init; } = DeviceKind.Other;
    public DeviceTransport Transport { get; init; } = DeviceTransport.Bluetooth;
    public bool IsConnected { get; init; }
    public int? Battery { get; init; }
    public bool IsEstimated { get; init; }
    public bool IsCharging { get; init; }
    public bool IsFull { get; init; }
    public BatteryOrigin Origin { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public bool IsFreshBattery => Origin == BatteryOrigin.Current && Battery.HasValue;
    public bool HasBattery => Battery.HasValue;
}
