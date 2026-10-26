using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BatteryDock.Controls;

/// <summary>
/// A grid that clips its children to a rounded rectangle. The shell border
/// draws the rounded edge; this panel keeps the ambient light field inside it.
/// </summary>
public sealed class RoundedClipPanel : Grid
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius), typeof(CornerRadius), typeof(RoundedClipPanel),
            new FrameworkPropertyMetadata(new CornerRadius(), FrameworkPropertyMetadataOptions.AffectsArrange));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        var radius = CornerRadius.TopLeft;
        Clip = new RectangleGeometry(
            new Rect(0, 0, size.Width, size.Height), radius, radius);
        return size;
    }
}
