using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;
using Lifestream.Desktop.Shell;
using Lifestream.Desktop.Theme;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// The command palette says when its results go on below what it shows (real-app walk 5: the list was cut at "Requests" with nothing to say that Stock,
/// Receipts and Staff were below): while more rows are below, the bottom of the list fades into the surface, and at the end of the list the fade is gone.
/// Up and Down keep the selected row in view and clear of the fade. Off-screen; no window is shown. Renders: <c>palette-more-below-{theme}.png</c> and
/// <c>palette-end-{theme}.png</c>.
/// </summary>
[Trait("Category", "RenderA")]
[Collection("Wpf")]
public sealed class PaletteMoreBelowTests
{
    private sealed record Band(bool Shown, double Top, double Bottom, double Left, double Right);

    /// <summary>The shell at 1440 x 900 with the palette open on an empty query (recent, actions, screens and settings: more rows than its 420 DIP hold).</summary>
    private static T WithPalette<T>(string theme, Func<Hosted, ShellViewModel, CommandPaletteView, ScrollViewer, T> act) =>
        StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            try
            {
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
                DesignData.Configure(rig.Api);
                rig.Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), DesignData.User));
                using var shell = rig.Get<ShellViewModel>();
                var view = new ShellView { DataContext = shell };
                using var host = Hosted.Create(view, 1440, 900);
                Hosted.Await(() => shell.StartAsync());
                host.Settle(100);
                shell.OpenPaletteCommand.Execute(null);
                host.Settle(200);
                var palette = (CommandPaletteView)view.FindName("Palette");
                var results = (ListBox)palette.FindName("Results");
                var scroll = (ScrollViewer)results.Template.FindName("Scroll", results);
                return act(host, shell, palette, scroll);
            }
            finally
            {
                ThemeService.Apply(ThemeMode.Light);
            }
        });

    private static FrameworkElement Fade(CommandPaletteView palette)
    {
        var results = (ListBox)palette.FindName("Results");
        return (FrameworkElement)results.Template.FindName("MoreBelow", results);
    }

    private static Band BandOf(Hosted host, CommandPaletteView palette)
    {
        var fade = Fade(palette);
        var shown = fade.Visibility == Visibility.Visible;
        var bounds = shown ? Hosted.BoundsOf(fade, host.Element) : Rect.Empty;
        return shown ? new(true, bounds.Top, bounds.Bottom, bounds.Left, bounds.Right) : new(false, 0, 0, 0, 0);
    }

    /// <summary>How far the pixels of a band of the render are from the surface's colour, summed (0: the band is the plain surface).</summary>
    private static double DistanceFromLayer(byte[] pixels, int stride, Band band, Color layer, int fromY, int toY)
    {
        var sum = 0.0;
        for (var y = fromY; y < toY; y++)
        {
            for (var x = (int)Math.Ceiling(band.Left) + 2; x < (int)Math.Floor(band.Right) - 2; x++)
            {
                var i = (y * stride) + (x * 4);
                sum += Math.Abs(pixels[i] - layer.B) + Math.Abs(pixels[i + 1] - layer.G) + Math.Abs(pixels[i + 2] - layer.R);
            }
        }

        return sum;
    }

    private static Rect RowIn(ScrollViewer scroll, ShellViewModel shell)
    {
        var results = (ListBox)scroll.TemplatedParent;
        var row = (FrameworkElement)results.ItemContainerGenerator.ContainerFromItem(shell.Palette.Items[shell.Palette.SelectedIndex]);
        return row.TransformToAncestor(scroll).TransformBounds(new Rect(row.RenderSize));
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void ResultsTallerThanThePalette_FadeAtTheBottomWhileMoreAreBelow_AndNotAtTheEnd(string theme)
    {
        var facts = WithPalette(theme, (host, shell, palette, scroll) =>
        {
            var layer = (Color)palette.FindResource("Color.Layer");
            var opened = (Items: shell.Palette.Items.Count, More: CommandPaletteView.HasMoreBelow(scroll), Offset: scroll.VerticalOffset);
            var viewportBottom = Hosted.BoundsOf(scroll, host.Element).Top + scroll.Padding.Top + scroll.ViewportHeight;
            var band = BandOf(host, palette);
            System.IO.Directory.CreateDirectory(RenderTests.Directory);
            File.WriteAllBytes(Path.Combine(RenderTests.Directory, $"palette-more-below-{theme}.png"), host.Capture());
            var width = (int)Math.Ceiling(host.Element.ActualWidth);
            var faded = host.Pixels();

            // The same rows without the fade: the band then shows them as they are.
            var fade = Fade(palette);
            fade.Visibility = Visibility.Collapsed;
            host.Settle(20);
            var plain = host.Pixels();
            fade.Visibility = Visibility.Visible;
            host.Settle(20);
            var (top, bottom) = ((int)Math.Ceiling(band.Top), (int)Math.Floor(band.Bottom));
            var dimming = (Faded: DistanceFromLayer(faded, width * 4, band, layer, top, bottom), Plain: DistanceFromLayer(plain, width * 4, band, layer, top, bottom));
            var lastLine = DistanceFromLayer(faded, width * 4, band, layer, bottom - 1, bottom) / Math.Max(1, band.Right - band.Left - 4);

            // Up from the first row goes to the last: the list scrolls to its end, and the fade goes.
            shell.Palette.Move(-1);
            host.Settle(100);
            File.WriteAllBytes(Path.Combine(RenderTests.Directory, $"palette-end-{theme}.png"), host.Capture());
            var end = (More: CommandPaletteView.HasMoreBelow(scroll), Band: BandOf(host, palette), Row: RowIn(scroll, shell), scroll.Padding.Top, scroll.ViewportHeight, scroll.VerticalOffset);
            return (opened, band, viewportBottom, dimming, lastLine, end);
        });

        Assert.True(facts.opened.More, $"set-up: the {facts.opened.Items} rows of an empty query should not fit the palette");
        Assert.Equal(0, facts.opened.Offset);
        Assert.True(facts.band.Shown, "nothing shows that the results go on below"); // released 2.1.6: the list was cut with no sign of more
        Assert.Equal(CommandPaletteView.FadeHeight, facts.band.Bottom - facts.band.Top, 1);
        Assert.Equal(facts.viewportBottom, facts.band.Bottom, 1); // over the last visible rows, ending where they are cut off
        Assert.True(facts.dimming.Faded < 0.6 * facts.dimming.Plain, $"the band does not fade the rows under it: {facts.dimming.Faded:N0} faded against {facts.dimming.Plain:N0} plain");
        Assert.True(facts.lastLine < 6, $"the fade does not reach the surface's colour at the bottom edge (mean {facts.lastLine:N1} off per pixel)");

        Assert.False(facts.end.More, "set-up: Up from the first row should reach the end of the list");
        Assert.True(facts.end.VerticalOffset > 0);
        Assert.False(facts.end.Band.Shown, "the fade stays at the end of the list, where nothing more is below");
        Assert.True(facts.end.Row.Top >= facts.end.Top - 0.5 && facts.end.Row.Bottom <= facts.end.Top + facts.end.ViewportHeight + 0.5, $"the last row {facts.end.Row} is not in view");
    }

    [Fact]
    public void DownPastTheLastVisibleRow_ScrollsTheSelectedRowIntoView_ClearOfTheFade_AndBackToTheTopAfterTheLast()
    {
        var facts = WithPalette("light", (host, shell, palette, scroll) =>
        {
            var query = (TextBox)palette.FindName("QueryBox");
            var source = PresentationSource.FromVisual(query)!;
            void Press(Key key)
            {
                // A routed key event raised on the query field, as WPF raises it; no input is sent to Windows and nothing takes focus.
                query.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                host.Settle(20);
            }

            var problems = new List<string>();
            var firstScroll = -1;
            var count = shell.Palette.Items.Count;
            for (var step = 1; step < count; step++)
            {
                Press(Key.Down);
                var row = RowIn(scroll, shell);
                var clear = scroll.Padding.Top + scroll.ViewportHeight - (CommandPaletteView.HasMoreBelow(scroll) ? CommandPaletteView.FadeHeight : 0);
                if (row.Top < scroll.Padding.Top - 0.5 || row.Bottom > clear + 0.5)
                {
                    problems.Add($"row {shell.Palette.SelectedIndex} ({shell.Palette.Items[shell.Palette.SelectedIndex].Title}) at {row.Top:N1}-{row.Bottom:N1}, clear area {scroll.Padding.Top:N1}-{clear:N1}, offset {scroll.VerticalOffset:N1}");
                }

                if (firstScroll < 0 && scroll.VerticalOffset > 0)
                {
                    firstScroll = step;
                }
            }

            var last = (Index: shell.Palette.SelectedIndex, More: CommandPaletteView.HasMoreBelow(scroll));
            Press(Key.Down); // wraps to the first row
            var wrapped = (Index: shell.Palette.SelectedIndex, scroll.VerticalOffset);
            return (count, problems, firstScroll, last, wrapped);
        });

        Assert.True(facts.firstScroll > 1, $"the list scrolled at step {facts.firstScroll}: set-up needs rows below the first view"); // the first rows were in view already
        Assert.True(facts.problems.Count == 0, string.Join(Environment.NewLine, facts.problems));
        Assert.Equal(facts.count - 1, facts.last.Index);
        Assert.False(facts.last.More); // the last row reached the end
        Assert.Equal(0, facts.wrapped.Index);
        Assert.Equal(0, facts.wrapped.VerticalOffset); // back at the top, with the first group's heading
    }
}
