using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Lifestream.Desktop.Tests;

/// <summary>Scroll bars use the dark theme, not the default light Windows style.</summary>
[Collection("Wpf")]
public sealed class ScrollBarThemeTests
{
    private static double Luminance(byte r, byte g, byte b)
    {
        static double Lin(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return (0.2126 * Lin(r / 255.0)) + (0.7152 * Lin(g / 255.0)) + (0.0722 * Lin(b / 255.0));
    }

    private static double Ratio(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    [Fact]
    public void ScrollBar_TrackAndThumb_AreDark_AndThumbStandsOutFromTheTrackBy3To1()
    {
        var (track, thumb, thickness) = StaHost.Run(() =>
        {
            var content = new StackPanel();
            for (var i = 0; i < 60; i++)
            {
                content.Children.Add(new TextBlock { Text = "Row " + i, Margin = new Thickness(0, 4, 0, 4) });
            }

            var viewer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
            using var host = Hosted.Create(Hosted.Stage(viewer), 400, 300);
            host.Settle(250);
            var bar = host.All<ScrollBar>().First(b => b.Orientation == Orientation.Vertical && b.IsVisible);
            var thumbElement = host.All<Thumb>().First(t => t.IsVisible);
            host.Capture("ScrollBar_Dark.png");
            var pixels = host.Pixels();
            var width = (int)Math.Ceiling(host.Element.ActualWidth);

            (byte R, byte G, byte B) At(Rect bounds, double fx, double fy)
            {
                var x = (int)(bounds.X + (bounds.Width * fx));
                var y = (int)(bounds.Y + (bounds.Height * fy));
                var i = ((y * width) + x) * 4;
                return (pixels[i + 2], pixels[i + 1], pixels[i]);
            }

            var barBounds = Hosted.BoundsOf(bar, host.Element);
            var thumbBounds = Hosted.BoundsOf(thumbElement, host.Element);
            // The track below the thumb (the thumb sits at the top of a long list).
            var trackPixel = At(barBounds, 0.5, 0.95);
            var thumbPixel = At(thumbBounds, 0.5, 0.5);
            return (trackPixel, thumbPixel, bar.ActualWidth);
        });

        var trackLuminance = Luminance(track.R, track.G, track.B);
        var thumbLuminance = Luminance(thumb.R, thumb.G, thumb.B);
        Assert.True(trackLuminance < 0.05, $"the scroll bar track is not dark: rgb({track.R},{track.G},{track.B})");
        Assert.True(Ratio(thumbLuminance, trackLuminance) >= 3, $"the thumb rgb({thumb.R},{thumb.G},{thumb.B}) has under 3:1 contrast with the track rgb({track.R},{track.G},{track.B})");
        Assert.InRange(thickness, 8, 16); // thin
    }
}
