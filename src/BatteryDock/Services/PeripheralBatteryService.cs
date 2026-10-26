using System.Text.RegularExpressions;
using BatteryDock.Models;

namespace BatteryDock.Services;

/// <summary>
/// Combines Bluetooth and 2.4 GHz HID battery sources into one device list.
/// </summary>
internal sealed class PeripheralBatteryService : IDisposable
{
    private readonly BluetoothBatteryService _bluetooth;
    private readonly WirelessHidBatteryService _wireless;
    private readonly LogitechHidppService _logitech;

    public PeripheralBatteryService(
        BluetoothBatteryService bluetooth,
        WirelessHidBatteryService wireless,
        LogitechHidppService logitech)
    {
        _bluetooth = bluetooth;
        _wireless = wireless;
        _logitech = logitech;
    }

    public async Task<IReadOnlyList<DeviceReading>> ScanAsync(
        CancellationToken cancellationToken,
        Action<IReadOnlyList<DeviceReading>>? preview = null)
    {
        var bluetoothPreviewed = false;
        IReadOnlyList<DeviceReading>? earlyWireless = null;
        IReadOnlyList<DeviceReading>? lastBluetoothPreview = null;
        Action<IReadOnlyList<DeviceReading>> bluetoothPreview = readings =>
        {
            bluetoothPreviewed = true;
            lastBluetoothPreview = readings;
            var combined = Merge(readings, earlyWireless ?? Array.Empty<DeviceReading>());
            preview?.Invoke(combined);
        };

        var bluetoothTask = _bluetooth.ScanAsync(cancellationToken, bluetoothPreview);
        var wirelessTask = _wireless.ScanAsync(cancellationToken);
        using var logitechTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        logitechTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        var logitechTask = _logitech.ScanAsync(logitechTimeout.Token);

        // HID has its own (slow) vendor queries. If Bluetooth enumeration is
        // delayed, show the 2.4G devices immediately instead of an empty loading
        // panel; the merged result replaces them when Bluetooth returns.
        var wireless = await wirelessTask.ConfigureAwait(false);

        IReadOnlyList<DeviceReading> logitech;
        try
        {
            logitech = await logitechTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Warn("罗技 HID++ 扫描超时，本次跳过罗技设备。");
            logitech = Array.Empty<DeviceReading>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("罗技 HID++ 扫描", ex);
            logitech = Array.Empty<DeviceReading>();
        }

        var combinedWireless = Merge(wireless, logitech);
        if (!bluetoothTask.IsCompleted)
        {
            if (!bluetoothPreviewed)
            {
                earlyWireless = combinedWireless;
                preview?.Invoke(combinedWireless);
            }
            else if (lastBluetoothPreview != null)
            {
                // Bluetooth preview already went out; refresh it with the
                // Logitech devices that finished afterwards.
                earlyWireless = combinedWireless;
                preview?.Invoke(Merge(lastBluetoothPreview, combinedWireless));
            }
        }
        else
        {
            earlyWireless = combinedWireless;
        }

        IReadOnlyList<DeviceReading> bluetooth;
        try
        {
            bluetooth = await bluetoothTask.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logger.Warn("蓝牙扫描超时，本次仅显示 2.4G 设备。");
            bluetooth = Array.Empty<DeviceReading>();
        }

        var merged = Merge(bluetooth, combinedWireless);
        return merged
            .OrderByDescending(reading => reading.IsConnected)
            .ThenBy(reading => KindOrder(reading.Kind))
            .ThenBy(reading => reading.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static List<DeviceReading> Merge(
        IReadOnlyList<DeviceReading> bluetooth,
        IReadOnlyList<DeviceReading> wireless)
    {
        var result = new List<DeviceReading>(bluetooth);
        foreach (var candidate in wireless)
        {
            var index = result.FindIndex(existing => SamePhysicalDevice(existing, candidate));
            if (index < 0)
            {
                result.Add(candidate);
                continue;
            }

            result[index] = ChooseBetter(result[index], candidate);
        }

        return result;
    }

    private static DeviceReading ChooseBetter(DeviceReading first, DeviceReading second)
    {
        // Prefer a live reading from either source. If both are live, the 2.4G
        // reading corresponds to the receiver actually in use right now.
        var firstFresh = first.Origin == BatteryOrigin.Current && first.Battery.HasValue;
        var secondFresh = second.Origin == BatteryOrigin.Current && second.Battery.HasValue;
        if (firstFresh != secondFresh)
        {
            return firstFresh ? first : second;
        }

        // When both sources are live, prefer the one that reports an exact
        // percentage over a voltage/level estimate.
        if (firstFresh && secondFresh && first.IsEstimated != second.IsEstimated)
        {
            return first.IsEstimated ? second : first;
        }

        if (first.Battery.HasValue != second.Battery.HasValue)
        {
            return first.Battery.HasValue ? first : second;
        }

        if (second.Transport == DeviceTransport.Wireless24G &&
            first.Transport != DeviceTransport.Wireless24G)
        {
            return second;
        }

        return first;
    }

    private static bool SamePhysicalDevice(DeviceReading first, DeviceReading second)
    {
        if (first.Key == second.Key) return true;
        var a = Normalize(first.Name);
        var b = Normalize(second.Name);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        return a.Length >= 5 && b.Length >= 5 &&
               (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }

    private static string Normalize(string name)
    {
        var text = name.ToLowerInvariant();
        foreach (var word in new[] { "razer", "logitech", "罗技", "dongle", "receiver", "wireless", "2.4g", "bluetooth", "multi-mode", "三模" })
        {
            text = text.Replace(word, string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        return Regex.Replace(text, @"[^\p{L}\p{N}]+", string.Empty);
    }

    private static int KindOrder(DeviceKind kind) => kind switch
    {
        DeviceKind.Keyboard => 0,
        DeviceKind.Mouse => 1,
        DeviceKind.Gamepad => 2,
        DeviceKind.Headset => 3,
        DeviceKind.Other => 4,
        DeviceKind.Phone => 5,
        _ => 6
    };

    public void Dispose()
    {
        _bluetooth.Dispose();
        _logitech.Dispose();
    }
}
