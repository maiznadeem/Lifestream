using Lifestream.Client.Api;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// FEAT-14 and FEAT-04, against the fake API client: a link from another screen opens the referenced RECORD by its id (selected in the list, its detail shown), never a
/// search by name; "Book appointment" on a donor and "New request" on a patient open the real editors with that person already chosen.
/// </summary>
public sealed class RecordLinkTests
{
    // ---- the links send an id ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Appointments_DonorLink_OpensTheDonorById_NotByName_FakeApi()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(Samples2.AppointmentRow(5, 42, "Same Name"))));
        var vm = rig.Appointments();
        await vm.LoadAsync();

        await vm.OpenDonorCommand.ExecuteAsync(vm.Items[0]);

        var (type, parameter) = Assert.Single(rig.Navigation.Visits);
        Assert.Equal(typeof(DonorsViewModel), type);
        var prefilter = Assert.IsType<ListPrefilter>(parameter);
        Assert.Equal(42, prefilter.RecordId); // the donor's id, not the appointment's (5)
        Assert.Null(prefilter.Search); // and no name search
    }

    [Fact]
    public async Task Requests_PatientLink_OpensThePatientById_NotByName_FakeApi()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(7, 3, "Same Name")], 1, 1, 50)));
        var vm = rig.Requests();
        await vm.LoadAsync();

        await vm.OpenPatientCommand.ExecuteAsync(vm.Items[0]);

        var (type, parameter) = Assert.Single(rig.Navigation.Visits);
        Assert.Equal(typeof(PatientsViewModel), type);
        Assert.Equal(3, ((ListPrefilter)parameter!).RecordId);
        Assert.Null(((ListPrefilter)parameter).Search);
    }

    [Fact]
    public async Task Receipts_SourceLink_OpensTheAppointmentOrRequestById_FakeApi()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([Samples2.ReceiptRow(90), Samples2.ReceiptRow(91, ReceiptKind.Issue)], 2, 1, 50)));
        var vm = rig.Receipts();
        await vm.LoadAsync();

        await vm.OpenSourceCommand.ExecuteAsync(vm.Items[0]); // a donation: appointment 5
        await vm.OpenSourceCommand.ExecuteAsync(vm.Items[1]); // an issue: request 7

        Assert.Equal(typeof(AppointmentsViewModel), rig.Navigation.Visits[0].ViewModel);
        Assert.Equal(5, ((ListPrefilter)rig.Navigation.Visits[0].Parameter!).RecordId);
        Assert.Equal(typeof(RequestsViewModel), rig.Navigation.Visits[1].ViewModel);
        Assert.Equal(7, ((ListPrefilter)rig.Navigation.Visits[1].Parameter!).RecordId);
    }

    [Fact]
    public async Task Receipts_PersonLink_OpensTheDonorOrPatientById_ThroughTheirAppointmentOrRequest_FakeApi()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([Samples2.ReceiptRow(90), Samples2.ReceiptRow(91, ReceiptKind.Issue)], 2, 1, 50)));
        rig.Fake.On("GetAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment(5, donorId: 42)));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7, patientId: 3)));
        var vm = rig.Receipts();
        await vm.LoadAsync();

        await vm.OpenPartyCommand.ExecuteAsync(vm.Items[0]);
        await vm.OpenPartyCommand.ExecuteAsync(vm.Items[1]);

        Assert.Equal(typeof(DonorsViewModel), rig.Navigation.Visits[0].ViewModel);
        Assert.Equal(42, ((ListPrefilter)rig.Navigation.Visits[0].Parameter!).RecordId);
        Assert.Equal(typeof(PatientsViewModel), rig.Navigation.Visits[1].ViewModel);
        Assert.Equal(3, ((ListPrefilter)rig.Navigation.Visits[1].Parameter!).RecordId);
    }

    [Fact]
    public async Task Receipts_PersonLink_WhenTheRecordCannotBeFetched_SaysSo_AndOpensNothing_FakeApi()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([Samples2.ReceiptRow(90)], 1, 1, 50)));
        rig.Fake.On("GetAppointmentAsync", FakeApiClient.Fail<AppointmentDetail>(404, ProblemCodes.NotFound));
        var vm = rig.Receipts();
        await vm.LoadAsync();

        await vm.OpenPartyCommand.ExecuteAsync(vm.Items[0]);

        Assert.Empty(rig.Navigation.Visits); // never a name search in its place
        Assert.Contains(rig.Toasts.Toasts, t => t.Kind == Lifestream.Client.Common.ToastKind.Error && t.Message.StartsWith("Couldn't open", StringComparison.Ordinal));
    }

    // ---- the target screen selects the record by id --------------------------------------------------------------------

    [Fact]
    public async Task Donors_OpenedById_SelectsThatRecordAndShowsItsDetail_EvenWithTwoPeopleOfTheSameName_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => new PagedResult<DonorListItem>([Samples.DonorRow(5, "Same Name"), Samples.DonorRow(6, "Same Name"), Samples.DonorRow(7)], 3, 1, 50));
        rig.Fake.On("GetDonorAsync", _ => Task.FromResult(ApiResult<DonorDetail>.Ok(Samples.Donor(rig.Recorder.LastArg<int>("GetDonorAsync"), "Same Name"))));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new(true, [], null)));
        var vm = rig.Donors();

        vm.ReceiveParameter(new ListPrefilter(RecordId: 6));
        await vm.LoadAsync();
        await vm.PendingDetail;

        Assert.Equal(6, vm.SelectedItem!.Id); // the second "Same Name", not the first
        Assert.Equal(6, vm.Detail!.Id);
        Assert.Equal(6, rig.Recorder.LastArg<int>("GetDonorAsync"));
        Assert.True(string.IsNullOrEmpty(vm.SearchText)); // not a name search
        Assert.Null(rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync").Search);
        Assert.Equal(3, vm.Items.Count);
    }

    [Fact]
    public async Task Donors_OpenedById_WhenTheRecordIsNotOnTheLoadedPage_StillShowsItsDetail_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples.Donor(900, "Far Away")));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new(true, [], null)));
        var vm = rig.Donors();

        vm.ReceiveParameter(new ListPrefilter(RecordId: 900));
        await vm.LoadAsync();
        await vm.PendingDetail;

        Assert.Null(vm.SelectedItem); // not in this page
        Assert.Equal(900, vm.Detail!.Id); // the list's reload does not throw the opened record away
        Assert.Equal("Far Away", vm.DetailTitle);

        vm.SelectedItem = vm.Items[0]; // the user picks another: the pane follows them
        await vm.PendingDetail;
        Assert.Equal(1, rig.Recorder.LastArg<int>("GetDonorAsync")); // the first row's own id was fetched
    }

    // ---- related actions on a record -----------------------------------------------------------------------------------

    [Fact]
    public async Task Donors_BookAppointment_OpensTheBookingFormWithThatDonor_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples.Donor(2, "Donor 002")));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new(true, [], null)));
        var vm = rig.Donors();
        await vm.LoadAsync();
        Assert.False(vm.BookAppointmentCommand.CanExecute(null)); // nothing selected

        vm.SelectedItem = vm.Items[1];
        await vm.PendingDetail;
        Assert.True(vm.BookAppointmentCommand.CanExecute(null));
        await vm.BookAppointmentCommand.ExecuteAsync();

        var (type, parameter) = Assert.Single(rig.Navigation.Visits);
        Assert.Equal(typeof(AppointmentsViewModel), type);
        Assert.Equal(new RelatedRecord(RecordKind.Donor, 2, "Donor 002"), parameter);
    }

    [Fact]
    public async Task Patients_NewRequest_OpensTheRequestFormWithThatPatient_FakeApi()
    {
        var rig = new FeatureRig();
        rig.PatientsReturn(_ => Samples.PatientPage(3));
        rig.Fake.On("GetPatientAsync", ApiResult<PatientDetail>.Ok(Samples.Patient(2, "Patient 002")));
        var vm = rig.Patients();
        await vm.LoadAsync();
        Assert.False(vm.NewRequestCommand.CanExecute(null));

        vm.SelectedItem = vm.Items[1];
        await vm.PendingDetail;
        await vm.NewRequestCommand.ExecuteAsync();

        var (type, parameter) = Assert.Single(rig.Navigation.Visits);
        Assert.Equal(typeof(RequestsViewModel), type);
        Assert.Equal(new RelatedRecord(RecordKind.Patient, 2, "Patient 002"), parameter);
    }

    [Fact]
    public async Task Requests_ReceivingAPatient_OpensTheRequestFormPrefilled_AndTheDashboardStatusOpensTheRealList_FakeApi()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(7, 3, "Omar Haddad")], 1, 1, 50)));
        var related = rig.Requests();
        ((Lifestream.Client.Common.INavigationParameterReceiver)related).ReceiveParameter(new RelatedRecord(RecordKind.Patient, 3, "Omar Haddad"));
        await related.LoadAsync();
        var editor = Assert.IsType<RequestEditorViewModel>(related.Editor);
        Assert.Equal(3, editor.PatientOption!.Id);
        Assert.Equal("Omar Haddad", editor.PatientName);

        var pending = rig.Requests(); // the dashboard's "Pending requests" card
        pending.ReceiveParameter(new ListPrefilter(Status: "Pending"));
        await pending.LoadAsync();
        Assert.Equal(RequestStatus.Pending, rig.Recorder.LastArg<RequestQuery>("GetRequestsAsync").Status);
        Assert.Equal(RequestStatus.Pending, pending.StatusFilter.Value);
    }
}
