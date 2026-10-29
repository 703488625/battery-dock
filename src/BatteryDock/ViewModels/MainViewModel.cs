using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using BatteryDock.Models;
using BatteryDock.Services;

namespace BatteryDock.ViewModels;

internal sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PeripheralBatteryService _service;
    private readonly SettingsService _settingsService;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly bool _isDemo;

    private IReadOnlyList<DeviceReading> _allReadings = Array.Empty<DeviceReading>();
    private IReadOnlyList<DeviceReading> _visibleReadings = Array.Empty<DeviceReading>();
    private ICommand _emptyActionCommand;
    private DateTimeOffset? _lastUpdated;
    private bool _hasLoaded;
    private bool _isRefreshing;
    private bool _isSettingsOpen;
    private string? _errorMessage;
    private bool _hadDevices;
    private bool _disposed;

    public MainViewModel(
        PeripheralBatteryService service,
        SettingsService settingsService,
        bool isDemo)
    {
        _service = service;
        _settingsService = settingsService;
        _isDemo = isDemo;

        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        if (DevicesView is ListCollectionView listView)
        {
            listView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(DeviceItemViewModel.BatteryGroup)));
        }

        Settings = new SettingsViewModel(
            settingsService,
            onThemeChanged: () => ThemeChanged?.Invoke(_settingsService.Settings.Theme),
            onDisplayChanged: () =>
            {
                ApplyFilter();
                VisibleDevicesChanged?.Invoke();
            },
            onRefreshChanged: () =>
            {
                OnPropertyChanged(nameof(FooterText));
                RefreshIntervalChanged?.Invoke();
            },
            onAlertChanged: () => OnPropertyChanged(nameof(FooterText)),
            onStartupChanged: () => StartupSettingChanged?.Invoke());

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
        OpenSettingsCommand = new RelayCommand(() => IsSettingsOpen = true);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
        OpenBluetoothSettingsCommand = new RelayCommand(OpenBluetoothSettings);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        _emptyActionCommand = OpenBluetoothSettingsCommand;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<IReadOnlyList<DeviceReading>>? ReadingsUpdated;
    public event Action? VisibleDevicesChanged;
    public event Action<AppThemeMode>? ThemeChanged;
    public event Action? RefreshIntervalChanged;
    public event Action? StartupSettingChanged;
    public event Action? ExitRequested;

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();
    public ICollectionView DevicesView { get; }
    public SettingsViewModel Settings { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }
    public ICommand OpenBluetoothSettingsCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand EmptyActionCommand => _emptyActionCommand;

    public bool IsDemo => _isDemo;

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (_isSettingsOpen == value) return;
            _isSettingsOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HeaderTitle));
            OnPropertyChanged(nameof(HeaderSubtitle));
            OnPropertyChanged(nameof(FooterText));
        }
    }

    public string HeaderTitle => IsSettingsOpen ? "设置" : "电量坞";

    public string HeaderSubtitle
    {
        get
        {
            if (IsSettingsOpen) return "调整外观、刷新与提醒";
            if (!HasLoaded) return "正在搜索蓝牙设备";
            var connected = _visibleReadings.Count(reading => reading.IsConnected);
            if (connected > 0) return $"已连接 {connected} 台设备";

            var remembered = _visibleReadings.Count(reading => reading.HasBattery);
            return remembered > 0
                ? $"显示 {remembered} 台设备（上次电量）"
                : "暂无已连接设备";
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (_isRefreshing == value) return;
            _isRefreshing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(ShowLoadingState));
            OnPropertyChanged(nameof(ShowEmptyState));
            (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasLoaded
    {
        get => _hasLoaded;
        private set
        {
            if (_hasLoaded == value) return;
            _hasLoaded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowLoadingState));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ShowDeviceList));
            OnPropertyChanged(nameof(HeaderSubtitle));
            UpdatePlaceholder();
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value) return;
            _errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
            UpdatePlaceholder();
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasDevices => Devices.Count > 0;
    public bool ShowDeviceList => HasLoaded && HasDevices;
    public bool ShowEmptyState =>
        HasLoaded && !HasDevices && !(IsRefreshing && _hadDevices);
    public bool ShowLoadingState => !HasLoaded;

    public string EmptyTitle { get; private set; } = "还没有发现蓝牙外设";
    public string EmptyDescription { get; private set; } = "连接蓝牙键盘、鼠标或手柄后，电量会自动出现在这里。";
    public string EmptyActionText { get; private set; } = "打开蓝牙设置";

    public string FooterText
    {
        get
        {
            if (IsSettingsOpen) return "设置会自动保存 · 数据仅在本机读取";
            if (IsRefreshing) return "正在刷新…";
            if (_lastUpdated.HasValue)
            {
                return $"上次更新 {_lastUpdated.Value:HH:mm} · {Settings.RefreshFooterText}";
            }
            return Settings.RefreshFooterText;
        }
    }

    public IReadOnlyList<DeviceReading> VisibleReadings => _visibleReadings;

    public async Task RefreshAsync()
    {
        if (_disposed || IsRefreshing) return;
        IsRefreshing = true;
        ErrorMessage = null;

        try
        {
            void ApplyPreviewCore(IReadOnlyList<DeviceReading> preview)
            {
                if (_disposed) return;

                // Previews arrive from partial sources (Bluetooth first, then
                // the HID scans). Merge instead of replacing so an early empty
                // preview cannot blank the panel while a refresh is running.
                _allReadings = MergeReadings(_allReadings, preview);
                HasLoaded = true;
                ApplyFilter();
                VisibleDevicesChanged?.Invoke();
            }

            // Preview callbacks come from the HID/Bluetooth worker threads;
            // ObservableCollection may only be changed on the UI thread.
            void ApplyPreview(IReadOnlyList<DeviceReading> preview)
            {
                if (_uiContext == null || SynchronizationContext.Current == _uiContext)
                {
                    ApplyPreviewCore(preview);
                    return;
                }

                _uiContext.Post(_ => ApplyPreviewCore(preview), null);
            }

            var readings = await _service.ScanAsync(_disposeCts.Token, ApplyPreview);
            if (_disposed) return;

            _allReadings = readings;
            _lastUpdated = DateTimeOffset.Now;
            HasLoaded = true;
            ApplyFilter();
            ReadingsUpdated?.Invoke(_visibleReadings);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            // Application is shutting down.
        }
        catch (Exception ex)
        {
            Logger.Error("刷新蓝牙电量", ex);
            HasLoaded = true;
            ErrorMessage = "无法读取蓝牙设备，请确认蓝牙已开启后重试。";
            UpdatePlaceholder();
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(FooterText));
        }
    }

    public void RequestRefreshIfStale(TimeSpan minimumAge)
    {
        if (_lastUpdated == null || DateTimeOffset.Now - _lastUpdated.Value >= minimumAge)
        {
            _ = RefreshAsync();
        }
    }

    private void ApplyFilter()
    {
        var settings = _settingsService.Settings;
        IEnumerable<DeviceReading> query = _allReadings;

        if (!settings.ShowDisconnectedDevices)
        {
            // Keep sleeping keyboards / mice / controllers visible with their
            // last-known percentage. Fully unknown or unrelated offline
            // devices still respect the setting.
            query = query.Where(reading =>
                reading.IsConnected ||
                (reading.HasBattery &&
                 reading.Kind is DeviceKind.Keyboard or DeviceKind.Mouse or DeviceKind.Gamepad));
        }

        if (!settings.ShowAllDevices)
        {
            query = query.Where(IsDefaultVisible);
        }

        _visibleReadings = query
            .OrderByDescending(reading => reading.IsConnected)
            .ThenBy(reading => KindOrder(reading.Kind))
            .ThenBy(reading => reading.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        SyncDevices(_visibleReadings);
        DevicesView.Refresh();

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(ShowDeviceList));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HeaderSubtitle));
        UpdatePlaceholder();
    }

    private void SyncDevices(IReadOnlyList<DeviceReading> readings)
    {
        if (readings.Count > 0) _hadDevices = true;

        var desired = new HashSet<string>(
            readings.Select(reading => reading.Key),
            StringComparer.OrdinalIgnoreCase);

        for (var index = Devices.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(Devices[index].Key))
            {
                Devices.RemoveAt(index);
            }
        }

        var byKey = new Dictionary<string, DeviceItemViewModel>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var device in Devices) byKey[device.Key] = device;

        for (var index = 0; index < readings.Count; index++)
        {
            var reading = readings[index];
            if (byKey.TryGetValue(reading.Key, out var existing))
            {
                existing.Update(reading);
                var currentIndex = Devices.IndexOf(existing);
                if (currentIndex != index) Devices.Move(currentIndex, index);
                continue;
            }

            var created = new DeviceItemViewModel(reading);
            byKey[reading.Key] = created;
            Devices.Insert(index, created);
        }
    }

    private static IReadOnlyList<DeviceReading> MergeReadings(
        IReadOnlyList<DeviceReading> current,
        IReadOnlyList<DeviceReading> preview)
    {
        if (current.Count == 0) return preview;

        var merged = new Dictionary<string, DeviceReading>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var reading in current) merged[reading.Key] = reading;

        foreach (var reading in preview)
        {
            if (merged.TryGetValue(reading.Key, out var existing) &&
                existing.IsConnected &&
                existing.Battery.HasValue &&
                !reading.IsConnected &&
                !reading.Battery.HasValue)
            {
                // A partial source can briefly report a live 2.4G device as
                // absent while the other scan is still running. Keep the
                // visible card until the final merged result arrives.
                continue;
            }

            merged[reading.Key] = reading;
        }

        return XboxReadingFilter.Dedupe(merged.Values);
    }

    private static bool IsDefaultVisible(DeviceReading reading)
    {
        // A disconnected device with no battery reading has nothing useful to
        // show. Devices that sleep and still have a last-known reading remain.
        if (!reading.IsConnected && !reading.HasBattery) return false;

        if (reading.Kind is DeviceKind.Keyboard or DeviceKind.Mouse or DeviceKind.Gamepad)
        {
            return true;
        }

        // Unknown devices are kept only when they actually report a battery or
        // are currently connected, so unrelated paired phones stay hidden.
        return reading.Kind == DeviceKind.Other && (reading.HasBattery || reading.IsConnected);
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

    private void UpdatePlaceholder()
    {
        if (HasError && _allReadings.Count == 0)
        {
            EmptyTitle = "暂时无法读取蓝牙设备";
            EmptyDescription = "请确认 Windows 蓝牙已开启，然后重试。";
            EmptyActionText = "重试";
            SetEmptyAction(RefreshCommand);
        }
        else if (_allReadings.Count == 0)
        {
            EmptyTitle = "还没有发现蓝牙外设";
            EmptyDescription = "连接蓝牙或 2.4G 键盘、鼠标、手柄后，电量会自动出现在这里。";
            EmptyActionText = "打开蓝牙设置";
            SetEmptyAction(OpenBluetoothSettingsCommand);
        }
        else if (_visibleReadings.Count == 0)
        {
            EmptyTitle = "暂无已连接的设备";
            EmptyDescription = "连接蓝牙键盘、鼠标或手柄后会自动出现。也可以在设置中调整显示范围。";
            EmptyActionText = "打开设置";
            SetEmptyAction(OpenSettingsCommand);
        }

        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(EmptyActionText));
    }

    private void SetEmptyAction(ICommand command)
    {
        if (ReferenceEquals(_emptyActionCommand, command)) return;
        _emptyActionCommand = command;
        OnPropertyChanged(nameof(EmptyActionCommand));
    }

    private void OpenBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("打开 Windows 蓝牙设置", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _disposeCts.Cancel();
        }
        catch
        {
            // Ignore.
        }
        _service.Dispose();
        _disposeCts.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
