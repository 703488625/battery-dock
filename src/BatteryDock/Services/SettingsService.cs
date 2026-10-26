using System.Text.Json;
using BatteryDock.Models;

namespace BatteryDock.Services;

internal sealed class SettingsService
{
    private readonly object _sync = new();

    public AppSettings Settings { get; private set; }
    public bool WasCreated { get; private set; }

    public SettingsService()
    {
        Settings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            if (!File.Exists(AppPaths.SettingsFile))
            {
                WasCreated = true;
                var fresh = new AppSettings();
                Save(fresh);
                return fresh;
            }

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options) ?? new AppSettings();
            Normalize(settings);
            return settings;
        }
        catch (Exception ex)
        {
            Logger.Error("读取设置", ex);
            WasCreated = true;
            var fallback = new AppSettings();
            try
            {
                File.Move(AppPaths.SettingsFile, AppPaths.SettingsFile + ".broken", true);
            }
            catch
            {
                // Ignore backup failures.
            }
            Save(fallback);
            return fallback;
        }
    }

    private static void Normalize(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme)) settings.Theme = AppThemeMode.System;
        if (!Enum.IsDefined(settings.Refresh)) settings.Refresh = RefreshInterval.ThirtySeconds;
        if (settings.LowBatteryThreshold is not (10 or 20 or 30)) settings.LowBatteryThreshold = 20;
        if (settings.QuietHoursStartMinutes is < 0 or >= 24 * 60) settings.QuietHoursStartMinutes = 23 * 60;
        if (settings.QuietHoursEndMinutes is < 0 or >= 24 * 60) settings.QuietHoursEndMinutes = 7 * 60;
    }

    public void Save()
    {
        lock (_sync)
        {
            Save(Settings);
        }
    }

    private static void Save(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            var json = JsonSerializer.Serialize(settings, JsonDefaults.Options);
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Logger.Error("保存设置", ex);
        }
    }
}
