using System.Windows;
using System.Windows.Media;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// A raised surface (cards, panes, popups) with a soft shadow. The shadow is drawn in the card's own visual as a few stacked, fading rounded
/// rectangles instead of a DropShadowEffect: an effect would render the whole card, text included, through an off-screen bitmap and cost the
/// text its ClearType. The shadow colour is the Color.Shadow token.
/// </summary>
public class Card : CrispBorder
{
    private const int Layers = 8;

    public static readonly DependencyProperty ElevationProperty =
        DependencyProperty.Register(nameof(Elevation), typeof(double), typeof(Card), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush[] ShadowBrushes = CreateBrushes();

    /// <summary>How far the shadow spreads: 1 for cards, 2 for popups and menus.</summary>
    public double Elevation
    {
        get => (double)GetValue(ElevationProperty);
        set => SetValue(ElevationProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var size = RenderSize;
        if (size.Width > 0 && size.Height > 0 && Elevation > 0)
        {
            var radius = CornerRadius.TopLeft;
            var drop = 2 * Elevation;
            for (var i = Layers; i >= 1; i--)
            {
                var spread = i * Elevation;
                var rect = new Rect(-spread, -spread + drop, size.Width + (2 * spread), size.Height + (2 * spread));
                dc.DrawRoundedRectangle(ShadowBrushes[i - 1], null, rect, radius + spread, radius + spread);
            }
        }

        base.OnRender(dc);
    }

    private static Brush[] CreateBrushes()
    {
        var shadow = Application.Current?.TryFindResource("Color.Shadow") is Color c ? c : Colors.Black;
        var brushes = new Brush[Layers];
        for (var i = 0; i < Layers; i++)
        {
            // Each layer adds a little darkness; together they fade from about 30 % at the edge to nothing at the outer ring.
            var brush = new SolidColorBrush(shadow) { Opacity = 0.063 * (1 - ((double)i / Layers)) };
            brush.Freeze();
            brushes[i] = brush;
        }

        return brushes;
    }
}
