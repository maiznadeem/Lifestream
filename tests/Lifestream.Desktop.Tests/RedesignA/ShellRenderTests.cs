using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Common;
using Lifestream.Client.Shell;
using Lifestream.Contracts.Auth;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Appointments;
using Lifestream.Desktop.Features.Donors;
using Lifestream.Desktop.Features.Patients;
using Lifestream.Desktop.Features.Profile;
using Lifestream.Desktop.Features.Receipts;
using Lifestream.Desktop.Features.Requests;
using Lifestream.Desktop.Features.Staff;
using Lifestream.Desktop.Features.Stock;
using Lifestream.Desktop.Shell;
using Lifestream.Desktop.Tests.Features;
using Lifestream.Desktop.Theme;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// Every screen inside the real shell (sidebar or rail, title bar, the page with its side panel open) at the three window sizes (1440 x 900, maximized on
/// 1920 x 1080, 1024 x 700), light and dark, and the command palette at the same sizes, so the whole window can be judged and not only the page area the
/// other render tests draw. Each screen must sit in the shell the same way: the page starts where the shell's padding ends (no padding of its own on top),
/// and its side panel reaches the window's top (under the title bar), right and bottom edges. Off-screen; no window is shown. Saved beside the other
/// renders as <c>shell-{screen}-{theme}-{width}x{height}.png</c> and <c>palette-{theme}-{width}x{height}.png</c>.
/// </summary>
[Trait("Category", "RenderA")]
[Collection("Wpf")]
public sealed class ShellRenderTests
{
    private static readonly Dictionary<string, (string Title, Type View)> Screens = new()
    {
        ["donors"] = ("Donors", typeof(DonorsView)),
        ["patients"] = ("Patients", typeof(PatientsView)),
        ["appointments"] = ("Appointments", typeof(AppointmentsView)),
        ["requests"] = ("Requests", typeof(RequestsView)),
        ["stock"] = ("Stock", typeof(StockView)),
        ["receipts"] = ("Receipts", typeof(ReceiptsView)),
        ["staff"] = ("Staff", typeof(StaffView)),
        ["profile"] = ("Profile", typeof(ProfileView)),
    };

    public static TheoryData<string, string, int, int> ScreensAndThemes()
    {
        var data = new TheoryData<string, string, int, int>();
        foreach (var screen in Screens.Keys)
        {
            foreach (var (width, height) in new[] { (1440, 900), (1920, 1080), (1024, 700) })
            {
                data.Add(screen, "light", width, height);
                data.Add(screen, "dark", width, height);
            }
        }

        return data;
    }

