using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace BatteryDock.Services;

/// <summary>
/// Last-resort battery reader for HID collections that declare a standard
/// battery usage. Uses HidP_GetUsageValue so the field is located from the
/// report descriptor instead of assuming a vendor byte offset.
/// </summary>
internal static class HidStandardBatteryReader
{
    private const int HidpInput = 0;
    private const int HidpStatusSuccess = 0x00110000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;

    private static readonly (ushort Page, ushort Usage)[] BatteryUsages =
    {
        (0x0006, 0x0020), // Battery Strength
        (0x0085, 0x0066), // Relative State of Charge
        (0x0085, 0x0065), // Battery Level
        (0x0085, 0x0001)  // Absolute State of Charge
    };

    public static int? TryRead(HidDevice device, CancellationToken cancellationToken)
    {
        if (SafeReportLength(device, input: true) <= 0) return null;

        using var handle = Open(device.DevicePath);
        if (handle.IsInvalid) return null;

        if (!HidD_GetPreparsedData(handle, out var preparsed) ||
            preparsed == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var caps = new HidpCaps { Reserved = new ushort[17] };
            if (HidP_GetCaps(preparsed, ref caps) != HidpStatusSuccess ||
                caps.InputReportByteLength == 0)
            {
                return null;
            }

            var buffer = new byte[caps.InputReportByteLength];
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt > 0) Thread.Sleep(120);

                // HidD_GetInputReport returns the most recent report without
                // waiting for new data. Unlike FileStream.ReadAsync it cannot
                // leave a cancelled overlapped read behind, which would crash
                // the process when the completion callback fires.
                if (!HidD_GetInputReport(handle, buffer, buffer.Length)) continue;

                var value = ReadBatteryValue(preparsed, buffer, buffer.Length);
                if (value.HasValue)
                {
                    string name;
                    try { name = device.GetProductName(); }
                    catch { name = device.DevicePath; }
                    Logger.Info($"标准 HID 电量：{name} = {value}%");
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"标准 HID 电量探测失败：{ex.Message}");
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }

        return null;
    }

    private static int? ReadBatteryValue(IntPtr preparsed, byte[] report, int length)
    {
        var caps = new HidpValueCaps[256];
        for (var index = 0; index < caps.Length; index++)
        {
            caps[index].Reserved2 = new ushort[5];
            caps[index].UsageUnion = new ushort[8];
        }

        ushort count = (ushort)caps.Length;
        if (HidP_GetValueCaps(HidpInput, caps, ref count, preparsed) == HidpStatusSuccess)
        {
            for (var index = 0; index < Math.Min(count, caps.Length); index++)
            {
                var cap = caps[index];
                if (cap.UsagePage is not (0x0006 or 0x0085)) continue;

                var first = cap.UsageUnion[0];
                var last = cap.IsRange != 0 ? cap.UsageUnion[1] : first;
                for (var usage = (int)first; usage <= last; usage++)
                {
                    if (!IsBatteryUsage(cap.UsagePage, (ushort)usage)) continue;
                    if (TryGetValue(preparsed, report, length, cap.UsagePage,
                            cap.LinkCollection, (ushort)usage, out var value))
                    {
                        return value;
                    }
                }
            }
        }

        // Some drivers do not expose value caps but still answer a top-level
        // usage query.
        foreach (var (page, usage) in BatteryUsages)
        {
            if (TryGetValue(preparsed, report, length, page, 0, usage, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryGetValue(
        IntPtr preparsed, byte[] report, int length,
        ushort page, ushort linkCollection, ushort usage, out int value)
    {
        value = 0;
        var status = HidP_GetUsageValue(
            HidpInput, page, linkCollection, usage, out var raw,
            preparsed, report, (uint)length);
        if (status != HidpStatusSuccess || raw > 100) return false;
        value = (int)raw;
        return true;
    }

    private static bool IsBatteryUsage(ushort page, ushort usage) =>
        (page == 0x0006 && usage == 0x0020) ||
        (page == 0x0085 && usage is 0x0001 or 0x0065 or 0x0066);

    private static SafeFileHandle Open(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead,
            FileShare.Read | FileShare.Write,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        handle.Dispose();
        return CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShare.Read | FileShare.Write,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
    }

    private static int SafeReportLength(HidDevice device, bool input)
    {
        try
        {
            return input ? device.GetMaxInputReportLength() : device.GetMaxOutputReportLength();
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpValueCaps
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public ushort[] Reserved2;

        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public ushort[] UsageUnion;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(
        SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetInputReport(
        SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(
        int reportType,
        [Out] HidpValueCaps[] valueCaps,
        ref ushort valueCapsLength,
        IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        ushort usage,
        out uint usageValue,
        IntPtr preparsedData,
        byte[] report,
        uint reportLength);
}
