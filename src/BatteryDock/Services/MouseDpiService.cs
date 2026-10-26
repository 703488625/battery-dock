using System.Globalization;
using System.Text.RegularExpressions;
using BatteryDock.Models;
using HidSharp;

namespace BatteryDock.Services;

/// <summary>
/// Reads and writes mouse DPI through vendor HID protocols. Razer uses its
/// 90-byte feature report (0x04/0x85 read, 0x04/0x05 write); Logitech delegates
/// to HID++ 0x2202 / 0x2201. Unsupported vendors report a clear message.
/// </summary>
internal sealed class MouseDpiService
{
    private readonly LogitechHidppService _logitech;

    public MouseDpiService(LogitechHidppService logitech)
    {
        _logitech = logitech;
    }

    public Task<MouseDpiState> ReadAsync(DeviceReading device, CancellationToken cancellationToken)
    {
        if (IsRazer(device))
        {
            return Task.Run(() => RazerDpi.Read(device, cancellationToken), cancellationToken);
        }

        if (IsLogitech(device))
        {
            return ReadLogitechAsync(device, cancellationToken);
        }

        throw new NotSupportedException(
            "暂未适配这款鼠标的 DPI 协议。当前支持雷蛇和部分罗技鼠标；ATK / VXE 的 DPI 协议尚未接入。");
    }

    public Task SetAsync(DeviceReading device, int dpiX, int dpiY, CancellationToken cancellationToken)
    {
        if (IsRazer(device))
        {
            return Task.Run(() => RazerDpi.Set(device, dpiX, dpiY, cancellationToken), cancellationToken);
        }

        if (IsLogitech(device))
        {
            return SetLogitechAsync(device, dpiX, dpiY, cancellationToken);
        }

        throw new NotSupportedException(
            "暂未适配这款鼠标的 DPI 协议。当前支持雷蛇和部分罗技鼠标；ATK / VXE 的 DPI 协议尚未接入。");
    }

    public Task<MousePollingState> ReadPollingAsync(
        DeviceReading device, CancellationToken cancellationToken)
    {
        if (IsRazer(device))
        {
            return Task.Run(() => RazerDpi.ReadPolling(device, cancellationToken), cancellationToken);
        }

        if (IsLogitech(device))
        {
            return ReadLogitechPollingAsync(device, cancellationToken);
        }

        throw new NotSupportedException(
            "暂未适配这款鼠标的回报率协议。当前支持雷蛇和部分罗技鼠标。");
    }

    public Task SetPollingAsync(
        DeviceReading device, int hz, CancellationToken cancellationToken)
    {
        if (IsRazer(device))
        {
            return Task.Run(() => RazerDpi.SetPolling(device, hz, cancellationToken), cancellationToken);
        }

        if (IsLogitech(device))
        {
            return SetLogitechPollingAsync(device, hz, cancellationToken);
        }

        throw new NotSupportedException(
            "暂未适配这款鼠标的回报率协议。当前支持雷蛇和部分罗技鼠标。");
    }

    private async Task<MousePollingState> ReadLogitechPollingAsync(
        DeviceReading device, CancellationToken cancellationToken)
    {
        var state = await _logitech.ReadPollingRateAsync(device.Name, cancellationToken)
            .ConfigureAwait(false);
        return state ?? throw new NotSupportedException(
            "该罗技设备没有可用的回报率特性（0x8060 / 0x8061）。");
    }

    private async Task SetLogitechPollingAsync(
        DeviceReading device, int hz, CancellationToken cancellationToken)
    {
        var success = await _logitech.SetPollingRateAsync(device.Name, hz, cancellationToken)
            .ConfigureAwait(false);
        if (!success)
        {
            throw new IOException("罗技回报率写入失败。请确认设备已唤醒，且没有其他软件占用 HID 接口。");
        }
    }

    private async Task<MouseDpiState> ReadLogitechAsync(
        DeviceReading device, CancellationToken cancellationToken)
    {
        var state = await _logitech.ReadDpiAsync(device.Name, cancellationToken).ConfigureAwait(false);
        return state ?? throw new NotSupportedException(
            "该罗技设备没有可用的 DPI 特性（0x2201 / 0x2202），可能是办公型号或固件未开放。");
    }

