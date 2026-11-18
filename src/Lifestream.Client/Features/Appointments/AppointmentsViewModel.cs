using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Navigation;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Lookups;
using Lifestream.Domain;

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

    private readonly ILifestreamApiClient _api;
    private readonly IFormDialogService _forms;
    private readonly IReceiptViewer _receipts;
    private readonly INavigationService _navigation;
    private FilterOption<AppointmentStatus> _statusFilter;
    private RecordOption? _donorFilter;
    private RecordOption? _bookFor;
    private bool _openBooking;

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
        StatusOptions = FilterOptions.WithAll(Enum.GetValues<AppointmentStatus>(), StatusText, "All statuses");
        _statusFilter = StatusOptions[0];
        if (ViewState.Filters.TryGetValue(StatusKey, out var savedStatus) && Enum.TryParse<AppointmentStatus>(savedStatus, out var status))
        {
            _statusFilter = StatusOptions.First(o => o.Value == status);
        }

        if (ViewState.Filters.TryGetValue(DonorIdKey, out var savedId) && int.TryParse(savedId, out var donorId))
        {
            ViewState.Filters.TryGetValue(DonorNameKey, out var savedName);
            _donorFilter = new RecordOption(donorId, savedName ?? $"Donor {donorId}", null);
        }

        RescheduleCommand = new AsyncRelayCommand(_ => EditCommand.ExecuteAsync(), () => IsPending && EditCommand.CanExecute(null));
        RecordDonationCommand = new AsyncRelayCommand(_ => RecordDonationAsync(), () => IsPending && !IsEditing);
        CancelAppointmentCommand = new AsyncRelayCommand(_ => CancelSelectedAsync(), () => IsPending && !IsEditing);
        FilterByDonorCommand = new AsyncRelayCommand((parameter, _) =>
        {
            if (parameter is AppointmentListItem item)
            {
                // The donor's id of the row, not the appointment's id (FEAT-10).
                DonorFilter = new RecordOption(item.DonorId, item.DonorName, null);
            }

            return Task.CompletedTask;
        });
        OpenDonorCommand = new AsyncRelayCommand(async (parameter, _) =>
        {
            if (parameter is AppointmentListItem item)
            {
                await _navigation.NavigateTo<DonorsViewModel>(new ListPrefilter(Search: item.DonorName)).ConfigureAwait(true);
            }
        });
        ViewReceiptCommand = new AsyncRelayCommand(_ => Detail?.ReceiptId is { } id ? _receipts.ShowAsync(id, isDuplicate: true) : Task.CompletedTask, () => Detail?.ReceiptId is not null);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Detail) or nameof(Editor))
            {
                RescheduleCommand.RaiseCanExecuteChanged();
                RecordDonationCommand.RaiseCanExecuteChanged();
                CancelAppointmentCommand.RaiseCanExecuteChanged();
                ViewReceiptCommand.RaiseCanExecuteChanged();
            }
        };
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
        OnPropertyChanged(nameof(DonorFilter));
    }

    /// <summary>The only column that sorts is the appointment time; the API supports "slot" and "placed" and this screen offers the first.</summary>
    public override IReadOnlyList<string> SortKeys { get; } = ["slot"];

    protected override Task<ApiResult<PagedResult<AppointmentListItem>>> FetchSortedAsync(ListRequest request, CancellationToken ct) =>
        _api.GetAppointmentsAsync(new AppointmentQuery(request.Search, _donorFilter?.Id, _statusFilter.Value, BloodTypeValue, null, null, request.Page, request.PageSize, request.Sort), ct);

    protected override void ApplyPrefilter(ListPrefilter prefilter)
    {
        base.ApplyPrefilter(prefilter);
        if (Enum.TryParse<AppointmentStatus>(prefilter.Status, true, out var status))
        {
            _statusFilter = StatusOptions.First(o => o.Value == status);
            ViewState.Filters[StatusKey] = status.ToString();
            OnPropertyChanged(nameof(StatusFilter));
            OnFiltersChanged();
        }
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
        base.Apply();
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
            new("Donor", RecordFormatting.NameWithId(a.DonorName, a.DonorId)),
            new("Blood type", a.DonorBloodType.ToDisplay()),
            new("Appointment", RecordFormatting.When(a.SlotStartUtc, Zone)),
            new("Status", StatusText(a.Status)),
            new("Booked on", RecordFormatting.When(a.PlacedAtUtc, Zone)),
        };
        if (!string.IsNullOrWhiteSpace(a.OverrideReason))
        {
            facts.Add(new DetailFact("Eligibility override", a.OverrideReason));
        }

        if (a.ReceiptId is { } receipt)
        {
            facts.Add(new DetailFact("Receipt", RecordFormatting.IdText(receipt)));
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
            receiptId is null ? $"Donation recorded for '{appointment.DonorName}'." : $"Donation recorded for '{appointment.DonorName}'. Receipt {RecordFormatting.IdText(receiptId.Value)} issued.",
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

        var when = RecordFormatting.When(appointment.SlotStartUtc, Zone);
        var confirmed = await Dialogs.ConfirmAsync(
            "Cancel appointment?",
            $"The appointment of '{appointment.DonorName}' on {when} will be cancelled and the time becomes free for someone else.",
            "Cancel appointment").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        ApiResult result = null!;
        await RunBusyAsync(async () => result = await Task.Run(() => _api.CancelAppointmentAsync(appointment.Id)).ConfigureAwait(true)).ConfigureAwait(true);
        if (!result.IsOk)
        {
            Toasts.Show(ToastKind.Error, ProblemText.For(result.Problem!, RecordType, appointment.DonorName));
            return;
        }

        Toasts.ShowForRecord(ToastKind.Success, "Appointment", appointment.DonorName, "cancelled");
        await RefreshAfterActionAsync(appointment.Id).ConfigureAwait(true);
    }

    private async Task RefreshAfterActionAsync(int id)
    {
        await RefreshAsync().ConfigureAwait(true);
        await LoadDetailAsync(id).ConfigureAwait(true);
    }

    public static string StatusText(AppointmentStatus status) => status == AppointmentStatus.NoShow ? "No show" : status.ToString();
}
