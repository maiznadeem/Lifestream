using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// No toast, and no toast action, from one sign-in session reaches the next (fake API, manual clock). The toast service tags every toast with the
/// session it was raised in and drops a Show from an ended session, so no view model checks it itself.
/// </summary>
public sealed class ToastSessionTests
{
    private static async Task<(FeatureRig Rig, DonorsViewModel Vm)> SelectedDonorAsync()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples.Donor(1, "Donor 001")));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new(true, [], null)));
        var vm = rig.Donors();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;
        return (rig, vm);
    }

    private static string Texts(FeatureRig rig) => string.Join(" | ", rig.Toasts.Toasts.Select(t => $"{t.Kind}:'{t.Message}' action={(t.HasAction ? t.ActionLabel : "none")}"));

    [Fact]
    public async Task SignOutWhileTheListReloadsAfterADelete_NoDeletedUndoToastReachesTheNextSession_FakeApi()
    {
        var (rig, vm) = await SelectedDonorAsync();
        var reload = new TaskCompletionSource();
        rig.Fake.On<PagedResult<DonorListItem>>("GetDonorsAsync", async _ =>
        {
            await reload.Task; // the reload after the delete is still waiting
            return ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(2));
        });
        rig.Fake.On("RestoreDonorAsync", ApiResult<DonorDetail>.Ok(Samples.Donor(1, "Donor 001")));

        var delete = vm.DeleteCommand.ExecuteAsync();
        await WaitUntilAsync(() => rig.Recorder.CountOf("DeleteDonorAsync") == 1);
        rig.Toasts.EndSession(); // sign-out while the reload waits
        reload.SetResult();
        await delete;

        Assert.True(rig.Toasts.Toasts.Count == 0, $"the next session sees: [{Texts(rig)}]");
        Assert.Equal(0, rig.Recorder.CountOf("RestoreDonorAsync"));
    }

    [Fact]
    public async Task SameSession_TheDeletedUndoToastStillShows_AfterASlowReload_FakeApi()
    {
        var (rig, vm) = await SelectedDonorAsync();
        var reload = new TaskCompletionSource();
        rig.Fake.On<PagedResult<DonorListItem>>("GetDonorsAsync", async _ =>
        {
            await reload.Task;
            return ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(2));
        });

        var delete = vm.DeleteCommand.ExecuteAsync();
        await WaitUntilAsync(() => rig.Recorder.CountOf("DeleteDonorAsync") == 1);
        reload.SetResult();
        await delete;

        Assert.Equal("Donor 'Donor 001' deleted.", Assert.Single(rig.Toasts.Toasts, t => t.ActionLabel == "Undo").Message);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("waking")]
    [InlineData("cancelled")]
    public async Task SignOutWhileAnUndoWaits_NoRestoredOrFailureToastReachesTheNextSession_FakeApi(string answer)
    {
        var (rig, vm) = await SelectedDonorAsync();
        await vm.DeleteCommand.ExecuteAsync();
        var undo = rig.Toasts.Toasts.Single(t => t.ActionLabel == "Undo");
        var gate = new TaskCompletionSource();
        rig.Fake.On<DonorDetail>("RestoreDonorAsync", async ct =>
        {
            await gate.Task;
            return answer switch
            {
                "ok" => ApiResult<DonorDetail>.Ok(Samples.Donor(1, "Donor 001")),
                "waking" => FakeApiClient.Fail<DonorDetail>(503, ProblemCodes.DatabaseWaking),
                _ => throw new OperationCanceledException(ct),
            };
        });

        var run = undo.ActionCommand.ExecuteAsync();
        rig.Toasts.EndSession();
        gate.SetResult();
        await run;

        Assert.True(rig.Toasts.Toasts.Count == 0, $"the next session sees: [{Texts(rig)}]");
    }

    [Fact]
    public async Task AToastActionOfAnEndedSession_DoesNotRun_EvenIfTheItemIsStillHeld_FakeApi()
    {
        var rig = new FeatureRig();
        var ran = 0;
        var toast = rig.Toasts.Show(ToastKind.Success, "Donor 'A' deleted.", "Undo", () =>
        {
            ran++;
            return Task.CompletedTask;
        });

        rig.Toasts.EndSession();
        await toast.ActionCommand.ExecuteAsync();

        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task AToastShownAfterTheNextSignIn_IsShown_FakeApi()
    {
        var rig = new FeatureRig();
        rig.Toasts.EndSession(); // sign-out
        rig.Toasts.Show(ToastKind.Info, "Welcome back."); // the next session raises its own toast

        Assert.Equal("Welcome back.", Assert.Single(rig.Toasts.Toasts).Message);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EnteredSession_DropsAShowFromAnEndedSession_WhateverTheCaller_FakeApi()
    {
        var rig = new FeatureRig();
        ToastItem shown;
        using (rig.Toasts.EnterSession())
        {
            rig.Toasts.EndSession();
            await Task.Yield();
            shown = rig.Toasts.Show(ToastKind.Success, "late");
        }

        Assert.Empty(rig.Toasts.Toasts);
        Assert.True(shown.IsDismissed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "the awaited call never happened");
            await Task.Delay(5);
        }
    }
}
