using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace BatteryDock;

public partial class KeyboardTestWindow : Window
{
    private const double KeyUnit = 40;
    private const double KeyHeight = 50;
    private const double KeyGap = 4;

    private readonly List<KeyboardKeyViewModel> _keys = new();
    private string _lastKey = "等待按键…";

    internal Action? OnBack { get; set; }

    public KeyboardTestWindow(string deviceName)
    {
        InitializeComponent();
        ApplyThemeSurface();
        DeviceNameText.Text = string.IsNullOrWhiteSpace(deviceName)
            ? "当前键盘 · 请只操作这把键盘"
            : $"{deviceName} · 请只操作这把键盘";
        BuildKeyboard();
        UpdateStatus();
    }

    private static KeySpec K(Key key, string label, double width = 1) =>
        new(key, label, width);

    private void BuildKeyboard()
    {
        AddRow(
            K(Key.Escape, "Esc"), K(Key.F1, "F1"), K(Key.F2, "F2"), K(Key.F3, "F3"),
            K(Key.F4, "F4"), K(Key.F5, "F5"), K(Key.F6, "F6"), K(Key.F7, "F7"),
            K(Key.F8, "F8"), K(Key.F9, "F9"), K(Key.F10, "F10"), K(Key.F11, "F11"),
            K(Key.F12, "F12"), K(Key.PrintScreen, "PrtSc"), K(Key.Scroll, "ScrLk"),
            K(Key.Pause, "Pause"));

        AddRow(
            K(Key.OemTilde, "` ~"), K(Key.D1, "1"), K(Key.D2, "2"), K(Key.D3, "3"),
            K(Key.D4, "4"), K(Key.D5, "5"), K(Key.D6, "6"), K(Key.D7, "7"),
            K(Key.D8, "8"), K(Key.D9, "9"), K(Key.D0, "0"), K(Key.OemMinus, "- _"),
            K(Key.OemPlus, "= +"), K(Key.Back, "Backspace", 2), K(Key.Insert, "Insert"),
            K(Key.Home, "Home"), K(Key.PageUp, "PgUp"), K(Key.NumLock, "NumLk"),
            K(Key.Divide, "/"), K(Key.Multiply, "*"), K(Key.Subtract, "-"));

        AddRow(
            K(Key.Tab, "Tab", 1.5), K(Key.Q, "Q"), K(Key.W, "W"), K(Key.E, "E"),
            K(Key.R, "R"), K(Key.T, "T"), K(Key.Y, "Y"), K(Key.U, "U"),
            K(Key.I, "I"), K(Key.O, "O"), K(Key.P, "P"),
            K(Key.OemOpenBrackets, "[ {"), K(Key.OemCloseBrackets, "] }"),
            K(Key.OemPipe, "\\ |", 1.25), K(Key.Delete, "Delete"), K(Key.End, "End"),
            K(Key.PageDown, "PgDn"), K(Key.NumPad7, "7"), K(Key.NumPad8, "8"),
            K(Key.NumPad9, "9"), K(Key.Add, "+"));

        AddRow(
            K(Key.CapsLock, "Caps", 1.75), K(Key.A, "A"), K(Key.S, "S"), K(Key.D, "D"),
            K(Key.F, "F"), K(Key.G, "G"), K(Key.H, "H"), K(Key.J, "J"),
            K(Key.K, "K"), K(Key.L, "L"), K(Key.OemSemicolon, "; :"),
            K(Key.OemQuotes, "' \""), K(Key.Enter, "Enter", 2.25),
            K(Key.NumPad4, "4"), K(Key.NumPad5, "5"), K(Key.NumPad6, "6"));

        AddRow(
            K(Key.LeftShift, "Shift", 2.25), K(Key.Z, "Z"), K(Key.X, "X"),
            K(Key.C, "C"), K(Key.V, "V"), K(Key.B, "B"), K(Key.N, "N"),
            K(Key.M, "M"), K(Key.OemComma, ", <"), K(Key.OemPeriod, ". >"),
            K(Key.OemQuestion, "/ ?"), K(Key.RightShift, "Shift", 2.75),
            K(Key.Up, "Up"), K(Key.NumPad1, "1"), K(Key.NumPad2, "2"),
            K(Key.NumPad3, "3"), K(Key.Enter, "Enter"));

        AddRow(
            K(Key.LeftCtrl, "Ctrl", 1.25), K(Key.LWin, "Win", 1.25),
            K(Key.LeftAlt, "Alt", 1.25), K(Key.Space, "Space", 6.25),
            K(Key.RightAlt, "Alt", 1.25), K(Key.RWin, "Win", 1.25),
            K(Key.Apps, "Menu", 1.25), K(Key.RightCtrl, "Ctrl", 1.25),
            K(Key.Left, "Left"), K(Key.Down, "Down"), K(Key.Right, "Right"),
            K(Key.NumPad0, "0"), K(Key.Decimal, "."));
    }

