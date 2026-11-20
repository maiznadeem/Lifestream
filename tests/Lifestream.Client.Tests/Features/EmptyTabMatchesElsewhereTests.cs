using Lifestream.Client.Api;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Requests;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// A search lands on the tab that is open (Today on Appointments, Open on Requests). When that tab has no match and another has, the empty state must
/// not say "nothing matches, try a different search" (2.1.5 did, with "eastwood" finding five appointments on the other tabs): it names the tab and
/// what was looked for, says how many each other tab holds (the counts the tabs already show), and its action goes to the tab with the most. When no
/// tab has a match, or the counts are not known, the plain text stays.
/// </summary>
public sealed class EmptyTabMatchesElsewhereTests
{
    private static ApiResult<PagedResult<AppointmentListItem>> NoAppointments => ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage());

    private static ApiResult<PagedResult<RequestListItem>> NoRequests => ApiResult<PagedResult<RequestListItem>>.Ok(new([], 0, 1, 50));

    [Fact]
    public async Task Appointments_ASearchWithNothingToday_NamesTheOtherTabs_AndTheActionOpensTheFullest()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", NoAppointments);
        rig.Fake.On("GetAppointmentViewCountsAsync", ApiResult<AppointmentViewCounts>.Ok(new AppointmentViewCounts(0, 1, 3, 1)));
        var vm = rig.Appointments();
        await vm.LoadAsync();
        vm.SearchText = "eastwood";
        await vm.SearchNowCommand.ExecuteAsync();

        Assert.True(vm.IsEmpty);
        Assert.Equal("No appointments today match “eastwood”", vm.EmptyTitle);
        Assert.Equal("3 in Past, 1 in Upcoming, 1 in Cancelled.", vm.EmptyMessage); // most matches first, the tabs' order among equals
        Assert.Equal("Show Past", vm.EmptyActionText);

        await ((Lifestream.Client.Common.AsyncRelayCommand)vm.EmptyActionCommand!).ExecuteAsync();
        await vm.PendingRefresh;
        Assert.True(vm.IsPastView);
        Assert.Equal(AppointmentView.Past, rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync").View);
        Assert.Equal("eastwood", rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync").Search); // the search is kept
    }

    [Fact]
    public async Task Appointments_AFilterWithoutASearch_SaysTheseFilters_AndEveryTabIsNamedByItsOwnTitle()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", NoAppointments);
        rig.Fake.On("GetAppointmentViewCountsAsync", ApiResult<AppointmentViewCounts>.Ok(new AppointmentViewCounts(2, 0, 0, 0)));
        var vm = rig.Appointments();
        await vm.LoadAsync();
        vm.DonorFilter = new RecordOption(41, "Selim Eastwood", null);
        await vm.PendingRefresh;
        vm.IsUpcomingView = true;
        await vm.PendingRefresh;

        Assert.Equal("No upcoming appointments match these filters", vm.EmptyTitle);
        Assert.Equal("2 in Today.", vm.EmptyMessage);
        Assert.Equal("Show Today", vm.EmptyActionText);

        vm.IsPastView = true;
        await vm.PendingRefresh;
        Assert.Equal("No past appointments match these filters", vm.EmptyTitle);
        vm.IsCancelledView = true;
        await vm.PendingRefresh;
        Assert.Equal("No cancelled appointments match these filters", vm.EmptyTitle);
    }

    [Fact]
    public async Task Appointments_NoTabHasAMatch_OrTheCountsAreUnknown_KeepsThePlainText()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", NoAppointments);
        rig.Fake.On("GetAppointmentViewCountsAsync", ApiResult<AppointmentViewCounts>.Ok(new AppointmentViewCounts(0, 0, 0, 0)));
        var vm = rig.Appointments();
        await vm.LoadAsync();
        Assert.Equal("No appointments today", vm.EmptyTitle); // nothing filtered: the tab's own empty text, whatever the other tabs hold

        vm.SearchText = "zzzz";
        await vm.SearchNowCommand.ExecuteAsync();
        Assert.Equal("No appointments match these filters", vm.EmptyTitle);
        Assert.Equal("Try a different search, or clear the filters to see everyone.", vm.EmptyMessage);
        Assert.Equal("Clear filters", vm.EmptyActionText);
        Assert.Same(vm.ClearFiltersCommand, vm.EmptyActionCommand);

        var unknown = new Part2Rig();
        unknown.Fake.On("GetAppointmentsAsync", NoAppointments);
        unknown.Fake.On("GetAppointmentViewCountsAsync", Lifestream.Client.Tests.Fakes.FakeApiClient.Unreachable<AppointmentViewCounts>());
        var blind = unknown.Appointments();
        await blind.LoadAsync();
        blind.SearchText = "eastwood";
        await blind.SearchNowCommand.ExecuteAsync();
        Assert.Null(blind.ViewCounts);
        Assert.Equal("No appointments match these filters", blind.EmptyTitle);
        Assert.Equal("Clear filters", blind.EmptyActionText);
    }

    [Fact]
    public async Task Appointments_WithoutAFilter_AnEmptyTabDoesNotPointElsewhere()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", NoAppointments);
        rig.Fake.On("GetAppointmentViewCountsAsync", ApiResult<AppointmentViewCounts>.Ok(new AppointmentViewCounts(0, 9, 20, 1)));
        var vm = rig.Appointments();
        await vm.LoadAsync();

        Assert.Equal("No appointments today", vm.EmptyTitle);
        Assert.Equal("Nobody is booked in for today.", vm.EmptyMessage);
    }

    [Fact]
    public async Task Requests_ASearchWithNothingOpen_NamesTheOtherTabs_WithoutCountingDueSoonTwice()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", NoRequests);
        rig.Fake.On("GetRequestViewCountsAsync", ApiResult<RequestViewCounts>.Ok(new RequestViewCounts(0, 0, 3, 1)));
        var vm = rig.Requests();
        await vm.LoadAsync();
        vm.SearchText = "wojcik";
        await vm.SearchNowCommand.ExecuteAsync();

        Assert.True(vm.IsEmpty);
        Assert.Equal("No open requests match “wojcik”", vm.EmptyTitle);
        Assert.Equal("3 in Fulfilled, 1 in Cancelled.", vm.EmptyMessage);
        Assert.Equal("Show Fulfilled", vm.EmptyActionText);

        await ((Lifestream.Client.Common.AsyncRelayCommand)vm.EmptyActionCommand!).ExecuteAsync();
        await vm.PendingRefresh;
        Assert.True(vm.IsFulfilledView);
        Assert.Equal("wojcik", rig.Recorder.LastArg<RequestQuery>("GetRequestsAsync").Search);
    }

    [Fact]
    public async Task Requests_OnAClosedTab_TheOpenOnesAreNamedOnce_AndNoMatchAnywhereStaysPlain()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", NoRequests);
        rig.Fake.On("GetRequestViewCountsAsync", ApiResult<RequestViewCounts>.Ok(new RequestViewCounts(2, 2, 0, 0))); // both open requests are due in 48 hours
        var vm = rig.Requests();
        await vm.LoadAsync();
        vm.SearchText = "wojcik";
        await vm.SearchNowCommand.ExecuteAsync();
        vm.IsCancelledView = true;
        await vm.PendingRefresh;

        Assert.Equal("No cancelled requests match “wojcik”", vm.EmptyTitle);
        Assert.Equal("2 in Open.", vm.EmptyMessage); // not "2 in Open, 2 in Due in 48 h": the same two requests
        vm.IsFulfilledView = true;
        await vm.PendingRefresh;
        Assert.Equal("No fulfilled requests match “wojcik”", vm.EmptyTitle);
        vm.IsDueSoonView = true;
        await vm.PendingRefresh;
        Assert.Equal("No requests due in 48 hours match “wojcik”", vm.EmptyTitle);

        rig.Fake.On("GetRequestViewCountsAsync", ApiResult<RequestViewCounts>.Ok(new RequestViewCounts(0, 0, 0, 0)));
        vm.SearchText = "zzzz";
        await vm.SearchNowCommand.ExecuteAsync();
        Assert.Equal("No requests match these filters", vm.EmptyTitle);
        Assert.Equal("Try a different search, or clear the filters to see everyone.", vm.EmptyMessage);
        Assert.Equal("Clear filters", vm.EmptyActionText);
    }
}
