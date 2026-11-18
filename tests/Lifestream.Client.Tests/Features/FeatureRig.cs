using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Stock;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;

namespace Lifestream.Client.Tests.Features;

/// <summary>The feature view models wired to the fake API (recording the arguments), a manual clock, fake dialogs and a real toast service.</summary>
public sealed class FeatureRig
{
    public FeatureRig()
    {
        Fake = new FakeApiClient();
        (Api, Recorder) = RecordingApi.Wrap(Fake);
        Clock = new ManualTimeProvider();
        Ui = new InlineUiDispatcher();
        Toasts = new ToastService(Ui, Clock);
        Dialogs = new FakeDialogs();
        States = new ListViewStateStore();
    }

    public FakeApiClient Fake { get; }

    public ILifestreamApiClient Api { get; }

    public RecordingApi Recorder { get; }

    public ManualTimeProvider Clock { get; }

    public InlineUiDispatcher Ui { get; }

    public ToastService Toasts { get; }

    public FakeDialogs Dialogs { get; }

    public ListViewStateStore States { get; }

    /// <summary>The local date on the manual clock, which is what the forms use as "today".</summary>
    public DateOnly Today => DateOnly.FromDateTime(Clock.GetLocalNow().DateTime);

    public DonorsViewModel Donors()
    {
        var vm = new DonorsViewModel(Api, States, Toasts, Dialogs, Ui);
        vm.Attach(null, Clock);
        return vm;
    }

    public PatientsViewModel Patients()
    {
        var vm = new PatientsViewModel(Api, States, Toasts, Dialogs, Ui);
        vm.Attach(null, Clock);
        return vm;
    }

    public StockViewModel Stock()
    {
        var vm = new StockViewModel(Api, States);
        vm.Attach(null, Clock);
        return vm;
    }

    public DashboardViewModel Dashboard(FakeNavigation? navigation = null)
    {
        var vm = new DashboardViewModel(Api, navigation ?? new FakeNavigation());
        vm.Attach(null, Clock);
        return vm;
    }

    public DonorEditorViewModel NewDonorForm() => DonorEditorViewModel.ForNew(Api, Toasts, Dialogs, Clock);

    public DonorEditorViewModel EditDonorForm(DonorDetail? donor = null) => DonorEditorViewModel.ForEdit(Api, Toasts, Dialogs, donor ?? Samples.Donor(), Clock);

    public PatientEditorViewModel NewPatientForm() => PatientEditorViewModel.ForNew(Api, Toasts, Dialogs, Clock);

    public PatientEditorViewModel EditPatientForm(PatientDetail? patient = null) => PatientEditorViewModel.ForEdit(Api, Toasts, Dialogs, patient ?? Samples.Patient(), Clock);

    // Scripted answers (the fake API; nothing here talks to a server).

    public void DonorsReturn(Func<DonorQuery, PagedResult<DonorListItem>> page) =>
        Fake.On("GetDonorsAsync", _ => Task.FromResult(ApiResult<PagedResult<DonorListItem>>.Ok(page(Recorder.LastArg<DonorQuery>("GetDonorsAsync")))));

    public void PatientsReturn(Func<PatientQuery, PagedResult<PatientListItem>> page) =>
        Fake.On("GetPatientsAsync", _ => Task.FromResult(ApiResult<PagedResult<PatientListItem>>.Ok(page(Recorder.LastArg<PatientQuery>("GetPatientsAsync")))));

    public static ApiProblem Problem(int status, string code, string title = "Failed", string? detail = null) => new(status, code, title, "corr-1", detail);
}
