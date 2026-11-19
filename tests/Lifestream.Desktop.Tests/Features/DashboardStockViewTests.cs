using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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

    /// <summary>True when both series colours (Brush.Chart1 and Brush.Chart2 of the theme in use) are on screen in numbers only a drawn chart produces.</summary>
    private static bool ChartsDrawn(byte[] png, FrameworkElement scope)
    {
        var one = ((SolidColorBrush)scope.TryFindResource("Brush.Chart1")).Color;
        var two = ((SolidColorBrush)scope.TryFindResource("Brush.Chart2")).Color;
        return StaHost.CountPixelsNear(png, one.R, one.G, one.B, 3) > 400 && StaHost.CountPixelsNear(png, two.R, two.G, two.B, 3) > 400;
    }

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
            var drawn = Hosted.PumpUntil(() => ChartsDrawn(host.Capture(), host.Element), 2000);
            var drawnMs = arrival.ElapsedMilliseconds;
            host.Settle(300);
            FeatureHarness.Save(host, "DashboardView_Loaded.png");
            return (skeletonVisible, contentVisible, skeletonMs, drawn, drawnMs, Calls: rig.Api.Calls.Where(c => c != "WarmUpAsync").ToList());
        });

        Assert.True(facts.skeletonVisible);
        Assert.False(facts.contentVisible);
        Assert.True(facts.drawn, "the charts were not drawn");
        Assert.True(facts.drawnMs < 300, $"charts took {facts.drawnMs} ms after the data arrived");
        // One dashboard call for the numbers and the chart; direction A's blocks (stock meters, today's appointments, open requests) read the existing
        // endpoints in parallel, best effort.
        Assert.Equal(1, facts.Calls.Count(c => c == "GetDashboardAsync"));
        Assert.Contains("GetStockAsync", facts.Calls);
        Assert.Contains("GetAppointmentsAsync", facts.Calls);
        Assert.Contains("GetRequestsAsync", facts.Calls);
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
            var series = view.DonationChart.Series.OfType<LiveChartsCore.SkiaSharpView.LineSeries<int>>().ToList();
            return (XName: x.Name, XLabels: x.Labels!.ToList(), YMin: y.MinLimit, Series: series.Select(s => s.Name).ToList(),
                Donated: series[0].Values!.ToList(), Model: vm.DonatedByMonth.ToList(), ModelLabels: vm.ChartMonthLabels.ToList(), Buckets: vm.MonthLabels.Count);
        });

        Assert.Null(facts.XName); // direction A: no axis titles; the panel header names the chart
        Assert.Equal(facts.ModelLabels, facts.XLabels); // the axis labels come from the same month buckets as the data
        Assert.Equal(12, facts.XLabels.Count);
        Assert.Equal(facts.Buckets, facts.XLabels.Count);
        Assert.Equal(facts.Model, facts.Donated);
        Assert.Equal(0d, facts.YMin);
        Assert.Equal(["Donated", "Issued"], facts.Series); // two lines, plus the end markers
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

    // Direction A removed the totals grid (and the KPI cards, and the demographics chart). The numbers it showed right-aligned live in the stock
    // meters of "Stock by blood group": one row per group, the count right-aligned in its own column, a group below its minimum drawn in Critical.
    [Fact]
    public void Dashboard_StockMeters_ShowTheEightGroups_CountsRightAligned_LowGroupsInCritical_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<DashboardViewModel>(rig);
            var view = FeatureHarness.DashboardView(vm);
            using var host = Hosted.Create(view, Width, 1700); // tall enough to show every block without scrolling
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(400);
            FeatureHarness.Save(host, "DashboardView_StockMeters.png");

            var list = host.Find<ItemsControl>("StockList");
            var critical = ((SolidColorBrush)host.Element.TryFindResource("Brush.Critical")).Color;
            var ink2 = ((SolidColorBrush)host.Element.TryFindResource("Brush.Ink2")).Color;
            var rows = new List<(string Group, string Count, HorizontalAlignment Align, double CountRight, double BarLeft, double BarRight, Color Fill, Color CountColour, bool Low)>();
            for (var i = 0; i < list.Items.Count; i++)
            {
                var container = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(i);
                var count = Hosted.Descendants(container).OfType<TextBlock>().Single(t => t.Name == "Count");
                var bar = Hosted.Descendants(container).OfType<MeterBar>().Single();
                var countBounds = Hosted.BoundsOf(count, list);
                var barBounds = Hosted.BoundsOf(bar, list);
                var model = (StockMeter)list.Items[i];
                rows.Add((model.Group, string.Concat(count.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)), count.HorizontalAlignment,
                    countBounds.Right, barBounds.Left, barBounds.Right, ((SolidColorBrush)bar.Fill!).Color, ((SolidColorBrush)count.Foreground).Color, model.IsLow));
            }

            return (Rows: rows, ListWidth: list.ActualWidth, Critical: critical, Ink2: ink2, Models: vm.StockMeters.ToList());
        });

        Assert.Equal(8, facts.Rows.Count);
        Assert.Equal(facts.Models.Select(m => m.Group), facts.Rows.Select(r => r.Group));
        Assert.Equal(8, facts.Rows.Select(r => r.Group).Distinct().Count());
        Assert.All(facts.Rows, r => Assert.StartsWith(facts.Models.Single(m => m.Group == r.Group).CountText, r.Count, StringComparison.Ordinal));

        // The counts are right-aligned: every one ends at the same x, at the right edge of the list, whatever its digits.
        Assert.All(facts.Rows, r => Assert.Equal(HorizontalAlignment.Right, r.Align));
        Assert.All(facts.Rows, r => Assert.True(Math.Abs(r.CountRight - facts.ListWidth) < 1, $"{r.Group}: the count ends at {r.CountRight}, the list at {facts.ListWidth}"));
        // The bars start and end at the same x in every row, so the eight meters can be compared by eye.
        Assert.Single(facts.Rows.Select(r => Math.Round(r.BarLeft, 1)).Distinct());
        Assert.Single(facts.Rows.Select(r => Math.Round(r.BarRight, 1)).Distinct());

        // Groups below their minimum (two in the demo data) use the Critical token for the bar and the count; the rest are Ink2 and never Critical.
        var low = facts.Rows.Where(r => r.Low).ToList();
        Assert.Equal(2, low.Count);
        Assert.All(low, r => Assert.Equal(facts.Critical, r.Fill));
        Assert.All(low, r => Assert.Equal(facts.Critical, r.CountColour));
        Assert.All(facts.Rows.Where(r => !r.Low), r => Assert.Equal(facts.Ink2, r.Fill));
        Assert.All(facts.Rows.Where(r => !r.Low), r => Assert.NotEqual(facts.Critical, r.CountColour));
    }

    // Stock (FEAT-19)

    [Fact]
    public void Stock_TileBelowThreshold_HasIconTextAndToken_Rendered_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<StockViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.StockView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(400);
            FeatureHarness.Save(host, "StockView_Loaded.png");

            var chips = host.All<StatusChip>().Where(c => c.IsVisible).ToList();
            var danger = (SolidColorBrush)host.Element.TryFindResource("Brush.StatusDanger");
            var tileTexts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            return (
                Chips: chips.Select(c => (c.Status, c.DisplayText, HasGlyph: c.Glyph.Length > 0, Accent: ((SolidColorBrush)c.AccentBrush!).Color)).ToList(),
                DangerColor: danger.Color,
                Tiles: tileTexts.Count(t => t.EndsWith("available", StringComparison.Ordinal)),
                Summary: tileTexts.Single(t => t.Contains("below their thresholds", StringComparison.Ordinal)),
                Calls: rig.Api.CountOf("GetStockAsync"));
        });

        var low = facts.Chips.Where(c => c.Status == StatusKind.LowStock).ToList();
        Assert.Equal(2, low.Count);
        Assert.All(low, c =>
        {
            Assert.Equal("Low stock", c.DisplayText); // words
            Assert.True(c.HasGlyph); // icon
            Assert.Equal(facts.DangerColor, c.Accent); // token colour
        });
        Assert.Equal(8, facts.Tiles);
        Assert.Equal("2 blood types are below their thresholds.", facts.Summary);
        Assert.Equal(1, facts.Calls);
    }

    [Fact]
    public void Stock_Filter_ChangesTheVisibleTilesAtOnce_NoExtraCall_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<StockViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.StockView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            int Tiles() => host.All<TextBlock>().Count(t => t.IsVisible && t.Text.EndsWith("available", StringComparison.Ordinal));
            var all = Tiles();

            vm.ViewFilter = vm.ViewOptions.First(o => o.Value == StockFilter.BelowThreshold);
            host.Settle(100); // one dispatcher turn: no debounce
            var low = Tiles();
            FeatureHarness.Save(host, "StockView_Filtered.png");

            vm.BloodTypeFilter = vm.BloodTypeOptions.First(o => o.Value == BloodType.OPos);
            host.Settle(100);
            var none = host.All<EmptyState>().Count(e => e.IsVisible && e.Title == "No blood types match");
            return (all, low, none, Calls: rig.Api.CountOf("GetStockAsync"));
        });

        Assert.Equal(8, facts.all);
        Assert.Equal(2, facts.low);
        Assert.Equal(1, facts.none);
        Assert.Equal(1, facts.Calls);
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
