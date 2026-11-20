using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Shell;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Shell;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// Real-app walk 11 and 6: the bell was hidden in a window at the minimum width (and with it the only way to what needs attention from any
/// screen but the dashboard), and before the list was read its accessible name described one item as if it were the whole list.
/// </summary>
public sealed partial class RealAppDefectViewTests
{
    [Theory]
    [InlineData(1024, 640)]
    [InlineData(1100, 700)]
    [InlineData(1440, 900)]
    public void Bell_IsThereAtEveryWindowWidth_ClearOfTheCaptionButtons_AndItsFlyoutOpensInsideTheWindow(int width, int height)
    {
        var f = StaHost.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            using var rig = SignedIn();
            using var shell = rig.Get<ShellViewModel>();
            Hosted.Await(() => shell.OpenByTitleAsync("Donors"));
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(view, width, height);
            host.Settle(300);
            var bell = host.Find<Button>("NotificationsButton");
            var visible = bell.IsVisible;
            var bounds = visible ? Hosted.BoundsOf(bell, view) : Rect.Empty;
            var search = host.All<Button>().Where(b => b.IsVisible && b.Name is "SearchField" or "CompactSearch").Select(b => Hosted.BoundsOf(b, view)).ToList();

            bell.Command.Execute(bell.CommandParameter);
            Hosted.PumpUntil(() => shell.Notifications.Count > 0, 5000);
            host.Settle(400);
            var flyout = host.Find<NotificationsFlyout>("Notifications");
            var surface = Hosted.Descendants(flyout).OfType<ElevatedSurface>().First();
            host.Capture($"fix212b-bell-{width}x{height}.png");
            return (visible, bounds, search, Open: surface.IsVisible, Surface: Hosted.BoundsOf(surface, view), Items: shell.Notifications.Items.Count);
        });

        Assert.True(f.visible, $"the bell is not shown at {width}x{height}"); // released 2.1.2: hidden below 1100
        Assert.True(f.bounds.Left >= 0 && f.bounds.Right <= width - (3 * 46) + 0.5, $"the bell runs under the caption buttons: {f.bounds}");
        Assert.All(f.search, s => Assert.True(s.Right <= f.bounds.Left + 0.5, $"the search button {s} overlaps the bell {f.bounds}"));
        Assert.True(f.Open && f.Items > 0, "the flyout did not open with its items");
        Assert.True(f.Surface.Left >= 0 && f.Surface.Right <= width + 0.5 && f.Surface.Bottom <= height + 0.5, $"the flyout leaves the window: {f.Surface}");
    }
}
