using System.ComponentModel;
using System.Runtime.CompilerServices;
using BatteryDock.Models;
using BatteryDock.Services;

namespace BatteryDock.ViewModels;

internal sealed class SettingsViewModel : INotifyPropertyChanged
{
    public static readonly string[] ThemeOptions = { "跟随系统", "浅色", "深色" };
    public static readonly string[] RefreshOptions = { "15 秒", "30 秒", "1 分钟", "5 分钟" };
    public static readonly string[] ThresholdOptions = { "10%", "20%", "30%" };

    private static readonly int[] RefreshSeconds = { 15, 30, 60, 300 };
    private static readonly int[] Thresholds = { 10, 20, 30 };

    private readonly SettingsService _settingsService;
    private readonly Action _onThemeChanged;
    private readonly Action _onDisplayChanged;
    private readonly Action _onRefreshChanged;
    private readonly Action _onAlertChanged;
    private readonly Action _onStartupChanged;

    private int _themeIndex;
    private int _refreshIndex;
    private int _thresholdIndex;
    private bool _lowBatteryAlert;
    private bool _showDisconnectedDevices;
    private bool _showAllDevices;
    private bool _startWithWindows;
    private bool _quietHoursEnabled;
    private string _quietStartText = "23:00";
    private string _quietEndText = "07:00";

    public SettingsViewModel(
        SettingsService settingsService,
        Action onThemeChanged,
        Action onDisplayChanged,
        Action onRefreshChanged,
        Action onAlertChanged,
        Action onStartupChanged)
    {
        _settingsService = settingsService;
        _onThemeChanged = onThemeChanged;
        _onDisplayChanged = onDisplayChanged;
        _onRefreshChanged = onRefreshChanged;
        _onAlertChanged = onAlertChanged;
        _onStartupChanged = onStartupChanged;

        var settings = settingsService.Settings;
        _themeIndex = (int)settings.Theme;
        _refreshIndex = Array.IndexOf(RefreshSeconds, (int)settings.Refresh);
        if (_refreshIndex < 0) _refreshIndex = 1;
        _thresholdIndex = Array.IndexOf(Thresholds, settings.LowBatteryThreshold);
        if (_thresholdIndex < 0) _thresholdIndex = 1;
        _lowBatteryAlert = settings.LowBatteryAlert;
        _showDisconnectedDevices = settings.ShowDisconnectedDevices;
        _showAllDevices = settings.ShowAllDevices;
        _startWithWindows = StartupService.IsEnabled();
        _quietHoursEnabled = settings.QuietHoursEnabled;
        _quietStartText = FormatTime(settings.QuietHoursStartMinutes);
        _quietEndText = FormatTime(settings.QuietHoursEndMinutes);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppSettings Model => _settingsService.Settings;

    public string[] Themes => ThemeOptions;
    public string[] Refreshes => RefreshOptions;
    public string[] ThresholdsLabels => ThresholdOptions;

    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (value < 0 || value > 2 || _themeIndex == value) return;
            _themeIndex = value;
            _settingsService.Settings.Theme = (AppThemeMode)value;
            _settingsService.Save();
            OnPropertyChanged();
            _onThemeChanged();
        }
    }

    public int RefreshIndex
    {
        get => _refreshIndex;
        set
        {
            if (value < 0 || value >= RefreshSeconds.Length || _refreshIndex == value) return;
            _refreshIndex = value;
            _settingsService.Settings.Refresh = (RefreshInterval)RefreshSeconds[value];
            _settingsService.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RefreshFooterText));
            _onRefreshChanged();
        }
    }

    public int ThresholdIndex
    {
        get => _thresholdIndex;
        set
        {
            if (value < 0 || value >= Thresholds.Length || _thresholdIndex == value) return;
            _thresholdIndex = value;
            _settingsService.Settings.LowBatteryThreshold = Thresholds[value];
            _settingsService.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThresholdSummary));
            _onAlertChanged();
        }
    }

    public bool LowBatteryAlert
    {
        get => _lowBatteryAlert;
        set
        {
            if (_lowBatteryAlert == value) return;
            _lowBatteryAlert = value;
            _settingsService.Settings.LowBatteryAlert = value;
            _settingsService.Save();
            OnPropertyChanged();
            _onAlertChanged();
        }
    }

    public bool ShowDisconnectedDevices
    {
        get => _showDisconnectedDevices;
        set
        {
            if (_showDisconnectedDevices == value) return;
            _showDisconnectedDevices = value;
            _settingsService.Settings.ShowDisconnectedDevices = value;
            _settingsService.Save();
            OnPropertyChanged();
            _onDisplayChanged();
        }
    }

    public bool ShowAllDevices
    {
        get => _showAllDevices;
        set
        {
            if (_showAllDevices == value) return;
            _showAllDevices = value;
            _settingsService.Settings.ShowAllDevices = value;
            _settingsService.Save();
            OnPropertyChanged();
            _onDisplayChanged();
        }
    }

    public bool QuietHoursEnabled
    {
        get => _quietHoursEnabled;
        set
        {
            if (_quietHoursEnabled == value) return;
            _quietHoursEnabled = value;
            _settingsService.Settings.QuietHoursEnabled = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }

    public string QuietStartText
    {
        get => _quietStartText;
        set
        {
            if (value == _quietStartText) return;
            if (!TryParseTime(value, out var minutes))
            {
                OnPropertyChanged();
                return;
            }

            _quietStartText = FormatTime(minutes);
            _settingsService.Settings.QuietHoursStartMinutes = minutes;
            _settingsService.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(QuietHoursSummary));
        }
    }

    public string QuietEndText
    {
        get => _quietEndText;
        set
        {
            if (value == _quietEndText) return;
            if (!TryParseTime(value, out var minutes))
            {
                OnPropertyChanged();
                return;
            }

            _quietEndText = FormatTime(minutes);
            _settingsService.Settings.QuietHoursEndMinutes = minutes;
            _settingsService.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(QuietHoursSummary));
        }
    }

    public string QuietHoursSummary => $"{_quietStartText} 至 {_quietEndText} 不弹低电量通知";

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            if (!StartupService.SetEnabled(value))
            {
                OnPropertyChanged();
                return;
            }
            _startWithWindows = value;
            OnPropertyChanged();
            _onStartupChanged();
        }
    }

    public string RefreshFooterText => $"自动刷新 {RefreshOptions[_refreshIndex]}";

    public string ThresholdSummary => $"低于 {Thresholds[_thresholdIndex]}% 时提醒";

    public string VersionText => "电量坞 1.0 · 支持蓝牙与 2.4G · 数据仅在本机读取";

    public int RefreshSecondsValue => RefreshSeconds[_refreshIndex];

    public int ThresholdValue => Thresholds[_thresholdIndex];

    public void ReloadStartupState()
    {
        var enabled = StartupService.IsEnabled();
        if (_startWithWindows == enabled) return;
        _startWithWindows = enabled;
        OnPropertyChanged(nameof(StartWithWindows));
    }

    private static string FormatTime(int minutes) =>
        $"{Math.Clamp(minutes, 0, 24 * 60 - 1) / 60:00}:{Math.Clamp(minutes, 0, 24 * 60 - 1) % 60:00}";

    private static bool TryParseTime(string? text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var hour) ||
            !int.TryParse(parts[1], out var minute)) return false;
        if (hour is < 0 or > 23 || minute is < 0 or > 59) return false;

        minutes = hour * 60 + minute;
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
