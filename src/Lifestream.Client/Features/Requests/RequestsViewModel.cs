using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Navigation;
using Lifestream.Contracts;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;
using ContractSortKeys = Lifestream.Contracts.SortKeys;

namespace Lifestream.Client.Features.Requests;

/// <summary>
/// The Requests screen (FEAT-14, FEAT-15, FEAT-16). A paged list that shows the patient's name with the id as secondary text; one search box finds a
/// request by patient name (or number) and the blood type filter narrows it, each in a single API call, with the patient column explaining why a
/// row matched. Direction A adds the saved views (Open, Due in 48 h, Fulfilled, Cancelled, with their counts), the open views sorted by the date needed
/// (overdue first, by the API), and the Cover of each open request from the stock. A request is fulfilled from its own side panel (the bags that will
/// be issued, then the receipt), edited while pending, or cancelled.
/// </summary>
public sealed class RequestsViewModel : SortedRecordListViewModel<RequestListItem, RequestDetail>, INavigationParameterReceiver
{
    private const string StatusKey = "status";
    private const string ViewKey = "view";

    private readonly ILifestreamApiClient _api;
    private readonly IReceiptViewer _receipts;
    private readonly INavigationService _navigation;
    private FilterOption<RequestStatus> _statusFilter;
    private RequestView? _view = RequestView.Open;
    private RequestViewCounts? _fetchedCounts;
    private IReadOnlyList<StockItem>? _fetchedStock;
    private Dictionary<BloodType, int>? _available;

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

        if (ViewState.Filters.TryGetValue(ViewKey, out var savedView) && Enum.TryParse<RequestView>(savedView, out var view))
        {
            _view = view;
        }

        FulfilCommand = Command(_ => FulfilSelectedAsync(), () => IsPending && !IsEditing && FulfilHoldReason is null, opensModal: true);
        CancelRequestCommand = Command(_ => CancelSelectedAsync(), () => IsPending && !IsEditing, opensModal: true);
        EditRequestCommand = Command(_ => EditCommand.ExecuteAsync(), () => IsPending && EditCommand.CanExecute(null));
        OpenPatientCommand = Command(async (parameter, _) =>
        {
            var patientId = parameter switch
            {
                RequestListItem item => item.PatientId,
                RequestDetail detail => detail.PatientId,
                _ => (int?)null,
            };
            if (patientId is { } id)
            {
                await _navigation.NavigateTo<PatientsViewModel>(new ListPrefilter(RecordId: id)).ConfigureAwait(true);
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
                OnPropertyChanged(nameof(DetailCover));
                OnPropertyChanged(nameof(FulfilHoldReason));
                OnPropertyChanged(nameof(IsPendingDetail));
                OnPropertyChanged(nameof(IsClosedDetail));
            }

            if (e.PropertyName is nameof(DetailFacts) or nameof(Detail))
            {
                OnPropertyChanged(nameof(PanelFacts));
            }

            // Another request chosen, or the panel closed, while the fulfil step is open: that step is left (nothing was issued).
            if (e.PropertyName is nameof(SelectedItem) && Fulfilment is { } open && (SelectedItem is null || SelectedItem.Id != open.RequestId))
            {
                open.CancelCommand.Execute(null);
            }

            // A fulfil asked for by another screen is dropped when the user chooses another request.
            if (e.PropertyName is nameof(SelectedItem) && _fulfilWhenOpen is { } wanted && SelectedItem is { } chosen && chosen.Id != wanted)
            {
                _fulfilWhenOpen = null;
                FulfilCommand.CanExecuteChanged -= OnFulfilCanExecuteChanged;
            }
        };
    }

    /// <summary>
    /// The fulfil step of the selected request, shown in the side panel in place of its details (FEAT-16): the bags that will be issued (oldest first), the stock of that
    /// blood, and "Fulfil request" to confirm or "Not now". Null when not fulfilling.
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

    /// <summary>The side panel's overline, "Request R-0148".</summary>
    public string? DetailCode => Detail is { } r ? "Request " + Code(r.Id) : null;

    /// <summary>The request's code as the screens show it, "R-0148".</summary>
    public static string Code(int id) => RecordCodes.Request(id);