    private void AddRow(params KeySpec[] specs)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, KeyGap),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        foreach (var spec in specs)
        {
            var key = new KeyboardKeyViewModel(spec.Key, spec.Label, spec.Width);
            _keys.Add(key);

            var label = new TextBlock
            {
                Text = spec.Label,
                FontSize = spec.Label.Length > 5 ? 10.5 : 11.5,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap
            };

            var border = new Border
            {
                Width = Math.Round(KeyUnit * spec.Width + KeyGap * (spec.Width - 1)),
                Height = KeyHeight,
                Margin = new Thickness(0, 0, KeyGap, 0),
                DataContext = key,
                Child = label
            };
            border.SetResourceReference(StyleProperty, "KeycapBorderStyle");
            row.Children.Add(border);
        }

        KeyboardRows.Children.Add(row);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = NormalizeKey(e);
        var matches = _keys.Where(item => item.Key == key).ToArray();
        if (matches.Length == 0)
        {
            _lastKey = $"未映射：{key}";
        }
        else
        {
            foreach (var item in matches)
            {
                item.IsPressed = true;
                item.IsTested = true;
            }

            _lastKey = matches[0].Label;
        }

        UpdateStatus();
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = NormalizeKey(e);
        foreach (var item in _keys.Where(item => item.Key == key))
        {
            item.IsPressed = false;
        }

        e.Handled = true;
    }

    private static Key NormalizeKey(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.System) return e.SystemKey;
        if (e.Key == Key.ImeProcessed) return e.ImeProcessedKey;
        if (e.Key == Key.DeadCharProcessed) return e.DeadCharProcessedKey;
        return e.Key;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        foreach (var key in _keys)
        {
            key.IsPressed = false;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Focus();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var key in _keys)
        {
            key.IsPressed = false;
            key.IsTested = false;
        }

        _lastKey = "等待按键…";
        UpdateStatus();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (OnBack != null) OnBack();
        else Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateStatus()
    {
        var tested = _keys.Count(key => key.IsTested);
        SummaryText.Text = $"已测试 {tested} / {_keys.Count} 个键";
        LastKeyText.Text = $"最后按键：{_lastKey}";
    }

    private void ApplyThemeSurface()
    {
        if (TryFindResource("WindowBackgroundBrush") is not SolidColorBrush brush) return;
        Background = new SolidColorBrush(Color.FromRgb(brush.Color.R, brush.Color.G, brush.Color.B));
    }

    private sealed record KeySpec(Key Key, string Label, double Width);
}

public sealed class KeyboardKeyViewModel : INotifyPropertyChanged
{
    private bool _isPressed;
    private bool _isTested;

    public KeyboardKeyViewModel(Key key, string label, double width)
    {
        Key = key;
        Label = label;
        Width = width;
    }

    public Key Key { get; }
    public string Label { get; }
    public double Width { get; }

    public bool IsPressed
    {
        get => _isPressed;
        set
        {
            if (_isPressed == value) return;
            _isPressed = value;
            OnPropertyChanged();
        }
    }

    public bool IsTested
    {
        get => _isTested;
        set
        {
            if (_isTested == value) return;
            _isTested = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
