using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BatteryDock.Models;
using BatteryDock.Services;
using BatteryDock.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace BatteryDock;

public partial class MouseSettingsWindow : Window
{
    private readonly DeviceItemViewModel _device;
    private readonly MouseDpiService _service;
    private readonly CancellationTokenSource _cts = new();
    private MouseDpiState? _state;
    private MousePollingState? _pollingState;
    private bool _syncing;
    private bool _busy;

    internal Action? OnBack { get; set; }

    internal MouseSettingsWindow(DeviceItemViewModel device, MouseDpiService service)
    {
        _device = device;
        _service = service;
        InitializeComponent();
        ApplyThemeSurface();
        DeviceNameText.Text = device.Name;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAllAsync();
    }

    private async Task RefreshAllAsync()
    {
        await RefreshDpiAsync();
        await RefreshPollingAsync();
    }

    private async Task RefreshDpiAsync()
    {
        if (_busy) return;
        SetBusy(true);
        SetStatus("正在读取设备 DPI…");
        try
        {
            var state = await _service.ReadAsync(_device.Source, _cts.Token);
            ApplyState(state);
            SetStatus("已读取。拖动滑块或输入数值后点击“应用 DPI”。");
        }
        catch (NotSupportedException ex)
        {
            ShowUnsupported(ex.Message);
        }
        catch (IOException ex)
        {
            SetStatus(ex.Message, warning: true);
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }
        catch (Exception ex)
        {
            SetStatus($"读取 DPI 失败：{ex.Message}", warning: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshPollingAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var state = await _service.ReadPollingAsync(_device.Source, _cts.Token);
            ApplyPollingState(state);
        }
        catch (NotSupportedException ex)
        {
            _pollingState = null;
            PollingCurrentText.Text = ex.Message;
            PollingList.ItemsSource = null;
            PollingList.IsEnabled = false;
        }
        catch (IOException ex)
        {
            PollingCurrentText.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }
        catch (Exception ex)
        {
            PollingCurrentText.Text = $"读取回报率失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyPollingState(MousePollingState state)
    {
        _pollingState = state;
        var rates = state.SupportedRates;
        var items = rates.Select(rate => $"{rate} Hz").ToList();
        PollingList.ItemsSource = items;
        PollingList.IsEnabled = true;
        if (state.Hz > 0)
        {
            var index = rates.ToList().IndexOf(state.Hz);
            PollingList.SelectedIndex = index >= 0 ? index : 0;
            PollingCurrentText.Text = $"当前 {state.Hz} Hz · {state.Backend}";
        }
        else
        {
            PollingList.SelectedIndex = -1;
            PollingCurrentText.Text = "当前值未知（设备不支持查询），请选择目标回报率后应用";
        }
    }

    private async void PollingApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _pollingState == null) return;
        if (PollingList.SelectedItem is not string text) return;
        if (!int.TryParse(text.Replace(" Hz", string.Empty, StringComparison.OrdinalIgnoreCase),
                out var hz))
        {
            return;
        }

        SetBusy(true);
        SetStatus($"正在设置回报率 {hz} Hz…");
        try
        {
            await _service.SetPollingAsync(_device.Source, hz, _cts.Token);
            SetStatus("回报率已写入，正在回读确认…");
            var state = await _service.ReadPollingAsync(_device.Source, _cts.Token);
            ApplyPollingState(state);
            SetStatus($"已应用：{state.Hz} Hz 回报率。");
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }
        catch (Exception ex)
        {
            SetStatus($"回报率设置失败：{ex.Message}", warning: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyState(MouseDpiState state)
    {
        _state = state;
        _syncing = true;
        try
        {
            DpiSlider.Minimum = state.MinDpi;
            DpiSlider.Maximum = state.MaxDpi;
            DpiSlider.TickFrequency = Math.Max(1, state.Step);
            DpiSlider.IsSnapToTickEnabled = state.Step > 1;
            var value = Math.Clamp(state.DpiX, state.MinDpi, state.MaxDpi);
            DpiSlider.Value = value;
            DpiBox.Text = value.ToString(CultureInfo.InvariantCulture);

            RangeText.Text = $"{state.MinDpi} – {state.MaxDpi} DPI · 步进 {state.Step}";
            CurrentDpiText.Text = $"{state.DpiX} DPI";
            CurrentXyText.Text = $"X {state.DpiX} · Y {state.DpiY}";
            BackendText.Text = state.Backend;
            ControlsPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyDpiAsync();
    }

    private async Task ApplyDpiAsync()
    {
        if (_busy || _state == null) return;
        if (!int.TryParse(DpiBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var dpi))
        {
            SetStatus("请输入有效的 DPI 数字。", warning: true);
            return;
        }

        dpi = Math.Clamp(dpi, _state.MinDpi, _state.MaxDpi);
        if (_state.Step > 1)
        {
            var steps = (int)Math.Round((dpi - _state.MinDpi) / (double)_state.Step);
            dpi = _state.MinDpi + steps * _state.Step;
        }

        dpi = Math.Clamp(dpi, _state.MinDpi, _state.MaxDpi);
        DpiBox.Text = dpi.ToString(CultureInfo.InvariantCulture);

        SetBusy(true);
        SetStatus($"正在写入 {dpi} DPI…");
        try
        {
            await _service.SetAsync(_device.Source, dpi, dpi, _cts.Token);
            SetStatus("已写入，正在回读确认…");
            var state = await _service.ReadAsync(_device.Source, _cts.Token);
            ApplyState(state);
            SetStatus($"已应用：{dpi} DPI（设备回读 X {state.DpiX} / Y {state.DpiY}）。");
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }
        catch (Exception ex)
        {
            SetStatus($"应用失败：{ex.Message}", warning: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshAllAsync();
    }

    private void DpiSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || DpiBox == null) return;
        DpiBox.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
    }

    private void DpiBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SyncSliderFromText();
    }

    private void DpiBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SyncSliderFromText();
        _ = ApplyDpiAsync();
    }

    private void SyncSliderFromText()
    {
        if (_state == null || _syncing) return;
        if (!int.TryParse(DpiBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var dpi))
        {
            return;
        }

        dpi = Math.Clamp(dpi, _state.MinDpi, _state.MaxDpi);
        _syncing = true;
        try
        {
            DpiSlider.Value = dpi;
            DpiBox.Text = dpi.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ApplyButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        DpiSlider.IsEnabled = !busy;
        DpiBox.IsEnabled = !busy;
        PollingApplyButton.IsEnabled = !busy && _pollingState != null;
        PollingList.IsEnabled = !busy && _pollingState != null;
    }

    private void ShowUnsupported(string message)
    {
        _state = null;
        ControlsPanel.Visibility = Visibility.Collapsed;
        CurrentDpiText.Text = "—";
        CurrentXyText.Text = string.Empty;
        BackendText.Text = string.Empty;
        SetStatus(message, warning: true);
    }

    private void SetStatus(string message, bool warning = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = warning
            ? (TryFindResource("WarningBrush") as Brush ?? Brushes.Orange)
            : (TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray);
    }

    private void ApplyThemeSurface()
    {
        if (TryFindResource("WindowBackgroundBrush") is not SolidColorBrush brush) return;
        Background = new SolidColorBrush(Color.FromRgb(brush.Color.R, brush.Color.G, brush.Color.B));
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (OnBack != null) OnBack();
        else Close();
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.ListBoxItem>(e.OriginalSource) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current)
                      ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
