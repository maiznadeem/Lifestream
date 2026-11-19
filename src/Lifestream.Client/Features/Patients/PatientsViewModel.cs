using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Navigation;
using Lifestream.Contracts;
using Lifestream.Contracts.Patients;
using Lifestream.Domain;
using ContractSortKeys = Lifestream.Contracts.SortKeys;

namespace Lifestream.Client.Features.Patients;

/// <summary>The Patients screen: a paged, filterable list with the selected patient's detail beside it and the editor in a drawer (FEAT-01 to FEAT-08).</summary>
public sealed class PatientsViewModel : SortedRecordListViewModel<PatientListItem, PatientDetail>
{
    private readonly ILifestreamApiClient api;
    private readonly INavigationService navigation;

    public PatientsViewModel(ILifestreamApiClient api, IListViewStateStore states, IToastService toasts, IDialogService dialogs, INavigationService navigation, IUiDispatcher ui)
        : base(states, "patients", toasts, dialogs, ui)
    {
        this.api = api;
        this.navigation = navigation;
        NewRequestCommand = Command(
            _ => Detail is { } patient ? this.navigation.NavigateTo<RequestsViewModel>(new RelatedRecord(RecordKind.Patient, patient.Id, patient.Name)) : Task.CompletedTask,
            () => Detail is not null && !IsEditing);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Detail) or nameof(Editor))
            {
                NewRequestCommand.RaiseCanExecuteChanged();
            }
        };
    }

    /// <summary>"New request" for the selected patient: opens the request form on the Requests screen with this patient already chosen (FEAT-04).</summary>
    public AsyncRelayCommand NewRequestCommand { get; }

    public override string PageTitle => "Patients";

    protected override string PluralName => "patients";

    protected override string RecordType => "patient";

    protected override bool RecordIsPerson => true;

    /// <summary>The columns that sort, as the API's validator names them (defined once in the contracts).</summary>
    public override IReadOnlyList<string> SortKeys { get; } = ContractSortKeys.Patients;

    protected override Task<ApiResult<PagedResult<PatientListItem>>> FetchSortedAsync(ListRequest request, CancellationToken ct) =>
        api.GetPatientsAsync(new PatientQuery(request.Search, BloodTypeValue, request.Page, request.PageSize, request.Sort), ct);

    protected override int IdOf(PatientListItem item) => item.Id;

    protected override string NameOf(PatientListItem item) => item.Name;

    protected override int IdOfDetail(PatientDetail detail) => detail.Id;

    protected override string NameOfDetail(PatientDetail detail) => detail.Name;

    protected override Task<ApiResult<PatientDetail>> GetDetailAsync(int id, CancellationToken ct) => api.GetPatientAsync(id, ct);

    protected override IReadOnlyList<DetailFact> FactsOf(PatientDetail patient) =>
    [
        new("Date of birth", $"{Formatting.Date(patient.DateOfBirth)} (age {Eligibility.AgeOn(patient.DateOfBirth, DateOnly.FromDateTime(Clock.GetLocalNow().DateTime))})"),
        new("Gender", patient.Gender.ToString()),
        new("Blood type", patient.BloodType.ToDisplay()),
        new("Phone", string.IsNullOrEmpty(patient.Phone) ? "Not given" : patient.Phone),
        new("Address", string.IsNullOrEmpty(patient.Address) ? "Not given" : patient.Address),
    ];

    protected override Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct) => api.DeletePatientAsync(id, ct);

    protected override Task<ApiResult<PatientDetail>> RestoreAsync(PatientDetail d, CancellationToken ct) =>
        api.RestorePatientAsync(d.Id, ct);

    protected override RecordEditorViewModel CreateEditor(PatientDetail? existing) =>
        existing is null
            ? PatientEditorViewModel.ForNew(api, Toasts, Dialogs, Clock)
            : PatientEditorViewModel.ForEdit(api, Toasts, Dialogs, existing, Clock);
}
