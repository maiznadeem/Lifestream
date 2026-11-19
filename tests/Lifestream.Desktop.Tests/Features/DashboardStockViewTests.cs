using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Stock;
using Lifestream.Contracts.Dashboard;
using Lifestream.Desktop.Controls;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>The dashboard (FEAT-27 to FEAT-31) and stock tiles (FEAT-19) rendered off-screen from the fake API. The window is never shown.</summary>
[Collection("Wpf")]
public sealed class DashboardStockViewTests
{
    private const int Width = 1366;
    private const int Height = 736;

    /// <summary>True when both series colours (Chart1 #FF7A8A and Chart2 #7CB8FF from the tokens) are on screen in numbers only a drawn chart produces.</summary>
    private static bool ChartsDrawn(byte[] png) =>
        StaHost.CountPixelsNear(png, 0xFF, 0x7A, 0x8A, 3) > 400 && StaHost.CountPixelsNear(png, 0x7C, 0xB8, 0xFF, 3) > 400;

    [Trait("Category", "Perf")] // asserts a wall-clock budget: runs in the perf pass (tests/perf/run-perf.ps1), not in the loaded default run
    [Trait("PerfPhase", "timing")]
    [Fact]
    public void Dashboard_OneApiCall_SkeletonShowsFirst_ChartsRenderWithin300msOfDataArrival_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var gate = new TaskCompletionSource();
            rig.Api.On("GetDashboardAsync", async _ =>
            {
                await gate.Task;
                return ApiResult<DashboardResponse>.Ok(DemoData.Dashboard());
            });
            var vm = FeatureHarness.Create<DashboardViewModel>(rig);
            var view = FeatureHarness.DashboardView(vm);
            using var host = Hosted.Create(view, Width, Height);

            var clock = Stopwatch.StartNew();
            var load = vm.LoadAsync();
            host.Settle(120);
            var skeletonVisible = host.All<Skeleton>().Any(s => s.IsVisible);
            var contentVisible = host.All<ChartFrame>().Any(f => f.IsVisible);
            var skeletonMs = clock.ElapsedMilliseconds;
            FeatureHarness.Save(host, "DashboardView_Skeleton.png");

