using Lifestream.Client.Api;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Contracts.Dashboard;
using Lifestream.Desktop.Features.Dashboard;
using Lifestream.Desktop.Tests.Features;
using Lifestream.Domain;
using LiveChartsCore.Drawing;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// The dashboard chart writes each line's last value beside its last point. That value stands where the plot area ends, and the room the plot leaves there
/// shrinks with the window: in the smallest window (1366 wide, the Store screenshots' size) a number of two digits was cut in half. Real Dashboard view, fake API,
/// off-screen render at the page size of that window: the value's ink there is compared with the same value at the page size of a 1920 window, where it always had room.
/// </summary>
[Collection("Wpf")]
public sealed class ChartEndLabelTests
{
    private const int Months = 12;

    // The page area the shell gives the dashboard in a 1366 x 768 window (the smallest supported, and the size of the Store screenshots): the window less the
    // sidebar and the title bar.
    private const double SmallPageWidth = 1129;
    private const double SmallPageHeight = 724;

    /// <summary>The red-ish pixels (the Donated line's colour on the white card) in the box to the right of the Donated line's last point.</summary>
    private static (int Ink, int RightMost) LabelInk(Hosted host, DashboardView view, int lastValue)
    {
        var chart = view.DonationChart;
        var bounds = Hosted.BoundsOf(chart, host.Element);
        var point = chart.ScaleDataToPixels(new LvcPointD(Months - 1, lastValue));
        var pixels = host.Pixels();
        var width = (int)Math.Round(host.Element.ActualWidth);
        var height = pixels.Length / 4 / width;
        var left = (int)Math.Round(bounds.X + point.X) + 8; // past the ring that marks the point
        var top = (int)Math.Round(bounds.Y + point.Y) - 10;
        var ink = 0;
        var rightMost = 0;
        for (var y = Math.Max(0, top); y < Math.Min(height, top + 20); y++)
        {
            for (var x = left; x < Math.Min(width, left + 70); x++)
            {
                var i = ((y * width) + x) * 4;
                if (pixels[i + 2] - pixels[i] > 60) // red well above blue: the label's ink, not the card, the grid or the page
                {
                    ink++;
                    rightMost = Math.Max(rightMost, x - left);
                }
            }
        }

        return (ink, rightMost);
    }

    [Theory]
    [InlineData(27)]
    [InlineData(118)]
    public void TheValueAtTheEndOfTheDonatedLine_IsDrawnInFull_InTheSmallestWindow_FakeApi(int lastMonth)
    {
        var (small, wide, cardRoom) = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            int[] donated = [20, 14, 14, 21, 19, 31, 21, 27, 19, 21, 29, lastMonth];
            int[] issued = [4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4]; // far below, so the two end values never touch
            var months = Enumerable.Range(0, Months).Select(i => new DateOnly(2025, 12, 1).AddMonths(i)).ToList();
            var series = BloodTypeExtensions.All.Select(t => t == BloodType.APos ? new BloodTypeSeries(t, donated, issued) : new BloodTypeSeries(t, new int[Months], new int[Months])).ToList();
            rig.Api.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(new DashboardResponse(new DashboardKpis(40, 35, 5, 40, 37, 8), months, series, [], [])));
            var vm = FeatureHarness.Create<DashboardViewModel>(rig);
            var view = FeatureHarness.DashboardView(vm);
            using var host = Hosted.Create(view, SmallPageWidth, SmallPageHeight);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(600);
            var small = LabelInk(host, view, lastMonth);
            _ = host.Capture($"Dashboard_ChartEndLabel_{lastMonth}.png");

            // How far the card reaches past the last point: the value must end inside it.
            var chart = view.DonationChart;
            var frame = Hosted.BoundsOf((System.Windows.FrameworkElement)view.FindName("DonationsFrame"), host.Element);
            var point = Hosted.BoundsOf(chart, host.Element).X + chart.ScaleDataToPixels(new LvcPointD(Months - 1, lastMonth)).X;
            var cardRoom = frame.Right - (point + 8);

            host.Layout(1920 - (1366 - SmallPageWidth), 1080 - (768 - SmallPageHeight));
            host.Settle(600);
            var wide = LabelInk(host, view, lastMonth);
            return (small, wide, cardRoom);
        });

        Assert.True(wide.Ink > 20, $"control: the value {lastMonth} was not found beside the last point in the wide window ({wide.Ink} pixels)");
        Assert.True(small.Ink >= wide.Ink * 0.9, $"the value {lastMonth} is cut in a 1366 wide window: {small.Ink} pixels of ink there, {wide.Ink} in a wide window");
        Assert.True(small.RightMost < cardRoom - 4, $"the value {lastMonth} runs to the card's edge: it ends {small.RightMost} px after the point, the card {cardRoom:0} px after it");
    }
}
