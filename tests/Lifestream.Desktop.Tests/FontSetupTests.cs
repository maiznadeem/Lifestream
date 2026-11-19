using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Tests;
using Lifestream.Desktop.Tests.Features;
using Lifestream.Desktop.Theme;
using SkiaSharp;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-01: when the bundled font cannot be loaded at startup the app does not die: it falls back to the system UI font and logs a warning (an injected failing loader).</summary>
[Collection("Wpf")]
public sealed class FontSetupTests
{
    private static void WithLoaders(Func<SKTypeface> chart, Func<bool> app, Action<List<string>> body)
    {
        var warnings = new List<string>();
        var (previousChart, previousApp, previousWarn, previousApply) = (FontSetup.ChartTypefaceLoader, FontSetup.AppFontAvailable, FontSetup.Warn, FontSetup.ApplyTypeface);
        FontSetup.ApplyTypeface = _ => { }; // the failure tests never touch the process-wide LiveCharts settings
        FontSetup.ChartTypefaceLoader = chart;
        FontSetup.AppFontAvailable = app;
        FontSetup.Warn = warnings.Add;
        try
        {
            ThemeLoader.Reset();
            body(warnings);
        }
        finally
        {
            FontSetup.ChartTypefaceLoader = previousChart;
            FontSetup.AppFontAvailable = previousApp;
            FontSetup.Warn = previousWarn;
            FontSetup.ApplyTypeface = previousApply;
            ThemeLoader.Reset();
            FontSetup.ApplyChartFont(); // put the real Inter back for every test that follows
        }
    }

    [Fact]
    public void WhenTheChartFontCannotBeLoaded_ThemeLoadingStillWorks_ChartsUseTheDefaultTypeface_AndAWarningIsLogged()
    {
        var (resources, warnings, chartFellBack, typeface) = StaHost.Run(() =>
        {
            ResourceDictionary? loaded = null;
            List<string>? logged = null;
            bool fell = false;
            string? face = null;
            WithLoaders(() => throw new IOException("Cannot locate resource 'fonts/intervariable.ttf'."), () => true, warnings =>
            {
                loaded = ThemeLoader.Resources; // used to throw here, before any window existed
                logged = warnings;
                fell = FontSetup.ChartFellBack;
                face = FontSetup.ChartTypeface?.FamilyName;
            });
            return (loaded, logged, fell, face);
        });

        Assert.NotNull(resources);
        Assert.True(chartFellBack);
        Assert.Contains(warnings!, w => w.Contains("chart font could not be loaded", StringComparison.Ordinal) && w.Contains("IOException", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(typeface)); // the fallback is a real typeface, never null
        Assert.DoesNotContain("Inter", typeface, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheAppFontCannotBeResolved_TextFallsBackToTheSystemUiFont_AndAWarningIsLogged()
    {
        var (family, warnings, fell) = StaHost.Run(() =>
        {
            FontFamily? resolved = null;
            List<string>? logged = null;
            var appFellBack = false;
            WithLoaders(() => SKTypeface.Default, () => false, warnings =>
            {
                resolved = (FontFamily)ThemeLoader.Resources["Font.Family"];
                logged = warnings;
                appFellBack = FontSetup.AppFellBack;
            });
            return (resolved, logged, appFellBack);
        });

        Assert.True(fell);
        Assert.Equal(SystemFonts.MessageFontFamily.Source, family!.Source);
        Assert.Contains(warnings!, w => w.Contains("system UI font", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheFontCheckItselfThrows_ItIsTreatedAsMissing_NotAsACrash()
    {
        var fell = StaHost.Run(() =>
        {
            var result = false;
            WithLoaders(() => SKTypeface.Default, () => throw new IOException("disk"), log =>
            {
                _ = ThemeLoader.Resources;
                result = FontSetup.AppFellBack;
            });
            return result;
        });

        Assert.True(fell);
    }

    [Fact]
    public void ABrokenFallbackToo_LeavesLiveChartsItsOwnDefault_WithoutThrowing()
    {
        var typeface = StaHost.Run(() =>
        {
            SKTypeface? seen = null;
            WithLoaders(() => throw new InvalidOperationException("no font"), () => true, log => seen = FontSetup.ChartTypeface);
            return seen is not null;
        });

        Assert.True(typeface); // Skia's default typeface exists on this machine; the point is that nothing threw
    }

    [Fact]
    public void AfterAChartFontLoadFailure_TheDashboardStillRendersItsCharts_WithTheFallbackFont_FakeApi()
    {
        var (errors, fellBack, family, distinctPixels, restored) = StaHost.Run(() =>
        {
            var errors = new List<string>();
            Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
            {
                errors.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}");
                e.Handled = true;
            };
            var previous = FontSetup.ChartTypefaceLoader;
            try
            {
                FontSetup.ChartTypefaceLoader = () => throw new IOException("Cannot locate resource 'fonts/intervariable.ttf'.");
                FontSetup.ApplyChartFont(); // for real: LiveCharts now has the fallback typeface
                using var rig = FeatureHarness.NewRig();
                var vm = FeatureHarness.Create<DashboardViewModel>(rig);
                var view = FeatureHarness.DashboardView(vm);
                using var host = Hosted.Create(view, 1366, 736);
                Hosted.Await(() => vm.LoadAsync());
                host.Settle(600);
                return (errors, FontSetup.ChartFellBack, FontSetup.ChartTypeface?.FamilyName, host.Pixels().Distinct().Count(), false);
            }
            finally
            {
                FontSetup.ChartTypefaceLoader = previous;
                FontSetup.ApplyChartFont(); // every later test gets the bundled Inter again
            }
        });

        Assert.Empty(errors); // no Tokenize NullReferenceException: the charts drew their text
        Assert.True(fellBack);
        Assert.False(string.IsNullOrEmpty(family));
        Assert.True(distinctPixels > 20, "the dashboard rendered nothing");
        Assert.False(restored);
        Assert.False(FontSetup.ChartFellBack); // the real loader is back
    }

    [Fact]
    public void TheBundledFonts_LoadNormally_WithNoFallback()
    {
        var (chart, app) = StaHost.Run(() =>
        {
            ThemeLoader.Reset();
            _ = ThemeLoader.Resources;
            return (FontSetup.ChartFellBack, FontSetup.AppFellBack);
        });

        Assert.False(chart);
        Assert.False(app);
    }

    // ---- the order of the startup (App.cs): handlers first, then the theme and the fonts -----------------------------------------

    [Fact]
    public void AppStartup_InstallsTheGlobalExceptionHandlers_BeforeAnyThemeOrFontWork()
    {
        var source = File.ReadAllText(RepoPaths.Find("src/Lifestream.Desktop/App.cs"));
        var start = source.IndexOf("protected override void OnStartup", StringComparison.Ordinal);
        var end = source.IndexOf("protected override void OnExit", StringComparison.Ordinal);
        var startup = source[start..end];

        var handlers = startup.IndexOf("_exceptions.Install()", StringComparison.Ordinal);
        var theme = startup.IndexOf("ThemeLoader.Resources", StringComparison.Ordinal);
        var chartWarmUp = startup.IndexOf("ChartWarmUp.Start", StringComparison.Ordinal);

        Assert.True(handlers > 0, "OnStartup no longer installs the global handlers");
        Assert.True(theme > handlers, "the theme (and with it the fonts) is loaded before the global exception handlers are installed");
        Assert.True(chartWarmUp > handlers);
        Assert.DoesNotContain("ThemeLoader", source[..start], StringComparison.Ordinal);
    }
}
