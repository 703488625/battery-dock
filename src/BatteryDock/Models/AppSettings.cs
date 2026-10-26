namespace BatteryDock.Models;

public enum AppThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

public enum RefreshInterval
{
    FifteenSeconds = 15,
    ThirtySeconds = 30,
    OneMinute = 60,
    FiveMinutes = 300
}

public sealed class AppSettings
{
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;
    public RefreshInterval Refresh { get; set; } = RefreshInterval.ThirtySeconds;
    public bool LowBatteryAlert { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool ShowDisconnectedDevices { get; set; } = true;
    public bool ShowAllDevices { get; set; } = false;
    public bool WelcomeShown { get; set; } = false;
    public bool QuietHoursEnabled { get; set; } = false;
    public int QuietHoursStartMinutes { get; set; } = 23 * 60;
    public int QuietHoursEndMinutes { get; set; } = 7 * 60;

    public bool IsQuietHours(DateTimeOffset time)
    {
        if (!QuietHoursEnabled || QuietHoursStartMinutes == QuietHoursEndMinutes) return false;

        var minutes = time.Hour * 60 + time.Minute;
        return QuietHoursStartMinutes < QuietHoursEndMinutes
            ? minutes >= QuietHoursStartMinutes && minutes < QuietHoursEndMinutes
            : minutes >= QuietHoursStartMinutes || minutes < QuietHoursEndMinutes;
    }
}
