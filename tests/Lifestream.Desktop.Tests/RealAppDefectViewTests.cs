using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Shell;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// The defects the owner found in the released 2.1.0 app, at the level of the views: the bell's notifications flyout, the icon rail's counts, the
/// sign-in form's padding at every window width, and the focus ring, which is for the keyboard only and follows the shape of its control.
/// </summary>
[Collection("Wpf")]
public sealed partial class RealAppDefectViewTests
{
    private static string DesktopDir => Path.Combine(RepoPaths.Root, "src", "Lifestream.Desktop");

    private static Rig SignedIn(Role role = Role.Staff)
    {
        var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
        rig.SignIn(role);
        var today = DateOnly.FromDateTime(TimeProvider.System.GetLocalNow().DateTime);
        IReadOnlyList<RequestListItem> requests =
        [
            new(11, 11, "Nadia Jovanovic", BloodType.ABNeg, 30, new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), today.AddDays(2), RequestStatus.Pending),
            new(12, 12, "Omar Haddad", BloodType.OPos, 4, new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), today, RequestStatus.Pending),
        ];
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(Samples.Stock()));
        rig.Api.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([.. requests], requests.Count, 1, 50)));
        rig.Api.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(new([], 0, 1, 50)));
        rig.Api.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(Samples.Dashboard()));
        return rig;
    }

    private static KeyEventArgs KeyDown(System.Windows.Media.Visual target, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent };

    // ---- 1. The bell opens the notifications flyout ---------------------------------------------------------------------------

    [Fact]
    public void Bell_OpensAFlyoutUnderIt_WithEachItemAndItsAction_AndEscClosesIt()
    {
        var f = StaHost.Run(() =>
        {
            // As in the app, the UI thread has a dispatcher synchronization context: a load resumes (and fills its bound list) on it, never on a pool thread.
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            using var rig = SignedIn();
            using var shell = rig.Get<ShellViewModel>();
            Hosted.Await(() => shell.OpenByTitleAsync("Donors"));
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(view, 1440, 900);
            host.Settle(300);
            var bell = host.Find<Button>("NotificationsButton");
            var flyout = host.Find<NotificationsFlyout>("Notifications");
            var openBefore = flyout.IsOpen;

            bell.Command.Execute(bell.CommandParameter);
            Hosted.PumpUntil(() => shell.Notifications.Count > 0, 5000);
            host.Settle(400);

            var surface = Hosted.Descendants(flyout).OfType<ElevatedSurface>().First();
            var bellBounds = Hosted.BoundsOf(bell, view);
            var surfaceBounds = Hosted.BoundsOf(surface, view);
            // In the order the keyboard reaches them: the items' actions (TabIndex 0, top to bottom), then "View all on the dashboard".
            var buttons = Hosted.Descendants(surface).OfType<Button>().Where(b => b.IsVisible).OrderBy(b => b.TabIndex).ThenBy(b => Hosted.BoundsOf(b, view).Top)
                .Select(b => System.Windows.Automation.AutomationProperties.GetName(b)).ToList();
            var topToBottom = Hosted.Descendants(surface).OfType<Button>().Where(b => b.IsVisible).OrderBy(b => Hosted.BoundsOf(b, view).Top)
                .Select(b => System.Windows.Automation.AutomationProperties.GetName(b)).ToList();
            host.Capture("test-1-bell-flyout.png");
            var open = surface.IsVisible;
            var items = shell.Notifications.Items.Count;
            var title = shell.WindowTitle;
            var name = System.Windows.Automation.AutomationProperties.GetName(bell);

            view.RaiseEvent(KeyDown(view, Key.Escape));
            host.Settle(100);
            return (openBefore, Open: open, Items: items, TopToBottom: topToBottom, State: shell.Notifications.State, Error: shell.Notifications.ErrorDetail, bellBounds, surfaceBounds, buttons, title, name, AfterEsc: flyout.IsOpen, View: new Size(view.ActualWidth, view.ActualHeight));
        });

        Assert.False(f.openBefore);
        Assert.True(f.Open, "the bell did not open the flyout");
        Assert.True(f.Items == 4, $"the flyout shows {f.Items} items, state {f.State}, {f.Error}");
        Assert.Equal("Lifestream, Donors", f.title); // released 2.1.0: the bell opened the dashboard
        Assert.Equal("Notifications, 4 items need attention", f.name);
        Assert.Equal(["Open request", "Review bags", "Open stock", "Fulfil", "View all on the dashboard"], f.buttons);
        Assert.Equal(f.TopToBottom, f.buttons); // the keyboard goes through the flyout in the order it reads

        // Anchored to the bell: just under it, right edges together, and wholly inside the window.
        Assert.InRange(f.surfaceBounds.Top - f.bellBounds.Bottom, 0, 12);
        Assert.InRange(Math.Abs(f.surfaceBounds.Right - f.bellBounds.Right), 0, 1);
        Assert.True(f.surfaceBounds.Left >= 0 && f.surfaceBounds.Bottom <= f.View.Height, $"the flyout leaves the window: {f.surfaceBounds}");
        Assert.False(f.AfterEsc, "Esc did not close the flyout");
    }

    // ---- 3. The icon rail keeps the counts ------------------------------------------------------------------------------------

    [Fact]
    public void Rail_ShowsEveryPagesIcon_WithItsCountOnABadge_AndTheTitleAsTheTooltip()
    {
        var f = StaHost.Run(() =>
        {
            using var rig = SignedIn(Role.Admin);
            using var shell = rig.Get<ShellViewModel>();
            shell.Items.Single(i => i.Title == "Appointments").Badge = "8";
            shell.Items.Single(i => i.Title == "Requests").Badge = "40";
            var stock = shell.Items.Single(i => i.Title == "Stock");
            stock.Badge = "7";
            stock.HasAlert = true;
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(view, 1200, 800); // below 1280: the sidebar is the rail
            host.Settle(300);
            host.Capture("test-3-rail.png");

            return host.All<RadioButton>().Where(r => r.IsVisible && r.DataContext is NavItem).Select(r =>
            {
                var item = (NavItem)r.DataContext;
                var icon = Hosted.Descendants(r).OfType<Icon>().FirstOrDefault();
                var badge = Hosted.Descendants(r).OfType<Grid>().FirstOrDefault(b => b.Name == "CountBadge");
                var text = badge is null ? null : Hosted.Descendants(badge).OfType<TextBlock>().FirstOrDefault()?.Text;
                var iconInside = icon is not null && Hosted.BoundsOf(icon, r) is var bounds && bounds.Left >= 0 && bounds.Right <= r.ActualWidth + 0.5 && bounds.Width >= 16;
                return (item.Title, Width: r.ActualWidth, IconShown: icon is { IsVisible: true, Kind: not IconKind.None } && iconInside, BadgeShown: badge is { IsVisible: true }, Text: text, ToolTip: r.ToolTip as string);
            }).ToList();
        });

        Assert.Equal(8, f.Count);
        Assert.All(f, r => Assert.True(r.IconShown, $"{r.Title} shows no icon on the rail"));
        Assert.All(f, r => Assert.Equal(40, r.Width));
        Assert.All(f, r => Assert.StartsWith(r.Title, r.ToolTip, StringComparison.Ordinal));
        Assert.Equal(
            [("Appointments", "8"), ("Requests", "40"), ("Stock", "7")],
            f.Where(r => r.BadgeShown).Select(r => (r.Title, r.Text!))); // released 2.1.0: the rail showed no count at all, only a dot for Stock
    }

    // ---- 4. The sign-in form keeps its padding at every width -----------------------------------------------------------------

    [Fact]
    public void SignIn_TheFormKeepsItsPadding_AtEveryWindowWidth_FromTheMinimumToAMaximizedWideScreen()
    {
        var gaps = StaHost.Run(() =>
        {
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            var view = new LoginView { DataContext = rig.Get<LoginViewModel>() };
            using var host = Hosted.Create(view, 1240, 800);
            host.Settle(200);
            var form = host.Find<Border>("FormSide");
            var parts = new FrameworkElement[] { host.Find<TextBox>("UsernameBox"), host.Find<Button>("SignInButton"), host.Find<Button>("DemoButton"), host.Find<TextBlock>("VersionText"), host.Find<CheckBox>("RememberBox") };
            var result = new List<(int Width, double Left, double Right)>();

            // Every 7 DIP from the minimum window width to a maximized 2560 screen: the breakpoints (900, 1280) and everything between them.
            for (var width = 440; width <= 2560; width += 7)
            {
                host.Layout(width, 800);
                var pane = Hosted.BoundsOf(form, view);
                var bounds = parts.Select(p => Hosted.BoundsOf(p, view)).ToList();
                result.Add((width, bounds.Min(b => b.Left) - pane.Left, pane.Right - bounds.Max(b => b.Right)));
            }

            return result;
        });

        Assert.True(gaps.Count > 300);
        var tight = gaps.Where(g => g.Left < 40 || g.Right < 40).Select(g => $"{g.Width}: left {g.Left:0}, right {g.Right:0}").ToList();
        Assert.True(tight.Count == 0, "the form is closer than 40 DIP to the edge of its side at: " + string.Join("; ", tight));
    }

    // ---- 5. Focus rings: for the keyboard only, and in the shape of the control -----------------------------------------------

    [Theory]
    [InlineData(Key.Tab, ModifierKeys.None, true)]
    [InlineData(Key.Tab, ModifierKeys.Shift, true)]
    [InlineData(Key.Down, ModifierKeys.None, true)]
    [InlineData(Key.Space, ModifierKeys.None, true)]
    [InlineData(Key.Enter, ModifierKeys.None, true)]
    [InlineData(Key.F6, ModifierKeys.None, true)]
    [InlineData(Key.A, ModifierKeys.None, true)]
    [InlineData(Key.K, ModifierKeys.Control, true)]
    [InlineData(Key.LeftShift, ModifierKeys.Shift, false)]
    [InlineData(Key.RightCtrl, ModifierKeys.Control, false)]
    [InlineData(Key.LeftAlt, ModifierKeys.Alt, false)]
    [InlineData(Key.LWin, ModifierKeys.Windows, false)]
    [InlineData(Key.PrintScreen, ModifierKeys.None, false)]
    [InlineData(Key.CapsLock, ModifierKeys.None, false)]
    [InlineData(Key.Tab, ModifierKeys.Alt, false)] // Alt+Tab belongs to Windows
    [InlineData(Key.S, ModifierKeys.Windows | ModifierKeys.Shift, false)] // Win+Shift+S, the screenshot
    public void FocusRing_OnlyAKeyThatMovesOrActs_CountsAsUsingTheKeyboard(Key key, ModifierKeys modifiers, bool keyboardUse) =>
        Assert.Equal(keyboardUse, FocusAssist.IsKeyboardUse(key, modifiers));

    [Fact]
    public void FocusRing_ShowsForKeyboardFocus_GoesOnAMouseClick_AndNeverComesFromAModifierKey()
    {
        var f = StaHost.Run(() =>
        {
            var was = FocusAssist.LastInputWasKeyboard;
            var box = new CheckBox { Content = "Remember my username on this PC", IsChecked = true };
            using var host = Hosted.Create(Hosted.Stage(box), 320, 80);
            host.Settle(100);
            var ring = Hosted.Descendants(box).OfType<CrispBorder>().Single(b => b.Name == "FocusRing");
            try
            {
                // A click on the box (the pointer goes down, the box takes the focus): no ring.
                box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
                FocusAssist.Apply(box, hasKeyboardFocus: true);
                host.Settle(30);
                var afterClick = ring.IsVisible;

                // Alt (as in Alt+Tab) or Print Screen, then the window comes back and the box gets its focus again: still no ring (released 2.1.0: the ring).
                box.RaiseEvent(KeyDown(box, Key.LeftAlt));
                box.RaiseEvent(KeyDown(box, Key.PrintScreen));
                FocusAssist.Apply(box, hasKeyboardFocus: false);
                FocusAssist.Apply(box, hasKeyboardFocus: true);
                host.Settle(30);
                var afterModifier = ring.IsVisible;

                // Tab arrives on the box: the ring.
                box.RaiseEvent(KeyDown(box, Key.Tab));
                FocusAssist.Apply(box, hasKeyboardFocus: true);
                host.Settle(30);
                var afterTab = ring.IsVisible;
                host.Capture("test-5-checkbox-keyboard-ring.png");

                // Then a click on the box that already has the focus (so no focus event follows): the ring goes (released 2.1.0: it stayed).
                box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
                host.Settle(30);
                var afterClickOnFocused = ring.IsVisible;
                host.Capture("test-5-checkbox-after-click.png");
                return (afterClick, afterModifier, afterTab, afterClickOnFocused);
            }
            finally
            {
                FocusAssist.LastInputWasKeyboard = was;
            }
        });

        Assert.False(f.afterClick, "a mouse click showed the focus ring");
        Assert.False(f.afterModifier, "a modifier key (Alt, Print Screen) made the next focus show the ring");
        Assert.True(f.afterTab, "keyboard focus showed no ring");
        Assert.False(f.afterClickOnFocused, "the ring stayed after a mouse click on the focused control");
    }

    [Fact]
    public void FocusRing_OfTheCheckBox_FollowsTheBox_TwoPixelsWideTwoPixelsOut_NotACircle()
    {
        var f = StaHost.Run(() =>
        {
            var box = new CheckBox { Content = "Remember my username on this PC" };
            using var host = Hosted.Create(Hosted.Stage(box), 320, 80);
            host.Settle(100);
            var ring = Hosted.Descendants(box).OfType<CrispBorder>().Single(b => b.Name == "FocusRing");
            var square = Hosted.Descendants(box).OfType<CrispBorder>().Single(b => b.Name == "Box");
            return (Ring: ring.CornerRadius.TopLeft, Box: square.CornerRadius.TopLeft, Out: -ring.Margin.Left, ring.BorderThickness.Left, Side: square.Width);
        });

        Assert.Equal(18, f.Side);
        Assert.Equal(2, f.Left);
        Assert.Equal(4, f.Out); // 2 of gap and the 2 of the ring itself
        Assert.Equal(f.Box + f.Out, f.Ring); // released 2.1.0: 10 around a box of radius 4, on a 26 DIP square: a circle
    }

    [GeneratedRegex("""Property="IsKeyboardFocused"|Binding IsKeyboardFocused\b""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex KeyboardFocusedTrigger();

    [Fact]
    public void FocusRing_NoTemplateDrawsItsFocusMarkOnIsKeyboardFocused_WhichIsAlsoTrueAfterAClick()
    {
        // The one use that stays: a row of a filter chip's drop-down list takes the hover colour while the arrow keys move through it (a list highlight, not a ring).
        var allowed = new[] { Path.Combine("Features", "ListScreens", "FilterChip.xaml") };
        var scanned = new List<string>();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(DesktopDir, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var name = Path.GetRelativePath(DesktopDir, file);
            scanned.Add(name);
            if (!allowed.Contains(name) && KeyboardFocusedTrigger().IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(name);
            }
        }

        Assert.Contains(Path.Combine("Controls", "Calendar.xaml"), scanned);
        Assert.Contains(Path.Combine("Features", "Appointments", "AppointmentEditorView.xaml"), scanned);
        Assert.Matches(KeyboardFocusedTrigger(), """<Trigger Property="IsKeyboardFocused" Value="True">""");
        Assert.True(offenders.Count == 0, "these templates show a focus mark after a mouse click (use controls:FocusAssist.ShowRing): " + string.Join(", ", offenders));
    }
}
