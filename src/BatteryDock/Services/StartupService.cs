using Microsoft.Win32;

namespace BatteryDock.Services;

internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BatteryDock";

    public static string ExecutablePath
    {
        get
        {
            var processPath = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(processPath)
                ? Path.Combine(AppContext.BaseDirectory, "BatteryDock.exe")
                : processPath;
        }
    }

    private static string DesiredCommand => $"\"{ExecutablePath}\" --tray";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var command = key?.GetValue(ValueName) as string;
            return string.Equals(command, DesiredCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Error("读取开机启动设置", ex);
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled)
            {
                key.SetValue(ValueName, DesiredCommand, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("修改开机启动设置", ex);
            return false;
        }
    }
}
