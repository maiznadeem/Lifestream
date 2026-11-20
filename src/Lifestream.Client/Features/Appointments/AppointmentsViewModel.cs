using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Navigation;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Lookups;
using Lifestream.Domain;
using ContractSortKeys = Lifestream.Contracts.SortKeys;

namespace Lifestream.Client.Features.Appointments;

/// <summary>
/// The Appointments screen (FEAT-10, FEAT-11, FEAT-13, FEAT-14, FEAT-16). A paged list that shows the donor's name with the id as secondary text,
/// filters by status, blood type and donor (the donor's own id goes to the API, never the appointment's), and an detail pane whose actions are
/// Reschedule, Record donation and Cancel appointment. Booking and rescheduling open in the drawer; recording a donation opens its own dialog.
/// </summary>
public sealed class AppointmentsViewModel : SortedRecordListViewModel<AppointmentListItem, AppointmentDetail>, INavigationParameterReceiver
{
    private const string StatusKey = "status";
    private const string DonorIdKey = "donorId";
    private const string DonorNameKey = "donorName";
    private const string ViewKey = "view";

    private readonly ILifestreamApiClient _api;
    private readonly IFormDialogService _forms;
    private readonly IReceiptViewer _receipts;
    private readonly INavigationService _navigation;
    private FilterOption<AppointmentStatus> _statusFilter;
    private RecordOption? _donorFilter;
    private RecordOption? _bookFor;
    private bool _openBooking;
    private AppointmentView? _view = AppointmentView.Today;
    private AppointmentViewCounts? _fetchedCounts;
    private int? _nextId;

