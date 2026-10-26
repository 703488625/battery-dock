using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BatteryDock.Models;
using BatteryDock.Services;
using BatteryDock.ViewModels;
using Forms = System.Windows.Forms;

namespace BatteryDock;

public partial class TrayPopupWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly MouseDpiService _mouseDpi;
    private Window? _toolWindow;
    private System.Drawing.Rectangle? _panelPositionBeforeTool;
    private System.Drawing.Point _anchorPoint;
    private DateTimeOffset _lastHiddenByDeactivation;
    private bool _allowClose;
    private bool _hasBeenActive;
    private bool _userPositioned;

    internal bool AutoHide { get; set; } = true;

    internal TrayPopupWindow(MainViewModel viewModel, MouseDpiService mouseDpi)
    {
        _viewModel = viewModel;
        _mouseDpi = mouseDpi;
        InitializeComponent();
        DataContext = viewModel;
        SizeChanged += OnWindowSizeChanged;
        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
        Closing += OnWindowClosing;
    }

    public bool HiddenByDeactivationRecently =>
        DateTimeOffset.Now - _lastHiddenByDeactivation < TimeSpan.FromMilliseconds(350);

    public void ShowNearCursor() => ShowPanelAt(useCursorAnchor: true, restorePosition: null);

    private void ShowAtSavedPosition()
    {
        if (_panelPositionBeforeTool.HasValue)
        {
            ShowPanelAt(useCursorAnchor: false, restorePosition: _panelPositionBeforeTool.Value);
        }
        else
        {
            ShowPanelAt(useCursorAnchor: true, restorePosition: null);
        }
    }

    private void ShowPanelAt(bool useCursorAnchor, System.Drawing.Rectangle? restorePosition)
    {
        var wasVisible = IsVisible;
        Services.Logger.Info(
            $"弹窗显示 useAnchor={useCursorAnchor}, saved={restorePosition}, wasVisible={wasVisible}");
        if (!wasVisible)
        {
            if (useCursorAnchor) _anchorPoint = Forms.Cursor.Position;
            RootContent.Opacity = 0;
            SlideTransform.Y = 7;
            Opacity = 0;
            _hasBeenActive = false;
            _userPositioned = !useCursorAnchor;
            Show();
            Services.Logger.Info("弹窗 Show() 完成");
        }

        UpdateLayout();
        Services.Logger.Info($"弹窗布局 {ActualWidth}x{ActualHeight}");
        if (useCursorAnchor)
        {
            PositionAtAnchor();
        }
        else if (restorePosition.HasValue)
        {
            PositionAtSaved(restorePosition.Value);
        }

        Opacity = 1;

        if (!wasVisible)
        {
            AnimateIn();
        }

        Activate();
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        RootContent.BeginAnimation(OpacityProperty, null);
        RootContent.Opacity = 1;
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        SlideTransform.Y = 0;
        Hide();
        if (_viewModel.IsSettingsOpen)
        {
            _viewModel.IsSettingsOpen = false;
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void AnimateIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        RootContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            });
        SlideTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            });
        RootContent.Opacity = 1;
        SlideTransform.Y = 0;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

        _userPositioned = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the move loop began.
        }
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source != null && source is not Window)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
            if (source == null) break;
        }
        return false;
    }

    private void PositionAtAnchor()
    {
        if (!IsVisible) return;
        if (_userPositioned)
        {
            EnsureOnScreen();
            return;
        }
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelWidth = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var pixelHeight = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var screen = Forms.Screen.FromPoint(_anchorPoint);
        var work = screen.WorkingArea;

        var x = _anchorPoint.X - pixelWidth + 24;
        var y = _anchorPoint.Y - pixelHeight - 10;

        if (y < work.Top + 8)
        {
            y = _anchorPoint.Y + 22;
        }

        x = Math.Clamp(x, work.Left + 8, Math.Max(work.Left + 8, work.Right - pixelWidth - 8));
        y = Math.Clamp(y, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - pixelHeight - 8));

        Services.Logger.Info($"定位: anchor={_anchorPoint}, dpi={dpi.DpiScaleX:F2}, actual={ActualWidth:F1}x{ActualHeight:F1}, pixel={pixelWidth}x{pixelHeight}, screen={screen.Bounds}, work={work}, target={x},{y}");
        SetWindowPos(handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void RememberPanelPosition()
    {
        if (!IsVisible) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (!GetWindowRect(handle, out var rect)) return;

        _panelPositionBeforeTool = new System.Drawing.Rectangle(
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        Services.Logger.Info($"记录电量面板位置：{_panelPositionBeforeTool}");
    }

    private void PositionAtSaved(System.Drawing.Rectangle saved)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelWidth = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var pixelHeight = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var screen = Forms.Screen.FromRectangle(saved);
        var work = screen.WorkingArea;
        var x = Math.Clamp(saved.Left, work.Left + 8, Math.Max(work.Left + 8, work.Right - pixelWidth - 8));
        var y = Math.Clamp(saved.Top, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - pixelHeight - 8));

        Services.Logger.Info($"恢复电量面板位置：saved={saved}, target={x},{y}");
        SetWindowPos(handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible) return;
        Dispatcher.BeginInvoke(PositionAtAnchor, DispatcherPriority.Loaded);
    }

    private void EnsureOnScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var screen = Forms.Screen.FromRectangle(
            new System.Drawing.Rectangle(rect.Left, rect.Top, width, height));
        var work = screen.WorkingArea;
        var x = Math.Clamp(rect.Left, work.Left + 8, Math.Max(work.Left + 8, work.Right - width - 8));
        var y = Math.Clamp(rect.Top, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - height - 8));
        if (x == rect.Left && y == rect.Top) return;
        SetWindowPos(handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _hasBeenActive = true;
        Services.Logger.Info("弹窗已激活");
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        Services.Logger.Info($"弹窗失活 hasBeenActive={_hasBeenActive}");
        if (!AutoHide || _allowClose || !IsVisible || !_hasBeenActive) return;
        _lastHiddenByDeactivation = DateTimeOffset.Now;
        HidePanel();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        HidePanel();
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // The settings ListBox lives inside a scrolling glass panel. Some
        // WPF input paths consume the mouse-down before ListBoxItem sees it,
        // so select the clicked segment explicitly from the preview tunnel.
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

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        e.Handled = true;
        HidePanel();
    }

    private void DeviceCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not DeviceItemViewModel device)
        {
            return;
        }

        switch (device.Kind)
        {
            case DeviceKind.Keyboard:
                RememberPanelPosition();
                HidePanel();
                var keyboardWindow = new KeyboardTestWindow(device.Name);
                keyboardWindow.OnBack = () => ReturnToPanel(keyboardWindow);
                ShowToolWindow(keyboardWindow);
                break;

            case DeviceKind.Mouse:
                RememberPanelPosition();
                HidePanel();
                var mouseWindow = new MouseSettingsWindow(device, _mouseDpi);
                mouseWindow.OnBack = () => ReturnToPanel(mouseWindow);
                ShowToolWindow(mouseWindow);
                break;

            case DeviceKind.Headset:
                // Headsets have no supported settings/control page in this version.
                break;

            default:
                HidePanel();
                _viewModel.OpenBluetoothSettingsCommand.Execute(null);
                break;
        }
    }

    private void ReturnToPanel(Window window)
    {
        try
        {
            if (window.IsVisible) window.Close();
        }
        catch
        {
            // Ignore a window that is already closing.
        }

        _viewModel.IsSettingsOpen = false;
        ShowAtSavedPosition();
        _viewModel.RequestRefreshIfStale(TimeSpan.FromSeconds(8));
    }

    private void ShowToolWindow(Window window)
    {
        if (_toolWindow != null)
        {
            try
            {
                _toolWindow.Close();
            }
            catch
            {
                // Ignore a tool window that is already closing.
            }
        }

        _toolWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_toolWindow, window)) _toolWindow = null;
        };
        window.Show();
        window.Activate();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HidePanel();
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
}
