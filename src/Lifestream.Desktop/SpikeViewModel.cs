using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Lifestream.Desktop;

public sealed partial class SpikeViewModel : ObservableObject
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