    public AppointmentsViewModel(
        ILifestreamApiClient api,
        IListViewStateStore states,
        IToastService toasts,
        IDialogService dialogs,
        IFormDialogService forms,
        IReceiptViewer receipts,
        INavigationService navigation,
        IUiDispatcher ui)
        : base(states, "appointments", toasts, dialogs, ui)
    {
        _api = api;
        _forms = forms;
        _receipts = receipts;
        _navigation = navigation;
        StatusOptions = SharedLists.WithAll(SharedLists.AppointmentStatuses, "All statuses");
        _statusFilter = StatusOptions[0];
        if (ViewState.Filters.TryGetValue(StatusKey, out var savedStatus) && Enum.TryParse<AppointmentStatus>(savedStatus, out var status))
        {
            _statusFilter = StatusOptions.First(o => o.Value == status);
        }

        if (ViewState.Filters.TryGetValue(ViewKey, out var savedView) && Enum.TryParse<AppointmentView>(savedView, out var view))
        {
            _view = view;
        }

        if (ViewState.Filters.TryGetValue(DonorIdKey, out var savedId) && int.TryParse(savedId, out var donorId))
        {
            ViewState.Filters.TryGetValue(DonorNameKey, out var savedName);
            _donorFilter = new RecordOption(donorId, savedName ?? $"Donor {donorId}", null);
        }

        RescheduleCommand = Command(_ => EditCommand.ExecuteAsync(), () => IsPending && EditCommand.CanExecute(null));
        RecordDonationCommand = Command(_ => RecordDonationAsync(), () => IsPending && !IsEditing, opensModal: true);
        CancelAppointmentCommand = Command(_ => CancelSelectedAsync(), () => IsPending && !IsEditing, opensModal: true);
        FilterByDonorCommand = Command((parameter, _) =>
        {
            if (parameter is AppointmentListItem item)
            {
                // The donor's id of the row, not the appointment's id (FEAT-10).
                DonorFilter = new RecordOption(item.DonorId, item.DonorName, null);
            }

            return Task.CompletedTask;
        });
        OpenDonorCommand = Command(async (parameter, _) =>
        {
            var donorId = parameter switch
            {
                AppointmentListItem item => item.DonorId,
                AppointmentDetail detail => detail.DonorId,
                _ => (int?)null,
            };
            if (donorId is { } id)
            {
                await _navigation.NavigateTo<DonorsViewModel>(new ListPrefilter(RecordId: id)).ConfigureAwait(true);
            }
        });
        ViewReceiptCommand = Command(_ => Detail?.ReceiptId is { } id ? _receipts.ShowAsync(id, isDuplicate: true) : Task.CompletedTask, () => Detail?.ReceiptId is not null, opensModal: true);
        RecordDonationForCommand = Command(async (parameter, _) =>
        {
            // This command is itself the modal-opening operation (as Record donation is), so it records the donation directly rather than through that command,
            // which the runner keeps disabled while any operation, this one included, runs.
            if (parameter is AppointmentListItem { Status: AppointmentStatus.Pending } item && await OpenRowAsync(item).ConfigureAwait(true) && IsPending && !IsEditing)
            {
                await RecordDonationAsync().ConfigureAwait(true);
            }
        }, opensModal: true);
        ClearDonorFilterCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => DonorFilter = null);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Detail) or nameof(Editor))
            {
                RescheduleCommand.RaiseCanExecuteChanged();
                RecordDonationCommand.RaiseCanExecuteChanged();
                CancelAppointmentCommand.RaiseCanExecuteChanged();
                ViewReceiptCommand.RaiseCanExecuteChanged();
            }

            if (e.PropertyName is nameof(Detail))
            {
                OnPropertyChanged(nameof(DetailCode));
                OnPropertyChanged(nameof(DetailWhen));
                OnPropertyChanged(nameof(DetailBloodGroup));
                OnPropertyChanged(nameof(DetailInitials));
                OnPropertyChanged(nameof(IsPendingDetail));
            }

            if (e.PropertyName is nameof(DetailFacts))
            {
                OnPropertyChanged(nameof(PanelFacts));
                ShowDonorEligibility();
            }
        };
    }

    /// <summary>The donor's eligibility on the day of the selected (pending) appointment, shown in the side panel; hidden for any other appointment.</summary>
    public EligibilityBannerViewModel DonorEligibility { get; } = new();

    /// <summary>The time zone the screen shows times in (the clinic's local time).</summary>
    public TimeZoneInfo DisplayZone => Zone;

    /// <summary>Today in the clinic's local time: the list highlights its day group.</summary>
    public DateOnly Today => RecordFormatting.LocalDate(Clock.GetUtcNow().UtcDateTime, Zone);

    /// <summary>The local day of an appointment: the list groups its rows by it.</summary>
    public DateOnly DayOf(AppointmentListItem item) => RecordFormatting.LocalDate((item ?? throw new ArgumentNullException(nameof(item))).SlotStartUtc, Zone);

    /// <summary>"Today, Thu 19 Nov", "Tomorrow, Fri 20 Nov", "Yesterday, Wed 18 Nov", "Mon 23 Nov 2026" (another year): a day group's heading.</summary>
    public string DayHeading(DateOnly day)
    {
        var today = Today;
        var date = day.ToString(day.Year == today.Year ? "ddd dd MMM" : "ddd dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return (day.DayNumber - today.DayNumber) switch
        {
            0 => "Today, " + date,
            1 => "Tomorrow, " + date,
            -1 => "Yesterday, " + date,
            _ => date,
        };
    }

    /// <summary>The side panel's overline, "Appointment A-0904".</summary>
    public string? DetailCode => Detail is { } a ? "Appointment " + Code(a.Id) : null;

    /// <summary>An appointment's code as the screens show it, "A-0905".</summary>
    public static string Code(int id) => RecordCodes.Appointment(id);

    /// <summary>A receipt's code, "RC-0012".</summary>
    private static string ReceiptCode(int id) => RecordCodes.Receipt(id);

    // ---- the saved views ----

    /// <summary>The saved view shown (Today by default), or null for every appointment (a link from another screen).</summary>
    public AppointmentView? View
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

    public bool IsTodayView { get => _view == AppointmentView.Today; set => SetViewFromTab(value, AppointmentView.Today); }

    public bool IsUpcomingView { get => _view == AppointmentView.Upcoming; set => SetViewFromTab(value, AppointmentView.Upcoming); }

    public bool IsPastView { get => _view == AppointmentView.Past; set => SetViewFromTab(value, AppointmentView.Past); }

    public bool IsCancelledView { get => _view == AppointmentView.Cancelled; set => SetViewFromTab(value, AppointmentView.Cancelled); }

    /// <summary>True on the Today tab: the list is the day's agenda, with Next, done and first-time marks and the inline Record donation.</summary>
    public bool IsAgenda => _view == AppointmentView.Today;

    /// <summary>How many appointments each view holds (the tabs' counts), with the same search and filters; null until known or when the count call failed.</summary>
    public AppointmentViewCounts? ViewCounts { get; private set; }

    private void SetViewFromTab(bool isChecked, AppointmentView view)
    {
        if (isChecked)
        {
            View = view;
        }
    }

    private void RaiseViewChanged()
    {
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(IsTodayView));
        OnPropertyChanged(nameof(IsUpcomingView));
        OnPropertyChanged(nameof(IsPastView));
        OnPropertyChanged(nameof(IsCancelledView));
        OnPropertyChanged(nameof(IsAgenda));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    protected override string EmptyNoFilterTitle => _view switch
    {
        AppointmentView.Today => "No appointments today",
        AppointmentView.Upcoming => "No upcoming appointments",
        AppointmentView.Past => "No past appointments",
        AppointmentView.Cancelled => "No cancelled appointments",
        _ => "No appointments yet",
    };

    protected override string EmptyInThisViewTitle => _view switch
    {
        AppointmentView.Today => "No appointments today match",
        AppointmentView.Upcoming => "No upcoming appointments match",
        AppointmentView.Past => "No past appointments match",
        AppointmentView.Cancelled => "No cancelled appointments match",
        _ => base.EmptyInThisViewTitle,
    };

    /// <summary>The other tabs that hold matches, from the tabs' counts (the same search and filters as the list).</summary>
    protected override IReadOnlyList<OtherViewMatch> MatchesInOtherViews
    {
        get
        {
            if (ViewCounts is not { } counts || _view is not { } here)
            {
                return [];
            }

            (AppointmentView View, string Tab, int Count)[] tabs =
            [
                (AppointmentView.Today, "Today", counts.Today),
                (AppointmentView.Upcoming, "Upcoming", counts.Upcoming),
                (AppointmentView.Past, "Past", counts.Past),
                (AppointmentView.Cancelled, "Cancelled", counts.Cancelled),
            ];
            return [.. tabs.Where(t => t.View != here && t.Count > 0).OrderByDescending(t => t.Count).Select(t => new OtherViewMatch(t.Tab, t.Count, () => View = t.View))];
        }
    }

    protected override string EmptyAddMessage => _view switch
    {
        AppointmentView.Today => "Nobody is booked in for today.",
        AppointmentView.Upcoming => "Nobody is booked in after today.",
        _ => "There are no appointments to show.",
    };

    /// <summary>
    /// Where a row stands in today's agenda (the Today tab): Done once recorded, Next for the first one still to come (the one to check in), Waiting for the later
    /// ones, and Missed or Cancelled. None outside the Today tab.
    /// </summary>
    public AgendaState AgendaOf(AppointmentListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsAgenda)
        {
            return AgendaState.None;
        }

        return item.Status switch
        {
            AppointmentStatus.Completed => AgendaState.Done,
            AppointmentStatus.NoShow => AgendaState.Missed,
            AppointmentStatus.Cancelled => AgendaState.Cancelled,
            _ => item.Id == _nextId ? AgendaState.Next : AgendaState.Waiting,
        };
    }

    /// <summary>Changes each time the agenda's marks were worked out again (a binding source for the rows' agenda cells).</summary>
    public int AgendaVersion { get; private set; }

    /// <summary>"Record donation" on a row of the agenda: opens that appointment and records its donation (the same dialog as the panel's).</summary>
    public AsyncRelayCommand RecordDonationForCommand { get; }

    /// <summary>Removes the donor filter (the x of the donor chip). No API call of its own; the list reloads.</summary>
    public System.Windows.Input.ICommand ClearDonorFilterCommand { get; }

    /// <summary>Selects a row and waits until its detail is in the panel; false when it did not load (or another row was chosen meanwhile).</summary>
    private async Task<bool> OpenRowAsync(AppointmentListItem item)
    {
        if (!ReferenceEquals(SelectedItem, item))
        {
            SelectedItem = item;
        }

        await PendingDetail.ConfigureAwait(true);
        return Detail?.Id == item.Id;
    }

    /// <summary>True while a status filter set by a link from another screen is on (it shows as a chip that can be cleared; the tabs replace it otherwise).</summary>
    public bool HasStatusFilter => _statusFilter.Value is not null;

    /// <summary>The appointment's time in the side panel's header: "Thu 19 Nov 2026, 11:00".</summary>
    public string? DetailWhen => Detail is { } a
        ? $"{RecordFormatting.LocalDate(a.SlotStartUtc, Zone).ToString("ddd dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}, {RecordFormatting.LocalTime(a.SlotStartUtc, Zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
        : null;

    public string? DetailBloodGroup => Detail?.DonorBloodType.ToTypographic();

    public string? DetailInitials => Detail is { } a ? DonorsViewModel.PersonInitials(a.DonorName) : null;

    /// <summary>True while the selected appointment is still to come or to be recorded (its actions apply).</summary>
    public bool IsPendingDetail => IsPending;

    /// <summary>The facts under "Details" in the side panel, without those its header already shows (donor, blood group, time).</summary>
    public IReadOnlyList<DetailFact> PanelFacts => [.. DetailFacts.Where(f => f.Label is not ("Donor" or "Blood group" or "Appointment"))];

    private (int AppointmentId, DonorDetail Donor)? _donorOfDetail;

    // The donor of a pending appointment, for the eligibility card: fetched with the detail, shown once the detail is in place (on the UI thread).
    protected override async Task<IReadOnlyList<DetailFact>> GetExtraFactsAsync(int id, CancellationToken ct)
    {
        _donorOfDetail = null;
        if (SelectedItem is { Status: AppointmentStatus.Pending } row && row.Id == id)
        {
            var donor = await _api.GetDonorAsync(row.DonorId, ct).ConfigureAwait(false);
            if (donor.IsOk)
            {
                _donorOfDetail = (id, donor.Value);
            }
        }

        return [];
    }

    private void ShowDonorEligibility()
    {
        if (Detail is { Status: AppointmentStatus.Pending } appointment && _donorOfDetail is { } found && found.AppointmentId == appointment.Id)
        {
            DonorEligibility.Show(found.Donor, RecordFormatting.LocalDate(appointment.SlotStartUtc, Zone), Today);
        }
        else
        {
            DonorEligibility.Hide();
        }
    }

    public override string PageTitle => "Appointments";

    protected override string PluralName => "appointments";

    protected override string RecordType => "appointment";

    /// <summary>The zone the screen shows times in; the user's own, unless a test says otherwise.</summary>
    internal TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public IReadOnlyList<FilterOption<AppointmentStatus>> StatusOptions { get; }

    public AsyncRelayCommand RescheduleCommand { get; }

    public AsyncRelayCommand RecordDonationCommand { get; }

    public AsyncRelayCommand CancelAppointmentCommand { get; }

    public AsyncRelayCommand FilterByDonorCommand { get; }

    public AsyncRelayCommand OpenDonorCommand { get; }

    public AsyncRelayCommand ViewReceiptCommand { get; }

    private bool IsPending => Detail is { Status: AppointmentStatus.Pending };

    /// <summary>The status filter; "All statuses" sends none. It applies at once.</summary>
    public FilterOption<AppointmentStatus> StatusFilter
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

    /// <summary>The donor whose appointments are shown, or null for everyone. The API gets this donor's id (FEAT-10).</summary>
    public RecordOption? DonorFilter
    {
        get => _donorFilter;
        set
        {
            if (SetProperty(ref _donorFilter, value))
            {
                ViewState.Filters[DonorIdKey] = value?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ViewState.Filters[DonorNameKey] = value?.Title;
                FilterChanged();
            }
        }
    }

    protected override bool HasExtraFilters => base.HasExtraFilters || _statusFilter.Value is not null || _donorFilter is not null;

    protected override void ResetExtraFilters()
    {
        base.ResetExtraFilters();
        _statusFilter = StatusOptions[0];
        _donorFilter = null;
        OnPropertyChanged(nameof(StatusFilter));
        OnPropertyChanged(nameof(HasStatusFilter));
        OnPropertyChanged(nameof(DonorFilter));
    }

    /// <summary>The only column that sorts is the appointment time; the API supports "slot" and "placed" and this screen offers the first.</summary>
    public override IReadOnlyList<string> SortKeys { get; } = [ContractSortKeys.Slot];

    /// <summary>
    /// One call for the page (the view, search, donor, blood type and status go with it; the API runs Today and Upcoming in time order unless a sort is chosen), and beside
    /// it, best effort, the views' counts.
    /// </summary>
    protected override async Task<ApiResult<PagedResult<AppointmentListItem>>> FetchSortedAsync(ListRequest request, CancellationToken ct)
    {
        var query = new AppointmentQuery(request.Search, _donorFilter?.Id, _statusFilter.Value, BloodTypeValue, null, null, request.Page, request.PageSize, request.Sort)
        {
            View = _view,
            UtcOffsetMinutes = (int)TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), Zone).Offset.TotalMinutes,
        };
        var page = _api.GetAppointmentsAsync(query, ct);
        var counts = _api.GetAppointmentViewCountsAsync(query, ct);
        var result = await page.ConfigureAwait(false);
        _fetchedCounts = await counts.ConfigureAwait(false) is { IsOk: true } c ? c.Value : null;
        return result;
    }

    protected override void ApplyPrefilter(ListPrefilter prefilter)
    {
        base.ApplyPrefilter(prefilter);
        if (Enum.TryParse<AppointmentStatus>(prefilter.Status, true, out var status))
        {
            _statusFilter = StatusOptions.First(o => o.Value == status);
            ViewState.Filters[StatusKey] = status.ToString();
            OnPropertyChanged(nameof(StatusFilter));
            OnPropertyChanged(nameof(HasStatusFilter));
            OnFiltersChanged();
        }

        if (prefilter.Status is not null)
        {
            // A link to a status (a dashboard card): every appointment with it, whatever its day. Not remembered.
            _view = null;
            RaiseViewChanged();
        }

        // A link to one appointment (a receipt, the dashboard's agenda) keeps a tab selected: once the appointment is in the panel the screen moves to the tab
        // that holds it (ShowViewOf).
    }

    /// <summary>
    /// The tab that lists this appointment, by the server's own rule for the views: cancelled ones under Cancelled, today's under Today, earlier ones under Past, and
    /// later pending ones under Upcoming. Moves there when another tab is open (a booking for tomorrow made from "Today"; an appointment another screen opened).
    /// </summary>
    protected override bool ShowViewOf(AppointmentDetail appointment)
    {
        var day = RecordFormatting.LocalDate(appointment.SlotStartUtc, Zone);
        AppointmentView? holds = appointment.Status == AppointmentStatus.Cancelled ? AppointmentView.Cancelled
            : day == Today ? AppointmentView.Today
            : day < Today ? AppointmentView.Past
            : appointment.Status == AppointmentStatus.Pending ? AppointmentView.Upcoming
            : null;
        if (holds is null || _view is null || _view == holds)
        {
            return false; // already listed (every appointment is shown, or its tab is open)
        }

        _view = holds;
        Page = 1;
        RaiseViewChanged();
        return true;
    }

    /// <summary>Another screen asked for this one: a prefilter, or "book an appointment for this donor" (a <see cref="RelatedRecord"/> of kind Donor).</summary>
    void INavigationParameterReceiver.ReceiveParameter(object parameter)
    {
        if (parameter is RelatedRecord { Kind: RecordKind.Donor } donor)
        {
            _donorFilter = new RecordOption(donor.Id, donor.Name, null);
            ViewState.Filters[DonorIdKey] = donor.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ViewState.Filters[DonorNameKey] = donor.Name;
            _bookFor = _donorFilter;
            _openBooking = true;
            OnPropertyChanged(nameof(DonorFilter));
            OnFiltersChanged();
            return;
        }

        ReceiveParameter(parameter);
    }

    protected override void Apply()
    {
        ViewCounts = _fetchedCounts;
        OnFiltersChanged(); // the empty state reads the tabs' counts: an empty tab says where the matches are
        base.Apply();
        // The agenda's next appointment: the first one of the day still pending, in time order. The rows' agenda cells bind AgendaVersion, so they read it again.
        _nextId = IsAgenda ? Items.Where(a => a.Status == AppointmentStatus.Pending).OrderBy(a => a.SlotStartUtc).Select(a => (int?)a.Id).FirstOrDefault() : null;
        AgendaVersion++;
        OnPropertyChanged(nameof(AgendaVersion));
        OnPropertyChanged(nameof(ViewCounts));
        if (_openBooking)
        {
            _openBooking = false;
            AddCommand.Execute(null);
        }
    }

    protected override int IdOf(AppointmentListItem item) => item.Id;

    protected override string NameOf(AppointmentListItem item) => item.DonorName;

    protected override int IdOfDetail(AppointmentDetail detail) => detail.Id;

    protected override string NameOfDetail(AppointmentDetail detail) => $"{detail.DonorName}, {RecordFormatting.When(detail.SlotStartUtc, Zone)}";

    protected override Task<ApiResult<AppointmentDetail>> GetDetailAsync(int id, CancellationToken ct) => _api.GetAppointmentAsync(id, ct);

    protected override IReadOnlyList<DetailFact> FactsOf(AppointmentDetail a)
    {
        var facts = new List<DetailFact>
        {
            new("Donor", RecordFormatting.NameWithCode(a.DonorName, RecordCodes.Donor(a.DonorId))),
            new("Blood group", a.DonorBloodType.ToDisplay()),
            new("Appointment", RecordFormatting.When(a.SlotStartUtc, Zone)),
            new("Status", StatusText(a.Status)),
            new("Booked on", RecordFormatting.Stamp(a.PlacedAtUtc, Zone)),
        };
        if (!string.IsNullOrWhiteSpace(a.OverrideReason))
        {
            facts.Add(new DetailFact("Eligibility override", a.OverrideReason));
        }

        if (a.ReceiptId is { } receipt)
        {
            facts.Add(new DetailFact("Receipt", ReceiptCode(receipt)));
        }

        return facts;
    }

    /// <summary>An appointment is cancelled, never deleted; this exists only because the list base class requires it, and no view offers Delete here.</summary>
    protected override Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct) => _api.CancelAppointmentAsync(id, ct);

    protected override Task<ApiResult<AppointmentDetail>> RestoreAsync(AppointmentDetail deleted, CancellationToken ct) =>
        Task.FromResult(ApiResult<AppointmentDetail>.Fail(new ApiProblem(409, ProblemCodes.Conflict, "A cancelled appointment can't be restored. Book a new one.")));

    protected override RecordEditorViewModel CreateEditor(AppointmentDetail? existing)
    {
        if (existing is not null)
        {
            return AppointmentEditorViewModel.ForReschedule(_api, Toasts, Dialogs, existing, Clock, Zone);
        }

        var donor = _bookFor;
        _bookFor = null;
        return AppointmentEditorViewModel.ForNew(_api, Toasts, Dialogs, Clock, Zone, donor);
    }

    private async Task RecordDonationAsync()
    {
        if (Detail is not { } appointment)
        {
            return;
        }

        var dialog = new RecordDonationDialogViewModel(_api, appointment, Clock, Zone);
        bool done;
        try
        {
            done = await _forms.ShowAsync(dialog).ConfigureAwait(true);
        }
        finally
        {
            dialog.Dispose();
        }

        if (!done)
        {
            return;
        }

        var receiptId = dialog.Receipt?.Id;
        Toasts.Show(
            ToastKind.Success,
            receiptId is null ? $"Donation recorded for '{appointment.DonorName}'." : $"Donation recorded for '{appointment.DonorName}'. Receipt {ReceiptCode(receiptId.Value)} issued.",
            receiptId is null ? null : "View receipt",
            receiptId is null ? null : () => _receipts.ShowAsync(receiptId.Value, isDuplicate: false));
        await RefreshAfterActionAsync(appointment.Id).ConfigureAwait(true);
    }

    private async Task CancelSelectedAsync()
    {
        if (Detail is not { } appointment)
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            "Cancel appointment?",
            $"{appointment.DonorName}'s appointment on {RecordFormatting.DayAndTime(appointment.SlotStartUtc, Zone)} will be cancelled, and the slot becomes free.",
            "Cancel appointment",
            "Keep appointment").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        ApiResult result = null!;
        await RunBusyAsync(async () => result = await RunOperationAsync(ct => _api.CancelAppointmentAsync(appointment.Id, ct)).ConfigureAwait(true)).ConfigureAwait(true);
        if (!result.IsOk)
        {
            Toasts.Show(ToastKind.Error, ProblemText.For(result.Problem!, RecordType, appointment.DonorName));
            return;
        }

        Toasts.Show(ToastKind.Success, $"Appointment for '{appointment.DonorName}' cancelled.");
        await RefreshAfterActionAsync(appointment.Id).ConfigureAwait(true);
    }

    private async Task RefreshAfterActionAsync(int id)
    {
        await RefreshAsync().ConfigureAwait(true);
        await LoadDetailAsync(id).ConfigureAwait(true);
    }

    public static string StatusText(AppointmentStatus status) => status == AppointmentStatus.NoShow ? "No show" : status.ToString();
}

/// <summary>Where an appointment stands in today's agenda.</summary>
public enum AgendaState
{
    /// <summary>Not on the Today tab.</summary>
    None,

    /// <summary>The donation is recorded.</summary>
    Done,

    /// <summary>The first appointment still to come: the one to check in.</summary>
    Next,

    /// <summary>A later appointment still to come.</summary>
    Waiting,

    /// <summary>The donor did not come.</summary>
    Missed,

    /// <summary>Cancelled.</summary>
    Cancelled,
}
