using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// The stack spike (plan Task 0): WPF-UI, LiveCharts2 and CommunityToolkit.Mvvm work together on .NET 11. It lives here, in the
/// test project: the shipped app has no spike window or view model (SHELL-19).
/// </summary>
public sealed partial class SpikeTests
{
    private sealed partial class SpikeViewModel : ObservableObject
    {
        [ObservableProperty]
        public partial string Caption { get; set; } = "Spike: WPF-UI + LiveCharts2 + Toolkit";

        public ISeries[] Series { get; } =
        [
            new ColumnSeries<int>
            {
                Values = [3, 7, 4, 9, 6, 2],
                Fill = new SolidColorPaint(new SKColor(0xC8, 0x10, 0x2E)),
                AnimationsSpeed = TimeSpan.Zero,
            },
        ];
    }

    private static Grid BuildView(SpikeViewModel viewModel)
    {
        var grid = new Grid { Width = 800, Height = 600, DataContext = viewModel };
        grid.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Wpf.Ui;component/Resources/Theme/Dark.xaml") });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        var button = new Wpf.Ui.Controls.Button { Appearance = ControlAppearance.Primary, Margin = new Thickness(16) };
        button.SetBinding(ContentControl.ContentProperty, nameof(SpikeViewModel.Caption));
        grid.Children.Add(button);
        var chart = new CartesianChart { Series = viewModel.Series, Margin = new Thickness(16) };
        Grid.SetRow(chart, 1);
        grid.Children.Add(chart);
        return grid;
    }

    [Fact]
    public void Spike_FluentWindowWithChart_RendersOffScreen()
    {
        var png = StaHost.Run(() =>
        {
            var window = new FluentWindow { Width = 800, Height = 600 }; // never shown
            var view = BuildView(new SpikeViewModel());
            window.Content = view;
            return StaHost.RenderPng(view, 800, 600);
        });

        var outputDirectory = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(Path.Combine(outputDirectory, "spike-fluentwindow-chart.png"), png);

        var distinct = StaHost.CountDistinctColours(png);
        Assert.True(distinct > 2, $"Expected more than 2 distinct colours, got {distinct}.");

        // The series colour (brand red C8102E) only appears if LiveCharts2 actually drew.
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