    private async Task SetLogitechAsync(
        DeviceReading device, int dpiX, int dpiY, CancellationToken cancellationToken)
    {
        var success = await _logitech.SetDpiAsync(device.Name, dpiX, dpiY, cancellationToken)
            .ConfigureAwait(false);
        if (!success)
        {
            throw new IOException("罗技 DPI 写入失败。请确认设备已唤醒，且没有其他软件占用 HID 接口。");
        }
    }

    private static bool IsRazer(DeviceReading device) =>
        device.Address.StartsWith("USB 1532:", StringComparison.OrdinalIgnoreCase) ||
        device.Key.StartsWith("hid:1532:", StringComparison.OrdinalIgnoreCase);

    private static bool IsLogitech(DeviceReading device) =>
        device.Address.StartsWith("USB 046D:", StringComparison.OrdinalIgnoreCase) ||
        device.Key.StartsWith("logi:", StringComparison.OrdinalIgnoreCase);

    private static class RazerDpi
    {
        private const int DefaultMinDpi = 100;
        private const int DefaultMaxDpi = 35000;
        private static readonly byte[] TransactionIds = { 0x1F, 0x9F, 0x3F, 0xFF };

        public static MouseDpiState Read(DeviceReading device, CancellationToken cancellationToken)
        {
            var candidates = FindCandidates(device);
            if (candidates.Count == 0)
            {
                throw new NotSupportedException(
                    "没有找到雷蛇鼠标的控制接口。请确认接收器已插好，并关闭雷云等可能占用 HID 的软件。");
            }

            try
            {
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dpi = ReadFrom(candidate, cancellationToken);
                    if (dpi == null) continue;

                    var (min, max, step) = GetCapability(candidate.ProductId, device.Name);
                    Logger.Info($"鼠标 DPI：{device.Name} = {dpi.Value.X}x{dpi.Value.Y}（雷蛇）");
                    return new MouseDpiState(dpi.Value.X, dpi.Value.Y, min, max, step, "Razer HID");
                }
            }
            finally
            {
                foreach (var candidate in candidates) candidate.Dispose();
            }

            throw new IOException("鼠标休眠或未响应。请先移动一下鼠标，再点“刷新”重试。");
        }

