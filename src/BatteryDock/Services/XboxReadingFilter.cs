using System.Text.RegularExpressions;
using BatteryDock.Models;

namespace BatteryDock.Services;

/// <summary>
/// XInput exposes four anonymous controller slots, while a Bluetooth Xbox
/// controller also occupies one of them. Prefer the BLE percentage and drop
/// the corresponding XInput placeholder so the same controller is not shown
/// twice. XInput cannot identify which slot belongs to the Bluetooth device,
/// so this follows the conservative K - N rule: keep at most
/// (XInput wireless slots - connected Bluetooth Xbox controllers) entries.
/// </summary>
internal static class XboxReadingFilter
{
    private static readonly Regex XboxNameRegex = new(
        @"xbox",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool IsXInput(DeviceReading reading) =>
        reading.Key.StartsWith("xinput:", StringComparison.OrdinalIgnoreCase);

    public static bool IsXboxBluetoothGamepad(DeviceReading reading) =>
        reading.IsConnected &&
        reading.Kind == DeviceKind.Gamepad &&
        reading.Transport == DeviceTransport.Bluetooth &&
        XboxNameRegex.IsMatch(reading.Name);

    public static IReadOnlyList<DeviceReading> SelectVisible(
        IReadOnlyList<DeviceReading> xinput,
        IReadOnlyList<DeviceReading> bluetooth)
    {
        var bluetoothXboxCount = bluetooth.Count(IsXboxBluetoothGamepad);
        var wireless = xinput
            .Where(reading => IsXInput(reading) && !reading.IsWired &&
                              reading.Transport == DeviceTransport.Wireless24G)
            .OrderByDescending(reading => reading.Battery.HasValue)
            .ThenBy(SlotOrder)
            .ToList();
        var wired = xinput.Where(reading => IsXInput(reading) && reading.IsWired).ToList();

        var keepCount = Math.Max(0, wireless.Count - bluetoothXboxCount);
        return wireless.Take(keepCount).Concat(wired).ToArray();
    }

    public static IReadOnlyList<DeviceReading> Dedupe(IEnumerable<DeviceReading> readings)
    {
        var all = readings.ToList();
        var selected = SelectVisible(all.Where(IsXInput).ToList(), all);
        var selectedKeys = new HashSet<string>(
            selected.Select(reading => reading.Key), StringComparer.OrdinalIgnoreCase);

        return all
            .Where(reading => !IsXInput(reading) || selectedKeys.Contains(reading.Key))
            .ToArray();
    }

    private static int SlotOrder(DeviceReading reading)
    {
        var suffix = reading.Key.StartsWith("xinput:", StringComparison.OrdinalIgnoreCase)
            ? reading.Key["xinput:".Length..]
            : string.Empty;
        return int.TryParse(suffix, out var slot) ? slot : int.MaxValue;
    }
}
