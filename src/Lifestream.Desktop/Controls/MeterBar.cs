using System.Windows;
using System.Windows.Media;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// A horizontal meter (direction A's stock meter): an 8 DIP track with a 1 DIP inner stroke, the filled share, an optional hatched share drawn over
/// the start of the fill (units expiring soon) and a minimum tick that reaches 4 DIP above and below the track. Fractions are 0 to 1 of the width.
/// Edges are snapped to device pixels. It draws no text; the count beside it is ordinary text.
/// </summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0);
    public static readonly DependencyProperty HatchedProperty = Register(nameof(Hatched), 0.0);
    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), -1.0);
    public static readonly DependencyProperty TrackProperty = RegisterBrush(nameof(Track));
    public static readonly DependencyProperty TrackStrokeProperty = RegisterBrush(nameof(TrackStroke));
    public static readonly DependencyProperty FillProperty = RegisterBrush(nameof(Fill));
    public static readonly DependencyProperty HatchProperty = RegisterBrush(nameof(Hatch));
    public static readonly DependencyProperty TickProperty = RegisterBrush(nameof(Tick));

    private const double TrackHeight = 8;
    private const double TickReach = 4;
    private const double TickWidth = 1.5;

    static MeterBar() => SnapsToDevicePixelsProperty.OverrideMetadata(typeof(MeterBar), new FrameworkPropertyMetadata(true));

    /// <summary>The filled share, 0 to 1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>The hatched share at the start of the fill, 0 to 1 (never more than <see cref="Value"/>).</summary>
    public double Hatched { get => (double)GetValue(HatchedProperty); set => SetValue(HatchedProperty, value); }

    /// <summary>Where the minimum tick sits, 0 to 1; negative hides it.</summary>
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public Brush? TrackStroke { get => (Brush?)GetValue(TrackStrokeProperty); set => SetValue(TrackStrokeProperty, value); }

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public Brush? Hatch { get => (Brush?)GetValue(HatchProperty); set => SetValue(HatchProperty, value); }

    public Brush? Tick { get => (Brush?)GetValue(TickProperty); set => SetValue(TickProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, TrackHeight + (2 * TickReach));

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        double Snap(double x) => Math.Round(x * dpi.DpiScaleX) / dpi.DpiScaleX;
        var top = Snap((ActualHeight - TrackHeight) / 2);
        var radius = TrackHeight / 2;
        var track = new Rect(0, top, width, TrackHeight);
        drawingContext.DrawRoundedRectangle(Track, TrackStroke is null ? null : new Pen(TrackStroke, 1), Inset(track), radius, radius);

        var fill = Snap(Math.Clamp(Value, 0, 1) * width);
        if (fill > 0 && Fill is not null)
        {
            drawingContext.DrawRoundedRectangle(Fill, null, new Rect(0, top, Math.Max(fill, TrackHeight), TrackHeight), radius, radius);
        }

        var hatched = Snap(Math.Clamp(Math.Min(Hatched, Value), 0, 1) * width);
        if (hatched > 0 && Hatch is not null)
        {
            drawingContext.DrawRoundedRectangle(Hatch, null, new Rect(0, top, Math.Max(hatched, TrackHeight), TrackHeight), radius, radius);
        }

        if (Minimum >= 0 && Tick is not null)
        {
            var x = Snap(Math.Clamp(Minimum, 0, 1) * width);
            drawingContext.DrawRectangle(Tick, null, new Rect(x - (TickWidth / 2), top - TickReach, TickWidth, TrackHeight + (2 * TickReach)));
        }
    }

    /// <summary>A rectangle inset by half a pixel, so a 1 DIP stroke lands inside it.</summary>
    private static Rect Inset(Rect r) => new(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1));

    private static DependencyProperty Register(string name, double value) =>
        DependencyProperty.Register(name, typeof(double), typeof(MeterBar), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty RegisterBrush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(MeterBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
}
