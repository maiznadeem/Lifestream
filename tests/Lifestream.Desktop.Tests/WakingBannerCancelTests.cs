using System.Windows.Controls;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Stock;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Stock;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Desktop.Tests.Features;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-07: the waking banner's Cancel button works on every data screen that shows it (real views, fake API, off-screen).</summary>
[Collection("Wpf")]
public sealed class WakingBannerCancelTests
{
    private static void Hang<T>(Rig rig, string method) =>
        rig.Api.On<T>(method, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return default!;
        });

    private static (bool Shown, bool HasCommand, LoadState After) CancelOn<TViewModel>(Func<TViewModel, System.Windows.FrameworkElement> view, Action<Rig> hang)
        where TViewModel : ViewModelBase
    {
        using var rig = FeatureHarness.NewRig();
        hang(rig);
        var vm = FeatureHarness.Create<TViewModel>(rig);
        using var host = Hosted.Create(view(vm), 1366, 736);
        _ = vm.LoadAsync();
        host.Settle(200);
        vm.State = LoadState.Waking; // what ViewModelBase does after 3 s of a slow call
        host.Settle(200);
        var cancel = host.All<Button>().FirstOrDefault(b => b.IsVisible && b.Content as string == "Cancel");
        var command = cancel?.Command;
        if (command is not null)
        {
            command.Execute(null);
            host.Settle(300);
        }

        return (cancel is not null, command is not null, vm.State);
    }

    [Fact]
    public void DonorsList_WakingBanner_Cancel_CancelsTheLoad_FakeApi()
    {
        var (shown, hasCommand, after) = StaHost.Run(() => CancelOn<DonorsViewModel>(FeatureHarness.DonorsView, r => Hang<PagedResult<DonorListItem>>(r, "GetDonorsAsync")));

        Assert.True(shown, "the waking banner's Cancel button is on screen");
        Assert.True(hasCommand, "the waking banner's Cancel button has no Command: it does nothing");
        Assert.Equal(LoadState.Error, after);
    }

    [Fact]
    public void PatientsList_WakingBanner_Cancel_CancelsTheLoad_FakeApi()
    {
        var (shown, hasCommand, after) = StaHost.Run(() => CancelOn<PatientsViewModel>(FeatureHarness.PatientsView, r => Hang<PagedResult<PatientListItem>>(r, "GetPatientsAsync")));

        Assert.True(shown);
        Assert.True(hasCommand, "the waking banner's Cancel button has no Command: it does nothing");
        Assert.Equal(LoadState.Error, after);
    }

    [Fact]
    public void Dashboard_WakingBanner_Cancel_CancelsTheLoad_FakeApi()
    {
        var (shown, hasCommand, after) = StaHost.Run(() => CancelOn<DashboardViewModel>(FeatureHarness.DashboardView, r => Hang<DashboardResponse>(r, "GetDashboardAsync")));

        Assert.True(shown);
        Assert.True(hasCommand, "the waking banner's Cancel button has no Command: it does nothing");
        Assert.Equal(LoadState.Error, after);
    }

    [Fact]
    public void Stock_WakingBanner_Cancel_CancelsTheLoad_FakeApi()
    {
        var (shown, hasCommand, after) = StaHost.Run(() => CancelOn<StockViewModel>(FeatureHarness.StockView, r => Hang<IReadOnlyList<StockItem>>(r, "GetStockAsync")));

        Assert.True(shown);
        Assert.True(hasCommand, "the waking banner's Cancel button has no Command: it does nothing");
        Assert.Equal(LoadState.Error, after);
    }
}
