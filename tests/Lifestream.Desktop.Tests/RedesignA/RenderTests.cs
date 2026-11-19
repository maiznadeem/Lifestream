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
            return (Png: png, MatrixShown: matrix.IsVisible, SignIn: Hosted.BoundsOf(signIn, host.Element), Server: vm.ServerText, Layer: ThemeService.ColorOf("Color.Layer"));
        });

        Assert.Equal("Server ready", facts.Server);
        Assert.Equal(width >= 900, facts.MatrixShown); // the compatibility side folds away only in a narrow window
        Assert.True(facts.SignIn.Bottom <= height && facts.SignIn.Right <= width, $"Sign in is clipped: {facts.SignIn}");
        Assert.True(StaHost.CountPixelsNear(facts.Png, facts.Layer.R, facts.Layer.G, facts.Layer.B, 1) > 10000, "the form's layer colour of the theme is missing");
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
            return (Png: png, view.Breakpoint, view.IsRail, Attention: dashboard.Attention.Select(a => a.Title).ToList(), Today: dashboard.Today.Count, Greeting: dashboard.Greeting,
                AttentionBounds: Hosted.BoundsOf(attention, host.Element), ChartBounds: Hosted.BoundsOf(chart, host.Element), Badges: shell.Items.Where(i => i.Badge is not null).Select(i => $"{i.Title} {i.Badge}").ToList());
        });

        Assert.Equal("Good morning, Hana", facts.Greeting);
        Assert.Equal(8, facts.Today);
        Assert.Equal("O\u2212 is out of stock", facts.Attention[0]);
        Assert.Contains("6 of 8 blood groups are below 10 units", facts.Attention);
        Assert.Contains("Stock 6", facts.Badges);
        Assert.Equal(width < 1280 ? Breakpoint.Compact : width >= 1600 ? Breakpoint.Wide : Breakpoint.Standard, facts.Breakpoint);
        Assert.Equal(width < 1280, facts.IsRail);
        if (width >= 1280)
        {
            // Standard and wide: the dashboard fits the window without scrolling.
            Assert.True(facts.ChartBounds.Bottom <= height, $"the chart panel ends at {facts.ChartBounds.Bottom} in a {height} DIP window");
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