            gate.SetResult();
            Hosted.Await(() => load);
            var arrival = Stopwatch.StartNew(); // the data has been applied; the charts must be drawn within 300 ms
            var drawn = Hosted.PumpUntil(() => ChartsDrawn(host.Capture()), 2000);
            var drawnMs = arrival.ElapsedMilliseconds;
            host.Settle(300);
            FeatureHarness.Save(host, "DashboardView_Loaded.png");
            return (skeletonVisible, contentVisible, skeletonMs, drawn, drawnMs, Calls: rig.Api.Calls.Where(c => c != "WarmUpAsync").ToList());
        });

        Assert.True(facts.skeletonVisible);
        Assert.False(facts.contentVisible);
        Assert.True(facts.drawn, "the charts were not drawn");
        Assert.True(facts.drawnMs < 300, $"charts took {facts.drawnMs} ms after the data arrived");
        Assert.Equal(["GetDashboardAsync"], facts.Calls); // one API call for the whole dashboard
    }

    [Fact]
    public void Dashboard_Loaded_AxesAreNamedAfterTheBucket_StartAtZero_AndLabelledFromTheSameMonths_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<DashboardViewModel>(rig);
            var view = FeatureHarness.DashboardView(vm);
            using var host = Hosted.Create(view, Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(500);

            var x = (LiveChartsCore.SkiaSharpView.Axis)view.DonationChart.XAxes.First();
            var y = (LiveChartsCore.SkiaSharpView.Axis)view.DonationChart.YAxes.First();
            var demographicsY = (LiveChartsCore.SkiaSharpView.Axis)view.GenderChart.YAxes.First();
            var series = view.DonationChart.Series.Cast<LiveChartsCore.SkiaSharpView.ColumnSeries<int>>().ToList();
            return (XName: x.Name, XLabels: x.Labels!.ToList(), YMin: y.MinLimit, DemographicsYMin: demographicsY.MinLimit, Series: series.Select(s => s.Name).ToList(),
                Donated: series[0].Values!.ToList(), Model: vm.DonatedByMonth.ToList(), ModelLabels: vm.MonthLabels.ToList());
        });

        Assert.Equal("Month", facts.XName);
        Assert.Equal(facts.ModelLabels, facts.XLabels); // the axis labels come from the same month buckets as the data
        Assert.Equal(12, facts.XLabels.Count);
        Assert.Equal(facts.Model, facts.Donated);
        Assert.Equal(0d, facts.YMin);
        Assert.Equal(0d, facts.DemographicsYMin);
        Assert.Equal(["Donated", "Issued"], facts.Series);
    }

    [Fact]
    public void Dashboard_ViewAsTable_ShowsTheSameNumbersAsTheChart_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<DashboardViewModel>(rig);
            var view = FeatureHarness.DashboardView(vm);
            using var host = Hosted.Create(view, Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(400);

            var frame = host.Find<ChartFrame>("DonationsFrame");
            var toggle = host.All<System.Windows.Controls.Primitives.ToggleButton>().First(t => AutomationProperties.GetName(t) == "View as table" && frame.IsAncestorOf(t));
            toggle.IsChecked = true;
            host.Settle(300);
            FeatureHarness.Save(host, "DashboardView_Table.png");
            var grid = frame.TableGrid!;
            return (ChartHidden: !view.DonationChart.IsVisible, TableVisible: grid.IsVisible, Rows: grid.Items.Count, Headers: grid.Columns.Select(c => c.Header?.ToString()).ToList(), First: ((IReadOnlyList<string>)grid.Items[0]).ToList(), Model: vm.DonationsTable.Rows[0].ToList());
        });

        Assert.True(facts.ChartHidden);
        Assert.True(facts.TableVisible);
        Assert.Equal(12, facts.Rows);
        Assert.Equal(["Month", "Donated", "Issued"], facts.Headers);
        Assert.Equal(facts.Model, facts.First);
    }

    [Fact]
    public void Dashboard_DemographicsChart_IsALabelledBarChart_WithEveryGender_AndReadableLabels_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<DashboardViewModel>(rig);
            var view = FeatureHarness.DashboardView(vm);
            using var host = Hosted.Create(view, Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(500);

            var bars = (LiveChartsCore.SkiaSharpView.ColumnSeries<int>)view.GenderChart.Series.Single();
            var labels = ((LiveChartsCore.SkiaSharpView.Axis)view.GenderChart.XAxes.Single()).Labels!.ToList();
            var paint = (LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint)bars.DataLabelsPaint!;
            var surface = (Color)view.TryFindResource("Color.SurfaceRaised");
            return (Labels: labels, Values: bars.Values!.ToList(), HasDataLabels: bars.DataLabelsPaint is not null, Ratio: Contrast(paint.Color.Red, paint.Color.Green, paint.Color.Blue, surface), Size: bars.DataLabelsSize);
        });

        Assert.Equal(["Male", "Female", "Other"], facts.Labels);
        Assert.Equal([61, 63, 4], facts.Values);
        Assert.True(facts.HasDataLabels);
        Assert.True(facts.Ratio >= 4.5, $"data label contrast {facts.Ratio:F2}:1");
        Assert.True(facts.Size >= 12);
    }

    private static double Contrast(byte r, byte g, byte b, Color background)
    {
        static double Lum(byte r, byte g, byte b)
        {
            static double F(byte c)
            {
                var s = c / 255d;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * F(r)) + (0.7152 * F(g)) + (0.0722 * F(b));
        }

        var a = Lum(r, g, b);
        var c = Lum(background.R, background.G, background.B);
        return (Math.Max(a, c) + 0.05) / (Math.Min(a, c) + 0.05);
    }

    [Fact]
    public void Dashboard_Cards_AreFlat_NoBlurOrShadowEffect_InXamlOrOnTheRenderedTree_FakeApi()
    {
        var featuresDir = Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop/Features");
        var xaml = Directory.EnumerateFiles(Path.GetFullPath(featuresDir), "*.xaml", SearchOption.AllDirectories).ToList();
        Assert.True(xaml.Count >= 10, $"scanned {xaml.Count} feature xaml files");
        foreach (var file in xaml)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("BlurEffect", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DropShadowEffect", text, StringComparison.Ordinal);
            Assert.DoesNotContain("BlurRadius", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Effect=", text, StringComparison.Ordinal);
        }

        var withEffect = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<DashboardViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.DashboardView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            return host.All<UIElement>().Count(e => e.Effect is not null);
        });
        Assert.Equal(0, withEffect);
    }

    [Fact]
    public void Dashboard_TotalsTable_NumbersAreRightAlignedUnderRightAlignedHeaders_NoCellBorders_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<DashboardViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.DashboardView(vm), Width, 1700); // tall enough to show every block without scrolling
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(400);

            var grid = host.Find<LifestreamDataGrid>("TotalsGrid");
            var headers = host.All<DataGridColumnHeader>().Where(h => grid.IsAncestorOf(h) && h.Content is TextBlock).Select(h => (TextBlock)h.Content).ToList();
            var cell = host.All<DataGridCell>().First(c => grid.IsAncestorOf(c) && c.Column.DisplayIndex == 1);
            var text = Hosted.Descendants(cell).OfType<TextBlock>().First();
            var textBounds = Hosted.BoundsOf(text, cell);
            var headerBox = host.All<DataGridColumnHeader>().First(h => h.Column?.DisplayIndex == 1 && grid.IsAncestorOf(h));
            var headerText = (TextBlock)headerBox.Content;
            var headerTextRight = Hosted.BoundsOf(headerText, headerBox).Right;
            var headerRight = headerBox.ActualWidth;
            FeatureHarness.Save(host, "DashboardView_Totals.png");
            return (
                HeaderAlignments: headers.Select(h => h.TextAlignment).ToList(),
                HeaderStretch: headers.All(h => h.HorizontalAlignment == HorizontalAlignment.Stretch),
                CellAlignment: text.TextAlignment,
                CellFillsColumn: Math.Abs(textBounds.Width - cell.ActualWidth) < 1,
                HeaderTextReachesTheRight: headerRight - headerTextRight < 13,
                GridLines: grid.GridLinesVisibility,
                Borders: grid.CellStyle.Setters.OfType<Setter>().Where(s => s.Property == Control.BorderThicknessProperty).Select(s => s.Value).ToList(),
                Rows: grid.Items.Count);
        });

        Assert.Equal([TextAlignment.Right, TextAlignment.Right], facts.HeaderAlignments);
        Assert.True(facts.HeaderStretch);
        Assert.Equal(TextAlignment.Right, facts.CellAlignment);
        Assert.True(facts.CellFillsColumn);
        Assert.True(facts.HeaderTextReachesTheRight);
        Assert.Equal(DataGridGridLinesVisibility.None, facts.GridLines);
        Assert.All(facts.Borders, thickness => Assert.Equal(new Thickness(0), thickness));
        Assert.Equal(9, facts.Rows);
    }

    // Stock (FEAT-19)

    [Fact]
    public void Stock_GroupBelowMinimum_HasIconWordsAndCriticalToken_Rendered_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<StockViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.StockView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(400);
            FeatureHarness.Save(host, "StockView_Loaded.png");

            var critical = ((SolidColorBrush)host.Element.TryFindResource("Brush.Critical")).Color;
            var badges = host.All<Badge>().Where(b => b.IsVisible).ToList();
            var table = host.Find<LifestreamDataGrid>("Grid");
            var counts = host.All<TextBlock>().Where(t => t.IsVisible && table.IsAncestorOf(t) && (t.Text.EndsWith(" units", StringComparison.Ordinal) || t.Text.EndsWith(" unit", StringComparison.Ordinal))).ToList();
            return (
                Low: badges.Where(b => b.Kind == BadgeKind.Critical).Select(b => (b.Text, b.Icon)).ToList(),
                Rows: counts.Count,
                CriticalCounts: counts.Count(t => t.Foreground is SolidColorBrush brush && brush.Color == critical),
                Shortage: host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).Single(t => t.Contains("below the minimum", StringComparison.Ordinal)),
                Calls: rig.Api.CountOf("GetStockAsync"));
        });

        // A- has 6 of 10 and AB- has 3 of 10: words (how many short), an icon, and the Critical kind, never colour alone.
        Assert.Equal([("Low, 4 short", IconKind.Alert), ("Low, 7 short", IconKind.Alert)], facts.Low);
        Assert.Equal(8, facts.Rows);
        Assert.Equal(2, facts.CriticalCounts); // the two low groups' unit counts are in the Critical token
        Assert.Equal("2 of 8 blood groups are below the minimum.", facts.Shortage);
        Assert.Equal(1, facts.Calls);
    }

    [Fact]
    public void Stock_Filter_ChangesTheVisibleRowsAtOnce_NoExtraCall_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<StockViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.StockView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            int Rows() => host.All<DataGridRow>().Count(r => r.IsVisible);
            var all = Rows();

            vm.ViewFilter = vm.ViewOptions.First(o => o.Value == StockFilter.BelowThreshold);
            host.Settle(100); // one dispatcher turn: no debounce
            var low = Rows();
            var lowTabChecked = host.All<RadioButton>().Single(r => r.Name == "LowTab").IsChecked == true;
            FeatureHarness.Save(host, "StockView_Filtered.png");

            vm.BloodTypeFilter = vm.BloodTypeOptions.First(o => o.Value == BloodType.OPos);
            host.Settle(100);
            var none = host.All<EmptyState>().Count(e => e.IsVisible && e.Title == "No blood groups match");
            return (all, low, lowTabChecked, none, Calls: rig.Api.CountOf("GetStockAsync"));
        });

        Assert.Equal(8, facts.all);
        Assert.Equal(2, facts.low);
        Assert.True(facts.lowTabChecked, "the Low tab did not follow the view filter");
        Assert.Equal(1, facts.none);
        Assert.Equal(1, facts.Calls);
    }

    [Fact]
    public void Stock_SelectingAGroup_OpensItsPanel_WithTheShelfLifeSplitAndCompatibility_EscCloses_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<StockViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.StockView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            var closedAtFirst = !host.All<SidePanel>().Single().IsOpen;

            vm.SelectedTile = vm.Tiles.Single(t => t.BloodType == BloodType.OPos);
            host.Settle(300);
            FeatureHarness.Save(host, "StockView_Panel.png");
            // Run-built lines ("66 units in stock") are read from their inlines.
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text).ToList();
            var open = host.All<SidePanel>().Single().IsOpen;

            vm.ClosePanelCommand.Execute(null);
            host.Settle(100);
            return (closedAtFirst, open, texts, ClosedAgain: !host.All<SidePanel>().Single().IsOpen, Selected: vm.SelectedTile);
        });

        Assert.True(facts.closedAtFirst);
        Assert.True(facts.open);
        Assert.Contains("66 units in stock", facts.texts);
        Assert.Contains("Find O+ donors", facts.texts);
        Assert.Contains("60", facts.texts); // more than 7 days left (66 - 6)
        Assert.Contains("6", facts.texts); // expires within 7 days
        Assert.Contains("A+, B+, AB+, O+", facts.texts); // can be given to
        Assert.True(facts.ClosedAgain);
        Assert.Null(facts.Selected);
    }

    [Fact]
    public void FeatureXaml_UsesThemeTokensForSpacingAndRadius_NoLiteralNumbers_NoHexColours()
    {
        var featuresDir = Path.GetFullPath(Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop/Features"));
        var literal = new System.Text.RegularExpressions.Regex(""""(?:Margin|Padding|CornerRadius|Spacing)\s*=\s*"([^"{]*)"|Property="(?:Margin|Padding|CornerRadius|Spacing)"\s+Value="([^"{]*)"""");
        var hex = new System.Text.RegularExpressions.Regex("""#[0-9A-Fa-f]{6,8}""");
        var offenders = new List<string>();
        var files = Directory.EnumerateFiles(featuresDir, "*.xaml", SearchOption.AllDirectories).ToList();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match match in literal.Matches(text))
            {
                var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (value.Split(',', ' ').Any(part => part.Length > 0 && part != "0"))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
                }
            }

            offenders.AddRange(hex.Matches(text).Select(m => $"{Path.GetFileName(file)}: {m.Value}"));
        }

        Assert.True(files.Count >= 11, $"scanned {files.Count} files");
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }
}
