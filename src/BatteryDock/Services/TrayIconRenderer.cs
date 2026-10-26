using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DrawingIcon = System.Drawing.Icon;

namespace BatteryDock.Services;

internal enum TrayIconState
{
    Empty,
    Normal,
    Low,
    Charging
}

internal sealed class TrayIconRenderer : IDisposable
{
    private readonly Dictionary<TrayIconState, DrawingIcon> _cache = new();

    public DrawingIcon GetIcon(TrayIconState state)
    {
        if (_cache.TryGetValue(state, out var existing)) return existing;
        var icon = CreateIcon(state);
        _cache[state] = icon;
        return icon;
    }

    private static DrawingIcon CreateIcon(TrayIconState state)
    {
        const int size = 128;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;

        var (start, end, fillRatio) = state switch
        {
            TrayIconState.Low => (Color.FromArgb(255, 255, 111, 112), Color.FromArgb(255, 222, 56, 82), 0.22f),
            TrayIconState.Charging => (Color.FromArgb(255, 99, 216, 155), Color.FromArgb(255, 47, 174, 121), 0.86f),
            TrayIconState.Normal => (Color.FromArgb(255, 91, 140, 255), Color.FromArgb(255, 155, 107, 255), 0.72f),
            _ => (Color.FromArgb(255, 116, 128, 151), Color.FromArgb(255, 72, 82, 101), 0.0f)
        };

        using (var background = new LinearGradientBrush(
                   new Rectangle(0, 0, size, size), start, end, LinearGradientMode.ForwardDiagonal))
        using (var path = RoundedRectangle(new Rectangle(2, 2, size - 5, size - 5), 31))
        {
            graphics.FillPath(background, path);
        }

        // A soft highlight keeps the small icon from looking flat.
        using (var highlight = new LinearGradientBrush(
                   new Rectangle(0, 0, size, size),
                   Color.FromArgb(46, 255, 255, 255),
                   Color.FromArgb(0, 255, 255, 255),
                   LinearGradientMode.Vertical))
        using (var highlightPath = RoundedRectangle(new Rectangle(2, 2, size - 5, size - 5), 31))
        {
            graphics.FillPath(highlight, highlightPath);
        }

        var white = Color.FromArgb(255, 255, 255, 255);
        using var outlinePen = new Pen(white, 6.5f) { LineJoin = LineJoin.Round };
        using (var batteryPath = RoundedRectangle(new Rectangle(22, 46, 72, 36), 9))
        {
            graphics.DrawPath(outlinePen, batteryPath);
        }

        using (var terminalPath = RoundedRectangle(new Rectangle(98, 56, 8, 16), 3))
        using (var terminalBrush = new SolidBrush(white))
        {
            graphics.FillPath(terminalBrush, terminalPath);
        }

        if (fillRatio > 0)
        {
            var innerWidth = (int)Math.Round((72 - 17) * fillRatio);
            if (innerWidth > 0)
            {
                using var fillPath = RoundedRectangle(new Rectangle(30, 54, innerWidth, 20), 5);
                using var fillBrush = new SolidBrush(white);
                graphics.FillPath(fillBrush, fillPath);
            }
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var borrowed = DrawingIcon.FromHandle(hIcon);
            return (DrawingIcon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        foreach (var icon in _cache.Values)
        {
            icon.Dispose();
        }
        _cache.Clear();
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
