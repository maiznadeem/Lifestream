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
/// Every screen inside the real shell (sidebar, title bar, the page with its side panel open) at 1440 x 900, light and dark, and the command palette
/// at the three window sizes, so the whole window can be judged and not only the page area the other render tests draw. Off-screen; no window is
/// shown. Saved beside the other renders as <c>shell-{screen}-{theme}-1440x900.png</c> and <c>palette-{theme}-{width}x{height}.png</c>.
/// </summary>
[Trait("Category", "RenderA")]
[Collection("Wpf")]
public sealed class ShellRenderTests
{
    private const int Width = 1440;
    private const int Height = 900;

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

    public static TheoryData<string, string> ScreensAndThemes()
    {
        var data = new TheoryData<string, string>();
        foreach (var screen in Screens.Keys)
        {
            data.Add(screen, "light");
            data.Add(screen, "dark");
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
    public void Screen_InsideTheShell_RendersDirectionA(string screen, string theme)
    {
        var (title, viewType) = Screens[screen];
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = Clinic(screen);
            var shell = rig.Get<ShellViewModel>();
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(RenderTests.Frame(view, false, 44), Width, Height);
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
            File.WriteAllBytes(Path.Combine(RenderTests.Directory, $"shell-{screen}-{theme}-{Width}x{Height}.png"), png);

            var pageView = host.All<FrameworkElement>().FirstOrDefault(e => e.GetType() == viewType);
            var panel = host.All<SidePanel>().FirstOrDefault(p => p.IsVisible && p.ActualWidth > 0);
            var canvas = ThemeService.ColorOf("Color.Canvas");
            return (
                Page: pageView is not null && ReferenceEquals(pageView.DataContext, page),
                PageBounds: pageView is null ? Rect.Empty : Hosted.BoundsOf(pageView, host.Element),
                Panel: panel is null ? Rect.Empty : Hosted.BoundsOf(panel, host.Element),
                Selected: shell.Items.Where(i => i.IsSelected).Select(i => i.Destination.Title).ToList(),
                view.Breakpoint,
                view.IsRail,
                Canvas: StaHost.CountPixelsNear(png, canvas.R, canvas.G, canvas.B, 1),
                State: page.GetType().GetProperty("State")?.GetValue(page) as LoadState?);
        });

        Assert.True(facts.Page, $"the {title} view is not the shell's page");
        Assert.Equal(screen == "profile" ? [] : [title], facts.Selected);
        Assert.Equal(Breakpoint.Standard, facts.Breakpoint);
        Assert.False(facts.IsRail);
        Assert.True(facts.State is null or LoadState.Ready, $"the {title} screen is {facts.State}, not loaded");
        Assert.True(facts.PageBounds.Left >= 236 && facts.PageBounds.Top >= 44, $"the page starts at {facts.PageBounds.TopLeft}: over the sidebar or the title bar");
        Assert.True(facts.Panel.IsEmpty || (facts.Panel.Right <= Width + 0.5 && facts.Panel.Bottom <= Height + 0.5), $"the side panel runs out of the window: {facts.Panel}");
        Assert.True(facts.Canvas > 5000, "the theme's canvas colour is missing from the page");
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
