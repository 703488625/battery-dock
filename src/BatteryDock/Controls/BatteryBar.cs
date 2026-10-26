using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace BatteryDock.Controls;

public sealed class BatteryBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BatteryBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(BatteryBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(BatteryBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var radius = height / 2;
        var track = TrackBrush ?? Brushes.Transparent;
        drawingContext.DrawRoundedRectangle(track, null, new Rect(0, 0, width, height), radius, radius);

        var ratio = Math.Clamp(Value / 100d, 0d, 1d);
        if (ratio <= 0) return;

        var fillWidth = Math.Max(height, width * ratio);
        fillWidth = Math.Min(width, fillWidth);
        var fillBrush = FillBrush ?? Brushes.White;
        var fillRadius = Math.Min(radius, fillWidth / 2);
        drawingContext.DrawRoundedRectangle(
            fillBrush, null, new Rect(0, 0, fillWidth, height), fillRadius, fillRadius);
    }
}
