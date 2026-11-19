using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Common;
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
/// The top row ("Needs attention" and today's appointments) is as tall as its content, and the bottom row (stock and the chart) takes the rest of the
/// page, so the dashboard fits 1280 x 800 without scrolling and leaves no empty bands in a larger window. "Needs attention" shows as many rows as the
/// window has room for (the view model words the count and "View all"). A compact window stacks the panels in one column at their own heights and only
/// the page scrolls.
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
            _model.PropertyChanged += OnModelChanged;
            if (_model.MonthLabels.Count > 0)
            {
                Draw(_model);
            }
        }

        ApplyLayout();
    }

    private void Detach()
    {
        if (_model is not null)
        {
            _model.ChartDataChanged -= OnChartDataChanged;
            _model.PropertyChanged -= OnModelChanged;
            _model = null;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.State) or nameof(DashboardViewModel.AttentionCountText))
        {
            // An error or the skeleton is shown in the window, not in the middle of a page as tall as the panels would be; and the appointments
            // list follows the height of "Needs attention" (its count text changes whenever its rows do).
            ApplyLayout();
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

    /// <summary>The height the bottom row is comfortable at (eight meters at 33 and the panel's header and padding); "Needs attention" leaves it that much.</summary>
    private const double BottomRowComfort = 340;

    /// <summary>One "Needs attention" row with a one-line description (34 tile, 2 x 12 padding, the divider) and the panel's header and bottom padding.</summary>
    private const double AttentionRowHeight = 63;
    private const double AttentionChrome = 54;

    /// <summary>The fewest rows "Needs attention" shows, however short the window (two fit above the stock panel on a maximized 1366 x 768 screen).</summary>
    private const int MinimumAttentionRows = 2;

    /// <summary>
    /// Standard and wide: "Needs attention" and today's appointments in a row as tall as its content, then stock and the chart in the rest of the page
    /// (in a wide window the chart gets the larger share, so the meters do not stretch). Compact: one column at natural heights; only the page scrolls.
    /// </summary>
    private void ApplyLayout()
    {
        var breakpoint = Responsive.GetBreakpoint(this);
        var compact = breakpoint == Breakpoint.Compact;
        var viewport = PageScroll.ViewportHeight;
        var ready = _model is null || _model.State == LoadState.Ready;

        // While the skeleton, the empty state or an error is shown, the page is the window: nothing to scroll, and the message sits where the eye is.
        Surface.MaxHeight = ready || viewport <= 0 ? double.PositiveInfinity : viewport;
        if (compact)
        {
            Layout.MinHeight = 0;
            BottomRow.Height = GridLength.Auto;
            RightTop.Width = new GridLength(0);
            StackRow.Height = GridLength.Auto;
            Grid.SetRow(TodayPanel, 2);
            Grid.SetColumn(TodayPanel, 0);
            TodayBox.Cap = double.PositiveInfinity; // every appointment, at its own height: no scroll inside the page's scroll
            AttentionPanel.Margin = ThemeTokens.Inset("Inset.B20");
            StockColumn.Width = new GridLength(1, GridUnitType.Star);
            BottomGrid.ColumnDefinitions[1].Width = new GridLength(0);
            ChartColumn.Width = new GridLength(0);
            StockRow.Height = GridLength.Auto;
            ChartGap.Height = new GridLength(ThemeTokens.Space("Space.20"));
            ChartRow.Height = new GridLength(CompactChartHeight);
            Grid.SetRow(DonationsFrame, 2);
            Grid.SetColumn(DonationsFrame, 0);
        }
        else
        {
            // At least the page: the bottom row (a star row) takes what the top row leaves. A shorter window scrolls instead of squeezing the panels.
            Layout.MinHeight = viewport > 0 ? viewport : 0;
            BottomRow.Height = new GridLength(1, GridUnitType.Star);
            RightTop.Width = new GridLength(TodayColumnWidth);
            StackRow.Height = new GridLength(0);
            Grid.SetRow(TodayPanel, 1);
            Grid.SetColumn(TodayPanel, 2);
            AttentionPanel.Margin = ThemeTokens.Inset("Inset.R20B20");
            var wide = breakpoint == Breakpoint.Wide;
            StockColumn.Width = new GridLength(wide ? 5 : 1, GridUnitType.Star);
            BottomGrid.ColumnDefinitions[1].Width = new GridLength(ThemeTokens.Space("Space.20"));
            ChartColumn.Width = new GridLength(wide ? 7 : 1, GridUnitType.Star);
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

        if (_model is not null)
        {
            _model.AttentionLimit = compact || viewport <= 0 ? int.MaxValue : AttentionRowsThatFit(viewport);
        }

        if (!compact)
        {
            // Beside "Needs attention" the appointments list asks for about the height that panel has (eight rows at most, three at least), so a short
            // window's top row is as tall as the attention rows it has room for, and the appointments scroll inside their panel.
            var rows = _model is null ? 0 : _model.IsAttentionExpanded ? _model.Attention.Count : Math.Min(_model.Attention.Count, _model.AttentionLimit);
            TodayBox.Cap = rows == 0 ? TodayRowsCap : Math.Clamp((rows * AttentionRowHeight) + AttentionChrome - TodayChrome, TodayRowsFloor, TodayRowsCap);
        }
    }

    /// <summary>The width of the appointments column beside "Needs attention".</summary>
    private const double TodayColumnWidth = 372;

    /// <summary>Eight appointment rows (8 x 34): what the appointments panel asks for at most beside "Needs attention"; three rows at least.</summary>
    private const double TodayRowsCap = 272;
    private const double TodayRowsFloor = 102;

    /// <summary>The header of the appointments panel.</summary>
    private const double TodayChrome = 46;

    /// <summary>The chart's height when it is stacked under the stock panel.</summary>
    private const double CompactChartHeight = 378;

    /// <summary>How many "Needs attention" rows fit above a comfortable bottom row in a page this tall.</summary>
    private int AttentionRowsThatFit(double viewport)
    {
        var header = HeaderRow.ActualHeight > 0 ? HeaderRow.ActualHeight : HeaderHeightGuess;
        var room = viewport - header - ThemeTokens.Space("Space.20") - BottomRowComfort - AttentionChrome;
        return Math.Max(MinimumAttentionRows, (int)Math.Floor(room / AttentionRowHeight));
    }

    /// <summary>The page header (the date, the greeting) with its 20 below, before it has been laid out.</summary>
    private const double HeaderHeightGuess = 78;

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
