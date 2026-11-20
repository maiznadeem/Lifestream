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
    private (int PatientId, IReadOnlyList<Contracts.Requests.RequestListItem>? Requests, int Total, IReadOnlyList<Contracts.Stock.StockItem>? Stock)? _fetchedWaiting;
    private (int PatientId, int? Requests)? _fetchedRequests;

    public PatientsViewModel(ILifestreamApiClient api, IListViewStateStore states, IToastService toasts, IDialogService dialogs, INavigationService navigation, IUiDispatcher ui)
        : base(states, "patients", toasts, dialogs, ui)
    {
        this.api = api;
        this.navigation = navigation;
        NewRequestCommand = Command(
            _ => Detail is { } patient ? this.navigation.NavigateTo<RequestsViewModel>(new RelatedRecord(RecordKind.Patient, patient.Id, patient.Name)) : Task.CompletedTask,
            () => Detail is not null && !IsEditing);
        FulfilRequestCommand = Command(
            (parameter, _) => parameter is PatientRequestRow { CanFulfil: true } row && Detail is { } patient
                ? this.navigation.NavigateTo<RequestsViewModel>(new RelatedRecord(RecordKind.Request, row.Id, patient.Name))
                : Task.CompletedTask,
            parameter => parameter is not PatientRequestRow { CanFulfil: false }); // no stock at all: nothing to fulfil with (the card says why)
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
                ShowWaiting();
            }
        };
    }

    /// <summary>"Fulfil" on one of the patient's waiting requests: opens that request on the Requests screen with its fulfil step (the bags to issue) in the panel.</summary>
    public AsyncRelayCommand FulfilRequestCommand { get; }

    /// <summary>The selected patient's requests waiting for blood (audit P1), each with whether today's stock covers it; empty while unknown.</summary>
    public IReadOnlyList<PatientRequestRow> WaitingRequests { get; private set; } = [];

    /// <summary>A line about the waiting requests when they could not be loaded, or when more wait than are listed; null otherwise.</summary>
    public string? WaitingNote { get; private set; }

    /// <summary>How many requests the panel lists at most.</summary>
    public const int WaitingLimit = 10;

    private void ShowWaiting()
    {
        WaitingRequests = [];
        WaitingNote = null;
        if (Detail is { } patient && _fetchedWaiting is { } fetched && fetched.PatientId == patient.Id)
        {
            if (fetched.Requests is not { } requests)
            {
                WaitingNote = "The waiting requests couldn't be loaded. Select the patient again to retry.";
            }
            else
            {
                var stock = fetched.Stock?.ToDictionary(s => s.BloodType, s => s.Available);
                var today = DateOnly.FromDateTime(Clock.GetLocalNow().DateTime);
                WaitingRequests = [.. requests.Select(r =>
                {
                    var cover = stock is null ? null : RequestCoverage.Of(r.Units, stock.GetValueOrDefault(r.BloodType));
                    return new PatientRequestRow(
                        r.Id,
                        $"{RecordFormatting.Units(r.Units)} of {r.BloodType.ToTypographic()}",
                        "Needed by " + Formatting.Date(r.RequiredBy),
                        RequestUrgency.Of(r.Status, r.RequiredBy, today),
                        cover)
                    {
                        HoldReason = cover?.HoldReason(r.BloodType),
                    };
                })];
                if (fetched.Total > requests.Count)
                {
                    WaitingNote = $"The {requests.Count} needed first of {fetched.Total} are listed.";
                }
            }
        }

        OnPropertyChanged(nameof(WaitingRequests));
        OnPropertyChanged(nameof(WaitingNote));
        OnPropertyChanged(nameof(OpenRequests));
        OnPropertyChanged(nameof(OpenRequestsText));
    }

    // The patient's open requests (needed first at the top), the stock, and how many requests of any status the patient has on record, side by side; any may fail
    // without failing the panel (an unknown number of requests leaves Delete to the server).
    protected override async Task<IReadOnlyList<DetailFact>> GetExtraFactsAsync(int id, CancellationToken ct)
    {
        var query = new Contracts.Requests.RequestQuery(PatientId: id, PageSize: WaitingLimit)
        {
            View = Contracts.Requests.RequestView.Open,
            UtcOffsetMinutes = (int)Clock.GetLocalNow().Offset.TotalMinutes,
        };
        var allCall = api.GetRequestsAsync(new Contracts.Requests.RequestQuery(PatientId: id, PageSize: 1), ct); // every status; only the total is read
        var requestsCall = api.GetRequestsAsync(query, ct);
        var stockCall = api.GetStockAsync(ct);
        var requests = await requestsCall.ConfigureAwait(false);
        var stock = await stockCall.ConfigureAwait(false);
        var all = await allCall.ConfigureAwait(false);
        _fetchedWaiting = (id, requests.IsOk ? requests.Value.Items : null, requests.IsOk ? requests.Value.Total : 0, stock.IsOk ? stock.Value : null);
        _fetchedRequests = (id, all.IsOk ? all.Value.Total : null);
        return [];
    }

    /// <summary>"New request" for the selected patient: opens the request form on the Requests screen with this patient already chosen (FEAT-04).</summary>
    public AsyncRelayCommand NewRequestCommand { get; }

    public override string PageTitle => "Patients";

    /// <summary>The patient's code as the screens show it, "P-0051".</summary>
    public static string Code(int id) => Receipts.RecordCodes.Patient(id);

    /// <summary>The side panel's overline, "Patient P-0051".</summary>
    public string? DetailCode => Detail is { } p ? "Patient " + Code(p.Id) : null;

    /// <summary>The line under the patient's name in the side panel: "Female, age 42".</summary>
    public string? DetailSubtitle => Detail is { } p ? $"{p.Gender}, age {Eligibility.AgeOn(p.DateOfBirth, DateOnly.FromDateTime(Clock.GetLocalNow().DateTime))}" : null;

    /// <summary>The patient's blood group with a true minus sign, for the large tag in the side panel.</summary>
    public string? DetailBloodGroup => Detail?.BloodType.ToTypographic();

    public string? DetailInitials => Detail is { } p ? Donors.DonorsViewModel.PersonInitials(p.Name) : null;

    /// <summary>The selected patient's requests still waiting for blood (from the panel's own call, else the list row), or null when not known.</summary>
    public int? OpenRequests => Detail is { } p && _fetchedWaiting is { Requests: not null } fetched && fetched.PatientId == p.Id ? fetched.Total
        : Detail is { } d && SelectedItem is { } row && row.Id == d.Id ? row.PendingRequests : null;

    /// <summary>"2 requests waiting for blood", "No requests waiting", for the card in the side panel.</summary>
    public string? OpenRequestsText => OpenRequests switch
    {
        null => null,
        0 => "No requests waiting",
        1 => "1 request waiting for blood",
        var n => $"{n} requests waiting for blood",
    };

    /// <summary>The facts under "Details" in the side panel, without the blood group (it is in the header), and the groups the patient can receive red cells from.</summary>
    public IReadOnlyList<DetailFact> PanelFacts => Detail is { } patient
        ? [.. DetailFacts.Where(f => f.Label != "Blood group"), new DetailFact("Can receive", CanReceiveText(patient.BloodType))]
        : [.. DetailFacts.Where(f => f.Label != "Blood group")];

    /// <summary>"O−, O+, A−, A+": the groups whose red cells a patient of this group can be given.</summary>
    public static string CanReceiveText(BloodType group) =>
        string.Join(", ", SharedLists.BloodTypes.Select(c => c.Value).Where(t => t.CanGiveTo(group)).Select(t => t.ToTypographic()));

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
        new("Blood group", patient.BloodType.ToDisplay()),
        new("Phone", string.IsNullOrEmpty(patient.Phone) ? "Not given" : patient.Phone),
        new("Address", string.IsNullOrEmpty(patient.Address) ? "Not given" : patient.Address),
    ];

    protected override Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct) => api.DeletePatientAsync(id, ct);

    /// <summary>
    /// A patient with a request on record (waiting, fulfilled or cancelled) can't be deleted: the server keeps every request and its receipt, and they point at the
    /// patient. Delete is offered only for a patient with none (one added by mistake).
    /// </summary>
    protected override string? DeleteBlockOf(PatientDetail patient) =>
        _fetchedRequests is { } fetched && fetched.PatientId == patient.Id && fetched.Requests is > 0
            ? HistoryKept
            : null;

    private const string HistoryKept = "A patient with requests on record can't be deleted: their history is kept.";

    protected override string? DeleteRefusedText(string name) =>
        $"Patient '{name}' can't be deleted: they have requests on record, and that history is kept. Nothing was changed.";

    protected override Task<ApiResult<PatientDetail>> RestoreAsync(PatientDetail d, CancellationToken ct) =>
        api.RestorePatientAsync(d.Id, ct);

    protected override RecordEditorViewModel CreateEditor(PatientDetail? existing) =>
        existing is null
            ? PatientEditorViewModel.ForNew(api, Toasts, Dialogs, Clock)
            : PatientEditorViewModel.ForEdit(api, Toasts, Dialogs, existing, Clock);
}
