using Lifestream.Client.Features.Dashboard;
using Lifestream.Desktop.Tests.Features;
using Lifestream.Desktop.Theme;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-26: chart text (axis labels, axis names, legend) is drawn with the bundled font, not the system font. Real Dashboard view, fake API, off-screen render.</summary>
[Collection("Wpf")]
public sealed class ChartFontTests
{
    private static SolidColorPaint With(object? old, SKTypeface typeface) =>
        new(old is SolidColorPaint solid ? solid.Color : SKColors.Black) { SKTypeface = typeface };

    private static void Restyle(CartesianChart chart, SKTypeface typeface)
    {
        // The reference renders set both the default and the explicit paints, so the only difference between them is the font.
        LiveCharts.Configure(settings => settings.HasTextSettings(new TextSettings { DefaultTypeface = typeface }));
        foreach (var axis in chart.XAxes.Concat(chart.YAxes))
        {
            axis.LabelsPaint = With(axis.LabelsPaint, typeface);
            axis.NamePaint = With(axis.NamePaint, typeface);
        }

        chart.LegendTextPaint = With(chart.LegendTextPaint, typeface);
    }

    [Fact]
    public void DashboardCharts_RenderTheirTextInTheBundledFont_NotSegoeUi_FakeApi()
    {
        var (asShipped, explicitBundled, explicitSegoe, defaultFamily, bundledFamily) = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<DashboardViewModel>(rig);
            var view = FeatureHarness.DashboardView(vm);
            using var host = Hosted.Create(view, 1366, 736);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(600);
            var chart = view.DonationChart;
            var shipped = host.Pixels();
            _ = host.Capture("Dashboard_ChartText_Font.png");

            Restyle(chart, ChartFont.Typeface);
            host.Settle(600);
            var bundled = host.Pixels();

            Restyle(chart, SKTypeface.FromFamilyName("Segoe UI"));
            host.Settle(600);
            var segoe = host.Pixels();
            _ = host.Capture("Dashboard_ChartText_SegoeUi_control.png");
            ChartFont.Apply();
            return (shipped, bundled, segoe, SKTypeface.Default.FamilyName, ChartFont.Typeface.FamilyName);
        });

        var againstBundled = Hosted.Differences(asShipped, explicitBundled);
        var againstSegoe = Hosted.Differences(asShipped, explicitSegoe);
        Assert.Contains("Google Sans Flex", bundledFamily, StringComparison.Ordinal);
        Assert.True(againstSegoe > 200, $"control: Segoe UI must render visibly different glyphs ({againstSegoe} pixels differ; the Skia default is {defaultFamily})");
        Assert.True(againstBundled == 0, $"the chart as shipped differs from an explicit render in the bundled font by {againstBundled} pixels (and from Segoe UI by {againstSegoe}): its text is not drawn in the bundled font");
    }
}
