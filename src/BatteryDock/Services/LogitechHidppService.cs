using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BatteryDock.Models;
using HidSharp;
using HidSharp.Reports;

namespace BatteryDock.Services;

/// <summary>
/// Reads Logitech wireless devices through HID++ 2.0. Receivers expose several
/// device slots (1..6), so each slot is queried separately and a device name /
/// marketing type is read from feature 0x0005. Battery is tried via 0x1004
/// (unified battery), then 0x1000 (level/status), then 0x0104 / 0x1001 / 0x1F20.
/// </summary>
internal sealed class LogitechHidppService : IDisposable
{
    private const byte ShortReportId = 0x10;
    private const byte LongReportId = 0x11;
    private const byte SoftwareId = 0x0B;
    private const ushort FeatureRoot = 0x0000;
    private const ushort FeatureDeviceInformation = 0x0003;
    private const ushort FeatureDeviceName = 0x0005;
    private const ushort FeatureBatteryLevelStatus = 0x1000;
    private const ushort FeatureBatteryVoltage = 0x1001;
    private const ushort FeatureUnifiedBattery = 0x1004;
    private const ushort FeatureCenturionBatterySoc = 0x0104;
    private const ushort FeatureAdcMeasurement = 0x1F20;
    private const ushort FeatureAdjustableDpi = 0x2201;
    private const ushort FeatureExtendedAdjustableDpi = 0x2202;
    private const ushort FeatureReportRate = 0x8060;
    private const ushort FeatureExtendedReportRate = 0x8061;

    private static readonly TimeSpan RediscoverAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BindingLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DiscoveryBudget = TimeSpan.FromSeconds(8);