    /// <summary>"3 units, needed by 19 Nov": the side panel's header line (the year only when it is not this year, so the line stays short in a narrow panel).</summary>
    public string? DetailSubtitle => Detail is { } r
        ? $"{RecordFormatting.Units(r.Units)}, needed by {r.RequiredBy.ToString(r.RequiredBy.Year == Today.Year ? "dd MMM" : "dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}"
        : null;

    public string? DetailBloodGroup => Detail?.BloodType.ToTypographic();

    /// <summary>True while the selected request is still waiting for blood (its actions apply).</summary>
    public bool IsPendingDetail => IsPending;

    /// <summary>True for a fulfilled or cancelled request: its status shows in the panel.</summary>
    public bool IsClosedDetail => Detail is { Status: not RequestStatus.Pending };

    /// <summary>Whether the stock covers the selected pending request now; null for a closed request or while the stock is unknown.</summary>
    public RequestCoverage? DetailCover => Detail is { Status: RequestStatus.Pending } r ? CoverOf(r.BloodType, r.Units) : null;

    /// <summary>
    /// Why "Fulfil request" is disabled for the selected request, shown under the action row: none of its group is in stock ("No O− in stock. The request stays open
    /// until stock arrives."). Null when it can be fulfilled, or while the stock is unknown (the server still checks).
    /// </summary>
    public string? FulfilHoldReason => Detail is { Status: RequestStatus.Pending } r ? DetailCover?.HoldReason(r.BloodType) : null;

    /// <summary>
    /// The side panel's details (audit R2): the patient with their code, when it is needed and how urgent, the stock against it, when it was placed, and the receipt or
    /// status of a closed request. The header already shows the patient's name, the group and the units.
    /// </summary>
    public IReadOnlyList<DetailFact> PanelFacts
    {
        get
        {
            if (Detail is not { } r)
            {
                return [.. DetailFacts];
            }

            var facts = new List<DetailFact> { new("Patient", $"{r.PatientName} ({PatientsViewModel.Code(r.PatientId)})") };
            var urgency = DetailUrgency;
            facts.Add(new("Needed by", Formatting.Date(r.RequiredBy) + (urgency is { Kind: not RequestUrgencyKind.None } u ? $" ({u.Text.ToLowerInvariant()})" : string.Empty)));
            facts.Add(new("Units", RecordFormatting.Units(r.Units)));
            if (DetailCover is { } cover)
            {
                facts.Add(new("In stock", cover.Sentence(r.BloodType)));
            }

            facts.AddRange(DetailFacts.Where(f => f.Label is "Requested on"));
            if (r.Status != RequestStatus.Pending)
            {
                facts.Add(new("Status", StatusText(r.Status)));
            }

            if (r.ReceiptId is { } receipt)
            {
                facts.Add(new("Receipt", ReceiptCode(receipt)));
            }

            return facts;
        }
    }

    /// <summary>A receipt's code, "RC-0012".</summary>
    public static string ReceiptCode(int id) => RecordCodes.Receipt(id);

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

    private int? _fulfilWhenOpen;

    /// <summary>
    /// The status filter; "All statuses" sends none. The saved views replace it on screen, so it is shown (as a chip that can be cleared) only while a link from another
    /// screen has set it.
    /// </summary>
    public FilterOption<RequestStatus> StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (SetProperty(ref _statusFilter, value))
            {
                ViewState.Filters[StatusKey] = value.Value?.ToString();
                OnPropertyChanged(nameof(HasStatusFilter));
                FilterChanged();
            }
        }
    }

    public bool HasStatusFilter => _statusFilter.Value is not null;

    // ---- the saved views ----

    /// <summary>The saved view shown (Open by default), or null for all requests (a link from another screen with a status of its own).</summary>
    public RequestView? View
    {
        get => _view;
        set
        {
            if (_view == value)
            {
                return;
            }

            _view = value;
            ViewState.Filters[ViewKey] = value?.ToString();
            RaiseViewChanged();
            FilterChanged();
        }
    }

    public bool IsOpenView { get => _view == RequestView.Open; set => SetViewFromTab(value, RequestView.Open); }

    public bool IsDueSoonView { get => _view == RequestView.DueSoon; set => SetViewFromTab(value, RequestView.DueSoon); }

    public bool IsFulfilledView { get => _view == RequestView.Fulfilled; set => SetViewFromTab(value, RequestView.Fulfilled); }

    public bool IsCancelledView { get => _view == RequestView.Cancelled; set => SetViewFromTab(value, RequestView.Cancelled); }

    /// <summary>How many requests each view holds (the tabs' counts), with the same search and filters; null until known or when the count call failed.</summary>
    public RequestViewCounts? ViewCounts { get; private set; }

    /// <summary>The Status column shows for closed requests (and the list of all requests); an open view's rows are all pending, so it would repeat "Pending".</summary>
    public bool ShowStatusColumn => _view is null or RequestView.Fulfilled or RequestView.Cancelled;

    /// <summary>The Urgency and Cover columns show where requests are still open; a fulfilled or cancelled request has neither.</summary>
    public bool ShowOpenColumns => _view is null or RequestView.Open or RequestView.DueSoon;

    private void SetViewFromTab(bool isChecked, RequestView view)
    {
        if (isChecked)
        {
            View = view;
        }
    }

    private void RaiseViewChanged()
    {
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(IsOpenView));
        OnPropertyChanged(nameof(IsDueSoonView));
        OnPropertyChanged(nameof(IsFulfilledView));
        OnPropertyChanged(nameof(IsCancelledView));
        OnPropertyChanged(nameof(ShowStatusColumn));
        OnPropertyChanged(nameof(ShowOpenColumns));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    protected override string EmptyNoFilterTitle => _view switch
    {
        RequestView.Open => "No requests waiting for blood",
        RequestView.DueSoon => "Nothing is due in the next 48 hours",
        RequestView.Fulfilled => "No fulfilled requests yet",
        RequestView.Cancelled => "No cancelled requests",
        _ => "No requests yet",
    };

    protected override string EmptyAddMessage => _view switch
    {
        RequestView.Open => "Every request has been fulfilled or cancelled.",
        RequestView.DueSoon => "No open request is needed by the day after tomorrow.",
        _ => "There are no requests to show.",
    };

    // ---- cover ----

    /// <summary>The Cover of a pending request from the stock fetched with the list (same group only, as Fulfil issues it); null for a closed request or unknown stock.</summary>
    public RequestCoverage? CoverOf(RequestListItem item) => item is { Status: RequestStatus.Pending } ? CoverOf(item.BloodType, item.Units) : null;

    private RequestCoverage? CoverOf(BloodType group, int units) => _available is { } stock ? RequestCoverage.Of(units, stock.GetValueOrDefault(group)) : null;

    protected override bool HasExtraFilters => base.HasExtraFilters || _statusFilter.Value is not null;

    protected override void ResetExtraFilters()
    {
        base.ResetExtraFilters();
        _statusFilter = StatusOptions[0];
        OnPropertyChanged(nameof(StatusFilter));
        OnPropertyChanged(nameof(HasStatusFilter));
    }

    /// <summary>The columns that sort: when the request was placed and the date it is needed by (the API supports exactly these two).</summary>
    public override IReadOnlyList<string> SortKeys { get; } = ContractSortKeys.Requests;

    private int UtcOffsetMinutes => (int)Clock.GetLocalNow().Offset.TotalMinutes;

    /// <summary>
    /// One call for the page (the search matches the patient's name or the request number; the view, blood type and status go with it), and beside it, best effort, the
    /// views' counts and the stock (for the Cover column). Either of those may fail without failing the list.
    /// </summary>
    protected override async Task<ApiResult<PagedResult<RequestListItem>>> FetchSortedAsync(ListRequest request, CancellationToken ct)
    {
        var query = new RequestQuery(request.Search, null, _statusFilter.Value, BloodTypeValue, request.Page, request.PageSize, request.Sort) { View = _view, UtcOffsetMinutes = UtcOffsetMinutes };
        var page = _api.GetRequestsAsync(query, ct);
        var counts = _api.GetRequestViewCountsAsync(query, ct);
        var stock = _api.GetStockAsync(ct);
        var result = await page.ConfigureAwait(false);
        _fetchedCounts = await counts.ConfigureAwait(false) is { IsOk: true } c ? c.Value : null;
        _fetchedStock = await stock.ConfigureAwait(false) is { IsOk: true } s ? s.Value : null;
        return result;
    }

    protected override void ApplyPrefilter(ListPrefilter prefilter)
    {
        base.ApplyPrefilter(prefilter);
        if (Enum.TryParse<RequestStatus>(prefilter.Status, true, out var status))
        {
            _statusFilter = StatusOptions.First(o => o.Value == status);
            ViewState.Filters[StatusKey] = status.ToString();
            // The status's own view, so the tabs agree with what the list shows.
            _view = status switch
            {
                RequestStatus.Pending => RequestView.Open,
                RequestStatus.Fulfilled => RequestView.Fulfilled,
                _ => RequestView.Cancelled,
            };
            ViewState.Filters[ViewKey] = _view.ToString();
            OnPropertyChanged(nameof(StatusFilter));
            OnPropertyChanged(nameof(HasStatusFilter));
            RaiseViewChanged();
            OnFiltersChanged();
        }
        else if (prefilter.RecordId is not null)
        {
            // A link to one request (a dashboard item, a patient's panel): every view, so the request is in the list whatever its status. Not remembered: the
            // next visit opens on the view the user chose.
            _view = null;
            RaiseViewChanged();
        }
    }

    /// <summary>A prefilter; "request blood for this patient" (a <see cref="RelatedRecord"/> of kind Patient); or "fulfil this request" (kind Request).</summary>
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

        if (parameter is RelatedRecord { Kind: RecordKind.Request } fulfil)
        {
            _fulfilWhenOpen = fulfil.Id;
            ReceiveParameter(new ListPrefilter(RecordId: fulfil.Id));
            _ = FulfilOnceOpenAsync(fulfil.Id);
            return;
        }

        ReceiveParameter(parameter);
    }

    /// <summary>Starts the fulfil step of the request another screen asked to fulfil, once its detail is in the panel (and only if it is still pending).</summary>
    private async Task FulfilOnceOpenAsync(int id)
    {
        await PendingDetail.ConfigureAwait(true);
        if (_fulfilWhenOpen == id)
        {
            TryStartPendingFulfil();
        }
    }

    /// <summary>
    /// Opens the fulfil step asked for by another screen as soon as it can run: the request's detail is in the panel and no operation of the screen (the list's load)
    /// keeps the step's command disabled. A request that is no longer pending, or another selection, drops the wish.
    /// </summary>
    private void TryStartPendingFulfil()
    {
        FulfilCommand.CanExecuteChanged -= OnFulfilCanExecuteChanged;
        if (_fulfilWhenOpen is not { } id || Detail is not { } detail || detail.Id != id)
        {
            return;
        }

        if (detail.Status != RequestStatus.Pending || FulfilHoldReason is not null)
        {
            _fulfilWhenOpen = null; // closed, or nothing in stock to fulfil it with (the panel says why)
            return;
        }

        if (FulfilCommand.CanExecute(null))
        {
            _fulfilWhenOpen = null;
            FulfilCommand.Execute(null);
        }
        else
        {
            FulfilCommand.CanExecuteChanged += OnFulfilCanExecuteChanged;
        }
    }

    private void OnFulfilCanExecuteChanged(object? sender, EventArgs e) => TryStartPendingFulfil();

    protected override void Apply()
    {
        // The stock first: the rows' Cover cells read it when the page's rows are put in place.
        _available = _fetchedStock?.ToDictionary(s => s.BloodType, s => s.Available);
        ViewCounts = _fetchedCounts;
        base.Apply();
        OnPropertyChanged(nameof(ViewCounts));
        OnPropertyChanged(nameof(DetailCover));
        OnPropertyChanged(nameof(FulfilHoldReason));
        OnPropertyChanged(nameof(PanelFacts));
        FulfilCommand.RaiseCanExecuteChanged(); // the stock behind the cover may have changed
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
            new("Patient", RecordFormatting.NameWithCode(r.PatientName, RecordCodes.Patient(r.PatientId))),
            new("Blood type", r.BloodType.ToDisplay()),
            new("Units", RecordFormatting.Units(r.Units)),
            new("Needed by", Formatting.Date(r.RequiredBy)),
            new("Status", StatusText(r.Status)),
            new("Requested on", RecordFormatting.When(r.PlacedAtUtc)),
        };
        if (r.ReceiptId is { } receipt)
        {
            facts.Add(new DetailFact("Receipt", ReceiptCode(receipt)));
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
            // In the side panel: the step closes itself after a fulfilled request, or on "Not now"; a refusal (not enough stock) keeps it open with the message.
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
            return; // not now, or the API refused (insufficient stock): nothing changed, so the list is left as it was
        }

        var receiptId = dialog.Receipt?.Id;
        Toasts.Show(
            ToastKind.Success,
            receiptId is null ? $"Request for '{request.PatientName}' fulfilled." : $"Request for '{request.PatientName}' fulfilled. Receipt {ReceiptCode(receiptId.Value)} issued.",
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
            $"{request.PatientName}'s request for {RecordFormatting.Units(request.Units)} of {request.BloodType.ToTypographic()} will be cancelled. No blood is issued.",
            "Cancel request",
            "Keep request").ConfigureAwait(true);
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
