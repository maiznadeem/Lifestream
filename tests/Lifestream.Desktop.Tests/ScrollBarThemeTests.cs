using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Theme;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// Scroll bars are Windows 11 style overlays (direction A, docs/design-system.md 5.24): a 12 DIP strip over the content's edge with a transparent
/// track at rest and a 2 DIP thumb (Ink3 at 55 %), which widens to 6 DIP while the pointer is on the strip. The strip never takes layout width.
/// (The 2.0 test asserted a dark track and a 3:1 thumb; A's track is transparent and the resting thumb is deliberately quiet, so those facts are gone.)
/// The thumb is quiet only at rest: while the pointer is on the strip, the thumb is dragged, or keyboard focus is inside the viewer, it reaches 3:1 against
/// every surface it can sit on, in both themes, with Windows animations on or off.
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

    /// <summary>The opaque surfaces a scrolling area lies on: the page, panels and dialogs, table headers and group rows, hovered rows, selected rows.</summary>
    private static readonly string[] Surfaces = ["Canvas", "Layer", "Layer2", "Hover", "Selected"];

    public static TheoryData<string, bool, string> ThumbsInUse()
    {
        var data = new TheoryData<string, bool, string>();
        foreach (var theme in new[] { "light", "dark" })
        {
            foreach (var animations in new[] { true, false })
            {
                foreach (var state in new[] { "pointer", "drag", "keyboard" })
                {
                    data.Add(theme, animations, state);
                }
            }
        }

        return data;
    }

    private sealed record InUse(
        List<(Color Colour, double Opacity)> ThumbAtRest,
        List<(Color Colour, double Opacity)> Thumb,
        Color Well,
        double WellOpacity,
        bool Emphasized,
        bool EmphasizedAtRest,
        double BarOpacity,
        bool EmphasizedAfterAMousePress,
        Dictionary<string, Color> Surfaces);

    /// <summary>The thumb's colour layers that are on show, bottom to top, each with the opacity it is drawn at (its own times its brush's).</summary>
    private static List<(Color Colour, double Opacity)> Layers(Thumb thumb) =>
    [
        .. new[] { "Rest", "Strong", "Drag" }
            .Select(name => (Border)thumb.Template.FindName(name, thumb))
            .Where(layer => layer.Visibility == Visibility.Visible && layer.Opacity > 0)
            .Select(layer =>
            {
                var colour = ((SolidColorBrush)layer.Background).Color;
                return (colour, layer.Opacity * colour.A / 255.0);
            }),
    ];

    private static InUse MeasureInUse(string theme, bool animations, string state)
    {
        MotionSettings.Override = animations; // the theme reads the setting when it loads
        ThemeLoader.Reset();
        ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
        try
        {
            var first = new Button { Content = "First row" };
            var content = new StackPanel();
            content.Children.Add(first);
            for (var i = 0; i < 60; i++)
            {
                content.Children.Add(new TextBlock { Text = "Row " + i, Margin = new Thickness(0, 4, 0, 4) });
            }

            var viewer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
            using var host = Hosted.Create(Hosted.Stage(viewer), 400, 300, animations: animations);
            host.Settle(250);
            var bar = host.All<ScrollBar>().First(b => b.Orientation == Orientation.Vertical && b.IsVisible);
            var thumb = host.All<Thumb>().First(t => t.IsVisible && bar.IsAncestorOf(t));
            var well = (Border)bar.Template.FindName("Well", bar);
            var atRest = Layers(thumb);
            var emphasizedAtRest = OverlayScroll.GetIsEmphasized(bar);

            switch (state)
            {
                case "pointer": // the pointer enters the bar's strip
                    bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                    break;
                case "drag": // the thumb starts a drag (as it does without the pointer having been seen to enter, on a touch screen)
                    thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
                    break;
                default: // a key is pressed while focus is inside the viewer
                    first.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(first)!, Environment.TickCount, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    break;
            }

            host.Settle(350); // the widening and the well take 120 ms when animations are on
            host.Capture($"ScrollBar_InUse_{state}_{theme}_{(animations ? "animated" : "still")}.png");
            var thumbInUse = Layers(thumb);
            var wellColour = ((SolidColorBrush)well.Background).Color;
            var wellOpacity = well.Opacity * wellColour.A / 255.0;
            var emphasized = OverlayScroll.GetIsEmphasized(bar);
            var barOpacity = bar.Opacity;
            var surfaces = Surfaces.ToDictionary(s => s, s => ThemeService.ColorOf("Color." + s));

            // A mouse press inside the viewer ends the keyboard's hold (the pointer's and a drag's end when the pointer leaves or the drag completes).
            first.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            host.Settle(50);
            return new InUse(atRest, thumbInUse, wellColour, wellOpacity, emphasized, emphasizedAtRest, barOpacity, OverlayScroll.GetIsEmphasized(bar), surfaces);
        }
        finally
        {
            MotionSettings.Override = null;
            ThemeLoader.Reset();
            ThemeService.Apply(ThemeMode.Light);
        }
    }

    private static (double R, double G, double B) Over(Color top, double opacity, (double R, double G, double B) under) =>
        ((top.R * opacity) + (under.R * (1 - opacity)), (top.G * opacity) + (under.G * (1 - opacity)), (top.B * opacity) + (under.B * (1 - opacity)));

    private static double Luminance((double R, double G, double B) c)
    {
        static double Lin(double v)
        {
            v /= 255;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Lin(c.R)) + (0.7152 * Lin(c.G)) + (0.0722 * Lin(c.B));
    }

    private static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [MemberData(nameof(ThumbsInUse))]
    public void Thumb_InUse_Reaches3To1_AgainstEverySurface_WithOrWithoutAnimations(string theme, bool animations, string state)
    {
        var f = StaHost.Run(() => MeasureInUse(theme, animations, state));

        Assert.False(f.EmphasizedAtRest); // quiet until it is used
        Assert.True(f.Emphasized, $"the thumb is not emphasised while in use ({state})");
        Assert.Equal(1, f.BarOpacity, 2); // shown, not faded out
        Assert.NotEmpty(f.Thumb);
        Assert.Equal(state != "keyboard", f.EmphasizedAfterAMousePress); // a mouse press ends the keyboard's hold; the pointer or the drag still holds theirs

        foreach (var (name, surface) in f.Surfaces)
        {
            // What lies behind the thumb: the surface, under the track's well when the well is on show (pointer on the strip, animations on).
            var plain = ((double)surface.R, (double)surface.G, (double)surface.B);
            var behind = Over(f.Well, f.WellOpacity, plain);
            var thumb = f.Thumb.Aggregate(behind, (under, layer) => Over(layer.Colour, layer.Opacity, under));
            var againstWhatItLiesOn = Contrast(thumb, behind);
            var againstTheSurface = Contrast(thumb, plain);
            Assert.True(againstWhatItLiesOn >= 3, $"{theme}, {state}, animations {(animations ? "on" : "off")}: the thumb is {againstWhatItLiesOn:0.00}:1 against what it lies on over {name}; 3:1 is the least");
            Assert.True(againstTheSurface >= 3, $"{theme}, {state}, animations {(animations ? "on" : "off")}: the thumb is {againstTheSurface:0.00}:1 against {name}; 3:1 is the least");
        }
    }
}
