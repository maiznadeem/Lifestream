using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// A border whose edge is always a whole number of device pixels, at every display scale. WPF draws a 1 DIP stroke as 1.25, 1.5 or 1.75
/// pixels at 125, 150 and 175 % scaling, which shows as one lit column plus a half-lit one: a soft, blurry edge. This border instead fills the
/// ring between two rounded rectangles whose edges are snapped to device pixels (guidelines), with the thickness rounded to whole pixels (at
/// least one), and fills the inside with the background; straight edges are crisp and the corners are one clean anti-aliased arc.
/// Used by every bordered control template and by <see cref="Card"/>. A non-uniform thickness or corner radius falls back to the WPF border.
/// </summary>
public class CrispBorder : Border
{
    static CrispBorder()
    {
        UseLayoutRoundingProperty.OverrideMetadata(typeof(CrispBorder), new FrameworkPropertyMetadata(true));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(CrispBorder), new FrameworkPropertyMetadata(true));
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var thickness = BorderThickness;
        var radius = CornerRadius;
        if (!IsUniform(thickness) || !IsUniform(radius))
        {
            base.OnRender(dc);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var edgeX = thickness.Left > 0 ? Math.Max(1, Math.Round(thickness.Left * dpi.DpiScaleX)) / dpi.DpiScaleX : 0;
        var edgeY = thickness.Left > 0 ? Math.Max(1, Math.Round(thickness.Left * dpi.DpiScaleY)) / dpi.DpiScaleY : 0;
        var outer = new Rect(0, 0, width, height);
        var inner = new Rect(edgeX, edgeY, Math.Max(0, width - (2 * edgeX)), Math.Max(0, height - (2 * edgeY)));
        var outerRadius = radius.TopLeft;
        var innerRadius = Math.Max(0, outerRadius - edgeX);

        var guidelines = new GuidelineSet([0, edgeX, width - edgeX, width], [0, edgeY, height - edgeY, height]);
        guidelines.Freeze();
        dc.PushGuidelineSet(guidelines);
        if (BorderBrush is { } stroke && edgeX > 0)
        {
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(outer, outerRadius, outerRadius), new RectangleGeometry(inner, innerRadius, innerRadius));
            ring.Freeze();
            dc.DrawGeometry(stroke, null, ring);
        }

        if (Background is { } fill)
        {
            var area = edgeX > 0 ? inner : outer;
            var areaRadius = edgeX > 0 ? innerRadius : outerRadius;
            dc.DrawRoundedRectangle(fill, null, area, areaRadius, areaRadius);
        }

        dc.Pop();
    }

    private static bool IsUniform(Thickness t) => t.Left == t.Top && t.Left == t.Right && t.Left == t.Bottom;

    private static bool IsUniform(CornerRadius r) => r.TopLeft == r.TopRight && r.TopLeft == r.BottomRight && r.TopLeft == r.BottomLeft;
}
