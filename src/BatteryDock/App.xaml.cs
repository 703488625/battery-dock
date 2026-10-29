using System.Windows;
using System.Windows.Threading;
using BatteryDock.Models;
using BatteryDock.Services;
using BatteryDock.ViewModels;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BatteryDock;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showEventRegistration;

    private SettingsService? _settingsService;
    private LastKnownStore? _lastKnown;
    private PeripheralBatteryService? _batteryService;
    private MainViewModel? _viewModel;
    private TrayPopupWindow? _popup;
    private TrayIconManager? _tray;
    private DispatcherTimer? _refreshTimer;
    private static readonly TimeSpan LowBatteryRepeatInterval = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, DateTimeOffset> _lowNotifiedAt = new(StringComparer.OrdinalIgnoreCase);

    private bool _isDemo;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = ParseArguments(e.Args);
        Logger.Initialize(options.Verbose);
        Logger.Info($"启动电量坞，参数：{string.Join(" ", e.Args)}");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _instanceMutex = new Mutex(true, @"Local\BatteryDock.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                SignalExistingInstance();
                Shutdown();
                return;
            }
            _ownsInstanceMutex = true;

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BatteryDock.Show");
            _showEventRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showEvent, OnShowSignal, null, Timeout.Infinite, false);

            _isDemo = options.Demo;
            Logger.Info("步骤：读取设置");
            _settingsService = new SettingsService();
            Logger.Info("步骤：应用主题");
            ThemeManager.Apply(_settingsService.Settings.Theme);
            Logger.Info("步骤：读取上次电量");
            _lastKnown = new LastKnownStore();
            Logger.Info("步骤：创建蓝牙服务");
            var bluetoothService = new BluetoothBatteryService(_isDemo, _lastKnown);
            var wirelessHidService = new WirelessHidBatteryService(_isDemo, _lastKnown);
            var logitechService = new LogitechHidppService(_isDemo, _lastKnown);
            var xinputService = new XInputBatteryService(_isDemo);
            var mouseDpiService = new MouseDpiService(logitechService);
            _batteryService = new PeripheralBatteryService(
                bluetoothService, wirelessHidService, logitechService, xinputService);
            Logger.Info("步骤：创建视图模型");
            _viewModel = new MainViewModel(_batteryService, _settingsService, _isDemo);
            logitechService.DevicesChanged += OnLogitechDevicesChanged;

            SubscribeToViewModel();

            Logger.Info("步骤：创建弹窗");
            _popup = new TrayPopupWindow(_viewModel, mouseDpiService) { AutoHide = !_isDemo };
            _popup.IsVisibleChanged += (_, _) => ConfigureRefreshTimer();
            Logger.Info("步骤：创建托盘图标");
            _tray = new TrayIconManager(
                onOpen: OnTrayOpen,
                onRefresh: OnTrayRefresh,
                isStartupEnabled: StartupService.IsEnabled,
                toggleStartup: ToggleStartupFromTray,
                onSettings: () => ShowPanel(true),
                onExit: ExitApplication);
            _tray.SetTheme(ThemeManager.IsDark(_settingsService.Settings.Theme));
            Logger.Info("步骤：托盘图标完成");

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SessionEnding += OnSessionEnding;

            ConfigureRefreshTimer();
            Logger.Info("步骤：开始初始化");

            _ = InitializeAsync(options.Show || options.Demo || options.Settings, options.Demo, options.Settings);
        }
        catch (Exception ex)
        {
            Logger.Error("启动", ex);
            System.Windows.MessageBox.Show(
                "电量坞启动失败。\n\n" + ex.Message,
                "电量坞",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Cleanup();
            Shutdown(1);
        }
    }

    private void SubscribeToViewModel()
    {
        if (_viewModel == null) return;
        _viewModel.ReadingsUpdated += OnReadingsUpdated;
        _viewModel.VisibleDevicesChanged += UpdateTray;
        _viewModel.ThemeChanged += OnThemeChanged;
        _viewModel.RefreshIntervalChanged += ConfigureRefreshTimer;
        _viewModel.StartupSettingChanged += () => _tray?.RefreshStartupState();
        _viewModel.ExitRequested += ExitApplication;
    }

    private async Task InitializeAsync(bool showPopup, bool demo, bool openSettings)
    {
        if (_viewModel == null || _settingsService == null) return;
        try
        {
            var refreshTask = _viewModel.RefreshAsync();
            if (showPopup)
            {
                await Task.Delay(180);
                ShowPanel(openSettings);
            }

            await refreshTask;

            if (!demo && !_settingsService.Settings.WelcomeShown)
            {
                _settingsService.Settings.WelcomeShown = true;
                _settingsService.Save();
                if (_popup?.IsVisible != true)
                {
                    await Task.Delay(1200);
                    if (!_isExiting && _popup?.IsVisible != true)
                    {
                        _tray?.ShowBalloon(
                            "电量坞已在托盘运行",
                            "点击任务栏中的电池图标，查看蓝牙键盘、鼠标和手柄电量。",
                            Forms.ToolTipIcon.Info);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("初始化电量坞", ex);
        }
    }

    private void ConfigureRefreshTimer()
    {
        if (_viewModel == null || _isExiting) return;
        if (_refreshTimer == null)
        {
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background);
            _refreshTimer.Tick += OnRefreshTimerTick;
        }

        var baseSeconds = _viewModel.Settings.RefreshSecondsValue;
        var panelVisible = _popup?.IsVisible == true;
        var seconds = panelVisible
            ? baseSeconds
            : Math.Min(600, Math.Max(baseSeconds, 120));
        var interval = TimeSpan.FromSeconds(seconds);
        if (_refreshTimer.Interval != interval)
        {
            _refreshTimer.Interval = interval;
            Logger.Info($"刷新间隔：{seconds} 秒（面板{(panelVisible ? "打开" : "关闭")}）");
        }

        _refreshTimer.Start();
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_isExiting || _viewModel == null) return;
        await _viewModel.RefreshAsync();
    }

    private void OnReadingsUpdated(IReadOnlyList<DeviceReading> readings)
    {
        UpdateTray();
        ProcessLowBatteryAlerts(readings);
    }

    private void UpdateTray()
    {
        if (_tray == null || _viewModel == null || _settingsService == null) return;

        var connected = _viewModel.VisibleReadings
            .Where(reading => reading.IsConnected)
            .ToList();
        var withBattery = connected
            .Where(reading => reading.HasBattery)
            .OrderBy(reading => reading.Battery)
            .ToList();

        var threshold = _settingsService.Settings.LowBatteryThreshold;
        var isLow = withBattery.Any(reading =>
            !reading.IsCharging && !reading.IsFull && reading.Battery <= threshold);
        var isCharging = connected.Any(reading => reading.IsCharging || reading.IsFull);
        var state = connected.Count == 0
            ? TrayIconState.Empty
            : isLow ? TrayIconState.Low
            : isCharging ? TrayIconState.Charging
            : TrayIconState.Normal;

        string tooltip;
        if (connected.Count == 0)
        {
            tooltip = "电量坞 · 暂无连接设备";
        }
        else if (withBattery.Count == 0)
        {
            tooltip = $"电量坞 · 已连接 {connected.Count} 台设备";
        }
        else
        {
            var parts = withBattery.Take(2)
                .Select(reading => $"{reading.Name} " +
                                   $"{(reading.IsEstimated ? "≈" : string.Empty)}{reading.Battery}%" +
                                   (reading.IsFull ? " 已满" : reading.IsCharging ? " 充电中" : string.Empty))
                .ToList();
            var unknownCount = connected.Count - withBattery.Count;
            if (unknownCount > 0)
            {
                parts.Add($"{unknownCount} 台暂无读数");
            }
            else if (connected.Count > parts.Count)
            {
                parts.Add($"等 {connected.Count} 台");
            }
            tooltip = "电量坞 · " + string.Join(" · ", parts);
        }

        _tray.Update(tooltip, state);
    }

    private void ProcessLowBatteryAlerts(IReadOnlyList<DeviceReading> readings)
    {
        if (_isDemo || _tray == null || _settingsService == null) return;

        var settings = _settingsService.Settings;
        if (!settings.LowBatteryAlert)
        {
            _lowNotifiedAt.Clear();
            return;
        }

        var now = DateTimeOffset.Now;
        var threshold = settings.LowBatteryThreshold;
        var lowNow = readings
            .Where(reading => reading.IsConnected && reading.IsFreshBattery &&
                              !reading.IsCharging && !reading.IsFull &&
                              reading.Battery <= threshold)
            .ToList();

        foreach (var key in _lowNotifiedAt.Keys.ToArray())
        {
            var current = readings.FirstOrDefault(reading =>
                string.Equals(reading.Key, key, StringComparison.OrdinalIgnoreCase));
            if (current == null ||
                !current.IsConnected ||
                !current.Battery.HasValue ||
                current.Battery.Value > threshold + 5)
            {
                _lowNotifiedAt.Remove(key);
            }
        }

        // Keep reminding every 30 minutes while the device is still below
        // the threshold, instead of notifying only once per discharge cycle.
        var newlyLow = lowNow
            .Where(reading =>
                !_lowNotifiedAt.TryGetValue(reading.Key, out var lastNotified) ||
                now - lastNotified >= LowBatteryRepeatInterval)
            .ToList();

        if (newlyLow.Count == 0) return;
        if (settings.IsQuietHours(now))
        {
            Logger.Info("免打扰时段：跳过低电量通知（" +
                        string.Join("、", newlyLow.Select(reading =>
                            $"{reading.Name} {reading.Battery}%")) + "）");
            return;
        }

        foreach (var reading in newlyLow)
        {
            _lowNotifiedAt[reading.Key] = now;
        }

        var message = newlyLow.Count == 1
            ? $"{newlyLow[0].Name} 仅剩 {newlyLow[0].Battery}%，建议及时充电。"
            : string.Join("、", newlyLow.Select(reading => $"{reading.Name} {reading.Battery}%")) +
              " 电量偏低，建议及时充电。";

        _tray.ShowBalloon("设备电量偏低", message, Forms.ToolTipIcon.Warning);
    }

    private void OnTrayOpen()
    {
        if (_popup == null) return;
        if (_popup.IsVisible)
        {
            _popup.HidePanel();
            return;
        }
        if (_popup.HiddenByDeactivationRecently)
        {
            return;
        }
        ShowPanel(false);
    }

    private void OnTrayRefresh()
    {
        _ = _viewModel?.RefreshAsync();
    }

    private void OnLogitechDevicesChanged()
    {
        if (_isExiting) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isExiting || _viewModel == null) return;
            _viewModel.RequestRefreshIfStale(TimeSpan.Zero);
        }));
    }

    private void ShowPanel(bool settings)
    {
        if (_isExiting || _popup == null || _viewModel == null) return;
        Logger.Info($"步骤：显示面板 settings={settings}");
        _viewModel.IsSettingsOpen = settings;
        _popup.ShowNearCursor();
        _viewModel.RequestRefreshIfStale(TimeSpan.FromSeconds(8));
    }

    private void ToggleStartupFromTray()
    {
        var enable = !StartupService.IsEnabled();
        if (!StartupService.SetEnabled(enable))
        {
            _tray?.ShowBalloon("无法修改开机启动", "系统拒绝写入启动项。", Forms.ToolTipIcon.Error);
            return;
        }

        _viewModel?.Settings.ReloadStartupState();
        _tray?.RefreshStartupState();
    }

    private void OnThemeChanged(AppThemeMode mode)
    {
        ThemeManager.Apply(mode);
        _tray?.SetTheme(ThemeManager.IsDark(mode));
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_settingsService?.Settings.Theme != AppThemeMode.System || _isExiting) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isExiting) return;
            ThemeManager.Apply(AppThemeMode.System);
            _tray?.SetTheme(ThemeManager.IsSystemDark());
        }));
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _isExiting) return;
        _ = RefreshAfterResumeAsync();
    }

    private async Task RefreshAfterResumeAsync()
    {
        try
        {
            await Task.Delay(1500);
            if (!_isExiting && _viewModel != null)
            {
                await _viewModel.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("睡眠恢复后刷新", ex);
        }
    }

    private void OnShowSignal(object? state, bool timedOut)
    {
        if (timedOut || _isExiting) return;
        Dispatcher.BeginInvoke(new Action(() => ShowPanel(false)));
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\BatteryDock.Show", out var handle))
            {
                handle.Set();
                handle.Dispose();
            }
        }
        catch
        {
            // Another instance may be starting or exiting.
        }
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        try
        {
            _settingsService?.Save();
            _lastKnown?.Save();
        }
        catch
        {
            // Ignore shutdown-time persistence failures.
        }
    }

    private void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;

        try
        {
            _settingsService?.Save();
            _lastKnown?.Save();
        }
        catch
        {
            // Ignore.
        }

        Cleanup();
        Shutdown();
    }

    private void Cleanup()
    {
        _isExiting = true;

        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTimerTick;
            _refreshTimer = null;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SessionEnding -= OnSessionEnding;

        try
        {
            _popup?.ForceClose();
        }
        catch
        {
            // Ignore.
        }
        _popup = null;

        _tray?.Dispose();
        _tray = null;

        if (_viewModel != null)
        {
            _viewModel.ReadingsUpdated -= OnReadingsUpdated;
            _viewModel.VisibleDevicesChanged -= UpdateTray;
            _viewModel.ThemeChanged -= OnThemeChanged;
            _viewModel.RefreshIntervalChanged -= ConfigureRefreshTimer;
            _viewModel.ExitRequested -= ExitApplication;
            _viewModel.Dispose();
            _viewModel = null;
        }

        _batteryService = null;
        _lastKnown?.Save();
        _lastKnown = null;

        try
        {
            _showEventRegistration?.Unregister(null);
        }
        catch
        {
            // Ignore.
        }
        _showEventRegistration = null;
        _showEvent?.Dispose();
        _showEvent = null;

        if (_instanceMutex != null)
        {
            try
            {
                if (_ownsInstanceMutex) _instanceMutex.ReleaseMutex();
            }
            catch
            {
                // Ignore.
            }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            _ownsInstanceMutex = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("未处理的界面异常", e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Logger.Error("未处理的应用程序异常", exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("未观察的异步异常", e.Exception);
        e.SetObserved();
    }

    private static StartupOptions ParseArguments(IEnumerable<string> args)
    {
        var set = args
            .Where(argument => !string.IsNullOrWhiteSpace(argument))
            .Select(argument => argument.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new StartupOptions(
            Demo: set.Contains("--demo"),
            Show: set.Contains("--show"),
            Settings: set.Contains("--settings"),
            Verbose: set.Contains("--verbose"));
    }

    private sealed record StartupOptions(bool Demo, bool Show, bool Settings, bool Verbose);
}
