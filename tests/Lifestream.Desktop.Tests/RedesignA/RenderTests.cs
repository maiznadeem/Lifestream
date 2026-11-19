using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lifestream.Client.Auth;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Chrome;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Shell;
using Lifestream.Desktop.Theme;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// Renders of direction A's sign-in and dashboard at 1440 x 900, maximized on a 1920 x 1080 screen and in a 1024 x 700 window, light and dark, from the
/// design dataset through the real view models and views (off-screen; no window is shown). Each render is saved for the side-by-side comparison with
/// the target images and checked for the facts a broken layout would lose: no clipped content, the breakpoint, the theme's canvas colour.
/// </summary>
[Trait("Category", "RenderA")]
public sealed class RenderTests
{
    /// <summary>Where the renders go: LIFESTREAM_CAPTURE_DIR, else the run's phase 1 captures folder, else the test output.</summary>
    public static string Directory { get; } = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } env
        ? env
        : System.IO.Directory.Exists(@"G:\Claude\workspace\lifestream\run") ? @"G:\Claude\workspace\lifestream\run\captures\redesign-a\phase1" : Hosted.CaptureDirectory;

    public static TheoryData<string, int, int> Sizes => new()
    {
        { "light", 1440, 900 },
        { "dark", 1440, 900 },
        { "light", 1920, 1080 },
        { "dark", 1920, 1080 },
        { "light", 1024, 700 },
        { "dark", 1024, 700 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void SignIn_RendersDirectionA(string theme, int width, int height)
    {
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            DesignData.Configure(rig.Api);
            var vm = rig.Get<LoginViewModel>();
            vm.Username = "hana.greenleaf";
            vm.Password = TestSecrets.Pw.PadRight(10, 'x')[..10];
            vm.RememberUsername = true;
            var view = new LoginView { DataContext = vm };
            using var host = Hosted.Create(Frame(view, height == 1080, width < 1280 ? 40 : 44), width, height);
            Hosted.Await(() => vm.CheckServerAsync());
            host.Settle(600);
            var png = Save(host, $"signin-{theme}-{width}x{height}.png");
            var matrix = host.Find<FrameworkElement>("Matrix");
            var signIn = host.Find<Button>("SignInButton");
            var hero = host.Find<FrameworkElement>("Hero");
            var block = host.Find<FrameworkElement>("HeroBlock");
            var formSide = host.Find<FrameworkElement>("FormSide");
            var heroTexts = host.All<TextBlock>().Where(t => t.IsVisible && hero.IsAncestorOf(t)).ToList();
            // A unit square of each text, carried to the view: rotated (the "Donor" label) it is still 1 x 1; scaled it is not.
            var scaled = heroTexts.Where(t => t.TransformToAncestor(view).TransformBounds(new Rect(0, 0, 1, 1)) is var unit && Math.Abs((unit.Width * unit.Height) - 1) > 0.01).Select(t => t.Text).ToList();
            var legend = host.Find<StackPanel>("HeroLegend");
            var cell = (FrameworkElement)VisualTreeHelper.GetParent(host.All<System.Windows.Shapes.Ellipse>().First(e => e.Name == "Ring"));
            var blockBounds = matrix.IsVisible ? Hosted.BoundsOf(block, view) : Rect.Empty;
            return (Png: png, MatrixShown: matrix.IsVisible, SignIn: Hosted.BoundsOf(signIn, host.Element), Server: vm.ServerText, Layer: ThemeService.ColorOf("Color.Layer"),
                Viewboxes: host.All<Viewbox>().Count(v => hero.IsAncestorOf(v)), Scaled: scaled, SmallestText: heroTexts.Count == 0 ? 0 : heroTexts.Min(t => t.FontSize), Cell: (cell.ActualWidth, cell.ActualHeight),
                Block: blockBounds, FormRight: Hosted.BoundsOf(formSide, view).Right, FormWidth: formSide.ActualWidth,
                Legend: legend.IsVisible ? legend.Children.OfType<TextBlock>().Select(t => t.Text).ToList() : [], LegendDots: legend.Children.OfType<System.Windows.Shapes.Ellipse>().Select(e => e.Opacity).ToList(),
                Remember: (string)host.Find<CheckBox>("RememberBox").Content);
        });

        Assert.Equal("Server ready", facts.Server);
        Assert.Equal(width >= 900, facts.MatrixShown); // the compatibility side folds away only in a narrow window
        Assert.True(facts.SignIn.Bottom <= height && facts.SignIn.Right <= width, $"Sign in is clipped: {facts.SignIn}");
        Assert.True(StaHost.CountPixelsNear(facts.Png, facts.Layer.R, facts.Layer.G, facts.Layer.B, 1) > 10000, "the form's layer colour of the theme is missing");
        Assert.Equal("Remember my username on this PC", facts.Remember);

        // The compatibility side is never scaled (design review 22): no Viewbox, every text at its own size of 12 DIP or more, the matrix cells at 40 DIP.
        // In a compact window the form side narrows to 440 so the matrix keeps its size.
        Assert.Equal(0, facts.Viewboxes);
        Assert.Empty(facts.Scaled);
        Assert.True(facts.SmallestText >= 12, $"a text of the compatibility side is {facts.SmallestText} DIP");
        Assert.Equal((40.0, 40.0), facts.Cell);
        Assert.Equal(width < 1280 ? 440 : 560, facts.FormWidth);

        // It is centred in the room right of the form (23) and wholly inside the window.
        var leftGap = facts.Block.Left - facts.FormRight;
        var rightGap = width - facts.Block.Right;
        Assert.True(Math.Abs(leftGap - rightGap) <= 1.5, $"the compatibility block has {leftGap} on its left and {rightGap} on its right");
        Assert.True(leftGap >= 24 && facts.Block.Top >= 0 && facts.Block.Bottom <= height, $"the compatibility block is cut off: {facts.Block}");

        // The two reds are explained (41): the muted dot and the full dot, each with its words.
        Assert.Equal(["Can give", "Universal: O\u2212 gives to every group, AB+ receives from every group"], facts.Legend);
        Assert.Equal(2, facts.LegendDots.Count);
        Assert.True(facts.LegendDots[0] < facts.LegendDots[1], "the legend's first dot is the muted red, the second the full red");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Dashboard_RendersDirectionA(string theme, int width, int height)
    {
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            DesignData.Configure(rig.Api);
            rig.Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), DesignData.User));
            var shell = rig.Get<ShellViewModel>();
            var clock = new DesignClock();
            shell.Attach(null, clock);
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(Frame(view, height == 1080, width < 1280 ? 40 : 44), width, height);
            Hosted.Await(() => shell.StartAsync());
            var dashboard = (DashboardViewModel)shell.CurrentPage!;
            dashboard.Attach(null, clock);
            Hosted.Await(() => dashboard.LoadAsync());
            Hosted.Await(() => shell.RefreshCountsAsync());
            host.Settle(1500);
            var png = Save(host, $"dashboard-{theme}-{width}x{height}.png");
            var attention = host.Find<FrameworkElement>("AttentionPanel");
            var chart = host.Find<FrameworkElement>("DonationsFrame");
            var layout = DashboardLayout.Read(host, dashboard);
            return (Png: png, view.Breakpoint, view.IsRail, Attention: dashboard.Attention.Select(a => a.Title).ToList(), Today: dashboard.Today.Count, Greeting: dashboard.Greeting,
                AttentionBounds: Hosted.BoundsOf(attention, host.Element), ChartBounds: Hosted.BoundsOf(chart, host.Element), Badges: shell.Items.Where(i => i.Badge is not null).Select(i => $"{i.Title} {i.Badge}").ToList(),
                Layout: layout);
        });

        Assert.Equal("Good morning, Hana", facts.Greeting);
        Assert.Equal(8, facts.Today);
        Assert.Equal("O\u2212 is out of stock", facts.Attention[0]);
        Assert.Contains("6 of 8 blood groups are below 10 units", facts.Attention);
        Assert.Contains("Stock 6", facts.Badges);
        Assert.Equal(width < 1280 ? Breakpoint.Compact : width >= 1600 ? Breakpoint.Wide : Breakpoint.Standard, facts.Breakpoint);
        Assert.Equal(width < 1280, facts.IsRail);
        var l = facts.Layout;

        // "Needs attention" (design review 10): the header counts the rows that are on screen, and offers the rest when some are not.
        Assert.Equal(l.RowsOnScreen, l.RowsInModel);
        Assert.Equal(l.RowsOnScreen == 5 ? "5 items" : $"{l.RowsOnScreen} of 5", l.CountText);
        Assert.Equal(l.RowsOnScreen < 5, l.ToggleShown);
        Assert.Equal(l.RowsOnScreen < 5 ? "View all 5" : string.Empty, l.ToggleText);
        Assert.Empty(l.CutDescriptions); // a description wraps to two lines; none of the demo clinic's is cut
        Assert.True(l.TallestDescription <= 36.5, $"a description is {l.TallestDescription} tall (two lines are 36)");

        if (width >= 1280)
        {
            // Standard and wide: the dashboard fits the window without scrolling.
            Assert.True(facts.ChartBounds.Bottom <= height, $"the chart panel ends at {facts.ChartBounds.Bottom} in a {height} DIP window");
            Assert.Equal(0, l.PageScrollable);
            Assert.Equal(5, l.RowsOnScreen); // 900 and 1080 high both have room for every row
            Assert.Equal(0, l.TodayScrollable); // eight appointments fit beside it

            // No dead bands (24, and the bottom row filling the height): the top row is as tall as its content, the meters share the stock panel's height.
            Assert.True(l.AttentionGap <= 12, $"{l.AttentionGap} DIP of empty panel under the last Needs attention row");
            Assert.True(l.StockGap <= 32, $"{l.StockGap} DIP of empty panel under the last stock meter");
            Assert.True(Math.Abs(l.StockBottom - l.ChartBottom) <= 0.5 && Math.Abs(l.ChartBottom - l.PageBottom) <= 0.5, $"the bottom row ends at {l.StockBottom} / {l.ChartBottom}, the page at {l.PageBottom}");
            Assert.True(l.MeterWidth <= 520, $"the stock meters are {l.MeterWidth} DIP long"); // in a wide window the chart takes the larger share
        }
        else
        {
            // Compact (11): one column at natural heights. Only the page scrolls, and its scroll bar sits beside the panels, not over them.
            Assert.Equal(5, l.RowsOnScreen);
            Assert.Equal(0, l.TodayScrollable);
            Assert.NotEqual(Visibility.Visible, l.TodayBar);
            Assert.True(l.PageScrollable > 0, "a compact dashboard is taller than the window: the page scrolls");
            Assert.True(l.PageBarLeft >= l.PanelsRight, $"the page's scroll bar starts at {l.PageBarLeft}, the panels end at {l.PanelsRight}");
            Assert.True(l.TodayRows == 8 && l.TodayListHeight >= 8 * 34, $"the appointments list shows {l.TodayRows} rows in {l.TodayListHeight} DIP");
        }
    }

    [Fact]
    public void Dashboard_NeedsAttention_InAShortWindow_ShowsTheMostUrgentRows_SaysHowMany_AndViewAllShowsTheRest()
    {
        var f = StaHost.Run(() =>
        {
            ThemeService.Apply(ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            DesignData.Configure(rig.Api);
            rig.Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), DesignData.User));
            var shell = rig.Get<ShellViewModel>();
            var clock = new DesignClock();
            shell.Attach(null, clock);
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(Frame(view, false, 44), 1366, 728); // a maximized window on a 1366 x 768 laptop: no room for five rows above the stock panel
            Hosted.Await(() => shell.StartAsync());
            var dashboard = (DashboardViewModel)shell.CurrentPage!;
            dashboard.Attach(null, clock);
            Hosted.Await(() => dashboard.LoadAsync());
            host.Settle(800);
            var folded = DashboardLayout.Read(host, dashboard);
            var foldedTitles = dashboard.VisibleAttention.Select(a => a.Title).ToList();
            var toggle = host.Find<Button>("AttentionToggle");
            toggle.Command.Execute(null);
            host.Settle(300);
            var all = DashboardLayout.Read(host, dashboard);
            toggle.Command.Execute(null);
            host.Settle(300);
            var again = DashboardLayout.Read(host, dashboard);
            return (folded, all, again, foldedTitles, Titles: dashboard.Attention.Select(a => a.Title).ToList());
        });

        Assert.InRange(f.folded.RowsOnScreen, 2, 4);
        Assert.Equal(f.folded.RowsOnScreen, f.folded.RowsInModel);
        Assert.Equal($"{f.folded.RowsOnScreen} of 5", f.folded.CountText);
        Assert.True(f.folded.ToggleShown);
        Assert.Equal("View all 5", f.folded.ToggleText);
        Assert.Equal(f.Titles.Take(f.folded.RowsOnScreen), f.foldedTitles); // the most urgent rows are the ones kept
        Assert.Equal(0, f.folded.PageScrollable); // folded, the dashboard still fits the window

        Assert.Equal(5, f.all.RowsOnScreen);
        Assert.Equal("5 items", f.all.CountText);
        Assert.True(f.all.ToggleShown);
        Assert.Equal("Show fewer", f.all.ToggleText);
        Assert.Empty(f.all.CutDescriptions);

        Assert.Equal(f.folded.RowsOnScreen, f.again.RowsOnScreen);
        Assert.Equal(f.folded.CountText, f.again.CountText);
    }

    /// <summary>What the dashboard's layout shows, read from the rendered view.</summary>
    private sealed record DashboardLayout(
        int RowsOnScreen, int RowsInModel, string CountText, bool ToggleShown, string ToggleText, List<string> CutDescriptions, double TallestDescription,
        double PageScrollable, double TodayScrollable, Visibility TodayBar, int TodayRows, double TodayListHeight, double AttentionGap, double StockGap,
        double StockBottom, double ChartBottom, double PageBottom, double MeterWidth, double PageBarLeft, double PanelsRight)
    {
        public static DashboardLayout Read(Hosted host, DashboardViewModel dashboard)
        {
            var root = host.Element;
            var attention = host.Find<FrameworkElement>("AttentionPanel");
            var list = host.Find<ItemsControl>("AttentionList");
            var rows = Enumerable.Range(0, list.Items.Count).Select(i => (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(i)).Where(c => c is { IsVisible: true }).ToList();
            var details = rows.SelectMany(r => Hosted.Descendants(r).OfType<TextBlock>().Where(t => t.Name == "Detail")).ToList();
            var cut = new List<string>();
            foreach (var detail in details)
            {
                // The same text with all the height it wants: taller than what the row shows means the row cut it.
                var whole = new TextBlock { Text = detail.Text, Style = detail.Style, TextWrapping = TextWrapping.Wrap, FontFamily = detail.FontFamily, FontSize = detail.FontSize };
                whole.Measure(new Size(detail.ActualWidth, double.PositiveInfinity));
                if (whole.DesiredSize.Height > detail.ActualHeight + 0.5)
                {
                    cut.Add(detail.Text);
                }
            }

            var toggle = host.Find<Button>("AttentionToggle");
            var pageScroll = host.Find<ScrollViewer>("PageScroll");
            var todayScroll = host.Find<ScrollViewer>("TodayScroll");
            var todayList = host.Find<ItemsControl>("TodayList");
            var stock = host.Find<FrameworkElement>("StockPanel");
            var stockList = host.Find<ItemsControl>("StockList");
            var chart = host.Find<FrameworkElement>("DonationsFrame");
            var lastMeter = (FrameworkElement)stockList.ItemContainerGenerator.ContainerFromIndex(stockList.Items.Count - 1);
            var meter = Hosted.Descendants(stockList).OfType<MeterBar>().First();
            var pageBar = (FrameworkElement)pageScroll.Template.FindName("PART_VerticalScrollBar", pageScroll);
            var attentionBounds = Hosted.BoundsOf(attention, root);
            var stockBounds = Hosted.BoundsOf(stock, root);
            var pageBounds = Hosted.BoundsOf(pageScroll, root);
            return new DashboardLayout(
                rows.Count,
                dashboard.VisibleAttention.Count,
                dashboard.AttentionCountText,
                toggle.IsVisible,
                toggle.IsVisible ? (string)toggle.Content : string.Empty,
                cut,
                details.Count == 0 ? 0 : details.Max(d => d.ActualHeight),
                pageScroll.ScrollableHeight,
                todayScroll.ScrollableHeight,
                todayScroll.ComputedVerticalScrollBarVisibility,
                todayList.Items.Count,
                todayList.ActualHeight,
                rows.Count == 0 ? 0 : attentionBounds.Bottom - Hosted.BoundsOf(rows[^1], root).Bottom,
                stockBounds.Bottom - Hosted.BoundsOf(lastMeter, root).Bottom,
                stockBounds.Bottom,
                Hosted.BoundsOf(chart, root).Bottom,
                pageBounds.Top + pageScroll.ViewportHeight,
                meter.ActualWidth,
                Hosted.BoundsOf(pageBar, root).Left,
                Math.Max(attentionBounds.Right, stockBounds.Right));
        }
    }

    /// <summary>
    /// The window around a view, as on screen: the view fills the frame (it draws the title bar itself) and the window's three caption buttons sit over its
    /// top-right corner, with the restore glyph when maximized. (The real buttons are part of ChromeWindow's template, which cannot be hosted off-screen.)
    /// </summary>
    public static FrameworkElement Frame(FrameworkElement view, bool maximized, double titleHeight)
    {
        // The text defaults every ChromeWindow gives its content (Chrome/Chrome.xaml): Onest 14, ink, whole-pixel layout.
        var grid = new Grid { UseLayoutRounding = true, SnapsToDevicePixels = true };
        grid.SetResourceReference(TextElement.FontFamilyProperty, "Font.Family");
        grid.SetResourceReference(TextElement.FontSizeProperty, "FontSize.Body");
        grid.SetResourceReference(TextElement.ForegroundProperty, "Brush.Ink");
        TextOptions.SetTextFormattingMode(grid, TextFormattingMode.Display);
        grid.Children.Add(view);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        foreach (var glyph in new[] { "\uE921", maximized ? "\uE923" : "\uE922", "\uE8BB" })
        {
            var text = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Ink");
            var cell = new Border { Width = ChromeWindow.CaptionButtonWidth, Height = titleHeight, Child = text };
            buttons.Children.Add(cell);
        }

        grid.Children.Add(buttons);
        return grid;
    }

    private static byte[] Save(Hosted host, string name)
    {
        var png = host.Capture();
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path.Combine(Directory, name), png);
        return png;
    }
}
