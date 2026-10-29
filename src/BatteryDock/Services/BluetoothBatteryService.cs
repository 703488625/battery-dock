using System.Globalization;
using System.Text.RegularExpressions;
using BatteryDock.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace BatteryDock.Services;

internal sealed class BluetoothBatteryService : IDisposable
{
    private static readonly Guid BatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    private static readonly string[] RequestedProperties =
    {
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.IsPaired",
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.Category",
        "System.Devices.BatteryLife"
    };

    private static readonly string[] MinimalProperties =
    {
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.IsPaired"
    };

    private static readonly string[] BatteryPropertyKeys =
    {
        "System.Devices.BatteryLife",
        "System.Devices.BatteryLevel",
        "System.Devices.BatteryLifePercent",
        "System.Devices.Aep.BatteryLevel",
        "System.Devices.Aep.BatteryLifePercent",
        "System.Devices.Aep.Bluetooth.Le.BatteryLevel"
    };

    private static readonly Regex MacRegex = new(
        @"(?:[0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HeadsetNameRegex = new(
        @"耳机|耳麦|headset|headphone|earbud|earbuds|airpods|buds|freebuds|tws|wh-1000|wi-1000",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex KeyboardNameRegex = new(
        @"键盘|keyboard|keychron|kbd|mx keys|magic keyboard|机械键盘",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MouseNameRegex = new(
        @"鼠标|mouse|mice|mx master|mx anywhere|trackball|轨迹球|无线鼠标",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GamepadNameRegex = new(
        @"手柄|gamepad|game pad|controller|xbox|dualsense|dualshock|joy-?con|switch pro|steam deck",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly bool _demo;
    private readonly LastKnownStore _lastKnown;
    private readonly HfpBatteryReader _hfp = new();

    public BluetoothBatteryService(bool demo, LastKnownStore lastKnown)
    {
        _demo = demo;
        _lastKnown = lastKnown;
    }

    public async Task<IReadOnlyList<DeviceReading>> ScanAsync(
        CancellationToken cancellationToken,
        Action<IReadOnlyList<DeviceReading>>? preview = null)
    {
        if (_demo)
        {
            await Task.Delay(650, cancellationToken);
            var demo = DemoReadings();
            preview?.Invoke(demo);
            return demo;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var leTask = EnumerateEndpointsAsync(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true, cancellationToken);
        var classicTask = EnumerateEndpointsAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true), false, cancellationToken);

        await Task.WhenAll(leTask, classicTask);
        var endpoints = leTask.Result.Concat(classicTask.Result).ToList();
        var groups = GroupEndpoints(endpoints);
        Logger.Info($"蓝牙扫描：{endpoints.Count} 个端点，归并为 {groups.Count} 台设备。");

        if (groups.Count > 0)
        {
            var preliminary = groups
                .Select(group => CreateReading(group, null, null, null))
                .OrderByDescending(reading => reading.IsConnected)
                .ThenBy(reading => KindOrder(reading.Kind))
                .ThenBy(reading => reading.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            preview?.Invoke(preliminary);
        }

        var readings = await Task.WhenAll(groups.Select(group => ReadGroupWithTimeoutAsync(group, cancellationToken)));
        var result = readings
            .OrderByDescending(reading => reading.IsConnected)
            .ThenBy(reading => KindOrder(reading.Kind))
            .ThenBy(reading => reading.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        Logger.Info("蓝牙扫描结果：" + string.Join(" | ", result.Select(reading =>
            $"{reading.Name}={(reading.Battery?.ToString() ?? "?")}%/{reading.Origin}/{(reading.IsConnected ? "在线" : "离线")}")));

        return result;
    }

    private async Task<List<Endpoint>> EnumerateEndpointsAsync(
        string selector, bool isLe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeviceInformationCollection devices;
        try
        {
            devices = await WithTimeout(
                DeviceInformation.FindAllAsync(
                    selector, RequestedProperties, DeviceInformationKind.AssociationEndpoint),
                TimeSpan.FromSeconds(6), cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Info($"读取扩展设备属性失败，改用最小属性集：{ex.Message}");
            try
            {
                devices = await WithTimeout(
                    DeviceInformation.FindAllAsync(
                        selector, MinimalProperties, DeviceInformationKind.AssociationEndpoint),
                    TimeSpan.FromSeconds(6), cancellationToken);
            }
            catch (Exception fallbackError)
            {
                Logger.Error(isLe ? "枚举 BLE 设备" : "枚举经典蓝牙设备", fallbackError);
                return new List<Endpoint>();
            }
        }

        var result = new List<Endpoint>(devices.Count);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = device.Properties;
            var name = CleanName(device.Name);
            var address = NormalizeAddress(GetString(properties, "System.Devices.Aep.DeviceAddress"))
                          ?? NormalizeAddress(ExtractLastMac(device.Id));

            int? propertyBattery = null;
            foreach (var key in BatteryPropertyKeys)
            {
                if (!properties.TryGetValue(key, out var raw)) continue;
                propertyBattery = ParseBatteryLevel(raw);
                if (propertyBattery.HasValue) break;
            }

            var categories = GetStringValues(properties.TryGetValue("System.Devices.Aep.Category", out var categoryValue)
                ? categoryValue
                : null);
            var kind = KindFromCategories(categories) ?? KindFromName(name);

            result.Add(new Endpoint
            {
                Id = device.Id,
                Name = name,
                Address = address,
                IsLe = isLe,
                IsConnected = GetBool(properties, "System.Devices.Aep.IsConnected"),
                IsPaired = GetBool(properties, "System.Devices.Aep.IsPaired"),
                PropertyBattery = propertyBattery,
                Kind = kind
            });
        }

        return result;
    }

    private async Task<DeviceReading> ReadGroupWithTimeoutAsync(
        DeviceGroup group, CancellationToken cancellationToken)
    {
        using var groupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        groupCts.CancelAfter(TimeSpan.FromSeconds(7));
        try
        {
            return await ReadGroupAsync(group, groupCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Info($"读取设备电量超时：{group.Name}");
            return CreateReading(group, null, null, null);
        }
        catch (Exception ex)
        {
            Logger.Info($"读取设备电量失败：{group.Name}：{ex.Message}");
            return CreateReading(group, null, null, null);
        }
    }

    private async Task<DeviceReading> ReadGroupAsync(DeviceGroup group, CancellationToken cancellationToken)
    {
        var kind = group.Kind;
        int? freshBattery = null;

        if (group.IsConnected)
        {
            var leEndpoint = group.Endpoints.FirstOrDefault(endpoint => endpoint.IsLe && endpoint.IsConnected);
            if (leEndpoint != null)
            {
                var outcome = await TryReadLeBatteryAsync(leEndpoint, cancellationToken);
                if (outcome.Battery.HasValue) freshBattery = outcome.Battery;
                kind = PreferKind(kind, outcome.Kind);
            }

            if (!freshBattery.HasValue)
            {
                var classicEndpoint = group.Endpoints.FirstOrDefault(endpoint => !endpoint.IsLe && endpoint.IsConnected);
                if (classicEndpoint != null)
                {
                    var outcome = await TryReadClassicBatteryAsync(classicEndpoint, cancellationToken);
                    if (outcome.Battery.HasValue) freshBattery = outcome.Battery;
                    kind = PreferKind(kind, outcome.Kind);
                }
            }

            if (!freshBattery.HasValue) freshBattery = group.PropertyBattery;
        }

        return CreateReading(group, freshBattery, kind, freshBattery.HasValue ? DateTimeOffset.Now : null);
    }

    private DeviceReading CreateReading(
        DeviceGroup group, int? freshBattery, DeviceKind? kind, DateTimeOffset? observedAt)
    {
        var resolvedKind = kind ?? group.Kind;
        var lastKnown = _lastKnown.Get(group.Key);
        int? battery = freshBattery;
        var origin = BatteryOrigin.None;
        DateTimeOffset? batteryTime = null;

        if (freshBattery.HasValue)
        {
            origin = BatteryOrigin.Current;
            batteryTime = observedAt ?? DateTimeOffset.Now;
            _lastKnown.Remember(group.Key, group.Name, freshBattery.Value, resolvedKind, batteryTime.Value);
        }
        else if (lastKnown != null)
        {
            battery = lastKnown.Battery;
            origin = BatteryOrigin.LastKnown;
            batteryTime = lastKnown.ObservedAt;
            if (resolvedKind == DeviceKind.Other) resolvedKind = lastKnown.Kind;
        }

        return new DeviceReading
        {
            Key = group.Key,
            Name = group.Name,
            Address = FormatAddress(group.Address),
            Kind = resolvedKind,
            Transport = DeviceTransport.Bluetooth,
            IsConnected = group.IsConnected,
            Battery = battery,
            Origin = origin,
            ObservedAt = batteryTime
        };
    }

    private async Task<(int? Battery, DeviceKind? Kind)> TryReadLeBatteryAsync(
        Endpoint endpoint, CancellationToken cancellationToken)
    {
        DeviceKind? lastKind = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await TryReadLeBatteryOnceAsync(endpoint, cancellationToken);
            lastKind = result.Kind ?? lastKind;

            if (result.Battery.HasValue || !result.Retryable)
            {
                return (result.Battery, lastKind);
            }

            if (attempt < 3)
            {
                Logger.Info($"BLE GATT 不可达，重试 {endpoint.Name}（{attempt}/3）");
                await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
            }
        }

        return (null, lastKind);
    }

    /// <summary>
    /// One GATT read attempt. Unreachable failures are retried by the caller
    /// after releasing the device/service references, which matches the
    /// recovery pattern needed when Windows keeps a stale BLE session.
    /// </summary>
    private async Task<(int? Battery, DeviceKind? Kind, bool Retryable)> TryReadLeBatteryOnceAsync(
        Endpoint endpoint, CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        try
        {
            device = await WithTimeout(
                BluetoothLEDevice.FromIdAsync(endpoint.Id),
                TimeSpan.FromSeconds(3), cancellationToken);
            if (device == null)
            {
                Logger.Info($"无法打开 BLE 设备：{endpoint.Name}");
                return (null, null, true);
            }

            var kind = KindFromAppearance(device.Appearance.RawValue);
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                Logger.Info($"BLE 设备当前不在连接状态：{endpoint.Name}");
                return (null, kind, false);
            }

            var services = await WithTimeout(
                device.GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Cached),
                TimeSpan.FromSeconds(3), cancellationToken);
            service = services.Services.FirstOrDefault();
            foreach (var extra in services.Services.Skip(1)) extra.Dispose();

            if (services.Status != GattCommunicationStatus.Success || service == null)
            {
                service?.Dispose();
                service = null;
                services = await WithTimeout(
                    device.GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Uncached),
                    TimeSpan.FromSeconds(5), cancellationToken);
                service = services.Services.FirstOrDefault();
                foreach (var extra in services.Services.Skip(1)) extra.Dispose();
            }

            if (services.Status != GattCommunicationStatus.Success || service == null)
            {
                Logger.Info($"设备没有可访问的电量服务：{endpoint.Name} ({services.Status})");
                return (null, kind, services.Status == GattCommunicationStatus.Unreachable);
            }

            var characteristics = await WithTimeout(
                service.GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Cached),
                TimeSpan.FromSeconds(2), cancellationToken);
            if (characteristics.Status != GattCommunicationStatus.Success ||
                characteristics.Characteristics.Count == 0)
            {
                characteristics = await WithTimeout(
                    service.GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Uncached),
                    TimeSpan.FromSeconds(4), cancellationToken);
            }

            var characteristic = characteristics.Characteristics.FirstOrDefault();
            if (characteristics.Status != GattCommunicationStatus.Success || characteristic == null)
            {
                Logger.Info($"设备没有电量特征值：{endpoint.Name} ({characteristics.Status})");
                return (null, kind, characteristics.Status == GattCommunicationStatus.Unreachable);
            }

            var read = await WithTimeout(
                characteristic.ReadValueAsync(BluetoothCacheMode.Uncached),
                TimeSpan.FromSeconds(4), cancellationToken);
            var battery = ParseGattBattery(read);

            if (!battery.HasValue)
            {
                var cachedRead = await WithTimeout(
                    characteristic.ReadValueAsync(BluetoothCacheMode.Cached),
                    TimeSpan.FromSeconds(2), cancellationToken);
                battery = ParseGattBattery(cachedRead);
                if (!battery.HasValue && cachedRead.Status == GattCommunicationStatus.Unreachable)
                {
                    return (null, kind, true);
                }
            }

            if (battery.HasValue) Logger.Info($"BLE 电量：{endpoint.Name} = {battery}%");
            return (battery, kind, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Logger.Info($"读取 BLE 电量超时：{endpoint.Name}");
            return (null, null, false);
        }
        catch (Exception ex)
        {
            Logger.Info($"读取 BLE 电量失败：{endpoint.Name}：{ex.Message}");
            return (null, null, false);
        }
        finally
        {
            service?.Dispose();
            device?.Dispose();
        }
    }

    private async Task<(int? Battery, DeviceKind? Kind)> TryReadClassicBatteryAsync(
        Endpoint endpoint, CancellationToken cancellationToken)
    {
        BluetoothDevice? device = null;
        try
        {
            device = await WithTimeout(BluetoothDevice.FromIdAsync(endpoint.Id), TimeSpan.FromSeconds(3), cancellationToken);
            if (device == null)
            {
                Logger.Info($"无法打开经典蓝牙设备：{endpoint.Name}");
                return (null, null);
            }

            var kind = KindFromClassOfDevice(device.ClassOfDevice);
            var battery = _hfp.Read(endpoint.Address ?? string.Empty);
            if (battery.HasValue) Logger.Info($"经典蓝牙电量：{endpoint.Name} = {battery}%");
            return (battery, kind);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Logger.Info($"读取经典蓝牙电量超时：{endpoint.Name}");
            return (null, null);
        }
        catch (Exception ex)
        {
            Logger.Info($"读取经典蓝牙电量失败：{endpoint.Name}：{ex.Message}");
            return (null, null);
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static async Task<T> WithTimeout<T>(
        IAsyncOperation<T> operation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await operation.AsTask(cancellationToken).WaitAsync(timeout, cancellationToken);
    }

    private static List<DeviceGroup> GroupEndpoints(IEnumerable<Endpoint> endpoints)
    {
        var groups = new Dictionary<string, DeviceGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            var key = !string.IsNullOrWhiteSpace(endpoint.Address)
                ? "addr:" + endpoint.Address
                : "id:" + endpoint.Id;
            if (!groups.TryGetValue(key, out var group))
            {
                group = new DeviceGroup(key);
                groups[key] = group;
            }
            group.Add(endpoint);
        }
        return groups.Values.ToList();
    }

    private static int? ParseBatteryLevel(object? raw)
    {
        if (raw == null) return null;
        try
        {
            if (raw is string text)
            {
                text = text.Trim().TrimEnd('%').Trim();
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                       parsed is >= 0 and <= 100
                    ? parsed
                    : null;
            }

            var value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            var number = (int)Math.Round(value);
            return number is >= 0 and <= 100 ? number : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ParseGattBattery(GattReadResult result)
    {
        if (result.Status != GattCommunicationStatus.Success ||
            result.Value == null ||
            result.Value.Length == 0)
        {
            return null;
        }

        try
        {
            using var reader = DataReader.FromBuffer(result.Value);
            var value = reader.ReadByte();
            return value <= 100 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static DeviceKind KindFromName(string name)
    {
        if (HeadsetNameRegex.IsMatch(name)) return DeviceKind.Headset;
        if (KeyboardNameRegex.IsMatch(name)) return DeviceKind.Keyboard;
        if (MouseNameRegex.IsMatch(name)) return DeviceKind.Mouse;
        if (GamepadNameRegex.IsMatch(name)) return DeviceKind.Gamepad;
        return DeviceKind.Other;
    }

    private static DeviceKind? KindFromCategories(IEnumerable<string> categories)
    {
        foreach (var category in categories.Select(value => value.ToLowerInvariant()))
        {
            if (category.Contains("input.keyboard")) return DeviceKind.Keyboard;
            if (category.Contains("input.mouse")) return DeviceKind.Mouse;
            if (category.Contains("input.gaming") || category.Contains("input.game") ||
                category.Contains("gamepad")) return DeviceKind.Gamepad;
            if (category.Contains("audio.headphone") || category.Contains("audio.headset") ||
                category.Contains("communication.headset")) return DeviceKind.Headset;
            if (category.Contains("communication.phone")) return DeviceKind.Phone;
        }
        return null;
    }

    private static DeviceKind? KindFromAppearance(ushort raw)
    {
        if (raw == 0) return null;
        var category = raw >> 6;
        var subcategory = raw & 0x3F;

        if (category == 0x0F)
        {
            return subcategory switch
            {
                0x01 => DeviceKind.Keyboard,
                0x02 or 0x09 => DeviceKind.Mouse,
                0x03 or 0x04 => DeviceKind.Gamepad,
                _ => DeviceKind.Other
            };
        }

        if (category is 0x08 or 0x0A or 0x25) return DeviceKind.Headset;
        return null;
    }

    private static DeviceKind? KindFromClassOfDevice(BluetoothClassOfDevice classOfDevice)
    {
        var minor = classOfDevice.MinorClass.ToString();
        var major = classOfDevice.MajorClass.ToString();
        if (minor.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Keyboard;
        if (minor.Contains("Pointing", StringComparison.OrdinalIgnoreCase) ||
            minor.Contains("Mouse", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Mouse;
        if (minor.Contains("Gamepad", StringComparison.OrdinalIgnoreCase) ||
            minor.Contains("Joystick", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Gamepad;
        if (major.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
            minor.Contains("Headset", StringComparison.OrdinalIgnoreCase) ||
            minor.Contains("Headphone", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Headset;
        return null;
    }

    private static DeviceKind PreferKind(DeviceKind current, DeviceKind? candidate)
    {
        if (current != DeviceKind.Other) return current;
        return candidate ?? current;
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

    private static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "未命名蓝牙设备";
        return name.Trim();
    }

    private static IEnumerable<string> GetStringValues(object? value)
    {
        if (value == null) return Array.Empty<string>();
        if (value is string text) return new[] { text };
        if (value is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object?>()
                .Where(item => item != null)
                .Select(item => item!.ToString() ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToArray();
        }
        return new[] { value.ToString() ?? string.Empty };
    }

    private static bool GetBool(IReadOnlyDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var raw) || raw == null) return false;
        try
        {
            return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, object> properties, string key)
    {
        return properties.TryGetValue(key, out var raw) ? raw as string : null;
    }

    private static string? ExtractLastMac(string text)
    {
        var matches = MacRegex.Matches(text);
        return matches.Count == 0 ? null : matches[^1].Value;
    }

    private static string? NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var compact = new string(address.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return compact.Length is 12 or 16 ? compact : null;
    }

    private static string FormatAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        var compact = address.Replace(":", string.Empty);
        if (compact.Length != 12) return address.ToUpperInvariant();
        return string.Join(":", Enumerable.Range(0, 6).Select(index => compact.Substring(index * 2, 2)))
            .ToUpperInvariant();
    }

    private static IReadOnlyList<DeviceReading> DemoReadings()
    {
        var now = DateTimeOffset.Now;
        return new[]
        {
            new DeviceReading
            {
                Key = "demo:mxkeys", Name = "MX Keys S", Address = "DEMO", Kind = DeviceKind.Keyboard,
                IsConnected = true, Battery = 78, Origin = BatteryOrigin.Current, ObservedAt = now
            },
            new DeviceReading
            {
                Key = "demo:mxmaster", Name = "MX Master 3S", Address = "DEMO", Kind = DeviceKind.Mouse,
                IsConnected = true, Battery = 16, Origin = BatteryOrigin.Current, ObservedAt = now
            },
            new DeviceReading
            {
                Key = "demo:xbox", Name = "Xbox Wireless Controller", Address = "DEMO", Kind = DeviceKind.Gamepad,
                IsConnected = true, Battery = 64, Origin = BatteryOrigin.Current, ObservedAt = now
            },
            new DeviceReading
            {
                Key = "demo:keychron", Name = "Keychron K3", Address = "DEMO", Kind = DeviceKind.Keyboard,
                IsConnected = false, Battery = 45, Origin = BatteryOrigin.LastKnown, ObservedAt = now.AddHours(-2)
            },
            new DeviceReading
            {
                Key = "demo:sony", Name = "Sony WH-1000XM5", Address = "DEMO", Kind = DeviceKind.Headset,
                IsConnected = true, Battery = 88, Origin = BatteryOrigin.Current, ObservedAt = now
            }
        };
    }

    public void Dispose()
    {
        // GATT sessions are opened per scan and disposed immediately. Nothing
        // persistent is held here.
    }

    private sealed class Endpoint
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Address { get; init; }
        public bool IsLe { get; init; }
        public bool IsConnected { get; init; }
        public bool IsPaired { get; init; }
        public int? PropertyBattery { get; init; }
        public DeviceKind Kind { get; init; }
    }

    private sealed class DeviceGroup
    {
        private readonly List<Endpoint> _endpoints = new();

        public DeviceGroup(string key) => Key = key;

        public string Key { get; }
        public IReadOnlyList<Endpoint> Endpoints => _endpoints;
        public string Name { get; private set; } = "未命名蓝牙设备";
        public string? Address { get; private set; }
        public bool IsConnected { get; private set; }
        public DeviceKind Kind { get; private set; } = DeviceKind.Other;
        public int? PropertyBattery { get; private set; }

        public void Add(Endpoint endpoint)
        {
            _endpoints.Add(endpoint);
            if (string.IsNullOrWhiteSpace(Name) || Name == "未命名蓝牙设备") Name = endpoint.Name;
            if (string.IsNullOrWhiteSpace(Address)) Address = endpoint.Address;
            IsConnected |= endpoint.IsConnected;
            if (Kind == DeviceKind.Other && endpoint.Kind != DeviceKind.Other) Kind = endpoint.Kind;
            if (!PropertyBattery.HasValue) PropertyBattery = endpoint.PropertyBattery;
        }
    }
}
