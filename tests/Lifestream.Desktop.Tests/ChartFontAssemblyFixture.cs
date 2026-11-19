using Lifestream.Desktop.Theme;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.WPF;

[assembly: Xunit.AssemblyFixture(typeof(Lifestream.Desktop.Tests.ChartFontAssemblyFixture))]

namespace Lifestream.Desktop.Tests;

/// <summary>
/// Draws one chart label with the process-wide bundled Inter before any test runs. LiveCharts latches its fallback typeface on the first label
/// drawn in the process and disposes every other typeface when a paint is torn down, so which test happens to draw first (it depends on the test
/// assembly's path) must not decide which font is the latched one: it is always the one <see cref="FontSetup"/> keeps for the whole process.
/// </summary>
public sealed class ChartFontAssemblyFixture
{
    public ChartFontAssemblyFixture()
    {
        _ = StaHost.Run(() =>
        {
            _ = ThemeLoader.Resources; // ApplyChartFont: Inter becomes the LiveCharts default
            var chart = new CartesianChart { Series = [new LineSeries<double> { Values = [1d, 3d, 2d] }] };
            return StaHost.RenderPng(chart, 400, 300, 800); // axis labels are drawn here, which latches the typeface
        });
    }
}
