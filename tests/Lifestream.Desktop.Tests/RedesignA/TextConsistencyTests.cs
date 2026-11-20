using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Auth;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Dialogs;
using Lifestream.Desktop.Shell;
using Lifestream.Desktop.Theme;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// The whole app reads as one typeface, drawn one way (<see cref="TextChecks"/>), in the app's own windows: the sign-in window at the size of the screenshot in
/// which 2.1.0 looked like two fonts, the main window, the dialogs, and the tool tips and context menus that open in a tree of their own. The windows are
/// the real window classes with their real styles, laid out without ever being shown. The render tests run the same check on every screen and state they
/// build (<see cref="RenderChecks"/> and their save helpers).
/// </summary>
[Trait("Category", "RenderA")]
[Collection("Wpf")]
public sealed class TextConsistencyTests
{
    /// <summary>The text settings an element hands down to everything in it, as <see cref="TextChecks"/> writes them.</summary>
    private static string Settings(DependencyObject element) => TextChecks.Inherited(element);

    /// <summary>Lays the window's content out at a size, as the window would, without showing the window: the content keeps the window as its parent, so it inherits what the window sets.</summary>
    private static FrameworkElement LayOut(Window window, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        StaHost.Settle(300);
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        return content;
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void SignInWindow_AtTheSizeOfTheOwnersScreenshot_IsOneTypefaceDrawnOneWay(string theme)
    {
        var (problems, texts, drawn) = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            DesignData.Configure(rig.Api);
            var vm = rig.Get<LoginViewModel>();
            vm.RememberUsername = true;
            var window = new LoginWindow { DataContext = vm }; // never shown
            Hosted.Await(() => vm.CheckServerAsync());
            var view = LayOut(window, 1135, 755);
            var runs = TextChecks.Runs(view);
            window.Close();
            return (TextChecks.Problems(runs), runs.Select(r => r.Text).ToList(), (Window: Settings(window), Text: runs.FirstOrDefault()?.Settings));
        });

        // The texts the owner named: the wordmark and the headings on one side, the small print on the other.
        foreach (var expected in new[] { "Lifestream", "Sign in", "Who can give to", "Can give", "Lifestream checks this for you", "No account needed.", "Server ready" })
        {
            Assert.Contains(texts, t => t.StartsWith(expected, StringComparison.Ordinal));
        }

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
        Assert.True(drawn.Text?.StartsWith(drawn.Window + "/", StringComparison.Ordinal) == true, $"the texts are drawn as {drawn.Text}, their window is set to {drawn.Window}");
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void MainWindow_WithTheDashboard_IsOneTypefaceDrawnOneWay_AndTheChartsUseTheSameFamily(string theme)
    {
        var (problems, families, chart, fellBack, count) = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher(), clock: new DesignClock());
            DesignData.Configure(rig.Api);
            rig.Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), DesignData.User));
            var shell = rig.Get<ShellViewModel>();
            var clock = new DesignClock();
            shell.Attach(null, clock);
            var window = new MainWindow { DataContext = shell }; // never shown
            Hosted.Await(() => shell.StartAsync());
            var dashboard = (DashboardViewModel)shell.CurrentPage!;
            dashboard.Attach(null, clock);
            Hosted.Await(() => dashboard.LoadAsync());
            var view = LayOut(window, 1440, 900);
            var runs = TextChecks.Runs(view);
            window.CloseWithoutPrompt();
            return (TextChecks.Problems(runs), runs.Where(r => r.Bundled).Select(r => r.Family).Distinct().ToList(), ChartFont.Typeface.FamilyName, FontSetup.ChartFellBack, runs.Count);
        });

        Assert.True(count > 40, $"only {count} texts were found in the main window");
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
        // The charts are drawn by Skia, not by WPF: their typeface is the same bundled family, not a fallback.
        Assert.False(fellBack, "the chart font fell back to a system font");
        Assert.Equal(families, [chart]);
    }

    [Fact]
    public void Dialogs_ToolTips_AndContextMenus_DrawTheirTextAsTheMainWindowDoes()
    {
        var (window, others, dialogProblems) = StaHost.Run(() =>
        {
            ThemeService.Apply(ThemeMode.Light);
            var theme = ThemeLoader.Resources;
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            var main = new MainWindow { DataContext = rig.Get<ShellViewModel>() };
            var login = new LoginWindow { DataContext = rig.Get<LoginViewModel>() };
            var confirm = new DialogWindow().ConfigureConfirm("Cancel this appointment?", "Adrian Mukherjee, 19 Nov 2026 at 10:30. The slot becomes free again.", "Cancel appointment", "Keep it");
            var form = new FormDialogWindow();

            // A tool tip and a context menu open in a tree of their own; they are styled by the theme, as in the app, and inherit nothing from a window.
            var tip = new ToolTip { Content = "Until 29 Jan 2027" };
            tip.Resources.MergedDictionaries.Add(theme);
            tip.ApplyTemplate();
            var menu = new ContextMenu();
            menu.Resources.MergedDictionaries.Add(theme);
            menu.Items.Add(new MenuItem { Header = "Copy" });
            menu.ApplyTemplate();

            var others = new Dictionary<string, string>
            {
                ["the sign-in window"] = Settings(login),
                ["the confirmation dialog"] = Settings(confirm),
                ["the form dialog"] = Settings(form),
                ["a tool tip"] = Settings(tip),
                ["a context menu"] = Settings(menu),
            };

            var dialog = LayOut(confirm, 544, 400);
            var problems = TextChecks.Problems(TextChecks.Runs(dialog));
            var settings = Settings(main);
            main.CloseWithoutPrompt();
            login.Close();
            confirm.Close();
            form.Close();
            return (settings, others, problems);
        });

        foreach (var (name, settings) in others)
        {
            Assert.True(settings == window, $"{name} draws text as {settings}, the main window as {window}");
        }

        Assert.True(dialogProblems.Count == 0, "the confirmation dialog: " + string.Join(" | ", dialogProblems));
    }
}
