using Lifestream.Client.Api;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;

namespace Lifestream.Client.Tests.Features;

/// <summary>The side panel always shows the selected row's record (design-qa-2 N2): a detail load replaced by a newer selection never hands its record to the newer one.</summary>
public sealed class DetailSelectionTests
{
    [Fact]
    public async Task Detail_AReplacedLoadWhoseAnswerArrivesLate_NeverShowsItsRecordForTheNewerSelection()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(new([Samples.DonorRow(1, "First Donor"), Samples.DonorRow(2, "Second Donor")], 2, 1, 50)));
        var first = new TaskCompletionSource<ApiResult<DonorDetail>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<ApiResult<DonorDetail>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answers = new Queue<TaskCompletionSource<ApiResult<DonorDetail>>>([first, second]);
        // The answers ignore the token: a response already on its way when the user picks another row still arrives.
        rig.Fake.On<DonorDetail>("GetDonorAsync", _ =>
        {
            lock (answers)
            {
                return answers.Dequeue().Task;
            }
        });
        var extras = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Fake.On<DonorEligibility>("GetDonorEligibilityAsync", async _ =>
        {
            await extras.Task;
            return ApiResult<DonorEligibility>.Ok(new DonorEligibility(true, [], null));
        });
        rig.Fake.On("GetDonorDonationsAsync", ApiResult<DonorDonations>.Ok(new DonorDonations(0, 0, [])));
        var vm = rig.Donors();
        await vm.LoadAsync();

        vm.SelectedItem = vm.Items[0];
        var firstLoad = vm.PendingDetail;
        await Until(() => rig.Fake.CountOf("GetDonorAsync") == 1); // the first row's call is out (so it gets the first answer)
        vm.SelectedItem = vm.Items[1];
        var secondLoad = vm.PendingDetail;
        await Until(() => rig.Fake.CountOf("GetDonorAsync") == 2);

        // The newer load gets its record first and goes on to the panel's other sections; then the replaced load's answer arrives.
        second.SetResult(ApiResult<DonorDetail>.Ok(Samples.Donor(2, "Second Donor")));
        await Until(() => rig.Fake.CountOf("GetDonorEligibilityAsync") >= 1);
        first.SetResult(ApiResult<DonorDetail>.Ok(Samples.Donor(1, "First Donor")));
        await firstLoad;
        extras.SetResult();
        await secondLoad;

        Assert.Equal(2, vm.SelectedItem!.Id);
        Assert.Equal(2, vm.Detail?.Id);
        Assert.Equal("Second Donor", vm.DetailTitle);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "the calls did not happen");
    }
}
