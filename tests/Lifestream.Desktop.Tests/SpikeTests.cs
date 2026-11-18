using Wpf.Ui.Controls;

namespace Lifestream.Desktop.Tests;

public sealed class SpikeTests
{
    [Fact]
    public void Spike_FluentWindowWithChart_RendersOffScreen()
    {
        var png = StaHost.Run(() =>
        {
            var window = new FluentWindow { Width = 800, Height = 600 }; // never shown
            var view = new SpikeView();
            window.Content = view;
            return StaHost.RenderPng(view, 800, 600);
        });

        var outputDirectory = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(Path.Combine(outputDirectory, "spike-fluentwindow-chart.png"), png);

        var distinct = StaHost.CountDistinctColours(png);
        Assert.True(distinct > 2, $"Expected more than 2 distinct colours, got {distinct}.");

        // The plan's check (more than 2 colours) is satisfied by the button alone, so also require the chart's own
        // series colour (brand red C8102E) to be present: that only appears if LiveCharts2 actually drew.
        var chartPixels = StaHost.CountPixelsNear(png, 0xC8, 0x10, 0x2E, tolerance: 4);
        Assert.True(chartPixels > 500, $"Expected the red column series to be drawn, found {chartPixels} matching pixels.");
    }

    [Fact]
    public void Spike_ViewModel_RaisesPropertyChanged_ThroughToolkit()
    {
        var raised = new List<string?>();
        var viewModel = new SpikeViewModel();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.Caption = "changed";

        Assert.Equal([nameof(SpikeViewModel.Caption)], raised);
    }
}
