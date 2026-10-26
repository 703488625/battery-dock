using System.Drawing;
using System.Windows.Forms;

namespace BatteryDock.Services;

internal sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly TrayIconRenderer _renderer = new();
    private readonly Func<bool> _isStartupEnabled;
    private readonly Action _toggleStartup;

    private TrayIconState _state = TrayIconState.Empty;
    private string _tooltip = "电量坞 · 正在读取设备…";

    public TrayIconManager(
        Action onOpen,
        Action onRefresh,
        Func<bool> isStartupEnabled,
        Action toggleStartup,
        Action onSettings,
        Action onExit)
    {
        _isStartupEnabled = isStartupEnabled;
        _toggleStartup = toggleStartup;

        _openItem = new ToolStripMenuItem("打开电量面板") { Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
        _refreshItem = new ToolStripMenuItem("立即刷新");
        _startupItem = new ToolStripMenuItem("开机启动");
        _settingsItem = new ToolStripMenuItem("设置…");
        _exitItem = new ToolStripMenuItem("退出");

        _openItem.Click += (_, _) => onOpen();
        _refreshItem.Click += (_, _) => onRefresh();
        _startupItem.Click += (_, _) => _toggleStartup();
        _settingsItem.Click += (_, _) => onSettings();
        _exitItem.Click += (_, _) => onExit();

        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9F),
            Padding = new Padding(4)
        };
        _menu.Items.Add(_openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_refreshItem);
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);
        _menu.Opening += (_, _) => UpdateStartupCheck();

        _notifyIcon = new NotifyIcon
        {
            Icon = _renderer.GetIcon(TrayIconState.Empty),
            Text = TruncateTooltip(_tooltip),
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left) onOpen();
        };

        ApplyMenuTheme(ThemeManager.IsSystemDark());
    }

    public void Update(string tooltip, TrayIconState state)
    {
        _tooltip = string.IsNullOrWhiteSpace(tooltip) ? "电量坞" : tooltip;
        _notifyIcon.Text = TruncateTooltip(_tooltip);

        if (_state != state)
        {
            _state = state;
            _notifyIcon.Icon = _renderer.GetIcon(state);
        }
    }

    public void RefreshStartupState() => UpdateStartupCheck();

    public void SetTheme(bool isDark) => ApplyMenuTheme(isDark);

    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(5000, title, message, icon);
        }
        catch (Exception ex)
        {
            Logger.Error("显示托盘通知", ex);
        }
    }

    private void UpdateStartupCheck()
    {
        bool enabled;
        try
        {
            enabled = _isStartupEnabled();
        }
        catch
        {
            enabled = false;
        }
        _startupItem.Checked = enabled;
    }

    private void ApplyMenuTheme(bool isDark)
    {
        _menu.Renderer = new ToolStripProfessionalRenderer(new MenuColorTable(isDark));
        _menu.BackColor = isDark ? Color.FromArgb(32, 35, 43) : Color.White;
        _menu.ForeColor = isDark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(26, 26, 26);
        foreach (ToolStripItem item in _menu.Items)
        {
            item.ForeColor = _menu.ForeColor;
            if (item is ToolStripMenuItem menuItem)
            {
                menuItem.Padding = new Padding(6, 4, 6, 4);
            }
        }
    }

    private static string TruncateTooltip(string text)
    {
        if (text.Length <= 63) return text;
        return text[..62] + "…";
    }

    public void Dispose()
    {
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _renderer.Dispose();
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private sealed class MenuColorTable : ProfessionalColorTable
    {
        private readonly bool _dark;

        public MenuColorTable(bool dark) => _dark = dark;

        public override Color ToolStripDropDownBackground =>
            _dark ? Color.FromArgb(32, 35, 43) : Color.White;

        public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
        public override Color MenuBorder => _dark ? Color.FromArgb(60, 65, 78) : Color.FromArgb(225, 228, 235);
        public override Color MenuItemBorder => _dark ? Color.FromArgb(62, 70, 88) : Color.FromArgb(228, 232, 240);
        public override Color MenuItemSelected => _dark ? Color.FromArgb(48, 54, 68) : Color.FromArgb(241, 244, 249);
        public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override Color SeparatorDark => _dark ? Color.FromArgb(58, 63, 75) : Color.FromArgb(229, 232, 238);
        public override Color SeparatorLight => SeparatorDark;
        public override Color CheckBackground => _dark ? Color.FromArgb(58, 74, 112) : Color.FromArgb(225, 233, 255);
        public override Color CheckSelectedBackground => CheckBackground;
        public override Color CheckPressedBackground => CheckBackground;
    }
}