    private static readonly Regex KeyboardName = new(
        @"keyboard|mx keys|mx mechanical|\bcraft\b|\bk\d{3}\b|g915|g815|g715|g413|g513|g512|g610|g810|pro x tkl",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MouseName = new(
        @"mouse|mx master|mx anywhere|mx vertical|\blift\b|\bm\d{3}\b|g102|g203|g304|g305|g309|g403|g502|g604|g703|g903|\bg pro\b|superlight|lightspeed",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // "PRO X Wireless" is ambiguous: the G PRO X SUPERLIGHT mouse uses that
    // internal marketing name too. Prefer the authoritative 0x0005 device type
    // and only fall back to this list when the type is unavailable.
    private static readonly Regex HeadsetName = new(
        @"headset|g733|g535|g335|g435|astro",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly bool _demo;
    private readonly LastKnownStore _lastKnown;
    private readonly object _bindingLock = new();
    private readonly List<LogitechBinding> _bindings = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _listenerLock = new();
    private readonly Dictionary<string, ReceiverListener> _listeners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<byte, ReceiverSlot>>
        _slotStates = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private Task? _watcherTask;
    private int _forceDiscovery;
    private int _hotplugPending;
    private bool _disposed;

    /// <summary>
    /// Raised when a receiver reports a device link change so the UI can
    /// refresh immediately instead of waiting for the next poll.
    /// </summary>
    public event Action? DevicesChanged;

    public LogitechHidppService(bool demo, LastKnownStore lastKnown)
    {
        _demo = demo;
        _lastKnown = lastKnown;
        if (!_demo)
        {
            _watcherTask = Task.Run(() => WatchReceiversAsync(_lifetime.Token));
        }
    }

    public Task<IReadOnlyList<DeviceReading>> ScanAsync(CancellationToken cancellationToken)
    {
        if (_demo)
        {
            return Task.FromResult<IReadOnlyList<DeviceReading>>(DemoReadings());
        }

        return Task.Run(() => ScanCore(cancellationToken), cancellationToken);
    }

    public Task<MouseDpiState?> ReadDpiAsync(string deviceName, CancellationToken cancellationToken)
    {
        if (_demo)
        {
            return Task.FromResult<MouseDpiState?>(
                new MouseDpiState(1600, 1600, 100, 25600, 50, "Logitech HID++（演示）"));
        }

        return Task.Run(() => ReadDpiCore(deviceName, cancellationToken), cancellationToken);
    }

    public Task<bool> SetDpiAsync(
        string deviceName, int dpiX, int dpiY, CancellationToken cancellationToken)
    {
        if (_demo) return Task.FromResult(true);
        return Task.Run(() => SetDpiCore(deviceName, dpiX, dpiY, cancellationToken), cancellationToken);
    }

    private MouseDpiState? ReadDpiCore(string deviceName, CancellationToken cancellationToken)
    {
        var binding = FindBinding(deviceName);
        if (binding == null)
        {
            Discover(cancellationToken, DateTimeOffset.Now);
            binding = FindBinding(deviceName);
        }

        if (binding == null) return null;

        using var channel = new LogitechChannel(
            binding.Path, binding.InputReportLength, binding.OutputReportLength);
        if (!channel.TryGetProtocolVersion(binding.DeviceIndex, cancellationToken, out _))
        {
            throw new IOException("罗技设备休眠或未响应，请移动鼠标后重试。");
        }

        var dpi = channel.ReadDpi(binding.DeviceIndex, cancellationToken);
        if (dpi == null) return null;

        Logger.Info($"鼠标 DPI：{binding.Name} = {dpi.Value.X}x{dpi.Value.Y}（罗技 HID++）");
        return new MouseDpiState(dpi.Value.X, dpi.Value.Y, 50, 50000, 50, "Logitech HID++");
    }

    private bool SetDpiCore(
        string deviceName, int dpiX, int dpiY, CancellationToken cancellationToken)
    {
        var binding = FindBinding(deviceName);
        if (binding == null)
        {
            Discover(cancellationToken, DateTimeOffset.Now);
            binding = FindBinding(deviceName);
        }

        if (binding == null) return false;

        using var channel = new LogitechChannel(
            binding.Path, binding.InputReportLength, binding.OutputReportLength);
        if (!channel.TryGetProtocolVersion(binding.DeviceIndex, cancellationToken, out _))
        {
            throw new IOException("罗技设备休眠或未响应，请移动鼠标后重试。");
        }

        var success = channel.SetDpi(binding.DeviceIndex, dpiX, dpiY, cancellationToken);
        if (success)
        {
            Logger.Info($"鼠标 DPI：{binding.Name} 设置为 {dpiX}x{dpiY}（罗技 HID++）");
        }

        return success;
    }

    public Task<MousePollingState?> ReadPollingRateAsync(
        string deviceName, CancellationToken cancellationToken)
    {
        if (_demo)
        {
            return Task.FromResult<MousePollingState?>(
                new MousePollingState(1000, new[] { 125, 250, 500, 1000 }, "Logitech HID++（演示）"));
        }

        return Task.Run(() => ReadPollingRateCore(deviceName, cancellationToken), cancellationToken);
    }

    public Task<bool> SetPollingRateAsync(
        string deviceName, int hz, CancellationToken cancellationToken)
    {
        if (_demo) return Task.FromResult(true);
        return Task.Run(() => SetPollingRateCore(deviceName, hz, cancellationToken), cancellationToken);
    }

    private MousePollingState? ReadPollingRateCore(
        string deviceName, CancellationToken cancellationToken)
    {
        var binding = FindBinding(deviceName);
        if (binding == null)
        {
            Discover(cancellationToken, DateTimeOffset.Now);
            binding = FindBinding(deviceName);
        }

        if (binding == null) return null;

        using var channel = new LogitechChannel(
            binding.Path, binding.InputReportLength, binding.OutputReportLength);
        if (!channel.TryGetProtocolVersion(binding.DeviceIndex, cancellationToken, out _))
        {
            throw new IOException("罗技设备休眠或未响应，请移动鼠标后重试。");
        }

        var polling = channel.ReadPollingRate(binding.DeviceIndex, cancellationToken);
        if (polling == null) return null;

        Logger.Info($"鼠标回报率：{binding.Name} = {polling.Value.Hz}Hz（罗技 HID++）");
        return new MousePollingState(polling.Value.Hz, polling.Value.Rates, "Logitech HID++");
    }

    private bool SetPollingRateCore(
        string deviceName, int hz, CancellationToken cancellationToken)
    {
        var binding = FindBinding(deviceName);
        if (binding == null)
        {
            Discover(cancellationToken, DateTimeOffset.Now);
            binding = FindBinding(deviceName);
        }

        if (binding == null) return false;

        using var channel = new LogitechChannel(
            binding.Path, binding.InputReportLength, binding.OutputReportLength);
        if (!channel.TryGetProtocolVersion(binding.DeviceIndex, cancellationToken, out _))
        {
            throw new IOException("罗技设备休眠或未响应，请移动鼠标后重试。");
        }

        var success = channel.SetPollingRate(binding.DeviceIndex, hz, cancellationToken);
        if (success)
        {
            Logger.Info($"鼠标回报率：{binding.Name} 设置为 {hz}Hz（罗技 HID++）");
        }

        return success;
    }

    private LogitechBinding? FindBinding(string deviceName)
    {
        var target = NormalizeName(deviceName);
        if (target.Length == 0) return null;

        lock (_bindingLock)
        {
            foreach (var binding in _bindings)
            {
                var name = NormalizeName(binding.Name);
                if (name == target) return binding;
                if (name.Length >= 5 && target.Length >= 5 &&
                    (name.Contains(target, StringComparison.Ordinal) ||
                     target.Contains(name, StringComparison.Ordinal)))
                {
                    return binding;
                }
            }
        }

        return null;
    }

    private IReadOnlyList<DeviceReading> ScanCore(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (Interlocked.Exchange(ref _forceDiscovery, 0) != 0 ||
            now - _lastDiscovery >= RediscoverAfter)
        {
            Discover(cancellationToken, now);
        }

        lock (_bindingLock)
        {
            _bindings.RemoveAll(binding => now - binding.LastSeen > BindingLifetime);
        }

        var readings = new List<DeviceReading>();
        foreach (var binding in SnapshotBindings())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reading = ReadBinding(binding, cancellationToken);
                if (reading != null) readings.Add(reading);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Info($"罗技读取失败：{binding.Name}：{ex.Message}");
            }
        }

        if (readings.Count > 0)
        {
            Logger.Info("罗技扫描结果：" + string.Join(" | ", readings.Select(reading =>
                $"{reading.Name}={(reading.Battery?.ToString() ?? "?")}%/{reading.Origin}")));
        }

        return readings;
    }

    private void Discover(CancellationToken cancellationToken, DateTimeOffset now)
    {
        _lastDiscovery = now;
        var found = new List<LogitechBinding>();
        try
        {
            List<HidDevice> devices;
            try
            {
                devices = DeviceList.Local.GetHidDevices()
                    .Where(device => device.VendorID == 0x046D)
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.Error("枚举罗技 HID 设备", ex);
                return;
            }

            var candidates = new List<HidDevice>();
            foreach (var device in devices)
            {
                try
                {
                    if (IsHidppEndpoint(device)) candidates.Add(device);
                }
                catch (Exception ex)
                {
                    Logger.Info($"罗技 HID 描述符读取失败：{ex.Message}");
                }
            }

            // A receiver exposes several HID interfaces; one long-report
            // endpoint can address all device slots. Group by identity and use
            // the best endpoint for each receiver/device.
            var endpoints = candidates
                .GroupBy(EndpointGroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(device => SafeReportLength(device, Reports.Output) >= 20)
                    .ThenByDescending(device => SafeReportLength(device, Reports.Output))
                    .First())
                .ToArray();

            var stopwatch = Stopwatch.StartNew();
            foreach (var endpoint in endpoints)
            {
                if (cancellationToken.IsCancellationRequested ||
                    stopwatch.Elapsed > DiscoveryBudget)
                {
                    break;
                }

                var productName = SafeProductName(endpoint);
                var isReceiver = IsReceiver(endpoint, productName);
                var receiverKey = ReceiverGroupKey(endpoint);
                var slotMap = GetSlotMap(receiverKey);
                var indexes = isReceiver
                    ? (slotMap is { Count: > 0 }
                        ? slotMap.Keys.OrderBy(index => index).ToArray()
                        : new byte[] { 1, 2, 3, 4, 5, 6 })
                    : new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 };

                using var channel = new LogitechChannel(endpoint);
                foreach (var deviceIndex in indexes)
                {
                    if (cancellationToken.IsCancellationRequested ||
                        stopwatch.Elapsed > DiscoveryBudget)
                    {
                        break;
                    }

                    ReceiverSlot? slotState = null;
                    if (slotMap != null)
                    {
                        slotMap.TryGetValue(deviceIndex, out slotState);
                    }

                    // Trust an offline link report briefly, then probe anyway:
                    // a missed arrival notification must not hide the device
                    // until the next receiver trigger.
                    if (slotState is { Online: false } &&
                        now - slotState.Seen < TimeSpan.FromSeconds(15))
                    {
                        continue;
                    }

                    if (!channel.TryGetProtocolVersion(deviceIndex, cancellationToken, out var version) ||
                        version < 2.0)
                    {
                        continue;
                    }

                    var battery = channel.ReadBattery(deviceIndex, cancellationToken);
                    if (battery == null) continue;

                    var name = channel.ReadDeviceName(deviceIndex, cancellationToken);
                    if (string.IsNullOrWhiteSpace(name)) name = productName;
                    name = CleanName(name);
                    if (string.IsNullOrWhiteSpace(name)) name = "罗技无线设备";
                    name = MapModelName(name, endpoint.ProductID);

                    var stableId = channel.ReadStableId(deviceIndex, cancellationToken);
                    var typeKind = slotState?.Kind;
                    if (typeKind == null && !isReceiver)
                    {
                        typeKind = channel.ReadDeviceType(deviceIndex, cancellationToken);
                    }
                    var kind = typeKind ?? GuessKind(name, endpoint.ProductID);

                    if (!string.IsNullOrWhiteSpace(stableId))
                    {
                        Logger.Info($"罗技身份：{name} -> {stableId}");
                    }

                    found.Add(new LogitechBinding
                    {
                        Path = endpoint.DevicePath,
                        VendorId = endpoint.VendorID,
                        ProductId = endpoint.ProductID,
                        DeviceIndex = deviceIndex,
                        Name = name,
                        Kind = kind,
                        StableId = stableId,
                        ReceiverKey = receiverKey,
                        IsReceiver = isReceiver,
                        InputReportLength = SafeReportLength(endpoint, Reports.Input),
                        OutputReportLength = SafeReportLength(endpoint, Reports.Output),
                        LastSeen = now
                    });

                    if (!isReceiver) break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("发现罗技设备", ex);
        }
        finally
        {
            MergeBindings(found);
        }
    }

    private void MergeBindings(List<LogitechBinding> found)
    {
        var now = DateTimeOffset.Now;
        lock (_bindingLock)
        {
            var merged = _bindings
                .Where(binding => now - binding.LastSeen <= BindingLifetime)
                .ToList();

            foreach (var binding in found)
            {
                var index = merged.FindIndex(existing =>
                    !string.IsNullOrWhiteSpace(binding.StableId) &&
                    string.Equals(existing.StableId, binding.StableId, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    index = merged.FindIndex(existing =>
                        string.Equals(existing.Path, binding.Path, StringComparison.OrdinalIgnoreCase) &&
                        existing.DeviceIndex == binding.DeviceIndex);
                }
                if (index < 0)
                {
                    var normalized = NormalizeName(binding.Name);
                    index = merged.FindIndex(existing =>
                        string.Equals(NormalizeName(existing.Name), normalized, StringComparison.Ordinal));
                }

                if (index >= 0)
                {
                    if (string.IsNullOrWhiteSpace(binding.StableId))
                    {
                        binding.StableId = merged[index].StableId;
                    }
                    merged[index] = binding;
                }
                else
                {
                    merged.Add(binding);
                }
            }

            _bindings.Clear();
            _bindings.AddRange(merged);
        }
    }

    private LogitechBinding[] SnapshotBindings()
    {
        lock (_bindingLock)
        {
            return _bindings.ToArray();
        }
    }

    private ConcurrentDictionary<byte, ReceiverSlot>? GetSlotMap(string receiverKey)
    {
        return _slotStates.TryGetValue(receiverKey, out var map) ? map : null;
    }

    private DeviceKind? GetSlotKind(string receiverKey, byte deviceIndex)
    {
        if (_slotStates.TryGetValue(receiverKey, out var map) &&
            map.TryGetValue(deviceIndex, out var slot))
        {
            return slot.Kind;
        }

        return null;
    }

    private DeviceReading? ReadBinding(LogitechBinding binding, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        int? battery = null;
        var estimated = false;
        var charging = false;
        var full = false;
        var online = false;
        var source = string.Empty;
        string? liveName = null;

        if (binding.Online)
        {
            try
            {
                using var channel = new LogitechChannel(
                    binding.Path, binding.InputReportLength, binding.OutputReportLength);
                if (channel.TryGetProtocolVersion(binding.DeviceIndex, cancellationToken, out _))
                {
                    var batteryReading = channel.ReadBattery(binding.DeviceIndex, cancellationToken);
                    if (batteryReading != null)
                    {
                        battery = batteryReading.Percent;
                        estimated = batteryReading.Estimated;
                        charging = batteryReading.Charging;
                        full = batteryReading.Full;
                        source = batteryReading.Source;
                        online = true;
                        binding.LastSeen = now;
                    }

                    liveName = channel.ReadDeviceName(binding.DeviceIndex, cancellationToken);
                    if (string.IsNullOrWhiteSpace(binding.StableId))
                    {
                        binding.StableId = channel.ReadStableId(
                            binding.DeviceIndex, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Info($"罗技读取异常：{binding.Name}：{ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(liveName))
        {
            var cleaned = CleanName(liveName);
            if (cleaned.Length > 0) binding.Name = cleaned;
        }

        binding.Name = MapModelName(binding.Name, binding.ProductId);
        var kind = GetSlotKind(binding.ReceiverKey, binding.DeviceIndex)
                   ?? GuessKind(binding.Name, binding.ProductId);
        if (kind != DeviceKind.Other) binding.Kind = kind;

        var key = BuildKey(binding);
        var lastKnown = _lastKnown.Get(key);
        int? shownBattery = battery;
        var shownEstimated = estimated;
        BatteryOrigin origin;
        DateTimeOffset? observedAt;

        if (battery.HasValue)
        {
            origin = BatteryOrigin.Current;
            observedAt = now;
            _lastKnown.Remember(key, binding.Name, battery.Value, binding.Kind, now, estimated);
            Logger.Info($"罗技：{binding.Name} = {battery}%" +
                        $"{(estimated ? "（推算）" : string.Empty)}" +
                        $"{(charging ? "（充电中）" : full ? "（已充满）" : string.Empty)}" +
                        (string.IsNullOrEmpty(source) ? string.Empty : $" [来源 {source}]"));
        }
        else if (lastKnown != null)
        {
            shownBattery = lastKnown.Battery;
            shownEstimated = lastKnown.Estimated;
            origin = BatteryOrigin.LastKnown;
            observedAt = lastKnown.ObservedAt;
            if (binding.Kind == DeviceKind.Other) binding.Kind = lastKnown.Kind;
        }
        else
        {
            return null;
        }

        return new DeviceReading
        {
            Key = key,
            Name = binding.Name,
            Address = $"USB {binding.VendorId:X4}:{binding.ProductId:X4}",
            Kind = binding.Kind,
            Transport = DeviceTransport.Wireless24G,
            IsConnected = online,
            Battery = shownBattery,
            IsEstimated = shownEstimated,
            IsCharging = charging,
            IsFull = full,
            Origin = origin,
            ObservedAt = observedAt
        };
    }

    private static bool IsHidppEndpoint(HidDevice device)
    {
        if (SafeReportLength(device, Reports.Input) < 7 ||
            SafeReportLength(device, Reports.Output) < 7)
        {
            return false;
        }

        var descriptor = device.GetReportDescriptor();
        foreach (var item in descriptor.DeviceItems)
        {
            if (HasVendorPage(item)) return true;
        }

        return false;
    }

    private static bool HasVendorPage(DescriptorItem item)
    {
        foreach (var usage in item.Usages.GetAllValues())
        {
            var usagePage = (int)(usage >> 16);
            if (usagePage == 0xFF00 || usagePage == 0xFF43) return true;
        }

        foreach (var child in item.ChildItems)
        {
            if (HasVendorPage(child)) return true;
        }

        return false;
    }

    private static string EndpointGroupKey(HidDevice device)
    {
        return $"{device.VendorID:X4}:{device.ProductID:X4}:{SafeSerialNumber(device)}:{SafeProductName(device)}";
    }

    private static string ReceiverGroupKey(HidDevice device)
    {
        return $"{device.VendorID:X4}:{device.ProductID:X4}:{SafeSerialNumber(device)}";
    }

    private static bool IsReceiver(HidDevice device, string productName)
    {
        if (productName.Contains("receiver", StringComparison.OrdinalIgnoreCase) ||
            productName.Contains("接收器", StringComparison.OrdinalIgnoreCase) ||
            productName.Contains("dongle", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return device.ProductID is >= 0xC500 and <= 0xC5FF;
    }

    private static DeviceKind GuessKind(string name, int productId)
    {
        // The G PRO X SUPERLIGHT (receiver C547 / wired C094) reports the
        // marketing name "PRO X Wireless", which is also a headset name.
        if (productId is 0xC547 or 0xC094) return DeviceKind.Mouse;

        if (HeadsetName.IsMatch(name)) return DeviceKind.Headset;
        if (KeyboardName.IsMatch(name)) return DeviceKind.Keyboard;
        if (MouseName.IsMatch(name)) return DeviceKind.Mouse;
        return DeviceKind.Other;
    }

    private static string MapModelName(string name, int productId)
    {
        if (productId is 0xC547 or 0xC094 &&
            name.Equals("PRO X Wireless", StringComparison.OrdinalIgnoreCase))
        {
            return "G PRO X SUPERLIGHT";
        }

        return name;
    }

    private static string BuildKey(LogitechBinding binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.StableId))
        {
            return $"logi:{binding.StableId}";
        }

        // Stable IDs survive receiver slots and USB paths. This fallback only
        // applies to devices that do not expose feature 0x0003.
        return $"logi:fallback:{binding.VendorId:X4}:{binding.ProductId:X4}:{NormalizeName(binding.Name)}";
    }

    private static string NormalizeName(string name)
    {
        var text = name.ToLowerInvariant();
        foreach (var word in new[] { "logitech", "罗技", "wireless", "receiver", "dongle", "lightspeed" })
        {
            text = text.Replace(word, string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        return Regex.Replace(text, @"[^\p{L}\p{N}]+", string.Empty);
    }

    private static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (!char.IsControl(character)) builder.Append(character);
        }
        return builder.ToString().Trim();
    }

    private static string SafeProductName(HidDevice device)
    {
        try
        {
            return CleanName(device.GetProductName());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeSerialNumber(HidDevice device)
    {
        try
        {
            return CleanName(device.GetSerialNumber());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int SafeReportLength(HidDevice device, Reports reports)
    {
        try
        {
            return reports switch
            {
                Reports.Input => device.GetMaxInputReportLength(),
                Reports.Output => device.GetMaxOutputReportLength(),
                _ => device.GetMaxFeatureReportLength()
            };
        }
        catch
        {
            return 0;
        }
    }

    private enum Reports
    {
        Input,
        Output,
        Feature
    }

    private static IReadOnlyList<DeviceReading> DemoReadings()
    {
        return new[]
        {
            new DeviceReading
            {
                Key = "demo:logi:g502x",
                Name = "Logitech G502 X PLUS",
                Address = "USB 046D:C547",
                Kind = DeviceKind.Mouse,
                Transport = DeviceTransport.Wireless24G,
                IsConnected = true,
                Battery = 64,
                Origin = BatteryOrigin.Current,
                ObservedAt = DateTimeOffset.Now
            }
        };
    }

    private sealed class LogitechBinding
    {
        public required string Path { get; init; }
        public required int VendorId { get; init; }
        public required int ProductId { get; init; }
        public required byte DeviceIndex { get; init; }
        public required string Name { get; set; }
        public required DeviceKind Kind { get; set; }
        public string? StableId { get; set; }
        public required string ReceiverKey { get; init; }
        public bool Online { get; set; } = true;
        public required bool IsReceiver { get; init; }
        public required int InputReportLength { get; init; }
        public required int OutputReportLength { get; init; }
        public DateTimeOffset LastSeen { get; set; }
    }

    private sealed record ReceiverSlot(
        bool Online, DeviceKind? Kind, ushort Wpid, DateTimeOffset Seen);

    private sealed class BatteryRead
    {
        public BatteryRead(
            int percent, bool charging, bool full, bool estimated, string source)
        {
            Percent = percent;
            Charging = charging;
            Full = full;
            Estimated = estimated;
            Source = source;
        }

        public int Percent { get; }
        public bool Charging { get; }
        public bool Full { get; }
        public bool Estimated { get; }
        public string Source { get; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _lifetime.Cancel();
        }
        catch
        {
            // Ignore.
        }

        ReceiverListener[] listeners;
        lock (_listenerLock)
        {
            listeners = _listeners.Values.ToArray();
            _listeners.Clear();
        }

        foreach (var listener in listeners) listener.Dispose();
        try
        {
            _watcherTask?.Wait(500);
        }
        catch
        {
            // Ignore shutdown races.
        }

        _lifetime.Dispose();
    }

    private async Task WatchReceiversAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    SyncReceivers(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Info($"罗技接收器监听：{ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void SyncReceivers(CancellationToken cancellationToken)
    {
        List<HidDevice> logitechDevices;
        try
        {
            logitechDevices = DeviceList.Local.GetHidDevices()
                .Where(device => device.VendorID == 0x046D)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Info($"罗技接收器枚举失败：{ex.Message}");
            return;
        }

        var candidates = new List<HidDevice>();
        foreach (var device in logitechDevices)
        {
            try
            {
                if (IsHidppEndpoint(device)) candidates.Add(device);
            }
            catch
            {
                // Ignore descriptors that disappear during hot-plug.
            }
        }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(
                     ReceiverGroupKey, StringComparer.OrdinalIgnoreCase))
        {
            if (!group.Any(device => IsReceiver(device, SafeProductName(device))))
            {
                continue;
            }

            // HID++ 1.0 register access and 0x41 notifications use the short
            // 7-byte vendor collection; battery/feature traffic uses the long
            // 20-byte collection.
            var shortEndpoint = group
                .Where(device =>
                    SafeReportLength(device, Reports.Input) >= 7 &&
                    SafeReportLength(device, Reports.Output) >= 7 &&
                    SafeReportLength(device, Reports.Output) < 20)
                .OrderBy(device => SafeReportLength(device, Reports.Output))
                .FirstOrDefault();
            if (shortEndpoint == null) continue;
            found[group.Key] = shortEndpoint.DevicePath;
        }

        var changed = false;
        lock (_listenerLock)
        {
            foreach (var key in _listeners.Keys
                         .Where(key => !found.ContainsKey(key))
                         .ToArray())
            {
                _listeners[key].Dispose();
                _listeners.Remove(key);
                _slotStates.TryRemove(key, out _);
                Logger.Info($"罗技接收器已移除：{key}");
                changed = true;
            }

            foreach (var (key, path) in found)
            {
                if (_listeners.TryGetValue(key, out var existing))
                {
                    if (string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    existing.Dispose();
                    _listeners.Remove(key);
                }

                var listener = new ReceiverListener(
                    key, path, cancellationToken, OnReceiverSlot);
                _listeners[key] = listener;
                Logger.Info($"罗技接收器监听：{key} -> {path}");
                changed = true;
            }
        }

        if (changed)
        {
            Interlocked.Exchange(ref _forceDiscovery, 1);
            ScheduleDevicesChanged();
        }
    }

    private void OnReceiverSlot(
        string receiverKey,
        byte slot,
        bool online,
        DeviceKind? kind,
        ushort wpid)
    {
        var map = _slotStates.GetOrAdd(
            receiverKey, _ => new ConcurrentDictionary<byte, ReceiverSlot>());
        map[slot] = new ReceiverSlot(online, kind, wpid, DateTimeOffset.Now);
        Logger.Info($"罗技接收器通知：{receiverKey} 槽 {slot} " +
                    $"{(online ? "上线" : "离线")}" +
                    (kind.HasValue ? $"（{kind.Value}）" : string.Empty) +
                    $" WPID={wpid:X4}");

        lock (_bindingLock)
        {
            foreach (var binding in _bindings)
            {
                if (binding.DeviceIndex != slot ||
                    !string.Equals(binding.ReceiverKey, receiverKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                binding.Online = online;
                if (kind.HasValue) binding.Kind = kind.Value;
                if (online) binding.LastSeen = DateTimeOffset.Now;
            }
        }

        Interlocked.Exchange(ref _forceDiscovery, 1);
        ScheduleDevicesChanged();
    }

    private void ScheduleDevicesChanged()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _hotplugPending, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
                return;
            }

            Volatile.Write(ref _hotplugPending, 0);
            try
            {
                DevicesChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Info($"罗技热插拔回调异常：{ex.Message}");
            }
        });
    }

    private static DeviceKind? MapReceiverKind(byte nibble) => nibble switch
    {
        1 or 3 => DeviceKind.Keyboard, // keyboard, numpad
        2 or 6 or 7 => DeviceKind.Mouse, // mouse, trackball, touchpad
        _ => null
    };

    private sealed class ReceiverListener : IDisposable
    {
        private const byte ReceiverIndex = 0xFF;
        private const byte GetRegister = 0x81;
        private const byte SetRegister = 0x80;
        private const byte NotificationFlagsRegister = 0x00;
        private const byte ConnectionsRegister = 0x02;
        private const int NotificationFlagWireless = 0x000100;

        private readonly string _key;
        private readonly string _path;
        private readonly Action<string, byte, bool, DeviceKind?, ushort> _onSlot;
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;

        public string Path => _path;

        public ReceiverListener(
            string key,
            string path,
            CancellationToken parent,
            Action<string, byte, bool, DeviceKind?, ushort> onSlot)
        {
            _key = key;
            _path = path;
            _onSlot = onSlot;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            _task = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
            }
            catch
            {
                // Ignore.
            }

            try
            {
                _task.Wait(2000);
            }
            catch
            {
                // Ignore shutdown races.
            }

            _cts.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var hid = new HidNativeDevice(_path);
                    if (!await EnableNotificationsAsync(hid, cancellationToken))
                    {
                        throw new IOException("接收器未响应通知寄存器");
                    }

                    Logger.Info($"罗技接收器通知已启用：{_key}");
                    await TriggerArrivalAsync(hid, cancellationToken);
                    var misses = 0;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var report = await hid.ReadAsync(
                            7, TimeSpan.FromSeconds(2), cancellationToken);
                        if (report == null || report.Length < 7)
                        {
                            if (++misses >= 5) break;
                            continue;
                        }

                        misses = 0;
                        if (report[2] != 0x41) continue;

                        var slot = report[1];
                        var deviceInfo = report[4];
                        var kind = MapReceiverKind((byte)(deviceInfo & 0x0F));
                        var online = (deviceInfo & 0x40) == 0;
                        var wpid = (ushort)(report[5] | (report[6] << 8));
                        _onSlot(_key, slot, online, kind, wpid);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Info($"罗技接收器监听异常：{_key}：{ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static async Task<bool> EnableNotificationsAsync(
            HidNativeDevice hid, CancellationToken cancellationToken)
        {
            var flags = await ReadRegisterAsync(
                hid, NotificationFlagsRegister, cancellationToken);
            if (flags == null)
            {
                Logger.Info("罗技接收器读取通知标志失败，仍尝试触发设备上报。");
                return true;
            }

            var word = (flags[0] << 16) | (flags[1] << 8) | flags[2];
            if ((word & NotificationFlagWireless) == 0)
            {
                word |= NotificationFlagWireless;
                if (!await WriteRegisterAsync(hid, NotificationFlagsRegister,
                        new[]
                        {
                            (byte)(word >> 16),
                            (byte)(word >> 8),
                            (byte)word
                        }, cancellationToken))
                {
                    return false;
                }
            }

            return true;
        }

        private static Task<bool> TriggerArrivalAsync(
            HidNativeDevice hid, CancellationToken cancellationToken)
        {
            // Register 0x02 write [0x02, 0, 0] asks the receiver to re-emit a
            // 0x41 report for every paired slot, including sleeping ones.
            return WriteRegisterAsync(
                hid, ConnectionsRegister, new byte[] { 0x02, 0x00, 0x00 },
                cancellationToken);
        }

        private static async Task<byte[]?> ReadRegisterAsync(
            HidNativeDevice hid, byte register, CancellationToken cancellationToken)
        {
            var request = new byte[]
            {
                ShortReportId, ReceiverIndex, GetRegister, register, 0x00, 0x00, 0x00
            };
            _ = await hid.ReadAsync(7, TimeSpan.FromMilliseconds(10), cancellationToken);
            if (!await hid.WriteAsync(request, cancellationToken)) return null;

            var deadline = Environment.TickCount64 + 700;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await hid.ReadAsync(
                    7, TimeSpan.FromMilliseconds(200), cancellationToken);
                if (response is { Length: >= 7 } &&
                    response[2] == GetRegister &&
                    response[3] == register)
                {
                    return response[4..7];
                }
            }

            return null;
        }

        private static async Task<bool> WriteRegisterAsync(
            HidNativeDevice hid, byte register, byte[] value,
            CancellationToken cancellationToken)
        {
            var request = new byte[]
            {
                ShortReportId, ReceiverIndex, SetRegister, register,
                value.Length > 0 ? value[0] : (byte)0,
                value.Length > 1 ? value[1] : (byte)0,
                value.Length > 2 ? value[2] : (byte)0
            };
            _ = await hid.ReadAsync(7, TimeSpan.FromMilliseconds(10), cancellationToken);
            return await hid.WriteAsync(request, cancellationToken);
        }
    }

    private sealed class LogitechChannel : IDisposable
    {
        private readonly HidNativeDevice _hid;
        private readonly byte _reportId;
        private readonly int _reportLength;
        private readonly Dictionary<ushort, byte> _featureIndexes = new();

        public LogitechChannel(HidDevice device)
            : this(
                device.DevicePath,
                SafeReportLength(device, Reports.Input),
                SafeReportLength(device, Reports.Output))
        {
        }

        public LogitechChannel(string path, int inputLength, int outputLength)
        {
            _hid = new HidNativeDevice(path);
            if (inputLength >= 20 && outputLength >= 20)
            {
                _reportId = LongReportId;
                _reportLength = 20;
            }
            else
            {
                _reportId = ShortReportId;
                _reportLength = 7;
            }
        }

        public bool TryGetProtocolVersion(
            byte deviceIndex, CancellationToken cancellationToken, out double version)
        {
            version = 0;
            var payload = Call(deviceIndex, (byte)FeatureRoot, 0x01,
                new byte[] { 0x00, 0x00, 0x00 }, 500, cancellationToken);
            if (payload == null || payload.Length < 2) return false;

            version = payload[0] + payload[1] / 10.0;
            return version > 0;
        }

        public byte GetFeatureIndex(
            byte deviceIndex, ushort featureId, CancellationToken cancellationToken)
        {
            if (_featureIndexes.TryGetValue(featureId, out var cached)) return cached;

            var payload = Call(deviceIndex, (byte)FeatureRoot, 0x00,
                new[] { (byte)(featureId >> 8), (byte)featureId }, 500, cancellationToken);
            if (payload == null || payload.Length < 1 || payload[0] == 0) return 0;

            _featureIndexes[featureId] = payload[0];
            return payload[0];
        }

        public BatteryRead? ReadBattery(byte deviceIndex, CancellationToken cancellationToken)
        {
            // 0x1004 is preferred: it may report a real state-of-charge or only
            // coarse level bits, which the capabilities byte disambiguates.
            var feature = GetFeatureIndex(deviceIndex, FeatureUnifiedBattery, cancellationToken);
            if (feature > 0)
            {
                var caps = Call(deviceIndex, feature, 0x00,
                    Array.Empty<byte>(), 600, cancellationToken);
                var status = Call(deviceIndex, feature, 0x01,
                    Array.Empty<byte>(), 600, cancellationToken);

                if (status is { Length: >= 1 })
                {
                    var hasStateOfCharge = true;
                    if (caps is { Length: >= 2 })
                    {
                        hasStateOfCharge = (caps[1] & 0x02) != 0;
                    }

                    var levelBits = status.Length > 1 ? status[1] : 0;
                    if (!hasStateOfCharge && caps is { Length: >= 1 })
                    {
                        levelBits &= caps[0];
                    }

                    int? percent;
                    var estimated = false;
                    if (hasStateOfCharge)
                    {
                        percent = Math.Clamp((int)status[0], 0, 100);
                    }
                    else
                    {
                        percent = ApproximatePercentFromLevel(levelBits);
                        estimated = percent.HasValue;
                    }

                    if (percent.HasValue)
                    {
                        var statusCode = status.Length > 2 ? status[2] : 255;
                        return new BatteryRead(
                            percent.Value,
                            statusCode is 1 or 2,
                            statusCode == 3,
                            estimated,
                            "0x1004");
                    }
                }
            }

            // Older devices (G304 etc.).
            feature = GetFeatureIndex(deviceIndex, FeatureBatteryLevelStatus, cancellationToken);
            if (feature > 0)
            {
                var status = Call(deviceIndex, feature, 0x00,
                    Array.Empty<byte>(), 600, cancellationToken);
                if (status is { Length: >= 1 })
                {
                    var statusCode = status.Length > 2 ? status[2] : 255;
                    return new BatteryRead(
                        Math.Clamp((int)status[0], 0, 100),
                        statusCode is 1 or 2 or 4,
                        statusCode == 3,
                        false,
                        "0x1000");
                }
            }

            // Newer Centurion family.
            feature = GetFeatureIndex(deviceIndex, FeatureCenturionBatterySoc, cancellationToken);
            if (feature > 0)
            {
                var data = Call(deviceIndex, feature, 0x00,
                    Array.Empty<byte>(), 600, cancellationToken);
                if (data is { Length: >= 1 })
                {
                    var statusCode = data.Length > 2 ? data[2] : 0;
                    return new BatteryRead(
                        Math.Clamp((int)data[0], 0, 100),
                        statusCode is 1 or 2,
                        statusCode == 3,
                        false,
                        "0x0104");
                }
            }

            // Voltage-only devices use a discharge curve; mark as estimated.
            feature = GetFeatureIndex(deviceIndex, FeatureBatteryVoltage, cancellationToken);
            if (feature > 0)
            {
                var data = Call(deviceIndex, feature, 0x00,
                    Array.Empty<byte>(), 600, cancellationToken);
                if (data is { Length: >= 3 })
                {
                    var millivolts = (data[0] << 8) | data[1];
                    var percent = EstimatePercentFromVoltage(millivolts);
                    if (percent.HasValue)
                    {
                        var (charging, full) = MapVoltageFlags(data[2]);
                        return new BatteryRead(
                            percent.Value, charging, full, true, "0x1001");
                    }
                }
            }

            // G PRO X Wireless and selected headsets expose ADC instead.
            feature = GetFeatureIndex(deviceIndex, FeatureAdcMeasurement, cancellationToken);
            if (feature > 0)
            {
                var data = Call(deviceIndex, feature, 0x00,
                    Array.Empty<byte>(), 600, cancellationToken);
                if (data is { Length: >= 3 })
                {
                    var millivolts = (data[0] << 8) | data[1];
                    var percent = EstimatePercentFromVoltage(millivolts);
                    if (percent.HasValue)
                    {
                        var flags = data[2];
                        var charging = (flags & 0x01) != 0 && (flags & 0x02) != 0;
                        return new BatteryRead(
                            percent.Value, charging, false, true, "0x1F20");
                    }
                }
            }

            return null;
        }

        private static int? ApproximatePercentFromLevel(int levelBits)
        {
            if ((levelBits & 0x08) != 0) return 90;
            if ((levelBits & 0x04) != 0) return 50;
            if ((levelBits & 0x02) != 0) return 20;
            if ((levelBits & 0x01) != 0) return 5;
            return null;
        }

        private static (bool Charging, bool Full) MapVoltageFlags(int flags)
        {
            var state = 0; // discharging
            if ((flags & 0x80) != 0)
            {
                state = (flags & 0x07) switch
                {
                    0 => 1, // charging
                    1 => 3, // full
                    2 => 0, // discharging
                    _ => 255
                };
            }

            if ((flags & 0x10) != 0) state = 2;          // trickle
            else if ((flags & 0x08) != 0 && state == 0) state = 1; // fast

            return (state is 1 or 2, state == 3);
        }

        private static int? EstimatePercentFromVoltage(int millivolts)
        {
            if (millivolts <= 0) return null;
            if (millivolts >= 4186) return 100;
            if (millivolts <= 3500) return 0;

            for (var index = 0; index + 1 < VoltageCurve.Length; index++)
            {
                var high = VoltageCurve[index];
                var low = VoltageCurve[index + 1];
                if (millivolts <= high.Mv && millivolts >= low.Mv)
                {
                    var ratio = (double)(millivolts - low.Mv) / (high.Mv - low.Mv);
                    return (int)Math.Round(
                        low.Pct + (high.Pct - low.Pct) * ratio,
                        MidpointRounding.AwayFromZero);
                }
            }

            return null;
        }

        private static readonly (int Mv, int Pct)[] VoltageCurve =
        {
            (4186, 100), (4067, 90), (3989, 80), (3922, 70),
            (3859, 60), (3811, 50), (3778, 40), (3751, 30),
            (3717, 20), (3671, 10), (3646, 5), (3579, 2),
            (3500, 0)
        };

        public (int X, int Y)? ReadDpi(byte deviceIndex, CancellationToken cancellationToken)
        {
            var feature = GetFeatureIndex(deviceIndex, FeatureExtendedAdjustableDpi, cancellationToken);
            if (feature > 0)
            {
                var payload = Call(deviceIndex, feature, 0x05,
                    new byte[] { 0x00 }, 600, cancellationToken);
                if (payload is { Length: >= 5 })
                {
                    var x = (payload[1] << 8) | payload[2];
                    var y = (payload[3] << 8) | payload[4];
                    if (y <= 0) y = x;
                    if (x is >= 50 and <= 50000) return (x, y);
                }
            }

            feature = GetFeatureIndex(deviceIndex, FeatureAdjustableDpi, cancellationToken);
            if (feature > 0)
            {
                var payload = Call(deviceIndex, feature, 0x01,
                    new byte[] { 0x00 }, 600, cancellationToken);
                if (payload is { Length: >= 3 })
                {
                    var x = (payload[1] << 8) | payload[2];
                    if (x is >= 50 and <= 50000) return (x, x);
                }
            }

            return null;
        }

        public bool SetDpi(byte deviceIndex, int dpiX, int dpiY, CancellationToken cancellationToken)
        {
            var feature = GetFeatureIndex(deviceIndex, FeatureExtendedAdjustableDpi, cancellationToken);
            if (feature > 0)
            {
                var payload = Call(deviceIndex, feature, 0x06, new byte[]
                {
                    0x00,
                    (byte)(dpiX >> 8), (byte)dpiX,
                    (byte)(dpiY >> 8), (byte)dpiY
                }, 700, cancellationToken);
                if (payload != null) return true;
            }

            feature = GetFeatureIndex(deviceIndex, FeatureAdjustableDpi, cancellationToken);
            if (feature > 0)
            {
                var payload = Call(deviceIndex, feature, 0x03, new byte[]
                {
                    0x00,
                    (byte)(dpiX >> 8), (byte)dpiX
                }, 700, cancellationToken);
                if (payload != null) return true;
            }

            return false;
        }

        public (int Hz, int[] Rates)? ReadPollingRate(
            byte deviceIndex, CancellationToken cancellationToken)
        {
            var feature = GetFeatureIndex(deviceIndex, FeatureExtendedReportRate, cancellationToken);
            if (feature > 0)
            {
                var payload = Call(deviceIndex, feature, 0x02,
                    Array.Empty<byte>(), 600, cancellationToken);
                if (payload is { Length: >= 1 })
                {
                    var hz = payload[0] switch
                    {
                        6 => 8000,
                        5 => 4000,
                        4 => 2000,
                        3 => 1000,
                        2 => 500,
                        1 => 250,
                        0 => 125,
                        _ => 0
                    };
                    if (hz > 0)
                    {
                        return (hz, new[] { 125, 250, 500, 1000, 2000, 4000, 8000 });
                    }
                }
            }

            feature = GetFeatureIndex(deviceIndex, FeatureReportRate, cancellationToken);
            if (feature > 0)
            {
                var payload = Call(deviceIndex, feature, 0x01,
                    Array.Empty<byte>(), 600, cancellationToken);
                if (payload is { Length: >= 1 })
                {
                    var hz = payload[0] switch
                    {
                        1 => 1000,
                        2 => 500,
                        4 => 250,
                        8 => 125,
                        _ => 0
                    };
                    if (hz > 0) return (hz, new[] { 125, 250, 500, 1000 });
                }
            }

            return null;
        }

        public bool SetPollingRate(byte deviceIndex, int hz, CancellationToken cancellationToken)
        {
            var feature = GetFeatureIndex(deviceIndex, FeatureExtendedReportRate, cancellationToken);
            if (feature > 0)
            {
                var rateIndex = hz switch
                {
                    >= 8000 => (byte)6,
                    >= 4000 => (byte)5,
                    >= 2000 => (byte)4,
                    >= 1000 => (byte)3,
                    >= 500 => (byte)2,
                    >= 250 => (byte)1,
                    _ => (byte)0
                };
                var payload = Call(deviceIndex, feature, 0x03,
                    new[] { rateIndex }, 700, cancellationToken);
                if (payload != null) return true;
            }

            feature = GetFeatureIndex(deviceIndex, FeatureReportRate, cancellationToken);
            if (feature > 0)
            {
                var interval = hz switch
                {
                    >= 1000 => (byte)1,
                    >= 500 => (byte)2,
                    >= 250 => (byte)4,
                    _ => (byte)8
                };
                var payload = Call(deviceIndex, feature, 0x02,
                    new[] { interval }, 700, cancellationToken);
                if (payload != null) return true;
            }

            return false;
        }

        public string? ReadDeviceName(byte deviceIndex, CancellationToken cancellationToken)
        {
            var feature = GetFeatureIndex(deviceIndex, FeatureDeviceName, cancellationToken);
            if (feature == 0) return null;

            var payload = Call(deviceIndex, feature, 0x01,
                new byte[] { 0x00 }, 500, cancellationToken);
            if (payload == null || payload.Length == 0) return null;

            var name = Encoding.UTF8.GetString(payload).TrimEnd('\0').Trim();
            return name.Length > 0 ? name : null;
        }

        public string? ReadStableId(
            byte deviceIndex, CancellationToken cancellationToken)
        {
            // 0x0003 deviceInformation: unit ID + model ID + optional serial.
            var feature = GetFeatureIndex(
                deviceIndex, FeatureDeviceInformation, cancellationToken);
            if (feature == 0) return null;

            var info = Call(deviceIndex, feature, 0x00,
                new byte[] { 0x00, 0x00, 0x00 }, 500, cancellationToken);
            if (info is not { Length: >= 15 }) return null;

            var unit = info.AsSpan(1, 4);
            var hasUnit = false;
            foreach (var value in unit)
            {
                if (value != 0)
                {
                    hasUnit = true;
                    break;
                }
            }

            if ((info[14] & 0x01) != 0)
            {
                var serial = Call(deviceIndex, feature, 0x02,
                    new byte[] { 0x00, 0x00, 0x00 }, 500, cancellationToken);
                if (serial is { Length: >= 1 })
                {
                    var text = Encoding.UTF8.GetString(
                        serial, 0, Math.Min(serial.Length, 12));
                    var terminator = text.IndexOf('\0');
                    if (terminator >= 0) text = text[..terminator];
                    text = text.Trim();
                    if (text.Length > 0) return "sn:" + text;
                }
            }

            if (!hasUnit) return null;
            var model = info.AsSpan(7, 6);
            return "uid:" + Convert.ToHexString(unit) + ":" +
                   Convert.ToHexString(model);
        }

        public DeviceKind? ReadDeviceType(
            byte deviceIndex, CancellationToken cancellationToken)
        {
            var feature = GetFeatureIndex(deviceIndex, FeatureDeviceName, cancellationToken);
            if (feature == 0) return null;

            var payload = Call(deviceIndex, feature, 0x02,
                new byte[] { 0x00, 0x00, 0x00 }, 300, cancellationToken);
            if (payload is not { Length: >= 1 }) return null;

            // 0x0005 deviceTypeAndName type values.
            return payload[0] switch
            {
                0 or 2 => DeviceKind.Keyboard,
                3 or 4 or 5 => DeviceKind.Mouse,
                8 or 14 or 15 => DeviceKind.Headset,
                10 or 11 or 12 or 17 or 18 => DeviceKind.Gamepad,
                _ => null
            };
        }

        private byte[]? Call(
            byte deviceIndex,
            byte featureIndex,
            byte function,
            byte[] parameters,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            var report = new byte[_reportLength];
            report[0] = _reportId;
            report[1] = deviceIndex;
            report[2] = featureIndex;
            report[3] = (byte)((function << 4) | SoftwareId);
            if (parameters.Length > 0)
            {
                Array.Copy(parameters, 0, report, 4,
                    Math.Min(parameters.Length, _reportLength - 4));
            }

            // Drop a queued input report from an earlier request.
            _ = _hid.ReadAsync(_reportLength, TimeSpan.FromMilliseconds(10), cancellationToken)
                .GetAwaiter().GetResult();
            if (!_hid.WriteAsync(report, cancellationToken).GetAwaiter().GetResult()) return null;

            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) return null;

                var response = _hid.ReadAsync(
                        _reportLength,
                        TimeSpan.FromMilliseconds(Math.Min(remaining, 150)),
                        cancellationToken)
                    .GetAwaiter().GetResult();
                if (response == null || response.Length < 7) continue;

                // Receivers may answer as 0x01 or 0x81 for the same slot.
                if ((response[1] & 0x7F) != (deviceIndex & 0x7F)) continue;
                if ((response[3] & 0x0F) != SoftwareId) continue;
                if (response[2] == 0xFF) return null;
                if (response[2] != featureIndex) continue;
                if ((response[3] >> 4) != function) continue;

                return response[4..];
            }
        }

        public void Dispose() => _hid.Dispose();
    }
}
