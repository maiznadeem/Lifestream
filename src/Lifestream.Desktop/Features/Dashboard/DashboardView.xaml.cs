using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Theme;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

namespace Lifestream.Desktop.Features.Dashboard;

/// <summary>
/// The dashboard screen (direction A). The donations and issues chart is a 12-month line chart drawn from the view model's buckets whenever they change
/// or the theme changes: donated in blood with a soft fill, issued in info blue, the last month marked and labelled at the end of each line, a dashed grid,
/// no axis titles (the panel header names the chart and holds the legend), and the same numbers behind "View as table". Colours are the theme's tokens.
/// The panels share the page height so the dashboard fits 1280 x 800 without scrolling; a compact window stacks them in one column and scrolls.
/// </summary>
public partial class DashboardView : UserControl
{
    private static readonly DependencyPropertyDescriptor BreakpointDescriptor = DependencyPropertyDescriptor.FromProperty(Responsive.BreakpointProperty, typeof(DashboardView));
    private DashboardViewModel? _model;

    /// <summary>How many times the chart has been drawn in this process (0 means the next draw is the first, cold one; for tests).</summary>
    internal static int ChartDrawCount { get; private set; }

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            ThemeService.Changed += OnThemeChanged;
            BreakpointDescriptor.AddValueChanged(this, OnBreakpointChanged);
            ApplyLayout();
        };
        Unloaded += (_, _) =>
        {
            ThemeService.Changed -= OnThemeChanged;
            BreakpointDescriptor.RemoveValueChanged(this, OnBreakpointChanged);
            Detach();
        };
        PageScroll.SizeChanged += (_, _) => ApplyLayout();
    }

    /// <summary>The donations chart (for tests).</summary>
    public LiveChartsCore.SkiaSharpView.WPF.CartesianChart DonationChart => DonationsChart;

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

    private void OnThemeChanged(object? sender, EventArgs e) => OnChartDataChanged(sender, e);

    private void OnBreakpointChanged(object? sender, EventArgs e) => ApplyLayout();

    /// <summary>Standard and wide: two rows of two panels sharing the page height. Compact: one column at natural heights, scrolling.</summary>
    private void ApplyLayout()
    {
        var compact = Responsive.GetBreakpoint(this) == Breakpoint.Compact;
        var viewport = PageScroll.ViewportHeight;
        if (compact)
        {
            Layout.Height = double.NaN;
            TopRow.Height = new GridLength(326);
            StackRow.Height = new GridLength(320);
            BottomRow.Height = GridLength.Auto;
            RightTop.Width = new GridLength(0);
            Grid.SetRow(TodayPanel, 2);
            Grid.SetColumn(TodayPanel, 0);
            AttentionPanel.Margin = ThemeTokens.Inset("Inset.B20");
            BottomGrid.ColumnDefinitions[1].Width = new GridLength(0);
            BottomGrid.ColumnDefinitions[2].Width = new GridLength(0);
            StockRow.Height = new GridLength(378);
            ChartGap.Height = new GridLength(20);
            ChartRow.Height = new GridLength(378);
            Grid.SetRow(DonationsFrame, 2);
            Grid.SetColumn(DonationsFrame, 0);
        }
        else
        {
            // Fill the page exactly (no scrolling) down to the layout's minimum height; below that, scroll.
            Layout.Height = viewport > 0 ? Math.Max(Layout.MinHeight, viewport) : double.NaN;
            TopRow.Height = new GridLength(330, GridUnitType.Star);
            StackRow.Height = new GridLength(0);
            BottomRow.Height = new GridLength(374, GridUnitType.Star);
            RightTop.Width = new GridLength(372);
            Grid.SetRow(TodayPanel, 1);
            Grid.SetColumn(TodayPanel, 2);
            AttentionPanel.Margin = ThemeTokens.Inset("Inset.R20B20");
            BottomGrid.ColumnDefinitions[1].Width = new GridLength(20);
            BottomGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            StockRow.Height = new GridLength(1, GridUnitType.Star);
            ChartGap.Height = new GridLength(0);
            ChartRow.Height = new GridLength(0);
            Grid.SetRow(DonationsFrame, 0);
            Grid.SetColumn(DonationsFrame, 2);
        }

        Grid.SetColumnSpan(TodayPanel, compact ? 3 : 1);
        Grid.SetColumnSpan(AttentionPanel, compact ? 3 : 1);
        Grid.SetColumnSpan(StockPanel, compact ? 3 : 1);
        Grid.SetColumnSpan(DonationsFrame, compact ? 3 : 1);
    }

    private static SKColor Token(string key)
    {
        var color = ThemeService.ColorOf(key);
        return new SKColor(color.R, color.G, color.B, color.A);
    }

    private static SolidColorPaint Paint(string key, float stroke = 0) => new(Token(key)) { StrokeThickness = stroke };

    private void Draw(DashboardViewModel model)
    {
        if (!ChartWarmUp.IsRunning)
        {
            ChartDrawCount++;
        }

        var donated = model.DonatedByMonth;
        var issued = model.IssuedByMonth;
        var blood = Token("Color.Chart1");
        var info = Token("Color.Chart2");
        var layer = Token("Color.Layer");
        var last = donated.Count - 1;

        DonationsChart.AnimationsSpeed = TimeSpan.Zero;
        var series = new List<ISeries>
        {
            new LineSeries<int>
            {
                Name = "Donated",
                Values = [.. donated],
                Stroke = new SolidColorPaint(blood) { StrokeThickness = 2.25f, StrokeJoin = SKStrokeJoin.Round },
                Fill = new LinearGradientPaint([blood.WithAlpha(56), blood.WithAlpha(0)], new SKPoint(0.5f, 0), new SKPoint(0.5f, 1)),
                GeometrySize = 0,
                GeometryStroke = null,
                GeometryFill = null,
                LineSmoothness = 0,
                AnimationsSpeed = TimeSpan.Zero,
            },
            new LineSeries<int>
            {
                Name = "Issued",
                Values = [.. issued],
                Stroke = new SolidColorPaint(info) { StrokeThickness = 2f, StrokeJoin = SKStrokeJoin.Round },
                Fill = null,
                GeometrySize = 0,
                GeometryStroke = null,
                GeometryFill = null,
                LineSmoothness = 0,
                AnimationsSpeed = TimeSpan.Zero,
            },
        };
        if (last >= 0)
        {
            // The end of each line: a ring on the last month and its value beside it (direct labels instead of a legend lookup).
            series.Add(EndMarker(last, donated[last], blood, layer));
            series.Add(EndMarker(last, issued[last], info, layer));
        }

        DonationsChart.Series = series;
        var text = Paint("Color.ChartText");
        DonationsChart.XAxes =
        [
            new Axis
            {
                Labels = [.. model.ChartMonthLabels],
                LabelsPaint = text,
                TextSize = 11,
                SeparatorsPaint = null,
                TicksPaint = null,
                MinStep = 1,
                ForceStepToMin = true,
            },
        ];
        DonationsChart.YAxes =
        [
            new Axis
            {
                MinLimit = DashboardViewModel.YAxisMinimum,
                MinStep = 10,
                LabelsPaint = text,
                TextSize = 11,
                SeparatorsPaint = new SolidColorPaint(Token("Color.ChartGrid")) { StrokeThickness = 1, PathEffect = new DashEffect([2, 4]) },
                Labeler = value => value.ToString("N0", CultureInfo.CurrentCulture),
            },
        ];
        DonationsChart.LegendPosition = LegendPosition.Hidden;
        DonationsChart.TooltipPosition = TooltipPosition.Hidden;
    }

    private static ScatterSeries<ObservablePoint> EndMarker(int index, int value, SKColor color, SKColor fill) => new()
    {
        Values = [new ObservablePoint(index, value)],
        GeometrySize = 8,
        Stroke = new SolidColorPaint(color) { StrokeThickness = 2 },
        Fill = new SolidColorPaint(fill),
        DataLabelsPaint = new SolidColorPaint(color),
        DataLabelsSize = 11.5,
        DataLabelsPosition = DataLabelsPosition.Right,
        DataLabelsFormatter = point => point.Coordinate.PrimaryValue.ToString("N0", CultureInfo.CurrentCulture),
        AnimationsSpeed = TimeSpan.Zero,
        IsHoverable = false,
    };
}
