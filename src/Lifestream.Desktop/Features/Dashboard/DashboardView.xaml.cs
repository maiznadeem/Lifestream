using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Desktop.Theme;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Lifestream.Desktop.Features.Dashboard;

/// <summary>
/// The dashboard screen. The charts are drawn from the view model's data whenever it changes (a load, a period, a blood type switch).
/// Colours come from the theme's tokens, the y-axes start at zero, each x-axis is named after its bucket, and every chart has its table
/// behind "View as table" (the frame).
/// </summary>
public partial class DashboardView : UserControl
{
    private DashboardViewModel? _model;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    /// <summary>The donations chart (for tests).</summary>
    public LiveChartsCore.SkiaSharpView.WPF.CartesianChart DonationChart => DonationsChart;

    /// <summary>The demographics chart (for tests).</summary>
    public LiveChartsCore.SkiaSharpView.WPF.CartesianChart GenderChart => DemographicsChart;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _model = e.NewValue as DashboardViewModel;
        if (_model is not null)
        {
            _model.ChartDataChanged += OnChartDataChanged;
            if (_model.MonthLabels.Count > 0)
            {
                Draw(_model);
            }
        }
    }

    private void Detach()
    {
        if (_model is not null)
        {
            _model.ChartDataChanged -= OnChartDataChanged;
            _model = null;
        }
    }

    private void OnChartDataChanged(object? sender, EventArgs e)
    {
        if (_model is not null)
        {
            Draw(_model);
        }
    }

    private SKColor Token(string key)
    {
        var color = TryFindResource(key) is Color found ? found : Colors.Gray;
        return new SKColor(color.R, color.G, color.B, color.A);
    }

    private SolidColorPaint Ink(string key, float strokeThickness = 0) => new(Token(key)) { StrokeThickness = strokeThickness };

    private Axis Axis(string name, IReadOnlyList<string>? labels = null, double? minimum = null, double? minimumStep = null)
    {
        var axis = new Axis
        {
            Name = name,
            NamePaint = Ink("Color.TextSecondary"),
            NameTextSize = 12,
            LabelsPaint = Ink("Color.TextSecondary"),
            TextSize = 12,
            SeparatorsPaint = Ink("Color.BorderSubtle", 1),
            Labels = labels is null ? null : [.. labels],
        };
        if (minimum is { } min)
        {
            axis.MinLimit = min; // y-axes start at zero so bar heights are honest
        }

        if (minimumStep is { } step)
        {
            axis.MinStep = step;
        }

        return axis;
    }

    private void Draw(DashboardViewModel model)
    {
        var labels = model.MonthLabels;

        DonationsChart.AnimationsSpeed = TimeSpan.Zero;
        DonationsChart.Series =
        [
            new ColumnSeries<int>
            {
                Name = "Donated",
                Values = [.. model.DonatedByMonth],
                Fill = Ink("Color.Chart1"),
                MaxBarWidth = ThemeTokens.Space("Space.24"),
                Padding = ThemeTokens.Space("Chart.BarGap"),
                AnimationsSpeed = TimeSpan.Zero,
            },
            new ColumnSeries<int>
            {
                Name = "Issued",
                Values = [.. model.IssuedByMonth],
                Fill = Ink("Color.Chart2"),
                MaxBarWidth = ThemeTokens.Space("Space.24"),
                Padding = ThemeTokens.Space("Chart.BarGap"),
                AnimationsSpeed = TimeSpan.Zero,
            },
        ];
        DonationsChart.XAxes = [Axis(model.XAxisTitle, labels)];
        DonationsChart.YAxes = [Axis(model.YAxisTitle, minimum: DashboardViewModel.YAxisMinimum, minimumStep: 1)];
        DonationsChart.LegendPosition = LegendPosition.Top;
        DonationsChart.LegendTextPaint = Ink("Color.TextPrimary");
        DonationsChart.LegendTextSize = 13;

        DemographicsChart.AnimationsSpeed = TimeSpan.Zero;
        DemographicsChart.Series =
        [
            new ColumnSeries<int>
            {
                Name = "Donors",
                Values = [.. model.Demographics.Select(b => b.Count)],
                Fill = Ink("Color.Chart1"),
                MaxBarWidth = 64,
                // Data labels sit above the bar on the raised surface, in the primary text colour (4.5:1 or better).
                DataLabelsPaint = Ink("Color.TextPrimary"),
                DataLabelsSize = 14,
                DataLabelsPosition = DataLabelsPosition.End,
                DataLabelsFormatter = point => point.Coordinate.PrimaryValue.ToString("N0", CultureInfo.CurrentCulture),
                AnimationsSpeed = TimeSpan.Zero,
            },
        ];
        DemographicsChart.XAxes = [Axis("Gender", [.. model.Demographics.Select(b => b.Label)])];
        DemographicsChart.YAxes = [Axis("Donors", minimum: 0, minimumStep: 1)];
        DemographicsChart.LegendPosition = LegendPosition.Hidden;
    }

    /// <summary>The totals grid does not scroll by itself; the wheel goes on to the page.</summary>
    private void OnTotalsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement { Parent: UIElement parent })
        {
            return;
        }

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }
}
