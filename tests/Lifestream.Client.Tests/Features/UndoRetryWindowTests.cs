using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;

namespace Lifestream.Client.Tests.Features;

/// <summary>SHELL-07: a cancelled or failed Undo can be retried while the server's restore window (<see cref="UndoWindow"/>, the API's own constant) is open, and not after (fake API, manual clock).</summary>
public sealed class UndoRetryWindowTests
{
    private static async Task<(FeatureRig Rig, DonorsViewModel Vm, ToastItem Undo)> DeletedDonorAsync()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples.Donor(1, "Donor 001")));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new(true, [], null)));
        var vm = rig.Donors();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;
        await vm.DeleteCommand.ExecuteAsync();
        return (rig, vm, rig.Toasts.Toasts.Single(t => t.ActionLabel == "Undo"));
    }

    private static void RestoreCancels(FeatureRig rig) =>
        rig.Fake.On<DonorDetail>("RestoreDonorAsync", ct => Task.FromException<ApiResult<DonorDetail>>(new OperationCanceledException(ct)));

    [Fact]
    public async Task UndoWindow_IsTheOneConstantTheApiAlsoUses_FakeApi()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), UndoWindow.Duration);
        var source = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "src", "Lifestream.Api", "Features", "Tombstones.cs"));
        Assert.Contains("Window = Lifestream.Contracts.UndoWindow.Duration", source, StringComparison.Ordinal); // not a second number
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("waking")]
    [InlineData("unreachable")]
    public async Task UndoCancelledOrFailed_InsideTheWindow_OffersRetryUndo_AndRetrySucceeds_FakeApi(string mode)
    {
        var (rig, vm, undo) = await DeletedDonorAsync();
        if (mode == "cancel")
        {
            RestoreCancels(rig);
        }
        else
        {
            rig.Fake.On("RestoreDonorAsync", ApiResult<DonorDetail>.Fail(FeatureRig.Problem(mode == "waking" ? 503 : 0, mode == "waking" ? ProblemCodes.DatabaseWaking : ClientProblemCodes.Unreachable)));
        }

        rig.Clock.Advance(TimeSpan.FromSeconds(4));
        await undo.ActionCommand.ExecuteAsync();

        var retry = Assert.Single(rig.Toasts.Toasts, t => t.ActionLabel == "Retry undo");
        Assert.Contains("Donor 'Donor 001' is still deleted", retry.Message, StringComparison.Ordinal);

        rig.Fake.On("RestoreDonorAsync", ApiResult<DonorDetail>.Ok(Samples.Donor(1, "Donor 001")));
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        await retry.ActionCommand.ExecuteAsync();

        Assert.Equal(2, rig.Recorder.CountOf("RestoreDonorAsync"));
        Assert.Contains(rig.Toasts.Toasts, t => t.Message == "Donor 'Donor 001' restored.");
        Assert.DoesNotContain(rig.Toasts.Toasts, t => t.ActionLabel == "Retry undo");
        Assert.Equal(3, vm.Items.Count);
    }

    [Fact]
    public async Task RetryUndo_CancelledAgain_OffersRetryUndoAgain_WhileTheWindowIsOpen_FakeApi()
    {
        var (rig, _, undo) = await DeletedDonorAsync();
        RestoreCancels(rig);
        await undo.ActionCommand.ExecuteAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        await rig.Toasts.Toasts.Single(t => t.ActionLabel == "Retry undo").ActionCommand.ExecuteAsync();

        Assert.Equal(2, rig.Recorder.CountOf("RestoreDonorAsync"));
        Assert.Single(rig.Toasts.Toasts, t => t.ActionLabel == "Retry undo");
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("unreachable")]
    public async Task UndoCancelledOrFailed_AfterTheWindowClosed_SaysItCanNoLongerBeRestored_WithNoAction_FakeApi(string mode)
    {
        var (rig, _, undo) = await DeletedDonorAsync();
        rig.Fake.On<DonorDetail>("RestoreDonorAsync", ct =>
        {
            // The restore took until after the window closed (a waking server can wait up to 120 s).
            rig.Clock.Advance(UndoWindow.Duration + TimeSpan.FromSeconds(1));
            return mode == "cancel" ? Task.FromException<ApiResult<DonorDetail>>(new OperationCanceledException(ct)) : Task.FromResult(FakeApiClient.Fail<DonorDetail>(0, ClientProblemCodes.Unreachable));
        });

        await undo.ActionCommand.ExecuteAsync();

        var toast = Assert.Single(rig.Toasts.Toasts, t => t.Message.Contains("no longer be restored", StringComparison.Ordinal));
        Assert.False(toast.HasAction);
        Assert.Contains("donor 'Donor 001'", toast.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Toasts.Toasts, t => t.ActionLabel == "Retry undo");
    }

    [Fact]
    public async Task RetryUndo_PressedAfterTheWindowClosed_DoesNotCallTheServer_AndSaysSo_FakeApi()
    {
        var (rig, _, undo) = await DeletedDonorAsync();
        RestoreCancels(rig);
        await undo.ActionCommand.ExecuteAsync();
        var retry = rig.Toasts.Toasts.Single(t => t.ActionLabel == "Retry undo");
        rig.Toasts.Toasts.Clear(); // the toast timer would have removed it; keep the item to press it late
        rig.Clock.Advance(UndoWindow.Duration + TimeSpan.FromSeconds(1));

        await retry.ActionCommand.ExecuteAsync();

        Assert.Equal(1, rig.Recorder.CountOf("RestoreDonorAsync"));
        var toast = Assert.Single(rig.Toasts.Toasts);
        Assert.Contains("no longer be restored", toast.Message, StringComparison.Ordinal);
        Assert.False(toast.HasAction);
    }

    [Theory]
    [InlineData(404, ProblemCodes.NotFound)]
    [InlineData(409, ProblemCodes.Conflict)]
    public async Task RestoreTheServerRejects_ShowsNoRetry_AndA404SaysTheWindowClosed_FakeApi(int status, string code)
    {
        var (rig, _, undo) = await DeletedDonorAsync();
        rig.Fake.On("RestoreDonorAsync", ApiResult<DonorDetail>.Fail(FeatureRig.Problem(status, code)));

        await undo.ActionCommand.ExecuteAsync();

        Assert.DoesNotContain(rig.Toasts.Toasts, t => t.ActionLabel == "Retry undo");
        var toast = Assert.Single(rig.Toasts.Toasts, t => t.Kind == ToastKind.Error);
        Assert.False(toast.HasAction);
        Assert.Contains("Couldn't restore donor 'Donor 001'", toast.Message, StringComparison.Ordinal);
        if (status == 404)
        {
            Assert.Contains("no longer be restored", toast.Message, StringComparison.Ordinal);
        }
    }
}
