using System.Reflection;
using System.Text;
using HidSharp;

namespace BatteryDock.Services;

/// <summary>
/// Writes a local HID diagnostic report: interfaces, usage pages, report
/// lengths/IDs, report descriptors, one raw input frame and the standard
/// battery-field fallback result. Intended for adapting unknown receivers.
/// </summary>
internal static class HidDiagnosticsService
{
    public static string WriteReport()
    {
        AppPaths.EnsureDataDirectory();
        var path = Path.Combine(
            AppPaths.DataDirectory,
            $"hid-dump-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var builder = new StringBuilder();
        builder.AppendLine("BatteryDock HID diagnostic");
        builder.AppendLine($"Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($"App: {GetAppVersion()}");
        builder.AppendLine("WARNING: device names, paths and addresses may be present.");
        builder.AppendLine();

        IReadOnlyList<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices().ToArray();
        }
        catch (Exception ex)
        {
            builder.AppendLine($"Failed to enumerate HID devices: {ex.Message}");
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
            return path;
        }

        builder.AppendLine($"HID interfaces: {devices.Count}");
        builder.AppendLine();

        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            builder.AppendLine($"===== [{index + 1}/{devices.Count}] =====");
            builder.AppendLine($"VID/PID : {Safe(() => $"{device.VendorID:X4}:{device.ProductID:X4}")}");
            builder.AppendLine($"Product : {Safe(() => device.GetProductName())}");
            builder.AppendLine($"Vendor  : {Safe(() => device.GetManufacturer())}");
            builder.AppendLine($"Serial  : {Safe(() => device.GetSerialNumber())}");
            builder.AppendLine($"Path    : {Safe(() => device.DevicePath)}");
            builder.AppendLine(
                $"Reports : in={SafeInt(() => device.GetMaxInputReportLength())} " +
                $"out={SafeInt(() => device.GetMaxOutputReportLength())} " +
                $"feature={SafeInt(() => device.GetMaxFeatureReportLength())}");

            DescribeDescriptor(builder, device);
            AppendRawInput(builder, device);
            AppendStandardBattery(builder, device);
            builder.AppendLine();
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        Logger.Info($"HID 诊断已导出：{path}");
        return path;
    }

    private static void DescribeDescriptor(StringBuilder builder, HidDevice device)
    {
        try
        {
            var descriptor = device.GetReportDescriptor();
            var usages = new HashSet<uint>();

            foreach (var item in descriptor.DeviceItems)
            {
                foreach (var usage in item.Usages.GetAllValues()) usages.Add(usage);
                foreach (var child in item.ChildItems)
                {
                    foreach (var usage in child.Usages.GetAllValues()) usages.Add(usage);
                }
            }

            builder.AppendLine("Usages  : " + string.Join(", ",
                usages.Select(usage =>
                    $"page=0x{(usage >> 16):X4} usage=0x{(usage & 0xFFFF):X4}")));

            builder.AppendLine("Input   : " + DescribeReports(descriptor.InputReports));
            builder.AppendLine("Output  : " + DescribeReports(descriptor.OutputReports));
            builder.AppendLine("Feature : " + DescribeReports(descriptor.FeatureReports));

            var raw = device.GetRawReportDescriptor();
            if (raw.Length > 0)
            {
                builder.AppendLine("Descriptor:");
                builder.AppendLine(Hex(raw, maxBytes: 1024));
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"Descriptor: {ex.Message}");
        }
    }

    private static string DescribeReports(IEnumerable<HidSharp.Reports.Report> reports)
    {
        var parts = reports
            .Select(report => $"id=0x{report.ReportID:X2} len={report.Length}")
            .ToArray();
        return parts.Length == 0 ? "-" : string.Join(", ", parts);
    }

    private static void AppendRawInput(StringBuilder builder, HidDevice device)
    {
        var length = SafeInt(() => device.GetMaxInputReportLength());
        if (length <= 0 || length > 512) return;

        try
        {
            using var hid = new HidNativeDevice(device.DevicePath);
            var response = ReadWithHardTimeout(
                hid, length, TimeSpan.FromMilliseconds(150));
            builder.AppendLine("Raw input:");
            builder.AppendLine(response == null ? "(no frame / timed out)" : Hex(response));
        }
        catch (Exception ex)
        {
            builder.AppendLine($"Raw input: {ex.Message}");
        }
    }

    private static void AppendStandardBattery(StringBuilder builder, HidDevice device)
    {
        try
        {
            var task = Task.Run(() =>
                HidStandardBatteryReader.TryRead(device, CancellationToken.None));
            if (!task.Wait(TimeSpan.FromMilliseconds(700)))
            {
                builder.AppendLine("Standard battery: timeout");
                return;
            }

            var value = task.Result;
            builder.AppendLine($"Standard battery: {(value.HasValue ? $"{value}%" : "-")}");
        }
        catch (Exception ex)
        {
            builder.AppendLine($"Standard battery: {ex.Message}");
        }
    }

    private static byte[]? ReadWithHardTimeout(
        HidNativeDevice hid, int length, TimeSpan timeout)
    {
        try
        {
            var task = hid.ReadAsync(length, timeout, CancellationToken.None);
            if (!task.Wait(timeout + TimeSpan.FromMilliseconds(250)))
            {
                // A HID read can ignore CancellationToken on some drivers.
                hid.Dispose();
                return null;
            }
            return task.GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    private static string Hex(byte[] bytes, int maxBytes = int.MaxValue)
    {
        var count = Math.Min(bytes.Length, maxBytes);
        return string.Join(" ", bytes.Take(count).Select(value => value.ToString("X2")));
    }

    private static string Safe(Func<string?> getter)
    {
        try { return getter() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static int SafeInt(Func<int> getter)
    {
        try { return getter(); }
        catch { return 0; }
    }

    private static string GetAppVersion()
    {
        try
        {
            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "-";
        }
        catch
        {
            return "-";
        }
    }
}
