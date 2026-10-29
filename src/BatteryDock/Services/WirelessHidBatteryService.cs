using System.Globalization;
using System.Text.RegularExpressions;
using BatteryDock.Models;
using HidSharp;

namespace BatteryDock.Services;

/// <summary>
/// Reads 2.4 GHz peripherals and wired controllers through their vendor HID
/// collections. There is no standard battery protocol: each vendor uses its
/// own command. Implementations here cover Razer, ATK/VXE, ROG Omni, MCHOSE,
/// Sony DualSense (USB), GameSir T4N2, and BETOP Kunpeng receivers.
/// </summary>
internal sealed class WirelessHidBatteryService
{
    private static readonly HashSet<int> SupportedVendors =
        new() { 0x1532, 0x373B, 0x3554, 0x320F, 0x0B05, 0x5253, 0x3837, 0x054C, 0x3537, 0x20BC };
    private static readonly int[] RazerTransactionIds = { 0x1F, 0x9F, 0x3F, 0xFF };
    private static int _atkRequestCode;

    private sealed record BatteryProbeResult(
        int Percent,
        bool Charging,
        bool Full,
        DeviceTransport Transport = DeviceTransport.Wireless24G,
        string? StableId = null);

    private static readonly Regex HeadsetRegex = new(
        @"headset|headphone|earbud|airpods|buds|blackshark|barracuda|kraken|nari",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KeyboardRegex = new(
        @"keyboard|keychron|blackwidow|huntsman|deathstalker|ornata|cynosa|z87|atk z|v75|g815|g915|g915|k70|k65|k100",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MouseRegex = new(
        @"mouse|deathadder|basilisk|viper|naga|mamba|lancehead|orochi|cobra|pro click|g502|g304|g305|g pro|gpx|superlight|zaopin|scyrox|dareu",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GameSirNameRegex = new(
        @"gamesir|nova|t4n2|controller|gamepad|xbox",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BetopNameRegex = new(
        @"betop|btp|kunpeng|鲲鹏|controller|gamepad",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly bool _demo;
    private readonly LastKnownStore _lastKnown;

    public WirelessHidBatteryService(bool demo, LastKnownStore lastKnown)
    {
        _demo = demo;
        _lastKnown = lastKnown;
    }

    public Task<IReadOnlyList<DeviceReading>> ScanAsync(CancellationToken cancellationToken)
    {
        if (_demo)
        {
            return Task.FromResult<IReadOnlyList<DeviceReading>>(DemoReadings());
        }

        return Task.Run(() => ScanCore(cancellationToken), cancellationToken);
    }

    private IReadOnlyList<DeviceReading> ScanCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices()
                .Where(device => SupportedVendors.Contains(device.VendorID))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Error("枚举 2.4G HID 设备", ex);
            return Array.Empty<DeviceReading>();
        }

        var groups = new Dictionary<string, HidGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name;
            string serial;
            try
            {
                name = device.GetProductName()?.Trim() ?? string.Empty;
            }
            catch
            {
                continue;
            }

            // Interfaces with no product string are normally virtual
            // collections; without a name they cannot be presented usefully.
            if (string.IsNullOrWhiteSpace(name)) continue;
            try
            {
                serial = device.GetSerialNumber() ?? string.Empty;
            }
            catch
            {
                serial = string.Empty;
            }

            var normalizedName = NormalizeName(name);
            var key = $"{device.VendorID:X4}:{device.ProductID:X4}:{normalizedName}";
            if (!groups.TryGetValue(key, out var group))
            {
                group = new HidGroup(device.VendorID, device.ProductID, name, serial);
                groups[key] = group;
            }
            group.Devices.Add(device);
        }

        Logger.Info($"2.4G HID：{devices.Count} 个接口，归并为 {groups.Count} 台设备：" +
                    string.Join(" | ", groups.Values.Select(group =>
                        $"{group.Name} [{group.VendorId:X4}:{group.ProductId:X4}]")));

        var readings = new List<DeviceReading>();
        foreach (var group in groups.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reading = ReadGroup(group, cancellationToken);
                if (reading != null) readings.Add(reading);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Info($"读取 2.4G 设备失败：{group.Name}：{ex.Message}");
            }
        }

        var result = readings
            .OrderByDescending(reading => reading.IsConnected)
            .ThenBy(reading => KindOrder(reading.Kind))
            .ThenBy(reading => reading.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        Logger.Info("2.4G 扫描结果：" + string.Join(" | ", result.Select(reading =>
            $"{reading.Name}={(reading.Battery?.ToString() ?? "?")}%/{reading.Origin}")));
        return result;
    }

    private DeviceReading? ReadGroup(HidGroup group, CancellationToken cancellationToken)
    {
        var isRazer = group.VendorId == 0x1532;
        var isRog = group.VendorId == 0x0B05;
        var isMchose = group.VendorId is 0x5253 or 0x3837;
        var isMchoseG3 = group.VendorId == 0x3837 && group.ProductId == 0x4245;
        var isSony = group.VendorId == 0x054C;
        var isDualSense = isSony && group.ProductId is 0x0CE6 or 0x0DF2;
        var likelyWirelessRazer = isRazer && IsLikelyWirelessRazer(group);
        var kind = GuessKind(group);
        var isAtkKeyboard = IsAtkKeyboard(group);
        var isGameSir = group.VendorId == 0x3537 &&
                        kind is not (DeviceKind.Keyboard or DeviceKind.Mouse) &&
                        (group.ProductId == 0x02E0 || GameSirNameRegex.IsMatch(group.Name));
        var isGameSirBluetooth = isGameSir && group.ProductId == 0x02E0;
        var isBetop = group.VendorId == 0x20BC &&
                      kind is not (DeviceKind.Keyboard or DeviceKind.Mouse) &&
                      BetopNameRegex.IsMatch(group.Name);

        // The G3 family uses a different protocol; do not feed it the L7
        // mouse commands or classify it as a supported mouse.
        if (isMchoseG3) return null;

        // Sony sells many HID devices under the same vendor id. Only the
        // DualSense gamepad interface has the report layout we understand;
        // never send a mouse/ATK probe to an unrelated Sony device.
        if (isSony && !isDualSense) return null;

        BatteryProbeResult? battery = null;
        string? unavailableReason = null;
        if (isRazer)
        {
            battery = TryReadRazer(group, cancellationToken);
        }
        else if (isRog)
        {
            battery = TryReadRog(group, cancellationToken);
            if (battery != null) kind = DeviceKind.Mouse;
        }
        else if (isMchose)
        {
            battery = TryReadMchose(group, cancellationToken);
            if (battery != null) kind = DeviceKind.Mouse;
        }
        else if (isDualSense)
        {
            kind = DeviceKind.Gamepad;
            battery = TryReadDualSense(group, cancellationToken);
            if (battery == null) unavailableReason = "DualSense 暂未上报电量，请用 USB 连接后重试";
        }
        else if (isGameSir)
        {
            kind = DeviceKind.Gamepad;
            if (isGameSirBluetooth)
            {
                unavailableReason = "盖世小鸡蓝牙模式不开放电量查询";
            }
            else
            {
                battery = TryReadGameSir(group, cancellationToken);
                if (battery == null) unavailableReason = "盖世小鸡接收器暂未响应电量查询";
            }
        }
        else if (isBetop)
        {
            kind = DeviceKind.Gamepad;
            battery = TryReadBetop(group, cancellationToken);
            if (battery == null) unavailableReason = "北通接收器暂未响应电量查询，按一下手柄后重试";
        }
        else if (isAtkKeyboard)
        {
            battery = TryReadAtkKeyboard(group, cancellationToken);
        }
        else
        {
            battery = TryReadCompxMouse(group, cancellationToken)
                      ?? TryReadHechiMouse(group, cancellationToken);
        }

        var key = battery?.StableId is { Length: > 0 } stableId
            ? stableId
            : BuildKey(group);
        var lastKnown = _lastKnown.Get(key);
        if (battery != null)
        {
            _lastKnown.Remember(key, group.Name, battery.Percent, kind, DateTimeOffset.Now);
        }

        // A receiver for an unsupported/wired Razer device with no history is
        // not a battery device for this app and would only add noise. Known
        // controller protocols are kept visible with an unavailable reason.
        if (battery == null && lastKnown == null && unavailableReason == null &&
            !likelyWirelessRazer && !isAtkKeyboard &&
            group.VendorId is not (0x373B or 0x3554 or 0x320F))
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var origin = BatteryOrigin.None;
        DateTimeOffset? observedAt = null;
        int? shownBattery = battery?.Percent;
        var isCharging = false;
        var isFull = false;

        if (battery != null)
        {
            origin = BatteryOrigin.Current;
            observedAt = now;
            isCharging = battery.Charging;
            isFull = battery.Full;
        }
        else if (lastKnown != null)
        {
            shownBattery = lastKnown.Battery;
            origin = BatteryOrigin.LastKnown;
            observedAt = lastKnown.ObservedAt;
            if (kind == DeviceKind.Other) kind = lastKnown.Kind;
        }

        var displayName = DisplayName(group.Name);
        if (isRog &&
            (string.IsNullOrWhiteSpace(displayName) ||
             displayName.Contains("OMNI", StringComparison.OrdinalIgnoreCase) ||
             displayName.Contains("RECEIVER", StringComparison.OrdinalIgnoreCase)))
        {
            displayName = "ROG 无线鼠标";
        }
        else if (isMchose &&
                 (string.IsNullOrWhiteSpace(displayName) ||
                  displayName.Contains("RECEIVER", StringComparison.OrdinalIgnoreCase) ||
                  displayName.Contains("DONGLE", StringComparison.OrdinalIgnoreCase)))
        {
            displayName = "MCHOSE 无线鼠标";
        }
        else if (isDualSense && string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "DualSense Wireless Controller";
        }
        else if (isGameSir && string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "GameSir 手柄";
        }
        else if (isBetop && string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "BETOP 手柄";
        }

        var transport = battery?.Transport
                        ?? (isDualSense ? DeviceTransport.Usb
                            : isGameSirBluetooth ? DeviceTransport.Bluetooth
                            : DeviceTransport.Wireless24G);

        return new DeviceReading
        {
            Key = key,
            Name = displayName,
            Address = $"USB {group.VendorId:X4}:{group.ProductId:X4}",
            Kind = kind,
            Transport = transport,
            IsConnected = true,
            Battery = shownBattery,
            IsCharging = isCharging,
            IsFull = isFull,
            Origin = origin,
            ObservedAt = observedAt,
            UnavailableReason = unavailableReason ?? string.Empty
        };
    }

    private static BatteryProbeResult? TryReadRazer(
        HidGroup group, CancellationToken cancellationToken)
    {
        foreach (var device in group.Devices
                     .Where(device => SafeReportLength(device, Reports.Feature) >= 91))
        {
            using var hid = new HidNativeDevice(device.DevicePath);
            if (!hid.OpenFeature())
            {
                Logger.Info($"2.4G：无法打开 Razer 控制接口 {device.DevicePath}");
                continue;
            }

            foreach (var transactionId in RazerTransactionIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = BuildRazerRequest(transactionId);
                if (!hid.SetFeature(request))
                {
                    Logger.Info($"2.4G：Razer 发送失败 tid=0x{transactionId:X2}");
                    continue;
                }

                Thread.Sleep(65);
                for (var poll = 0; poll < 5; poll++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var response = new byte[91];
                    if (!hid.GetFeature(response)) break;

                    var body = response.AsSpan(1);
                    if (body.Length < 90) continue;
                    if (body[6] != 0x07 || body[7] != 0x80) continue;

                    var status = body[0];
                    if (status == 0x02)
                    {
                        var raw = body[9];
                        var percent = (int)Math.Round(raw / 255d * 100d, MidpointRounding.AwayFromZero);
                        percent = Math.Clamp(percent, 0, 100);
                        var charging = TryReadRazerCharging(hid, transactionId, cancellationToken);
                        Logger.Info($"2.4G：Razer {group.Name} 原始电量 0x{raw:X2} -> {percent}%" +
                                    $"{(charging ? "（充电中）" : string.Empty)}");
                        return new BatteryProbeResult(
                            percent, charging, charging && percent >= 100);
                    }

                    if (status == 0x01)
                    {
                        Thread.Sleep(80);
                        continue;
                    }

                    break;
                }
            }
        }

        return null;
    }

    private static bool TryReadRazerCharging(
        HidNativeDevice hid, int transactionId, CancellationToken cancellationToken)
    {
        var request = BuildRazerRequest(transactionId, 0x07, 0x84, 0x02);
        if (!hid.SetFeature(request)) return false;

        Thread.Sleep(65);
        for (var poll = 0; poll < 4; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new byte[91];
            if (!hid.GetFeature(response)) break;

            var body = response.AsSpan(1);
            if (body.Length < 90) continue;
            if (body[6] != 0x07 || body[7] != 0x84) continue;

            if (body[0] == 0x02) return body[9] == 1;
            if (body[0] == 0x01)
            {
                Thread.Sleep(50);
                continue;
            }

            break;
        }

        return false;
    }

    /// <summary>
    /// ASUS ROG Omni receivers use report id 0x03 and a 64-byte frame for
    /// 0x12/0x07 (battery at report byte 5, charging at byte 10). Wired ROG
    /// devices prepend a 0x00 placeholder and use the same field offsets.
    /// The query is retried because a sleeping mouse often ignores the first
    /// request. Everything is strictly validated before accepting a reading.
    /// </summary>
    private static BatteryProbeResult? TryReadRog(
        HidGroup group, CancellationToken cancellationToken)
    {
        foreach (var device in group.Devices.Where(device =>
                     SafeReportLength(device, Reports.Input) >= 64 &&
                     SafeReportLength(device, Reports.Output) >= 64))
        {
            var length = SafeReportLength(device, Reports.Output) >= 65 ? 65 : 64;
            if (SafeReportLength(device, Reports.Input) < length) continue;

            using var hid = new HidNativeDevice(device.DevicePath);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DrainInput(hid, length, cancellationToken);

                var request = new byte[length];
                request[0] = length >= 65 ? (byte)0x00 : (byte)0x03;
                request[1] = 0x12;
                request[2] = 0x07;
                if (!hid.WriteAsync(request, cancellationToken).GetAwaiter().GetResult()) break;

                var timeout = attempt == 0
                    ? TimeSpan.FromMilliseconds(600)
                    : TimeSpan.FromMilliseconds(250);
                for (var readAttempt = 0; readAttempt < 4; readAttempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var response = hid.ReadAsync(length, timeout, cancellationToken)
                        .GetAwaiter().GetResult();
                    if (!TryParseRogBattery(response, length, out var percent, out var charging))
                    {
                        continue;
                    }

                    var full = charging && percent >= 100;
                    Logger.Info($"2.4G：ROG {group.Name} = {percent}%" +
                                $"{(full ? "（已充满）" : charging ? "（充电中）" : string.Empty)}");
                    return new BatteryProbeResult(percent, charging, full);
                }

                Thread.Sleep(250);
            }
        }

        return null;
    }

    private static void DrainInput(
        HidNativeDevice hid, int length, CancellationToken cancellationToken)
    {
        for (var index = 0; index < 8; index++)
        {
            var stale = hid.ReadAsync(
                    length, TimeSpan.FromMilliseconds(5), cancellationToken)
                .GetAwaiter().GetResult();
            if (stale == null) break;
        }
    }

    private static bool TryParseRogBattery(
        byte[]? response, int length, out int percent, out bool charging)
    {
        percent = 0;
        charging = false;
        if (response == null || response.Length < 11) return false;

        var reportId = length >= 65 ? (byte)0x00 : (byte)0x03;
        if (response[0] != reportId || response[1] != 0x12 || response[2] != 0x07)
        {
            return false;
        }

        var rawPercent = response[5];
        var chargingFlag = response[10];
        if (rawPercent > 100 || chargingFlag > 1) return false;

        percent = rawPercent;
        charging = chargingFlag == 1;
        return true;
    }

    /// <summary>
    /// DualSense USB input report: byte 52 low nibble is the 0-10 battery
    /// level (x10 = percent), high nibble is charge state 1/2. USB only; the
    /// Bluetooth report has a different layout, so BTHENUM interfaces are
    /// deliberately skipped.
    /// </summary>
    private static BatteryProbeResult? TryReadDualSense(
        HidGroup group, CancellationToken cancellationToken)
    {
        foreach (var device in group.Devices)
        {
            if (device.DevicePath.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)) continue;

            var inputLength = SafeReportLength(device, Reports.Input);
            var outputLength = SafeReportLength(device, Reports.Output);
            if (inputLength < 53 || outputLength < 48) continue;

            using var hid = new HidNativeDevice(device.DevicePath);
            DrainInput(hid, inputLength, cancellationToken);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timeout = attempt == 0
                    ? TimeSpan.FromMilliseconds(500)
                    : TimeSpan.FromMilliseconds(250);
                var response = hid.ReadAsync(inputLength, timeout, cancellationToken)
                    .GetAwaiter().GetResult();
                if (!TryParseDualSense(response, out var percent, out var charging, out var full))
                {
                    continue;
                }

                var stableId = string.IsNullOrWhiteSpace(group.Serial)
                    ? null
                    : $"sony:{group.Serial}";
                Logger.Info($"HID：DualSense {group.Name} = {percent}%" +
                            $"{(full ? "（已充满）" : charging ? "（充电中）" : string.Empty)}");
                return new BatteryProbeResult(
                    percent, charging, full, DeviceTransport.Usb, stableId);
            }
        }

