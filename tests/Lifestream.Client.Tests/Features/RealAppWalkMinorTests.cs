using Lifestream.Client.Api;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Requests;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Staff;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// The minor defects of the real-app walk of 2.1.0 on the list screens, at the level of the view models (run/agents/real-app-walk.md): 10 (a record opened from
/// another screen left no tab selected and its row out of the list), 20 (after a save the list stayed on a tab that does not hold the record), 22 (toasts named an appointment or request after the person), 24 (a receipt that was only opened said
/// "Duplicate copy printed") and 25 (the Access list promised a Staff page the role does not have).
/// </summary>
public sealed class RealAppWalkMinorTests
{
    private static ApiResult<PagedResult<T>> Page<T>(params T[] items) => ApiResult<PagedResult<T>>.Ok(new([.. items], items.Length, 1, 50));

    // ---- 24 ----

    [Fact]
    public async Task Receipt_OpenedAgainLater_ShowsNoDuplicateLine_AndTheCopyThatIsPrintedSaysWhenItWasPrinted()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(90)));
        var viewer = new ReceiptViewerViewModel(rig.Api, rig.Printer, 90, isDuplicate: true);
        viewer.Attach(null, rig.Clock);
        await viewer.LoadAsync();

        Assert.NotNull(viewer.Document);
        Assert.Null(viewer.Document!.CopyNotice); // released 2.1.0: "Duplicate copy printed <now>" on a receipt that was only opened
        Assert.Empty(rig.Printer.Printed);

        var opened = rig.Clock.GetUtcNow();
        rig.Clock.Advance(TimeSpan.FromMinutes(7));
        await viewer.PrintCommand.ExecuteAsync();

        var printed = Assert.Single(rig.Printer.Printed);
        Assert.Equal("Duplicate copy printed " + RecordFormatting.When(opened.UtcDateTime.AddMinutes(7), null), printed.CopyNotice); // the time of the print, not of the opening
        Assert.Null(viewer.Document.CopyNotice); // what is on screen is still the receipt, not the copy
    }

    [Fact]
    public async Task Receipt_ShownRightAfterItWasIssued_PrintsWithoutTheDuplicateLine()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(90)));
        var viewer = new ReceiptViewerViewModel(rig.Api, rig.Printer, 90, isDuplicate: false);
        viewer.Attach(null, rig.Clock);
        await viewer.LoadAsync();
        await viewer.PrintCommand.ExecuteAsync();

        Assert.Null(Assert.Single(rig.Printer.Printed).CopyNotice);
    }

    // ---- 25 ----

    [Theory]
    [InlineData(Role.Staff, "No access to staff accounts (administrators only)")] // released 2.1.0: "Staff list, view only", but the role has no Staff page at all
    [InlineData(Role.Admin, "Staff accounts: add, change and reset passwords")]
    public async Task Staff_TheAccessList_SaysWhatTheRoleCanDoWithStaffAccounts(Role role, string expected)
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetStaffAsync", ApiResult<PagedResult<StaffListItem>>.Ok(Samples2.StaffPage(Samples2.StaffRow(2, role: role))));
        rig.Fake.On("GetStaffMemberAsync", ApiResult<StaffDetail>.Ok(Samples2.Staff(2, role: role)));
        var vm = rig.Staff();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.Equal(expected, vm.DetailStaffAccessText);
    }

    // ---- 10 and 20 ----

    private static Part2Rig RequestRig(RequestStatus status)
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", Page(Samples2.RequestRow(7, 3, "Omar Haddad", status: status)));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7, status: status)));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 8, 0, 10, true)]));
        return rig;
    }

    [Fact]
    public async Task Requests_ALinkToAnOpenRequest_KeepsTheOpenTabSelected_SelectsItsRow_AndAsksTheListToShowIt()
    {
        var rig = RequestRig(RequestStatus.Pending);
        var vm = rig.Requests();
        var reveals = 0;
        vm.RevealSelectionRequested += (_, _) => reveals++;
        vm.ReceiveParameter(new ListPrefilter(RecordId: 7));
        await vm.LoadAsync();
        await vm.PendingDetail;

        Assert.True(vm.IsOpenView); // released 2.1.0: no tab selected, and every request of every status in the list
        Assert.All(rig.Recorder.ArgsOf<RequestQuery>("GetRequestsAsync"), q => Assert.Equal(RequestView.Open, q.View));
        Assert.Equal(1, rig.Recorder.CountOf("GetRequestsAsync")); // the tab already holds it: the list is read once
        Assert.Equal(7, vm.SelectedItem?.Id);
        Assert.Equal(7, vm.Detail?.Id);
        Assert.Equal(1, reveals);
    }

    [Fact]
    public async Task Requests_ALinkToAFulfilledRequest_MovesToTheFulfilledTab_AndTheNextVisitOpensOnTheUsersOwnTab()
    {
        var rig = RequestRig(RequestStatus.Fulfilled);
        var vm = rig.Requests();
        vm.ReceiveParameter(new ListPrefilter(RecordId: 7));
        await vm.LoadAsync();
        await vm.PendingDetail;
        await vm.PendingRefresh;

        Assert.True(vm.IsFulfilledView);
        Assert.Equal(RequestView.Fulfilled, rig.Recorder.LastArg<RequestQuery>("GetRequestsAsync").View);
        Assert.Equal(7, vm.SelectedItem?.Id);

        // The user's own choice of tab is what is remembered: a link does not change it.
        var next = rig.Requests();
        await next.LoadAsync();
        Assert.True(next.IsOpenView);
    }

    [Fact]
    public async Task Requests_ARequestAddedFromAnotherTab_MovesToTheOpenTab_WhereItIs_AndTheToastSaysWhoseRequest()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", Page<RequestListItem>());
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([]));
        var vm = rig.Requests();
        await vm.LoadAsync();
        vm.IsFulfilledView = true;
        await vm.PendingRefresh;

        vm.AddCommand.Execute(null);
        var form = Assert.IsType<RequestEditorViewModel>(vm.Editor);
        form.PatientOption = new RecordOption(3, "Omar Haddad", "A−");
        form.UnitsText = "3";
        form.RequiredBy = rig.Today.AddDays(3);
        rig.Fake.On("CreateRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7)));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7)));
        rig.Fake.On("GetRequestsAsync", Page(Samples2.RequestRow(7, 3, "Omar Haddad")));
        var reveals = 0;
        vm.RevealSelectionRequested += (_, _) => reveals++;
        await form.SaveCommand.ExecuteAsync();
        await vm.PendingRefresh;

        Assert.True(vm.IsOpenView); // released 2.1.0: still on Fulfilled, "No fulfilled requests yet", the new request nowhere to be seen
        Assert.Equal(RequestView.Open, rig.Recorder.LastArg<RequestQuery>("GetRequestsAsync").View);
        Assert.Equal(7, vm.SelectedItem?.Id);
        Assert.Equal(1, reveals);
        Assert.Contains(rig.Toasts.Toasts, t => t.Message == "Request for 'Omar Haddad' added."); // released 2.1.0: "Request 'Omar Haddad' added."
    }

    [Fact]
    public async Task Requests_CancellingARequest_SaysWhoseRequestWasCancelled()
    {
        var rig = RequestRig(RequestStatus.Pending);
        var vm = rig.Requests();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        await vm.CancelRequestCommand.ExecuteAsync();

        Assert.Contains(rig.Toasts.Toasts, t => t.Message == "Request for 'Omar Haddad' cancelled."); // released 2.1.0: "Request 'Omar Haddad' cancelled."
    }

    [Fact]
    public async Task Appointments_ALinkToAnAppointmentOnALaterDay_MovesToTheUpcomingTab_AndSelectsItsRow()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(Samples2.AppointmentRow(5, 42))));
        rig.Fake.On("GetAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment())); // 10 Mar 2027: after the rig's today
        var vm = rig.Appointments();
        vm.ReceiveParameter(new ListPrefilter(RecordId: 5));
        await vm.LoadAsync();
        await vm.PendingDetail;
        await vm.PendingRefresh;

        Assert.True(vm.IsUpcomingView); // released 2.1.0: no tab selected
        Assert.Equal(AppointmentView.Upcoming, rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync").View);
        Assert.Equal(5, vm.SelectedItem?.Id);
    }
}
