using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// One outline icon of the direction A set (Controls/IconData.cs, from tools/icons/icons-a.svg): 24 x 24 geometry drawn at <see cref="Size"/>
/// with a 1.5/24 round stroke in <see cref="Foreground"/>, which inherits the text colour of its container. <see cref="IsFilled"/> fills the
/// icon's "duo" area at 14 % of the colour (the selected sidebar item). Decorative: it is hidden from UI Automation, so the button or text next
/// to it carries the name.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(IconKind), typeof(Icon), new FrameworkPropertyMetadata(IconKind.None, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(Icon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFilledProperty =
        DependencyProperty.Register(nameof(IsFilled), typeof(bool), typeof(Icon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty =
        DependencyProperty.Register(nameof(StrokeWidth), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly ConcurrentDictionary<IconKind, (Geometry Stroke, Geometry? Duo, bool Dots)> Cache = new();

    static Icon()
    {
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(true));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(Icon), new UIPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
    }

    public IconKind Kind { get => (IconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>Edge length in DIP: 16 inside buttons, 18 in lists and the sidebar, 20 for page-level marks.</summary>
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public bool IsFilled { get => (bool)GetValue(IsFilledProperty); set => SetValue(IsFilledProperty, value); }

    /// <summary>Stroke width in the icon's 24-unit grid (1.5 by default).</summary>
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    /// <summary>The frozen 24 x 24 geometry of an icon (for tests and for code that draws icons itself).</summary>
    public static Geometry GeometryOf(IconKind kind) => Load(kind).Stroke;

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (Kind == IconKind.None || Foreground is not { } brush)
        {
            return;
        }

        var (stroke, duo, dots) = Load(Kind);
        var scale = Size / 24.0;
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        if (IsFilled && duo is not null)
        {
            var fill = brush.CloneCurrentValue();
            fill.Opacity = 0.14;
            fill.Freeze();
            drawingContext.DrawGeometry(fill, null, duo);
        }

        var pen = new Pen(brush, dots ? 3 : StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, stroke);
        drawingContext.Pop();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new IconAutomationPeer(this);

    private static (Geometry Stroke, Geometry? Duo, bool Dots) Load(IconKind kind) => Cache.GetOrAdd(kind, static k =>
    {
        if (!IconData.All.TryGetValue(k, out var data))
        {
            return (Geometry.Empty, null, false);
        }

        var stroke = Geometry.Parse(data.Stroke);
        stroke.Freeze();
        Geometry? duo = null;
        if (data.Duo.Length > 0)
        {
            duo = Geometry.Parse(data.Duo);
            duo.Freeze();
        }

        return (stroke, duo, data.Dots);
    });

    /// <summary>Icons are decoration: not in the control or content view, so screen readers skip them.</summary>
    private sealed class IconAutomationPeer(Icon owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override string GetClassNameCore() => nameof(Icon);
    }
}
