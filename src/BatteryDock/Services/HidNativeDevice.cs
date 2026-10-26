using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryDock.Services;

/// <summary>
/// Minimal shared-access HID transport. Vendor control collections are often
/// opened with FILE_SHARE_READ/WRITE by vendor software, which some general HID
/// libraries do not do. Feature reports go through hid.dll; interrupt
/// input/output reports go through an async FileStream.
/// </summary>
internal sealed class HidNativeDevice : IDisposable
{
    private readonly string _path;
    private SafeFileHandle? _featureHandle;
    private FileStream? _stream;

    public HidNativeDevice(string path) => _path = path;

    public bool OpenFeature()
    {
        if (_featureHandle is { IsInvalid: false }) return true;
        try
        {
            // Some vendor collections deny read access while allowing writes
            // (HidD_GetFeature works on a write-only handle too). Try the
            // permissive mode first, then the conventional read/write one.
            _featureHandle = CreateFile(
                _path,
                GenericWrite,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (!_featureHandle.IsInvalid) return true;

            _featureHandle.Dispose();
            _featureHandle = CreateFile(
                _path,
                GenericRead | GenericWrite,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (!_featureHandle.IsInvalid) return true;

            // Some vendor collections reject read/write access but still allow
            // a zero-access handle to be used with HidD_GetFeature.
            _featureHandle.Dispose();
            _featureHandle = CreateFile(
                _path,
                0,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (!_featureHandle.IsInvalid) return true;

            Logger.Info($"打开 HID Feature 失败：{_path}，Win32 错误 {Marshal.GetLastWin32Error()}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Info($"打开 HID Feature 异常：{ex.Message}");
            return false;
        }
    }

    public bool SetFeature(byte[] buffer)
    {
        return OpenFeature() && HidD_SetFeature(_featureHandle!, buffer, buffer.Length);
    }

    public bool GetFeature(byte[] buffer)
    {
        return OpenFeature() && HidD_GetFeature(_featureHandle!, buffer, buffer.Length);
    }

    private bool OpenStream()
    {
        if (_stream != null) return true;
        try
        {
            _stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 0,
                FileOptions.Asynchronous);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Info($"打开 HID 数据流失败：{ex.Message}");
            return false;
        }
    }

    public async Task<bool> WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (!OpenStream()) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await _stream!.WriteAsync(data.AsMemory(), cts.Token);
            await _stream.FlushAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Info("HID 写入超时。");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Info($"HID 写入失败：{ex.Message}");
            return false;
        }
    }

    public async Task<byte[]?> ReadAsync(
        int length, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!OpenStream()) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var buffer = new byte[length];
        try
        {
            var read = await _stream!.ReadAsync(buffer.AsMemory(0, length), cts.Token);
            if (read <= 0) return null;
            return read == length ? buffer : buffer[..read];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Info($"HID 读取失败：{ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // Ignore.
        }
        _stream = null;

        try
        {
            _featureHandle?.Dispose();
        }
        catch
        {
            // Ignore.
        }
        _featureHandle = null;
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;

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
    private static extern bool HidD_SetFeature(
        SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetFeature(
        SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);
}
