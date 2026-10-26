namespace BatteryDock.Models;

public sealed class LastKnownDevice
{
    public string Name { get; set; } = string.Empty;
    public int Battery { get; set; }
    public bool Estimated { get; set; }
    public DeviceKind Kind { get; set; } = DeviceKind.Other;
    public DateTimeOffset ObservedAt { get; set; }
}

public sealed class LastKnownState
{
    public Dictionary<string, LastKnownDevice> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
