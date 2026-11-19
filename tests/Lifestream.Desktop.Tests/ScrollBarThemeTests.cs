using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// Scroll bars are Windows 11 style overlays (direction A, docs/design-system.md 5.24): a 12 DIP strip over the content's edge with a transparent
/// track at rest and a 2 DIP thumb (Ink3 at 55 %), which widens to 6 DIP while the pointer is on the strip. The strip never takes layout width.
/// (The 2.0 test asserted a dark track and a 3:1 thumb; A's track is transparent and the thumb is deliberately quiet, so those facts are gone.)
/// </summary>
[Collection("Wpf")]
public sealed class ScrollBarThemeTests
{
    private sealed record Facts(
        double BarWidth,
        double ViewerWidth,
        double ViewportAtRest,
        double ViewportExpanded,
        bool TrackIsTransparent,
        double WellOpacityAtRest,
        double PillAtRest,
        double PillExpanded,
        double WellOpacityExpanded,
        double ThumbOpacityAtRest,
        double ThumbOpacityExpanded,
        Color ThumbColour,
        Color Ink3);

    private static Facts Measure()
    {
        MotionSettings.Override = true; // animations on, so the widening runs; the theme reads the setting when it loads
        ThemeLoader.Reset();
        try
        {
            var content = new StackPanel();
            for (var i = 0; i < 60; i++)
            {
                content.Children.Add(new TextBlock { Text = "Row " + i, Margin = new Thickness(0, 4, 0, 4) });
            }

            var viewer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
            using var host = Hosted.Create(Hosted.Stage(viewer), 400, 300, animations: true);
            host.Settle(250);
            var bar = host.All<ScrollBar>().First(b => b.Orientation == Orientation.Vertical && b.IsVisible);
            var thumb = host.All<Thumb>().First(t => t.IsVisible);
            var pill = (FrameworkElement)thumb.Template.FindName("Pill", thumb);
            var rest = (Border)thumb.Template.FindName("Rest", thumb);
            var well = (Border)bar.Template.FindName("Well", bar);
            host.Capture("ScrollBar_Overlay_Rest.png");

            var barWidth = bar.ActualWidth;
            var viewportAtRest = viewer.ViewportWidth;
            var trackTransparent = bar.Background is SolidColorBrush { Color.A: 0 } or null;
            var wellAtRest = well.Opacity;
            var pillAtRest = pill.ActualWidth;
            var thumbOpacityAtRest = rest.Opacity;
            var thumbColour = ((SolidColorBrush)rest.Background).Color;

            OverlayScroll.SetIsExpanded(bar, true); // what the pointer entering the strip does
            host.Settle(300); // the widening takes 120 ms
            host.Capture("ScrollBar_Overlay_Expanded.png");
            return new Facts(
                barWidth,
                viewer.ActualWidth,
                viewportAtRest,
                viewer.ViewportWidth,
                trackTransparent,
                wellAtRest,
                pillAtRest,
                pill.ActualWidth,
                well.Opacity,
                thumbOpacityAtRest,
                rest.Opacity,
                thumbColour,
                ((SolidColorBrush)host.Element.TryFindResource("Brush.Ink3")).Color);
        }
        finally
        {
            MotionSettings.Override = null;
            ThemeLoader.Reset();
        }
    }

    [Fact]
    public void ScrollBar_IsAnOverlayStrip_WithATransparentTrack_AndAThinQuietThumb_ThatWidensOnHover()
    {
        var f = StaHost.Run(Measure);

        Assert.Equal(12, f.BarWidth); // the strip is 12 DIP so a 2 DIP thumb can still be grabbed
        Assert.True(f.TrackIsTransparent, "the bar's track has a fill at rest: it must be transparent");
        Assert.Equal(0, f.WellOpacityAtRest); // no track well until the pointer is on the strip
        Assert.Equal(2, f.PillAtRest);
        Assert.Equal(f.Ink3, f.ThumbColour);
        Assert.Equal(0.55, f.ThumbOpacityAtRest, 2);

        Assert.Equal(6, f.PillExpanded);
        Assert.Equal(1, f.WellOpacityExpanded, 2);
        Assert.Equal(0.85, f.ThumbOpacityExpanded, 2);
    }

    [Fact]
    public void ScrollBar_NeverTakesLayoutWidth_NotAtRest_NotWhenExpanded()
    {
        var f = StaHost.Run(Measure);

        // The bar sits over the content: the viewport is the whole viewer, so showing, hiding or widening the bar never moves the content.
        Assert.Equal(f.ViewerWidth, f.ViewportAtRest);
        Assert.Equal(f.ViewerWidth, f.ViewportExpanded);
    }
}
