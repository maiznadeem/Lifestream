using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// The in-progress mark of a step of the sign-in window's wait card (2.1.9): the busy spinner's arc, in the info colour. It turns while it
/// is visible and Windows animation effects are on, and stands still otherwise, like the busy spinner of a button.
/// </summary>
public sealed class WakeSpinner : UserControl
{
    private readonly RotateTransform _turn = new();

    public WakeSpinner()
    {
        Focusable = false;
        IsTabStop = false;
        var arc = new Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 8,1 A 7,7 0 1 1 1,8"),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _turn,
        };
        arc.SetResourceReference(Shape.StrokeProperty, "Brush.Info");
        Content = arc;
        IsVisibleChanged += (_, _) => Update();
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => _turn.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void Update()
    {
        if (IsLoaded && IsVisible && MotionSettings.AnimationsEnabled)
        {
            _turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, MotionSettings.SpinnerTurn) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            _turn.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }
}