    /// <summary>
    /// The demo clinic on 19 November 2026, Hana Greenleaf signed in: the shell's counts and the dashboard from the design dataset, and for each screen
    /// the same data its page-area render test uses (the design lists for Donors, Patients, Appointments and Requests; the part 2 demo data for the rest).
    /// </summary>
    private static Lifestream.Client.Tests.Fakes.Rig Clinic(string screen)
    {
        var listScreen = screen is "donors" or "patients" or "appointments" or "requests";
        var rig = listScreen ? new Lifestream.Client.Tests.Fakes.Rig(dispatcher: WpfTestUi.Dispatcher(), clock: new DesignClock()) : Part2Harness.NewRig(clock: new DesignClock());
        RecordPicker.InlineResults = true; // never open a popup window on the desktop
        DesignData.Configure(rig.Api);
        if (!listScreen)
        {
            Phase2bRenderTests.Dress(rig, source: DesignData.Stock()); // the clinic's stock with its age bands, a group's bags, and the receipt tab counts
        }

        switch (screen)
        {
            case "donors":
                ListScreensDesignData.ConfigureDonors(rig.Api);
                break;
            case "patients":
                PatientDesignData.Configure(rig.Api);
                break;
            case "appointments":
                AppointmentDesignData.Configure(rig.Api);
                break;
            case "requests":
                RequestDesignData.Configure(rig.Api);
                break;
        }

        rig.Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), DesignData.User));
        return rig;
    }

    [Theory]
    [MemberData(nameof(ScreensAndThemes))]
    public void Screen_InsideTheShell_RendersDirectionA(string screen, string theme, int width, int height)
    {
        var (title, viewType) = Screens[screen];
        var compact = width < 1280;
        var titleBar = compact ? 40 : 44;
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = Clinic(screen);
            var shell = rig.Get<ShellViewModel>();
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(RenderTests.Frame(view, height == 1080, titleBar), width, height);
            Hosted.Await(() => shell.StartAsync());
            host.Settle(300);
            if (screen == "profile")
            {
                Hosted.Await(() => shell.ProfileCommand.ExecuteAsync());
            }
            else
            {
                Hosted.Await(() => shell.OpenAsync(shell.Items.Single(i => i.Destination.Title == title).Destination));
            }

            host.Settle(500);
            var page = shell.CurrentPage!;
            SelectFirst(page);
            host.Settle(700);
            var png = host.Capture();
            System.IO.Directory.CreateDirectory(RenderTests.Directory);
            File.WriteAllBytes(Path.Combine(RenderTests.Directory, $"shell-{screen}-{theme}-{width}x{height}.png"), png);
            RenderChecks.Check(host, $"shell-{screen}-{theme}-{width}x{height}");

            var pageView = host.All<FrameworkElement>().FirstOrDefault(e => e.GetType() == viewType);
            var panel = host.All<SidePanel>().FirstOrDefault(p => p.IsVisible && p.ActualWidth > 0);
            var pageHost = host.Find<ContentControl>("PageHost");
            var header = host.All<PageHeader>().FirstOrDefault(h => h.IsVisible && pageView is not null && pageView.IsAncestorOf(h));
            var canvas = ThemeService.ColorOf("Color.Canvas");
            return (
                Page: pageView is not null && ReferenceEquals(pageView.DataContext, page),
                PageBounds: pageView is null ? Rect.Empty : Hosted.BoundsOf(pageView, host.Element),
                Panel: panel is null ? Rect.Empty : Hosted.BoundsOf(panel, host.Element),
                Host: Hosted.BoundsOf(pageHost, host.Element),
                HostPad: pageHost.Margin,
                Header: header is null ? Rect.Empty : Hosted.BoundsOf(header, host.Element),
                Selected: shell.Items.Where(i => i.IsSelected).Select(i => i.Destination.Title).ToList(),
                view.Breakpoint,
                view.IsRail,
                Canvas: StaHost.CountPixelsNear(png, canvas.R, canvas.G, canvas.B, 1),
                State: page.GetType().GetProperty("State")?.GetValue(page) as LoadState?);
        });

        Assert.True(facts.Page, $"the {title} view is not the shell's page");
        Assert.Equal(screen == "profile" ? [] : [title], facts.Selected);
        Assert.Equal(width >= 1920 ? Breakpoint.Wide : compact ? Breakpoint.Compact : Breakpoint.Standard, facts.Breakpoint);
        Assert.Equal(compact, facts.IsRail);
        Assert.True(facts.State is null or LoadState.Ready, $"the {title} screen is {facts.State}, not loaded");
        Assert.True(facts.PageBounds.Left >= (compact ? 48 : 236) && facts.PageBounds.Top >= titleBar, $"the page starts at {facts.PageBounds.TopLeft}: over the sidebar or the title bar");
        Assert.True(facts.Canvas > 5000, "the theme's canvas colour is missing from the page");

        // Every screen sits in the shell the same way. The page fills the shell's page host (which pads it), and its header starts at the page's own top-left:
        // a screen that pads itself as well would start lower and further right than the others.
        Assert.True(Math.Abs(facts.PageBounds.Left - facts.Host.Left) <= 0.5 && Math.Abs(facts.PageBounds.Top - facts.Host.Top) <= 0.5, $"the page {facts.PageBounds} does not fill the shell's page host {facts.Host}");
        Assert.False(facts.Header.IsEmpty, "the page shows no page header");
        Assert.True(Math.Abs(facts.Header.Top - facts.PageBounds.Top) <= 0.5, $"the page header starts at y {facts.Header.Top}, the page at {facts.PageBounds.Top}: the screen adds its own top padding");
        if (screen != "profile")
        {
            // (Profile's content is a centred column of at most 960, so its header's left edge moves with the window.)
            Assert.True(Math.Abs(facts.Header.Left - facts.PageBounds.Left) <= 0.5, $"the page header starts at x {facts.Header.Left}, the page at {facts.PageBounds.Left}: the screen adds its own left padding");

            // The side panel bleeds through the shell's padding: from under the title bar to the bottom edge, flush with the window's right edge.
            Assert.False(facts.Panel.IsEmpty, "the selected record's side panel did not open");
            Assert.True(Math.Abs(facts.Panel.Right - width) <= 0.5, $"the side panel ends at x {facts.Panel.Right}, the window at {width}");
            Assert.True(Math.Abs(facts.Panel.Bottom - height) <= 0.5, $"the side panel ends at y {facts.Panel.Bottom}, the window at {height}");
            var pageRowTop = facts.Host.Top - facts.HostPad.Top; // where the shell's page area starts, under the title bar
            Assert.True(Math.Abs(facts.Panel.Top - pageRowTop) <= 0.5, $"the side panel starts at y {facts.Panel.Top}, the page area at {pageRowTop}");
        }
    }

    [Theory]
    [MemberData(nameof(RenderTests.Sizes), MemberType = typeof(RenderTests))]
    public void CommandPalette_OverTheDashboard_RendersDirectionA(string theme, int width, int height)
    {
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = Clinic("dashboard");
            var shell = rig.Get<ShellViewModel>();
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(RenderTests.Frame(view, height == 1080, width < 1280 ? 40 : 44), width, height);
            Hosted.Await(() => shell.StartAsync());
            host.Settle(600);
            shell.OpenPaletteCommand.Execute(null);
            host.Settle(500);
            var png = host.Capture();
            System.IO.Directory.CreateDirectory(RenderTests.Directory);
            File.WriteAllBytes(Path.Combine(RenderTests.Directory, $"palette-{theme}-{width}x{height}.png"), png);
            var palette = (CommandPaletteView)view.FindName("Palette");
            var surface = (FrameworkElement)palette.FindName("Surface");
            var rows = host.All<ListBoxItem>().Where(i => i.IsVisible && palette.IsAncestorOf(i)).Select(i => Hosted.BoundsOf(i, host.Element)).ToList();
            return (palette.IsOpen, Items: shell.Palette.Items.Count, Rows: rows, Surface: Hosted.BoundsOf(surface, host.Element));
        });

        Assert.True(facts.IsOpen);
        Assert.True(facts.Items > 0 && facts.Rows.Count > 0, "the palette lists nothing");
        // The palette itself is inside the window (its list scrolls inside it), centred, and its first row is inside the palette.
        Assert.True(facts.Surface.Left >= 0 && facts.Surface.Right <= width + 0.5 && facts.Surface.Top >= 0 && facts.Surface.Bottom <= height + 0.5, $"the palette is outside the window: {facts.Surface}");
        Assert.True(Math.Abs((facts.Surface.Left + facts.Surface.Right) - width) <= 1.5, $"the palette is not centred: {facts.Surface}");
        Assert.True(facts.Rows[0].Top >= facts.Surface.Top && facts.Rows[0].Bottom <= facts.Surface.Bottom && facts.Rows[0].Left >= facts.Surface.Left && facts.Rows[0].Right <= facts.Surface.Right, $"the first row {facts.Rows[0]} is outside the palette {facts.Surface}");
    }

    /// <summary>Selects the first row of a list screen and waits for its side panel (the screens without a list are left as they open).</summary>
    private static void SelectFirst(object page)
    {
        var type = page.GetType();
        if (page is Lifestream.Client.Features.Stock.StockViewModel stock)
        {
            // Stock has groups, not a paged list: open the group the page-area renders open (A−, below its minimum).
            stock.SelectedTile = stock.Tiles.FirstOrDefault(t => t.BloodType == Lifestream.Domain.BloodType.ANeg);
            Hosted.Await(() => stock.PendingBags);
            return;
        }

        if (type.GetProperty("Items")?.GetValue(page) is not IList { Count: > 0 } items || type.GetProperty("SelectedItem") is not { CanWrite: true } selected)
        {
            return;
        }

        Hosted.Await(() =>
        {
            selected.SetValue(page, items[0]);
            return type.GetProperty("PendingDetail", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(page) as Task ?? Task.CompletedTask;
        });
    }
}
