using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BatteryDock.Services;

/// <summary>
/// Reads the battery value that Windows stores for Bluetooth hands-free (HFP)
/// devices. Some headsets and a few peripherals publish a battery property on
/// their BTHENUM node. This is only a compatibility fallback for classic
/// Bluetooth devices that do not expose a GATT Battery Service.
/// </summary>
internal sealed class HfpBatteryReader
{
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const int CmLocateDevNodeNormal = 0;
    private const int DevPropTypeByte = 0x03;
    private const int DevPropTypeInt32 = 0x06;
    private const int DevPropTypeUInt32 = 0x07;
    private const int DevPropTypeString = 0x12;

    private static readonly DevPropKey BatteryKey = new()
    {
        FormatId = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
        PropertyId = 2
    };

    private readonly Dictionary<string, string> _instanceByAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    private IEnumerable<string> EnumerateInstanceIds(string mac)
    {
        using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM");
        if (enumKey == null) yield break;

        foreach (var serviceName in enumKey.GetSubKeyNames())
        {
            if (!serviceName.Contains("111e", StringComparison.OrdinalIgnoreCase)) continue;

            using var serviceKey = enumKey.OpenSubKey(serviceName);
            if (serviceKey == null) continue;

            foreach (var instanceName in serviceKey.GetSubKeyNames())
            {
                if (!instanceName.Contains(mac, StringComparison.OrdinalIgnoreCase)) continue;
                yield return $"BTHENUM\\{serviceName}\\{instanceName}";
            }
        }
    }

    public int? Read(string normalizedAddress)
    {
        if (string.IsNullOrWhiteSpace(normalizedAddress)) return null;
        var mac = normalizedAddress.Replace(":", string.Empty).Replace("-", string.Empty).ToUpperInvariant();
        if (mac.Length != 12) return null;

        try
        {
            string? cachedInstance;
            lock (_sync)
            {
                _instanceByAddress.TryGetValue(mac, out cachedInstance);
            }

            if (cachedInstance != null)
            {
                var cached = ReadProperty(cachedInstance);
                if (cached.HasValue) return cached;
                lock (_sync)
                {
                    _instanceByAddress.Remove(mac);
                }
            }

            foreach (var instanceId in EnumerateInstanceIds(mac))
            {
                var value = ReadProperty(instanceId);
                if (!value.HasValue) continue;
                lock (_sync)
                {
                    _instanceByAddress[mac] = instanceId;
                }
                return value;
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"HFP battery read failed for {mac}: {ex.Message}");
        }

        return null;
    }

    private static int? ReadProperty(string instanceId)
    {
        int result = CM_Locate_DevNodeW(out int devNode, instanceId, CmLocateDevNodeNormal);
        if (result != CrSuccess) return null;

        int bufferSize = 0;
        var key = BatteryKey;
        result = CM_Get_DevNode_PropertyW(devNode, ref key, out int propertyType, IntPtr.Zero, ref bufferSize, 0);
        if (result != CrBufferSmall && result != CrSuccess) return null;
        if (bufferSize <= 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = CM_Get_DevNode_PropertyW(devNode, ref key, out propertyType, buffer, ref bufferSize, 0);
            if (result != CrSuccess) return null;

            int? value = propertyType switch
            {
                DevPropTypeByte when bufferSize >= 1 => Marshal.ReadByte(buffer),
                DevPropTypeInt32 when bufferSize >= 4 => Marshal.ReadInt32(buffer),
                DevPropTypeUInt32 when bufferSize >= 4 => unchecked((int)Marshal.ReadInt32(buffer)),
                DevPropTypeString => PtrToString(buffer, bufferSize),
                _ => null
            };

            if (value is >= 0 and <= 100) return value;
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? PtrToString(IntPtr buffer, int byteSize)
    {
        try
        {
            var text = Marshal.PtrToStringUni(buffer, byteSize / 2)?.Trim().TrimEnd('\0').Replace("%", string.Empty);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out int devNode, string deviceId, int flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(
        int devNode,
        ref DevPropKey propertyKey,
        out int propertyType,
        IntPtr propertyBuffer,
        ref int propertyBufferSize,
        int flags);
}