        public static void Set(DeviceReading device, int dpiX, int dpiY, CancellationToken cancellationToken)
        {
            var (min, max, _) = GetCapability(ParseProductId(device.Address), device.Name);
            dpiX = Math.Clamp(dpiX, min, max);
            dpiY = Math.Clamp(dpiY, min, max);

            var candidates = FindCandidates(device);
            if (candidates.Count == 0)
            {
                throw new NotSupportedException(
                    "没有找到雷蛇鼠标的控制接口。请确认接收器已插好，并关闭雷云等可能占用 HID 的软件。");
            }

            try
            {
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!WriteTo(candidate, dpiX, dpiY, cancellationToken)) continue;

                    Logger.Info($"鼠标 DPI：{device.Name} 设置为 {dpiX}x{dpiY}（雷蛇）");
                    var verified = ReadFrom(candidate, cancellationToken);
                    if (verified != null)
                    {
                        Logger.Info($"鼠标 DPI 回读：{verified.Value.X}x{verified.Value.Y}");
                    }
                    return;
                }
            }
            finally
            {
                foreach (var candidate in candidates) candidate.Dispose();
            }

            throw new IOException("DPI 写入失败。请唤醒鼠标后重试；如果仍失败，可能是该型号的 DPI 命令不同。");
        }

        public static MousePollingState ReadPolling(
            DeviceReading device, CancellationToken cancellationToken)
        {
            var candidates = FindCandidates(device);
            if (candidates.Count == 0)
            {
                throw new NotSupportedException(
                    "没有找到雷蛇鼠标的控制接口。请确认接收器已插好，并关闭雷云等可能占用 HID 的软件。");
            }

            var unsupported = false;
            var firstPid = candidates[0].ProductId;
            try
            {
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var body = candidate.Exchange(
                        0x00, 0xC0, 0x01, Array.Empty<byte>(), cancellationToken);
                    if (body is not { Length: >= 10 })
                    {
                        if (candidate.SawUnsupported) unsupported = true;
                        Logger.Info("Razer 回报率查询：没有收到有效响应（状态 " +
                                    (candidate.LastStatus.HasValue
                                        ? candidate.LastStatus.Value.ToString("X2")
                                        : "无") + "）");
                        continue;
                    }

                    var hz = MapPollingRaw(body[9]);
                    if (hz <= 0) continue;

                    var rates = GetPollingRates(candidate.ProductId, device.Name).ToList();
                    if (!rates.Contains(hz))
                    {
                        rates.Add(hz);
                        rates.Sort();
                    }

                    Logger.Info($"鼠标回报率：{device.Name} = {hz}Hz（雷蛇）");
                    return new MousePollingState(hz, rates, "Razer HID");
                }
            }
            finally
            {
                foreach (var candidate in candidates) candidate.Dispose();
            }

            if (unsupported)
            {
                // Some models accept the set command but do not expose a
                // readable current rate. Let the user pick a target rate.
                Logger.Info($"鼠标回报率：{device.Name} 不支持查询当前值，允许尝试设置。");
                return new MousePollingState(0, GetPollingRates(firstPid, device.Name),
                    "Razer HID（当前值未知）");
            }

            throw new IOException("鼠标休眠或未响应。请先移动一下鼠标，再点“刷新”重试。");
        }

        public static void SetPolling(
            DeviceReading device, int hz, CancellationToken cancellationToken)
        {
            var rates = GetPollingRates(ParseProductId(device.Address), device.Name);
            if (!rates.Contains(hz))
            {
                throw new NotSupportedException($"这款鼠标不支持 {hz}Hz 回报率。");
            }

            var raw = MapPollingHz(hz);
            var candidates = FindCandidates(device);
            if (candidates.Count == 0)
            {
                throw new NotSupportedException(
                    "没有找到雷蛇鼠标的控制接口。请确认接收器已插好，并关闭雷云等可能占用 HID 的软件。");
            }

            var unsupported = false;
            try
            {
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var body = candidate.Exchange(
                        0x00, 0x40, 0x02, new byte[] { 0x01, raw }, cancellationToken);
                    if (body == null)
                    {
                        if (candidate.SawUnsupported) unsupported = true;
                        continue;
                    }

                    Logger.Info($"鼠标回报率：{device.Name} 设置为 {hz}Hz（雷蛇）");
                    return;
                }
            }
            finally
            {
                foreach (var candidate in candidates) candidate.Dispose();
            }

            if (unsupported)
            {
                throw new NotSupportedException(
                    "该鼠标固件不支持通过 HID 修改回报率；可以在雷云或厂商软件中查看/调整。");
            }

            throw new IOException("回报率写入失败。请唤醒鼠标后重试。");
        }

        private static int[] GetPollingRates(int? pid, string name)
        {
            var text = name.ToLowerInvariant();
            if (pid == 0x009C || text.Contains("deathadder v2 x"))
            {
                return new[] { 125, 250, 500, 1000 };
            }

            if (pid is 0x00BF or 0x00BE or 0x00E6 or 0x00E5 or 0x00DF or 0x00DE or
                0x00CC or 0x00CD or 0x00D6 or 0x00D7 or 0x00C4 or 0x00C5 ||
                text.Contains("v4 pro") || text.Contains("viper v3 pro") || text.Contains("35k"))
            {
                return new[] { 125, 250, 500, 1000, 2000, 4000, 8000 };
            }

            if (pid is 0x00B7 or 0x00B6 or 0x00A6 or 0x00A5 or 0x009B or 0x009A or
                0x00AA or 0x00AC or 0x00AB ||
                text.Contains("v3 pro") || text.Contains("v2 pro") || text.Contains("cobra pro"))
            {
                return new[] { 125, 250, 500, 1000, 2000, 4000 };
            }

            if (text.Contains("ultimate") || text.Contains("8khz"))
            {
                return new[] { 125, 250, 500, 1000, 2000 };
            }

            return new[] { 125, 250, 500, 1000 };
        }

        private static int MapPollingRaw(byte raw) => raw switch
        {
            0x01 => 8000,
            0x02 => 4000,
            0x04 => 2000,
            0x08 => 1000,
            0x10 => 500,
            0x20 => 250,
            0x40 => 125,
            _ => 0
        };

        private static byte MapPollingHz(int hz) => hz switch
        {
            >= 8000 => 0x01,
            >= 4000 => 0x02,
            >= 2000 => 0x04,
            >= 1000 => 0x08,
            >= 500 => 0x10,
            >= 250 => 0x20,
            _ => 0x40
        };

        private static (int X, int Y)? ReadFrom(
            RazerTransport transport, CancellationToken cancellationToken)
        {
            // Current live DPI.
            var body = transport.Exchange(0x04, 0x85, 0x07, new byte[] { 0x00 }, cancellationToken);
            if (body is { Length: >= 13 })
            {
                var x = (body[9] << 8) | body[10];
                var y = (body[11] << 8) | body[12];
                if (y <= 0) y = x;
                if (x is >= 50 and <= 50000) return (x, y);
            }

            // Mice that only expose the onboard DPI stage table.
            body = transport.Exchange(0x04, 0x86, 0x26, new byte[] { 0x01 }, cancellationToken);
            if (body is { Length: >= 11 })
            {
                var activeStage = body[9];
                var count = body[10];
                if (count is >= 1 and <= 5)
                {
                    for (var index = 0; index < count; index++)
                    {
                        var offset = 11 + index * 7;
                        if (offset + 4 >= body.Length) break;
                        var stage = body[offset];
                        var x = (body[offset + 1] << 8) | body[offset + 2];
                        var y = (body[offset + 3] << 8) | body[offset + 4];
                        if (y <= 0) y = x;
                        if (stage == activeStage && x is >= 50 and <= 50000) return (x, y);
                    }
                }
            }

            return null;
        }

        private static bool WriteTo(
            RazerTransport transport, int dpiX, int dpiY, CancellationToken cancellationToken)
        {
            var args = new byte[]
            {
                0x01, // VARSTORE
                (byte)(dpiX >> 8), (byte)dpiX,
                (byte)(dpiY >> 8), (byte)dpiY,
                0x00, 0x00
            };
            var body = transport.Exchange(0x04, 0x05, 0x07, args, cancellationToken);
            return body != null;
        }

        private static List<RazerTransport> FindCandidates(DeviceReading device)
        {
            var result = new List<RazerTransport>();
            var target = NormalizeName(device.Name);
            var targetPid = ParseProductId(device.Address);
            if (target.Length == 0) return result;

            try
            {
                foreach (var hid in DeviceList.Local.GetHidDevices()
                             .Where(candidate => candidate.VendorID == 0x1532))
                {
                    if (targetPid.HasValue && hid.ProductID != targetPid.Value) continue;
                    var name = SafeProductName(hid);
                    if (!NameMatches(name, target)) continue;

                    var featureLength = SafeReportLength(hid, Reports.Feature);
                    if (featureLength < 90) continue;

                    var prepended = featureLength >= 91;
                    result.Add(new RazerTransport(
                        hid.DevicePath,
                        prepended ? 91 : 90,
                        prepended,
                        hid.ProductID));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("枚举雷蛇 DPI 接口", ex);
            }

            return result
                .OrderByDescending(candidate => candidate.ReportLength)
                .Take(4)
                .ToList();
        }

        private static (int Min, int Max, int Step) GetCapability(int? pid, string name)
        {
            var text = name.ToLowerInvariant();
            if (pid == 0x009C || text.Contains("deathadder v2 x"))
            {
                return (100, 14000, 50);
            }

            if (pid is 0x00BF or 0x00BE or 0x00E6 or 0x00E5 or 0x00DF or 0x00DE or
                0x00CC or 0x00CD or 0x00D6 or 0x00D7 or 0x00C4 or 0x00C5 ||
                text.Contains("v4 pro") || text.Contains("viper v3 pro") || text.Contains("35k"))
            {
                return (100, 35000, 1);
            }

            if (pid is 0x00B7 or 0x00B6 or 0x00A6 or 0x00A5 or 0x009B or 0x009A or
                0x00AA or 0x00AC or 0x00AB ||
                text.Contains("v3 pro") || text.Contains("v2 pro") || text.Contains("cobra pro"))
            {
                return (100, 30000, 1);
            }

            if (text.Contains("ultimate") || text.Contains("8khz") || text.Contains("focus+"))
            {
                return (100, 20000, 50);
            }

            if (text.Contains("mini")) return (200, 8500, 100);
            if (text.Contains("essential")) return (200, 6400, 100);
            return (DefaultMinDpi, DefaultMaxDpi, 1);
        }

        private static int? ParseProductId(string address)
        {
            var match = Regex.Match(address, @"USB\s+([0-9A-Fa-f]{4}):([0-9A-Fa-f]{4})");
            if (!match.Success) return null;
            return int.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out var pid)
                ? pid
                : null;
        }

        private static bool NameMatches(string productName, string normalizedTarget)
        {
            var normalized = NormalizeName(productName);
            if (normalized.Length == 0) return false;
            if (normalized == normalizedTarget) return true;
            return normalized.Length >= 5 && normalizedTarget.Length >= 5 &&
                   (normalized.Contains(normalizedTarget, StringComparison.Ordinal) ||
                    normalizedTarget.Contains(normalized, StringComparison.Ordinal));
        }

        private static string NormalizeName(string name)
        {
            var text = name.ToLowerInvariant();
            foreach (var word in new[] { "razer", "wireless", "hyperspeed", "receiver", "dongle" })
            {
                text = text.Replace(word, string.Empty, StringComparison.OrdinalIgnoreCase);
            }
            return Regex.Replace(text, @"[^\p{L}\p{N}]+", string.Empty);
        }

        private static string SafeProductName(HidDevice device)
        {
            try
            {
                return device.GetProductName() ?? string.Empty;
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
                return reports == Reports.Feature
                    ? device.GetMaxFeatureReportLength()
                    : reports == Reports.Input
                        ? device.GetMaxInputReportLength()
                        : device.GetMaxOutputReportLength();
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

        private sealed class RazerTransport : IDisposable
        {
            private readonly HidNativeDevice _hid;
            private readonly bool _prepended;

            public RazerTransport(string path, int reportLength, bool prepended, int productId)
            {
                _hid = new HidNativeDevice(path);
                ReportLength = reportLength;
                _prepended = prepended;
                ProductId = productId;
            }

            public int ReportLength { get; }
            public int ProductId { get; }
            public byte? LastStatus { get; private set; }
            public bool SawUnsupported { get; private set; }

            public byte[]? Exchange(
                byte commandClass, byte commandId, byte dataSize, byte[] args,
                CancellationToken cancellationToken)
            {
                LastStatus = null;
                SawUnsupported = false;
                foreach (var transactionId in TransactionIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_hid.SetFeature(Build(transactionId, commandClass, commandId, dataSize, args)))
                    {
                        continue;
                    }

                    Thread.Sleep(15);
                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var response = new byte[ReportLength];
                        response[0] = 0x00;
                        if (!_hid.GetFeature(response))
                        {
                            Thread.Sleep(12);
                            continue;
                        }

                        var body = _prepended ? response.AsSpan(1) : response.AsSpan();
                        if (body.Length < 10) continue;
                        if (body[0] == 0x01)
                        {
                            Thread.Sleep(12);
                            continue;
                        }

                        if (body[6] == commandClass && body[7] == commandId)
                        {
                            LastStatus = body[0];
                            if (body[0] == 0x05) SawUnsupported = true;
                            if (body[0] == 0x02) return body.ToArray();
                        }

                        Thread.Sleep(12);
                    }
                }

                return null;
            }

            private byte[] Build(
                byte transactionId, byte commandClass, byte commandId, byte dataSize, byte[] args)
            {
                var body = new byte[90];
                body[1] = transactionId;
                body[5] = dataSize;
                body[6] = commandClass;
                body[7] = commandId;
                var count = Math.Min(args.Length, 80);
                Array.Copy(args, 0, body, 8, count);

                byte crc = 0;
                for (var index = 2; index < 88; index++) crc ^= body[index];
                body[88] = crc;

                if (!_prepended) return body;

                var packet = new byte[91];
                packet[0] = 0x00;
                Array.Copy(body, 0, packet, 1, body.Length);
                return packet;
            }

            public void Dispose() => _hid.Dispose();
        }
    }
}
