using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Desktop.Tests.Features;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// Real-app walk, defect 27: while a list screen's load waited on the server, the screen showed "Waking up the server" twice: the load
/// surface's notice over the skeleton and the screen's own operation notice above it. One waking server, one notice.
/// </summary>
public sealed class OneWakingNoticeTests
{
    [Fact]
    public void DonorsList_WhileItsLoadWaitsOnAWakingServer_ShowsOneWakingNotice_NotTwo()
    {
        var f = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            rig.Api.On<PagedResult<DonorListItem>>("GetDonorsAsync", async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return default!;
            });
            var clock = new ManualTimeProvider();
            var status = new ServerStatus();
            var vm = FeatureHarness.Create<DonorsViewModel>(rig);
            vm.Attach(status, clock);
            using var host = Hosted.Create(FeatureHarness.DonorsView(vm), 1366, 736);
            _ = vm.LoadAsync();
            host.Settle(300);
            status.Enter(); // what the retry handler does once a call has been slow for 3 s
            clock.Advance(TimeSpan.FromSeconds(3.5));
            host.Settle(300);
            FeatureHarness.Save(host, "fix212b-one-waking-notice.png");
            var notices = host.All<TextBlock>().Count(t => t.IsVisible && t.Text.StartsWith("Waking up the server", StringComparison.Ordinal));
            var result = (vm.State, vm.Operations.IsWaking, notices);
            vm.CancelLoad();
            host.Settle(200);
            return result;
        });

        Assert.Equal((LoadState.Waking, true, 1), f); // released 2.1.2: 2 notices
    }
}
