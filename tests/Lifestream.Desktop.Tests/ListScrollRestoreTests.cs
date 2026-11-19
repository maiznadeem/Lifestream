using System.Windows.Controls;
using Lifestream.Client.Features.Donors;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Tests.Features;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-24: the list's scroll position survives leaving the screen and coming back (real Donors view, fake API, off-screen).</summary>
[Collection("Wpf")]
public sealed class ListScrollRestoreTests
{
    [Fact]
    public void DonorsList_KeepsScrollPosition_AcrossAVisit_FakeApi()
    {
        var (scrollable, before, after, stored) = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            double offset;
            double extent;
            {
                var vm = FeatureHarness.Create<DonorsViewModel>(rig);
                using var host = Hosted.Create(FeatureHarness.DonorsView(vm), 1366, 736);
                Hosted.Await(() => vm.LoadAsync());
                host.Settle(300);
                var viewer = Hosted.Descendants(host.Find<LifestreamDataGrid>("Grid")).OfType<ScrollViewer>().First();
                extent = viewer.ScrollableHeight;
                viewer.ScrollToVerticalOffset(Math.Min(20, viewer.ScrollableHeight));
                host.Settle(300);
                offset = viewer.VerticalOffset;
                vm.Dispose();
            }

            // Come back: a new view model and view, as navigation does on every visit.
            var vm2 = FeatureHarness.Create<DonorsViewModel>(rig);
            using var host2 = Hosted.Create(FeatureHarness.DonorsView(vm2), 1366, 736);
            Hosted.Await(() => vm2.LoadAsync());
            host2.Settle(500);
            var viewer2 = Hosted.Descendants(host2.Find<LifestreamDataGrid>("Grid")).OfType<ScrollViewer>().First();
            return (extent, offset, viewer2.VerticalOffset, vm2.ViewState.ScrollOffset);
        });

        Assert.True(scrollable > 0, "the grid must be scrollable for this test to mean anything");
        Assert.True(before > 0, "scrolled before leaving");
        Assert.Equal(before, stored);
        Assert.True(Math.Abs(after - before) < 1, $"scroll offset before leaving {before:0}, after coming back {after:0}");
    }
}