        return null;
    }

    private static bool TryParseDualSense(
        byte[]? response, out int percent, out bool charging, out bool full)
    {
        percent = 0;
        charging = false;
        full = false;
        if (response == null || response.Length < 53 || response[0] != 0x01) return false;

        var raw = response[52];
        var level = raw & 0x0F;
        var chargeState = (raw >> 4) & 0x0F;
        if (level > 10) return false;

        percent = Math.Clamp(level * 10, 0, 100);
        charging = chargeState is 1 or 2;
        full = charging && percent >= 100;
        return true;
    }

    /// <summary>
    /// GameSir T4N2 (Nova 2 Lite) vendor report: write 65 bytes with the
    /// 00 01 01 00 header, then read a 65-byte reply whose MAC is at [2..7],
    /// battery percent at [19], and charge flag at [20]. Bluetooth interfaces
    /// intentionally do not expose this query.
    /// </summary>
    private static BatteryProbeResult? TryReadGameSir(
        HidGroup group, CancellationToken cancellationToken)
    {
        if (group.ProductId == 0x02E0) return null;

        foreach (var device in group.Devices.Where(device =>
                     SafeReportLength(device, Reports.Input) >= 21 &&
                     SafeReportLength(device, Reports.Output) >= 4))
        {
            var inputLength = SafeReportLength(device, Reports.Input);
            var outputLength = SafeReportLength(device, Reports.Output);
            using var hid = new HidNativeDevice(device.DevicePath);
            DrainInput(hid, inputLength, cancellationToken);

            var request = new byte[outputLength];
            request[0] = 0x00;
            request[1] = 0x01;
            request[2] = 0x01;
            request[3] = 0x00;
            if (!hid.WriteAsync(request, cancellationToken).GetAwaiter().GetResult()) continue;

            for (var attempt = 0; attempt < 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = hid.ReadAsync(
                        inputLength,
                        attempt == 0 ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromMilliseconds(180),
                        cancellationToken)
                    .GetAwaiter().GetResult();
                if (response == null || response.Length < 21) continue;
                if (response[0] != 0x00 || response[1] != 0x01) continue;

                var percent = response[19];
                var chargeFlag = response[20];
                if (percent > 100) continue;

                var charging = chargeFlag != 0;
                var mac = Convert.ToHexString(response, 2, 6);
                Logger.Info($"HID：GameSir T4N2 = {percent}% (MAC {mac})" +
                            $"{(charging ? "（充电中）" : string.Empty)}");
                return new BatteryProbeResult(
                    percent, charging, charging && percent >= 100,
                    DeviceTransport.Wireless24G, $"gamesir:{mac}");
            }
        }

        return null;
    }

    /// <summary>
    /// BETOP Kunpeng 20 receiver: vendor interface query 02 15 (padded to
    /// 64/32/16 bytes); reply header 15, battery at byte 2 and charging in
    /// the low nibble of byte 4.
    /// </summary>
    private static BatteryProbeResult? TryReadBetop(
        HidGroup group, CancellationToken cancellationToken)
    {
        foreach (var device in group.Devices.Where(device =>
                     SafeReportLength(device, Reports.Input) >= 5 &&
                     SafeReportLength(device, Reports.Output) >= 16))
        {
            var inputLength = SafeReportLength(device, Reports.Input);
            using var hid = new HidNativeDevice(device.DevicePath);
            DrainInput(hid, inputLength, cancellationToken);

            foreach (var writeLength in new[] { 64, 32, 16 })
            {
                if (SafeReportLength(device, Reports.Output) < writeLength) continue;

                var request = new byte[writeLength];
                request[0] = 0x02;
                request[1] = 0x15;
                if (!hid.WriteAsync(request, cancellationToken).GetAwaiter().GetResult()) continue;

                for (var attempt = 0; attempt < 3; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var response = hid.ReadAsync(
                            inputLength,
                            attempt == 0 ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMilliseconds(220),
                            cancellationToken)
                        .GetAwaiter().GetResult();
                    if (!TryParseBetop(response, out var percent, out var charging, out var full))
                    {
                        continue;
                    }

                    var stableId = string.IsNullOrWhiteSpace(group.Serial)
                        ? $"betop:{group.ProductId:X4}:{NormalizeName(group.Name)}"
                        : $"betop:{group.Serial}";
                    Logger.Info($"HID：BETOP {group.Name} = {percent}%" +
                                $"{(full ? "（已充满）" : charging ? "（充电中）" : string.Empty)}");
                    return new BatteryProbeResult(
                        percent, charging, full, DeviceTransport.Wireless24G, stableId);
                }
            }
        }

        return null;
    }

    private static bool TryParseBetop(
        byte[]? response, out int percent, out bool charging, out bool full)
    {
        percent = 0;
        charging = false;
        full = false;
        if (response == null || response.Length < 5) return false;
        if (response[0] != 0x02 || response[1] != 0x15) return false;

        var raw = response[2];
        if (raw > 100) return false;

        percent = raw;
        charging = (response[4] & 0x0F) != 0;
        full = charging && percent >= 100;
        return true;
    }

    /// <summary>
    /// MCHOSE L7-family mice use HID feature report 0x11. The payload is
    /// inverted byte-for-byte (padding included), and 0x11/0x06 returns
    /// identity plus battery at logical byte 9 and charge status at byte 10.
    /// The first reply after idle may be all zero, so retry; the G3 family is
    /// intentionally excluded because it speaks a different protocol.
    /// </summary>
    private static BatteryProbeResult? TryReadMchose(
        HidGroup group, CancellationToken cancellationToken)
    {
        foreach (var device in group.Devices.Where(device =>
                     SafeReportLength(device, Reports.Feature) >= 21))
        {
            using var hid = new HidNativeDevice(device.DevicePath);
            if (!hid.OpenFeature()) continue;

            for (var attempt = 0; attempt < 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new byte[21];
                Array.Fill(request, (byte)0xFF);
                request[0] = 0x11;
                request[1] = 0x06 ^ 0xFF;
                if (!hid.SetFeature(request))
                {
                    Thread.Sleep(40);
                    continue;
                }

                Thread.Sleep(20);
                var response = new byte[21];
                response[0] = 0x11;
                if (!hid.GetFeature(response) ||
                    response.Length < 13 ||
                    response[0] != 0x11 ||
                    (response[1] ^ 0xFF) != 0x06)
                {
                    Thread.Sleep(30 * (attempt + 1));
                    continue;
                }

                var payload = new byte[response.Length - 2];
                for (var index = 0; index < payload.Length; index++)
                {
                    payload[index] = (byte)(response[index + 2] ^ 0xFF);
                }

                if (payload.All(value => value == 0))
                {
                    // The mouse returns a well-formed but empty identity
                    // shortly after waking.
                    Thread.Sleep(120);
                    continue;
                }

                var vid = payload[0] | (payload[1] << 8);
                var pid = payload[2] | (payload[3] << 8);
                var flags = payload[8];
                var battery = payload[9];
                var charge = payload[10];
                var connectMode = flags & 0x07;
                var connected = (flags & 0x08) != 0;

                if (vid is not (0x5253 or 0x3837) || battery > 100) continue;
                if (connectMode == 1 && !connected)
                {
                    Logger.Info($"2.4G：MCHOSE 接收器没有活动鼠标（VID 0x{vid:X4} PID 0x{pid:X4}）");
                    return null;
                }

                var charging = charge != 0;
                var full = charging && battery >= 100;
                Logger.Info($"2.4G：MCHOSE {group.Name} = {battery}% (VID 0x{vid:X4} PID 0x{pid:X4})" +
                            $"{(full ? "（已充满）" : charging ? "（充电中）" : string.Empty)}");
                return new BatteryProbeResult(battery, charging, full);
            }
        }

        return null;
    }

    /// <summary>
    /// ATK keyboards expose an Evision-style vendor command discovered by the
    /// FerrisPulse ATK V75X work: Report 4, a non-zero 16-bit sequence, command
    /// 0x1A; the reply must echo the sequence and carries percent at byte 8 and
    /// the charging flag at byte 9. The Z87 is queried with the same frame as a
    /// probe before falling back to its older 0x87 command.
    /// </summary>
    private static BatteryProbeResult? TryReadAtkKeyboard(
        HidGroup group, CancellationToken cancellationToken)
    {
        foreach (var device in group.Devices.Where(Is64ByteInterface))
        {
            using var hid = new HidNativeDevice(device.DevicePath);

            // Drop a queued input report from an earlier poll.
            _ = hid.ReadAsync(64, TimeSpan.FromMilliseconds(40), cancellationToken).GetAwaiter().GetResult();

            var code = NextAtkRequestCode();
            var request = new byte[64];
            request[0] = 0x04;
            request[1] = (byte)code;
            request[2] = (byte)(code >> 8);
            request[3] = 0x1A;
            if (!hid.WriteAsync(request, cancellationToken).GetAwaiter().GetResult()) continue;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timeout = attempt == 0
                    ? TimeSpan.FromMilliseconds(700)
                    : TimeSpan.FromMilliseconds(220);
                var response = hid.ReadAsync(64, timeout, cancellationToken)
                    .GetAwaiter().GetResult();
                if (!TryParseAtkEvision(response, code, out var percent, out var charging)) continue;

                Logger.Info($"2.4G：ATK Evision {group.Name} = {percent}%" +
                            $"{(charging ? "（充电中）" : string.Empty)}");
                return new BatteryProbeResult(percent, charging, charging && percent >= 100);
            }
        }

        // Older ATK Z87 dongles only answered the BYKB 0x87 query.
        return TryReadAtkLegacyZ87(group, cancellationToken);
    }

    private static BatteryProbeResult? TryReadAtkLegacyZ87(
        HidGroup group, CancellationToken cancellationToken)
    {
        foreach (var device in group.Devices.Where(Is64ByteInterface))
        {
            using var hid = new HidNativeDevice(device.DevicePath);

            _ = hid.ReadAsync(64, TimeSpan.FromMilliseconds(40), cancellationToken).GetAwaiter().GetResult();

            var request = new byte[64];
            request[0] = 0x04;
            request[1] = 0x87; // BYKB/ATK battery query observed on the Z87 dongle
            if (!hid.WriteAsync(request, cancellationToken).GetAwaiter().GetResult()) continue;

            for (var attempt = 0; attempt < 6; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timeout = attempt == 0 ? TimeSpan.FromMilliseconds(1600) : TimeSpan.FromMilliseconds(250);
                var response = hid.ReadAsync(64, timeout, cancellationToken)
                    .GetAwaiter().GetResult();
                if (response == null || response.Length < 4) continue;
                if (response[0] != 0x04 || response[1] != 0x87) continue;

                var raw = response[3];
                if (raw == 0)
                {
                    // The legacy Z87 query answers 0x00 while the keyboard is
                    // asleep; that must not be reported as an empty battery.
                    Logger.Info("2.4G：ATK Z87 legacy 返回 0x00，视为休眠/无读数");
                    continue;
                }

                var percent = raw <= 100
                    ? raw
                    : (int)Math.Round(raw / 255d * 100d, MidpointRounding.AwayFromZero);
                percent = Math.Clamp(percent, 0, 100);
                Logger.Info($"2.4G：ATK Z87 原始电量 0x{raw:X2} -> {percent}%");
                return new BatteryProbeResult(percent, false, false);
            }
        }

        return null;
    }

    private static bool TryParseAtkEvision(
        byte[]? response, ushort requestCode, out int percent, out bool charging)
    {
        percent = 0;
        charging = false;
        if (response == null || response.Length < 10 || response[0] != 0x04) return false;
        if (response[1] != (byte)requestCode ||
            response[2] != (byte)(requestCode >> 8) ||
            response[3] != 0x1A) return false;

        var raw = response[8];
        var chargingFlag = response[9];
        if (raw > 100 || chargingFlag > 1) return false;

        percent = raw;
        charging = chargingFlag == 1;
        Logger.Info($"2.4G：ATK Evision 原始 {raw}%，充电标志 0x{chargingFlag:X2}");
        return true;
    }

    private static ushort NextAtkRequestCode()
    {
        var value = Interlocked.Increment(ref _atkRequestCode);
        var code = (ushort)(value & 0xFFFF);
        return code == 0 ? (ushort)1 : code;
    }

    private static BatteryProbeResult? TryReadCompxMouse(
        HidGroup group, CancellationToken cancellationToken)
    {
        foreach (var device in group.Devices.Where(Is64ByteInterface))
        {
            using var hid = new HidNativeDevice(device.DevicePath);

            // Nordic 54L15: 64-byte report id 8, sub-index 7 = battery.
            var request54 = new byte[64];
            request54[0] = 0x08;
            request54[1] = 0x7D;
            request54[2] = 0x72;
            request54[3] = 0x02;
            request54[5] = 0x01;
            request54[6] = 0x07;
            request54[7] = 0x01;
            if (hid.WriteAsync(request54, cancellationToken).GetAwaiter().GetResult())
            {
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var response = hid.ReadAsync(64, TimeSpan.FromMilliseconds(180), cancellationToken)
                        .GetAwaiter().GetResult();
                    if (response == null || response.Length < 10) continue;
                    if (response[1] != 0x72 || response[5] != 0x07 || response[2] != 0x00) continue;
                    var percent = response[7];
                    if (percent <= 100)
                    {
                        Logger.Info($"2.4G：ATK/VXE 鼠标 {group.Name} = {percent}% (Nordic 54)");
                        return new BatteryProbeResult(percent, false, false);
                    }
                }
            }
        }

        foreach (var device in group.Devices.Where(device =>
                     SafeReportLength(device, Reports.Input) >= 17 &&
                     SafeReportLength(device, Reports.Output) >= 17))
        {
            using var hid = new HidNativeDevice(device.DevicePath);

            // COMPX (also used by several ATK/VXE Nordic mice): 17-byte
            // output report id 8, sub-command 4, checksum at byte 16.
            var request = new byte[17];
            request[0] = 0x08;
            request[1] = 0x04;
            request[16] = 0x49;
            if (hid.WriteAsync(request, cancellationToken).GetAwaiter().GetResult())
            {
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var response = hid.ReadAsync(17, TimeSpan.FromMilliseconds(180), cancellationToken)
                        .GetAwaiter().GetResult();
                    if (!TryParseCompx(response, out var percent, out var charging, out var full)) continue;

                    Logger.Info($"2.4G：COMPX 鼠标 {group.Name} = {percent}%" +
                                $"{(full ? "（已充满）" : charging ? "（充电中）" : string.Empty)}");
                    return new BatteryProbeResult(percent, charging, full);
                }
            }

            // Some receivers expose the same command only as a feature report.
            if (hid.OpenFeature() && hid.SetFeature(request))
            {
                var response = new byte[18];
                if (hid.GetFeature(response) &&
                    TryParseCompx(response, out var percent, out var charging, out var full))
                {
                    Logger.Info($"2.4G：COMPX 鼠标 {group.Name} = {percent}% (Feature)");
                    return new BatteryProbeResult(percent, charging, full);
                }
            }
        }

        return null;
    }

    private static bool TryParseCompx(
        byte[]? response, out int percent, out bool charging, out bool full)
    {
        percent = 0;
        charging = false;
        full = false;
        if (response == null) return false;

        var data = response.Length > 0 && response[0] == 0x08
            ? response.AsSpan(1)
            : response.AsSpan();
        if (data.Length < 7 || data[0] != 0x04 || data[1] == 0xFF) return false;

        var raw = data[5];
        if (raw > 100) return false;

        var flags = data[6];
        full = (flags & 2) != 0;
        charging = !full && (flags & 1) != 0;
        percent = full ? 100 : raw;
        return true;
    }

    /// <summary>
    /// HECHI is the second ATK/VXE mouse protocol used by atktool. Report id 11,
    /// 63-byte command, battery at normalized byte 17 and charge flags at byte
    /// 16. It is tried only when the COMPX/Nordic probes did not answer.
    /// </summary>
    private static BatteryProbeResult? TryReadHechiMouse(
        HidGroup group, CancellationToken cancellationToken)
    {
        foreach (var device in group.Devices.Where(Is64ByteInterface))
        {
            using var hid = new HidNativeDevice(device.DevicePath);

            var request = new byte[64];
            request[0] = 0x0B;
            request[1] = 0x13;

            // Fast path: HECHI devices expose a 65-byte feature report.
            if (hid.OpenFeature() && hid.SetFeature(request))
            {
                var response = new byte[65];
                if (hid.GetFeature(response) &&
                    TryParseHechi(response, out var percent, out var charging, out var full))
                {
                    Logger.Info($"2.4G：HECHI 鼠标 {group.Name} = {percent}% (Feature)");
                    return new BatteryProbeResult(percent, charging, full);
                }
            }

            _ = hid.ReadAsync(64, TimeSpan.FromMilliseconds(40), cancellationToken)
                .GetAwaiter().GetResult();
            if (!hid.WriteAsync(request, cancellationToken).GetAwaiter().GetResult()) continue;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timeout = attempt == 0
                    ? TimeSpan.FromMilliseconds(500)
                    : TimeSpan.FromMilliseconds(220);
                var response = hid.ReadAsync(65, timeout, cancellationToken)
                    .GetAwaiter().GetResult();
                if (!TryParseHechi(response, out var percent, out var charging, out var full)) continue;

                Logger.Info($"2.4G：HECHI 鼠标 {group.Name} = {percent}%" +
                            $"{(full ? "（已充满）" : charging ? "（充电中）" : string.Empty)}");
                return new BatteryProbeResult(percent, charging, full);
            }
        }

        return null;
    }

    private static bool TryParseHechi(
        byte[]? response, out int percent, out bool charging, out bool full)
    {
        percent = 0;
        charging = false;
        full = false;
        if (response == null) return false;

        var data = response.Length > 0 && response[0] == 0x0B
            ? response.AsSpan(1)
            : response.AsSpan();
        if (data.Length < 18 || data[0] != 0x13 || data[2] == 0xFF) return false;

        var raw = data[17];
        if (raw > 100) return false;

        var flags = data[16];
        full = (flags & 2) != 0;
        charging = !full && (flags & 1) != 0;
        percent = full ? 100 : raw;
        return true;
    }

    private static byte[] BuildRazerRequest(
        int transactionId,
        byte commandClass = 0x07,
        byte commandId = 0x80,
        byte dataSize = 0x02,
        byte[]? args = null)
    {
        var report = new byte[90];
        report[1] = (byte)transactionId;
        report[5] = dataSize;
        report[6] = commandClass;
        report[7] = commandId;
        if (args != null)
        {
            Array.Copy(args, 0, report, 8, Math.Min(args.Length, 80));
        }

        byte crc = 0;
        for (var index = 2; index < 88; index++) crc ^= report[index];
        report[88] = crc;

        var packet = new byte[91];
        packet[0] = 0x00;
        Array.Copy(report, 0, packet, 1, report.Length);
        return packet;
    }

    private static bool Is64ByteInterface(HidDevice device)
    {
        return SafeReportLength(device, Reports.Input) >= 64 &&
               SafeReportLength(device, Reports.Output) >= 64;
    }

    private static int SafeReportLength(HidDevice device, Reports kind)
    {
        try
        {
            return kind switch
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

    private static bool IsLikelyWirelessRazer(HidGroup group)
    {
        var text = group.Name.ToLowerInvariant();
        return text.Contains("hyperspeed") || text.Contains("wireless") ||
               text.Contains("receiver") || text.Contains("dongle") ||
               text.Contains("blackshark") || text.Contains("barracuda") ||
               text.Contains("deathadder") || text.Contains("basilisk") ||
               text.Contains("viper") || text.Contains("naga") ||
               text.Contains("orochi") || text.Contains("cobra") ||
               text.Contains("mamba") || text.Contains("lancehead");
    }

    private static DeviceKind GuessKind(HidGroup group)
    {
        if (group.Name.Contains("Z87", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Keyboard;
        if (HeadsetRegex.IsMatch(group.Name)) return DeviceKind.Headset;
        if (KeyboardRegex.IsMatch(group.Name)) return DeviceKind.Keyboard;
        if (MouseRegex.IsMatch(group.Name)) return DeviceKind.Mouse;
        if (group.VendorId is 0x373B or 0x3554) return DeviceKind.Mouse;
        return DeviceKind.Other;
    }

    private static bool IsAtkKeyboard(HidGroup group)
    {
        if (group.Name.Contains("Z87", StringComparison.OrdinalIgnoreCase) ||
            group.Name.Contains("V75", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return group.VendorId == 0x320F && GuessKind(group) == DeviceKind.Keyboard;
    }

    private static string BuildKey(HidGroup group) =>
        $"hid:{group.VendorId:X4}:{group.ProductId:X4}:{NormalizeName(group.Name)}";

    private static string DisplayName(string name)
    {
        var result = name.Trim();
        foreach (var suffix in new[] { " Dongle", " Receiver", " 2.4G Dongle", " 2.4G Receiver" })
        {
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[..^suffix.Length].TrimEnd();
            }
        }
        return result;
    }

    private static string NormalizeName(string name)
    {
        var text = name.ToLowerInvariant();
        foreach (var word in new[] { "dongle", "receiver", "wireless", "2.4g", "bluetooth", "multi-mode", "三模" })
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

    private static IReadOnlyList<DeviceReading> DemoReadings()
    {
        var now = DateTimeOffset.Now;
        return new[]
        {
            new DeviceReading
            {
                Key = "demo:hid:razer", Name = "Razer DeathAdder V2 X HyperSpeed",
                Address = "USB 1532:009C", Kind = DeviceKind.Mouse,
                Transport = DeviceTransport.Wireless24G, IsConnected = true,
                Battery = 18, IsCharging = true,
                Origin = BatteryOrigin.Current, ObservedAt = now
            },
            new DeviceReading
            {
                Key = "demo:hid:atkz87", Name = "ATK Z87",
                Address = "USB 373B:1012", Kind = DeviceKind.Keyboard,
                Transport = DeviceTransport.Wireless24G, IsConnected = true,
                Battery = 100, IsFull = true,
                Origin = BatteryOrigin.Current, ObservedAt = now
            }
        };
    }

    private sealed class HidGroup
    {
        public HidGroup(int vendorId, int productId, string name, string serial)
        {
            VendorId = vendorId;
            ProductId = productId;
            Name = name;
            Serial = serial;
        }

        public int VendorId { get; }
        public int ProductId { get; }
        public string Name { get; }
        public string Serial { get; }
        public List<HidDevice> Devices { get; } = new();
    }
}
