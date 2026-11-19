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

            if (e.PropertyName is nameof(Detail))
            {
                OnPropertyChanged(nameof(DetailCode));
                OnPropertyChanged(nameof(DetailSubtitle));
                OnPropertyChanged(nameof(DetailBloodGroup));
                OnPropertyChanged(nameof(DetailInitials));
            }

            if (e.PropertyName is nameof(Detail) or nameof(SelectedItem))
            {
                OnPropertyChanged(nameof(OpenRequests));
                OnPropertyChanged(nameof(OpenRequestsText));
            }

            if (e.PropertyName is nameof(DetailFacts))
            {
                OnPropertyChanged(nameof(PanelFacts));
            }
        };
    }

    /// <summary>"New request" for the selected patient: opens the request form on the Requests screen with this patient already chosen (FEAT-04).</summary>
    public AsyncRelayCommand NewRequestCommand { get; }

    public override string PageTitle => "Patients";

    /// <summary>The patient's code as the screens show it, "P-0051".</summary>
    public static string Code(int id) => $"P-{id:0000}";

    /// <summary>The side panel's overline, "Patient P-0051".</summary>
    public string? DetailCode => Detail is { } p ? "Patient " + Code(p.Id) : null;

    /// <summary>The line under the patient's name in the side panel: "Female, age 42".</summary>
    public string? DetailSubtitle => Detail is { } p ? $"{p.Gender}, age {Eligibility.AgeOn(p.DateOfBirth, DateOnly.FromDateTime(Clock.GetLocalNow().DateTime))}" : null;

    /// <summary>The patient's blood group with a true minus sign, for the large tag in the side panel.</summary>
    public string? DetailBloodGroup => Detail?.BloodType.ToTypographic();

    public string? DetailInitials => Detail is { } p ? Donors.DonorsViewModel.PersonInitials(p.Name) : null;

    /// <summary>The selected patient's requests still waiting for blood (from the list row), or null when not known.</summary>
    public int? OpenRequests => Detail is { } p && SelectedItem is { } row && row.Id == p.Id ? row.PendingRequests : null;

    /// <summary>"2 requests waiting for blood", "No requests waiting", for the card in the side panel.</summary>
    public string? OpenRequestsText => OpenRequests switch
    {
        null => null,
        0 => "No requests waiting",
        1 => "1 request waiting for blood",
        var n => $"{n} requests waiting for blood",
    };

    /// <summary>The facts under "Details" in the side panel, without the blood group (it is in the header).</summary>
    public IReadOnlyList<DetailFact> PanelFacts => [.. DetailFacts.Where(f => f.Label != "Blood type")];

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
