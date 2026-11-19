using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Navigation;
using Lifestream.Contracts;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Requests;
using Lifestream.Domain;
using ContractSortKeys = Lifestream.Contracts.SortKeys;

namespace Lifestream.Client.Features.Requests;

/// <summary>
/// The Requests screen (FEAT-14, FEAT-15, FEAT-16). A paged list that shows the patient's name with the id as secondary text; one search box finds a
/// request by patient name (or number) and the blood type filter narrows it, each in a single API call, with the patient column explaining why a
/// row matched. A request is fulfilled from its own detail pane (a dialog that issues the receipt), edited while pending, or cancelled.
/// </summary>
public sealed class RequestsViewModel : SortedRecordListViewModel<RequestListItem, RequestDetail>, INavigationParameterReceiver
{
    private const string StatusKey = "status";

    private readonly ILifestreamApiClient _api;
    private readonly IReceiptViewer _receipts;
    private readonly INavigationService _navigation;
    private FilterOption<RequestStatus> _statusFilter;

    public RequestsViewModel(
        ILifestreamApiClient api,
        IListViewStateStore states,
        IToastService toasts,
        IDialogService dialogs,
        IFormDialogService forms,
        IReceiptViewer receipts,
        INavigationService navigation,
        IUiDispatcher ui)
        : base(states, "requests", toasts, dialogs, ui)
    {
        _api = api;
        _ = forms; // the fulfil step is shown in the side panel (Fulfilment), not as a dialog; the parameter stays for the screen's constructor contract
        _receipts = receipts;
        _navigation = navigation;
        StatusOptions = SharedLists.WithAll(SharedLists.RequestStatuses, "All statuses");
        _statusFilter = StatusOptions[0];
        if (ViewState.Filters.TryGetValue(StatusKey, out var saved) && Enum.TryParse<RequestStatus>(saved, out var status))
        {
            _statusFilter = StatusOptions.First(o => o.Value == status);
        }

        FulfilCommand = Command(_ => FulfilSelectedAsync(), () => IsPending && !IsEditing, opensModal: true);
        CancelRequestCommand = Command(_ => CancelSelectedAsync(), () => IsPending && !IsEditing, opensModal: true);
        EditRequestCommand = Command(_ => EditCommand.ExecuteAsync(), () => IsPending && EditCommand.CanExecute(null));
        OpenPatientCommand = Command(async (parameter, _) =>
        {
            if (parameter is RequestListItem item)
            {
                await _navigation.NavigateTo<PatientsViewModel>(new ListPrefilter(RecordId: item.PatientId)).ConfigureAwait(true);
            }
        });
        ViewReceiptCommand = Command(_ => Detail?.ReceiptId is { } id ? _receipts.ShowAsync(id, isDuplicate: true) : Task.CompletedTask, () => Detail?.ReceiptId is not null, opensModal: true);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Detail) or nameof(Editor))
            {
                FulfilCommand.RaiseCanExecuteChanged();
                CancelRequestCommand.RaiseCanExecuteChanged();
                EditRequestCommand.RaiseCanExecuteChanged();
                ViewReceiptCommand.RaiseCanExecuteChanged();
            }

            if (e.PropertyName is nameof(Detail))
            {
                OnPropertyChanged(nameof(DetailCode));
                OnPropertyChanged(nameof(DetailSubtitle));
                OnPropertyChanged(nameof(DetailBloodGroup));
                OnPropertyChanged(nameof(DetailUrgency));
                OnPropertyChanged(nameof(IsPendingDetail));
            }

            if (e.PropertyName is nameof(DetailFacts))
            {
                OnPropertyChanged(nameof(PanelFacts));
            }

            // Another request chosen, or the panel closed, while the fulfil step is open: that step is left (nothing was issued).
            if (e.PropertyName is nameof(SelectedItem) && Fulfilment is { } open && (SelectedItem is null || SelectedItem.Id != open.RequestId))
            {
                open.CancelCommand.Execute(null);
            }
        };
    }

    /// <summary>
    /// The fulfil step of the selected request, shown in the side panel in place of its details (FEAT-16): what will be issued, the stock of that blood, and "Fulfil
    /// request" to confirm or Cancel. Null when not fulfilling.
    /// </summary>
    public FulfilRequestDialogViewModel? Fulfilment
    {
        get => _fulfilment;
        private set
        {
            if (SetProperty(ref _fulfilment, value))
            {
                OnPropertyChanged(nameof(IsFulfilling));
            }
        }
    }

    private FulfilRequestDialogViewModel? _fulfilment;

    public bool IsFulfilling => Fulfilment is not null;

    /// <summary>Today in the clinic's local time: how urgent a request is depends on how many days are left until it is needed.</summary>
    public DateOnly Today => DateOnly.FromDateTime(Clock.GetLocalNow().DateTime);

    /// <summary>How urgent a pending request is today (<see cref="RequestUrgency"/>); fulfilled and cancelled requests are not urgent.</summary>
    public RequestUrgency UrgencyOf(RequestStatus status, DateOnly requiredBy) => RequestUrgency.Of(status, requiredBy, Today);

    /// <summary>The selected request's urgency, for the side panel's header.</summary>
    public RequestUrgency? DetailUrgency => Detail is { } r ? UrgencyOf(r.Status, r.RequiredBy) : null;

    /// <summary>The side panel's overline, "Request #148".</summary>
    public string? DetailCode => Detail is { } r ? "Request " + RecordFormatting.IdText(r.Id) : null;

    /// <summary>"3 units of AB+ for Petra Iversen": the side panel's header line.</summary>
    public string? DetailSubtitle => Detail is { } r ? $"{RecordFormatting.Units(r.Units)}, needed by {Formatting.Date(r.RequiredBy)}" : null;

    public string? DetailBloodGroup => Detail?.BloodType.ToTypographic();

    /// <summary>True while the selected request is still waiting for blood (its actions apply).</summary>
    public bool IsPendingDetail => IsPending;

    /// <summary>The facts under "Details" in the side panel, without those its header already shows (patient, blood group, units, date, status).</summary>
    public IReadOnlyList<DetailFact> PanelFacts => [.. DetailFacts.Where(f => f.Label is not ("Patient" or "Blood type" or "Units" or "Needed by" or "Status"))];

    public override string PageTitle => "Requests";

    protected override string PluralName => "requests";

    protected override string RecordType => "request";

    public IReadOnlyList<FilterOption<RequestStatus>> StatusOptions { get; }

    public AsyncRelayCommand FulfilCommand { get; }

    public AsyncRelayCommand CancelRequestCommand { get; }

    public AsyncRelayCommand EditRequestCommand { get; }

    public AsyncRelayCommand OpenPatientCommand { get; }

    public AsyncRelayCommand ViewReceiptCommand { get; }

    private bool IsPending => Detail is { Status: RequestStatus.Pending };

    internal RecordOption? NewFor { get; set; }

    private bool _openNew;

    public FilterOption<RequestStatus> StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (SetProperty(ref _statusFilter, value))
            {
                ViewState.Filters[StatusKey] = value.Value?.ToString();
                FilterChanged();
            }
        }
    }

    protected override bool HasExtraFilters => base.HasExtraFilters || _statusFilter.Value is not null;

    protected override void ResetExtraFilters()
    {
        base.ResetExtraFilters();
        _statusFilter = StatusOptions[0];
        OnPropertyChanged(nameof(StatusFilter));
    }

    /// <summary>One call: the search text matches the patient's name (or the request number), the blood type and status filters are sent with it.</summary>
    /// <summary>The columns that sort: when the request was placed and the date it is needed by (the API supports exactly these two).</summary>
    public override IReadOnlyList<string> SortKeys { get; } = ContractSortKeys.Requests;

    protected override Task<ApiResult<PagedResult<RequestListItem>>> FetchSortedAsync(ListRequest request, CancellationToken ct) =>
        _api.GetRequestsAsync(new RequestQuery(request.Search, null, _statusFilter.Value, BloodTypeValue, request.Page, request.PageSize, request.Sort), ct);

    protected override void ApplyPrefilter(ListPrefilter prefilter)
    {
        base.ApplyPrefilter(prefilter);
        if (Enum.TryParse<RequestStatus>(prefilter.Status, true, out var status))
        {
            _statusFilter = StatusOptions.First(o => o.Value == status);
            ViewState.Filters[StatusKey] = status.ToString();
            OnPropertyChanged(nameof(StatusFilter));
            OnFiltersChanged();
        }
    }

    /// <summary>A prefilter, or "request blood for this patient" (a <see cref="RelatedRecord"/> of kind Patient).</summary>
    void INavigationParameterReceiver.ReceiveParameter(object parameter)
    {
        if (parameter is RelatedRecord { Kind: RecordKind.Patient } patient)
        {
            SetSearchWithoutRefresh(patient.Name);
            NewFor = new RecordOption(patient.Id, patient.Name, null);
            _openNew = true;
            OnFiltersChanged();
            return;
        }

        ReceiveParameter(parameter);
    }

    protected override void Apply()
    {
        base.Apply();
        if (_openNew)
        {
            _openNew = false;
            AddCommand.Execute(null);
        }
    }

    protected override int IdOf(RequestListItem item) => item.Id;

    protected override string NameOf(RequestListItem item) => item.PatientName;

    protected override int IdOfDetail(RequestDetail detail) => detail.Id;

    protected override string NameOfDetail(RequestDetail detail) => $"{detail.PatientName}, {detail.BloodType.ToDisplay()}";

    protected override Task<ApiResult<RequestDetail>> GetDetailAsync(int id, CancellationToken ct) => _api.GetRequestAsync(id, ct);

    protected override IReadOnlyList<DetailFact> FactsOf(RequestDetail r)
    {
        var facts = new List<DetailFact>
        {
            new("Patient", RecordFormatting.NameWithId(r.PatientName, r.PatientId)),
            new("Blood type", r.BloodType.ToDisplay()),
            new("Units", RecordFormatting.Units(r.Units)),
            new("Needed by", Formatting.Date(r.RequiredBy)),
            new("Status", StatusText(r.Status)),
            new("Requested on", RecordFormatting.When(r.PlacedAtUtc)),
        };
        if (r.ReceiptId is { } receipt)
        {
            facts.Add(new DetailFact("Receipt", RecordFormatting.IdText(receipt)));
        }

        return facts;
    }

    /// <summary>A request is cancelled, never deleted; this exists only because the list base class requires it, and no view offers Delete here.</summary>
    protected override Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct) => _api.CancelRequestAsync(id, ct);

    protected override Task<ApiResult<RequestDetail>> RestoreAsync(RequestDetail deleted, CancellationToken ct) =>
        Task.FromResult(ApiResult<RequestDetail>.Fail(new ApiProblem(409, ProblemCodes.Conflict, "A cancelled request can't be restored. Create a new one.")));

    protected override RecordEditorViewModel CreateEditor(RequestDetail? existing)
    {
        if (existing is not null)
        {
            return RequestEditorViewModel.ForEdit(_api, Toasts, Dialogs, existing, Clock);
        }

        var editor = RequestEditorViewModel.ForNew(_api, Toasts, Dialogs, Clock);
        if (NewFor is { } patient)
        {
            editor.PatientOption = patient;
            editor.PatientName = patient.Title;
            NewFor = null;
        }

        return editor;
    }

    private async Task FulfilSelectedAsync()
    {
        if (Detail is not { } request)
        {
            return;
        }

        var dialog = new FulfilRequestDialogViewModel(_api, request);
        bool done;
        try
        {
            // In the side panel: the step closes itself after a fulfilled request, or on Cancel; a refusal (not enough stock) keeps it open with the message.
            dialog.Bind(CurrentServerStatus, Clock, Dialogs);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.CloseRequested += (_, _) => closed.TrySetResult();
            Fulfilment = dialog;
            await closed.Task.ConfigureAwait(true);
            done = dialog.Completed;
        }
        finally
        {
            Fulfilment = null;
            dialog.Dispose();
        }

        if (!done)
        {
            return; // cancelled, or the API refused (insufficient stock): nothing changed, so the list is left as it was
        }

        var receiptId = dialog.Receipt?.Id;
        Toasts.Show(
            ToastKind.Success,
            receiptId is null ? $"Request for '{request.PatientName}' fulfilled." : $"Request for '{request.PatientName}' fulfilled. Receipt {RecordFormatting.IdText(receiptId.Value)} issued.",
            receiptId is null ? null : "View receipt",
            receiptId is null ? null : () => _receipts.ShowAsync(receiptId.Value, isDuplicate: false));
        await RefreshAfterActionAsync(request.Id).ConfigureAwait(true);
    }

    private async Task CancelSelectedAsync()
    {
        if (Detail is not { } request)
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            "Cancel request?",
            $"The request for {RecordFormatting.Units(request.Units)} of {request.BloodType.ToDisplay()} for '{request.PatientName}' will be cancelled. No blood is issued.",
            "Cancel request").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        ApiResult result = null!;
        await RunBusyAsync(async () => result = await RunOperationAsync(ct => _api.CancelRequestAsync(request.Id, ct)).ConfigureAwait(true)).ConfigureAwait(true);
        if (!result.IsOk)
        {
            Toasts.Show(ToastKind.Error, ProblemText.For(result.Problem!, RecordType, request.PatientName));
            return;
        }

        Toasts.ShowForRecord(ToastKind.Success, "Request", request.PatientName, "cancelled");
        await RefreshAfterActionAsync(request.Id).ConfigureAwait(true);
    }

    private async Task RefreshAfterActionAsync(int id)
    {
        await RefreshAsync().ConfigureAwait(true);
        await LoadDetailAsync(id).ConfigureAwait(true);
    }

    public static string StatusText(RequestStatus status) => status.ToString();
}
