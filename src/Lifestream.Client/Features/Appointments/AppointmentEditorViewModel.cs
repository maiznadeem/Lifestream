using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Lookups;

namespace Lifestream.Client.Features.Appointments;

/// <summary>
/// The booking form (a new appointment) and the reschedule form (an existing one), FEAT-09, FEAT-11, FEAT-13. The date can't be in the
/// past, the time comes from the clinic's slots in the user's local time with taken slots disabled, an ineligible donor needs a reason, and no
/// mix of empty or odd inputs reaches the API: each is a message under its field. A reschedule sends the date and the time, and keeps the existing
/// time of day when only the date was changed.
/// </summary>
public sealed partial class AppointmentEditorViewModel : RecordEditorViewModel
{
    public const int MaxReasonLength = 300;

    private readonly ILifestreamApiClient _api;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly int? _id;
    private readonly AppointmentDetail? _existing;
    private string _rowVersion = string.Empty;
    private DateOnly? _baselineDate;
    private TimeOnly? _baselineTime;
    private DonorDetail? _donor;
    private AppointmentDetail? _reloaded;
    private CancellationTokenSource? _slotsCts;
    private CancellationTokenSource? _donorCts;
    private bool _slotsKnown;
    private DateOnly? _slotsDate; // the day the slots in the list belong to; null while a day is loading or when its times could not be loaded
    private IReadOnlyList<string>? _refusedReasons; // set when the API refused the donor as ineligible (applied on the UI thread after the save)
    private bool _syncing;
    private bool _slotWasTaken;

    private AppointmentEditorViewModel(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, TimeProvider clock, TimeZoneInfo? zone, AppointmentDetail? existing, RecordOption? donor)
        : base(toasts, dialogs, clock, "appointment", existing is null)
    {
        _api = api;
        _time = clock;
        _zone = zone ?? TimeZoneInfo.Local;
        _existing = existing;
        _id = existing?.Id;
        Banner.PropertyChanged += OnBannerChanged;
        DeclareFormFields(nameof(DonorId), nameof(Date), nameof(Time), nameof(OverrideReason));
        if (existing is not null)
        {
            Load(existing);
        }
        else if (donor is not null)
        {
            DonorOption = donor;
            DonorName = donor.Title;
        }

        if (existing is not null)
        {
            OverrideReason = existing.OverrideReason ?? string.Empty; // the reason the appointment already carries, offered again for the new day
            PendingDonor = LoadDonorAsync(); // the new day is judged for eligibility like a booking, so the donor's facts are needed here too
        }

        // A slot that was just taken by someone else: after the failed save, show the list again with that slot disabled.
        SaveCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AsyncRelayCommand.IsRunning) && !SaveCommand.IsRunning && _slotWasTaken)
            {
                _slotWasTaken = false;
                PendingSlots = LoadSlotsAsync();
            }

            if (e.PropertyName == nameof(AsyncRelayCommand.IsRunning) && !SaveCommand.IsRunning && _refusedReasons is { } reasons)
            {
                _refusedReasons = null;
                Banner.ShowRefused(reasons); // the donor turned out not to be eligible: the reason field appears
            }
        };
    }

    /// <summary>A form for a new appointment, optionally for a donor already known (opened from a donor).</summary>
    public static AppointmentEditorViewModel ForNew(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, TimeProvider? clock = null, TimeZoneInfo? zone = null, RecordOption? donor = null) =>
        new(api, toasts, dialogs, clock ?? TimeProvider.System, zone, null, donor);

    /// <summary>A form that moves an existing appointment.</summary>
    public static AppointmentEditorViewModel ForReschedule(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, AppointmentDetail appointment, TimeProvider? clock = null, TimeZoneInfo? zone = null) =>
        new(api, toasts, dialogs, clock ?? TimeProvider.System, zone, appointment, null);

    public new string Title => IsNew ? "Book appointment" : "Reschedule appointment";

    public new string SaveText => IsNew ? "Book appointment" : "Save new time";

    public new string SaveBusyText => IsNew ? "Booking..." : "Saving...";

    /// <summary>The earliest date the date field accepts: today (a past date can't be booked).</summary>
    public DateOnly EarliestDate => LocalToday;

    /// <summary>The slot list the picker shows (local times).</summary>
    public ObservableCollection<SlotChoice> Slots { get; } = [];

    public EligibilityBannerViewModel Banner { get; } = new();

    private void OnBannerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EligibilityBannerViewModel.State))
        {
            RaiseCanSaveChanged();
        }
    }

    /// <summary>Completes when the slot list started by the last date change has finished (for tests).</summary>
    internal Task PendingSlots { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the donor check started by the last donor change has finished (for tests).</summary>
    internal Task PendingDonor { get; private set; } = Task.CompletedTask;

    public bool ShowDonorPicker => IsNew;

    /// <summary>The donor chosen in the record picker (the name and the id), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DonorId))]
    public partial RecordOption? DonorOption { get; set; }

    public int? DonorId => DonorOption?.Id;

    /// <summary>The donor's name: read-only text when rescheduling.</summary>
    [ObservableProperty]
    public partial string DonorName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateOnly? Date { get; set; }

    [ObservableProperty]
    public partial TimeOnly? SelectedTime { get; set; }

    /// <summary>The time as typed: "13:00" or "1:00 PM". It always shows the chosen slot, and typing a time chooses that slot.</summary>
    [ObservableProperty]
    public partial string TimeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SlotChoice? SelectedSlot { get; set; }

    [ObservableProperty]
    public partial string OverrideReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoadingSlots { get; set; }

    /// <summary>The action waits for the chosen day's times (a slot of the old day must never be sent for the new one) and for the donor check (the answer decides whether a reason is needed).</summary>
    protected override bool CanSave => !IsLoadingSlots && Banner.State != BannerState.Checking;

    partial void OnIsLoadingSlotsChanged(bool value) => RaiseCanSaveChanged();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSlotsMessage))]
    public partial string? SlotsMessage { get; set; }

    public bool HasSlotsMessage => !string.IsNullOrEmpty(SlotsMessage);

    // The field names the view binds to ("[Time]", FormFocus.FieldName).
    public const string Time = "Time";

    public override bool HasUnsavedChanges => IsNew
        ? DonorId is not null || Date is not null || SelectedTime is not null || !string.IsNullOrWhiteSpace(OverrideReason)
        : Date != _baselineDate || SelectedTime != _baselineTime;

    protected override string RecordName => !string.IsNullOrEmpty(_donor?.Name) ? _donor.Name : string.IsNullOrEmpty(DonorName) ? "appointment" : DonorName;

    private void Load(AppointmentDetail appointment)
    {
        _rowVersion = appointment.RowVersion;
        DonorOption = new RecordOption(appointment.DonorId, appointment.DonorName, null);
        DonorName = appointment.DonorName;

        // The order matters. Setting the date asks for that day's times, and their answer runs ReconcileTime, which reads the baseline and the
        // chosen time to tell the appointment's own time from a newly chosen one. So both are in place before the date is set: an answer that
        // arrives at any moment (on another thread when there is no dispatcher) finds the form's own time, and never clears it as "past".
        var date = RecordFormatting.LocalDate(appointment.SlotStartUtc, _zone);
        var time = RecordFormatting.LocalTime(appointment.SlotStartUtc, _zone);
        _baselineDate = date;
        _baselineTime = time;
        SelectedTime = time;
        Date = date;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    // ---- reacting to the user's input -------------------------------------------------------------------------------

    partial void OnDonorOptionChanged(RecordOption? value)
    {
        FieldEdited(nameof(DonorId));
        if (IsNew)
        {
            PendingDonor = LoadDonorAsync();
        }
    }

    partial void OnDateChanged(DateOnly? value)
    {
        FieldEdited(nameof(Date));
        RefreshBanner();

        // The old day's times are gone the moment the date changes: until the new day's times arrive nothing can be chosen from them or sent with them.
        _syncing = true;
        SelectedSlot = null;
        _syncing = false;
        Slots.Clear();
        _slotsKnown = false;
        _slotsDate = null;
        PendingSlots = LoadSlotsAsync();
    }

    partial void OnSelectedTimeChanged(TimeOnly? value)
    {
        FieldEdited(Time);
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            TimeText = value is { } t ? SlotTime.Format(t) : string.Empty;
            SelectedSlot = value is { } local ? Slots.FirstOrDefault(s => s.Local == local) : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnTimeTextChanged(string value)
    {
        FieldEdited(Time);
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            SelectedTime = SlotTime.TryParse(value, out var parsed) ? parsed : null;
            SelectedSlot = SelectedTime is { } local ? Slots.FirstOrDefault(s => s.Local == local) : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnSelectedSlotChanged(SlotChoice? value)
    {
        if (_syncing || value is null)
        {
            return;
        }

        if (!value.IsAvailable)
        {
            SelectedSlot = null; // a taken or past slot can't be chosen
            return;
        }

        SelectedTime = value.Local;
    }

    partial void OnOverrideReasonChanged(string value) => FieldEdited(nameof(OverrideReason));

    // ---- loading the donor and the slots ----------------------------------------------------------------------------

    private void RefreshBanner()
    {
        if (_donor is { } donor)
        {
            Banner.Show(donor, Date ?? LocalToday, LocalToday);
        }
    }

    private async Task LoadDonorAsync()
    {
        _donorCts?.Cancel();
        _donor = null;
        if (DonorId is not { } id)
        {
            Banner.Hide();
            return;
        }

        var cts = new CancellationTokenSource();
        _donorCts = cts;
        Banner.ShowChecking();
        try
        {
            var result = await Operations.RunAsync(ct => _api.GetDonorAsync(id, ct), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (!result.IsOk)
            {
                Banner.ShowUnavailable();
                return;
            }

            _donor = result.Value;
            DonorName = result.Value.Name;
            RefreshBanner();
        }
        catch (OperationCanceledException)
        {
            // A newer donor replaced this one.
        }
    }

    private async Task LoadSlotsAsync()
    {
        _slotsCts?.Cancel();
        if (Date is not { } day)
        {
            Slots.Clear();
            SlotsMessage = null;
            _slotsKnown = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _slotsCts = cts;
        IsLoadingSlots = true;
        SlotsMessage = null;
        try
        {
            var result = await Operations.RunAsync(ct => _api.GetSlotsAsync(day, ct), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Slots.Clear();
            _slotsDate = null;
            if (!result.IsOk)
            {
                _slotsKnown = false;
                SlotsMessage = "Couldn't load the times for that day. Choose the date again to retry.";
                return;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var slot in result.Value)
            {
                var choice = SlotChoice.From(slot, _zone, now);
                // The appointment being moved occupies its own slot; that slot is still free for it.
                Slots.Add(_existing is not null && choice.StartUtc == DateTime.SpecifyKind(_existing.SlotStartUtc, DateTimeKind.Utc) ? choice with { IsTaken = false } : choice);
            }

            _slotsKnown = true;
            _slotsDate = day;
            SlotsMessage = Slots.Count == 0 ? "The clinic has no times on that day." : null;
            ReconcileTime();
        }
        catch (OperationCanceledException)
        {
            // A newer date replaced this one.
        }
        finally
        {
            if (ReferenceEquals(_slotsCts, cts))
            {
                IsLoadingSlots = false;
            }
        }
    }

    /// <summary>After the slots of a new day arrive: keep the chosen time when that slot is free (a reschedule keeps its time of day), otherwise ask for a new one.</summary>
    private void ReconcileTime()
    {
        if (SelectedTime is not { } time)
        {
            return;
        }

        if (IsUnmovedOriginal(time))
        {
            return; // the appointment's own time on its own day, even if that time has passed: nothing about it was changed
        }

        var match = Slots.FirstOrDefault(s => s.Local == time);
        if (match is { IsAvailable: true })
        {
            _syncing = true;
            SelectedSlot = match;
            _syncing = false;
            return;
        }

        SelectedTime = null; // taken, past, or not offered on that day: the user must choose another
        if (match is not null || Slots.Count > 0)
        {
            SetServerError(Time, $"{SlotTime.Format(time)} isn't available on that day. Choose another time.");
            ValidateOnBlur(Time);
        }
    }

    // ---- validation -------------------------------------------------------------------------------------------------

    protected override string? MapServerField(string serverField) => serverField.ToLowerInvariant() switch
    {
        "slotstartutc" => Time,
        "donorid" => nameof(DonorId),
        "overridereason" => nameof(OverrideReason),
        _ => null,
    };

    protected override string? ValidateFormField(string field)
    {
        switch (field)
        {
            case nameof(DonorId):
                return IsNew && DonorId is null ? "Choose a donor." : null;
            case nameof(Date):
                if (Date is not { } date)
                {
                    return "Choose a date.";
                }

                return date < LocalToday ? "Appointments can't be booked in the past. Choose today or a later date." : null;
            case Time:
                return ValidateTime();
            case nameof(OverrideReason):
                var reason = OverrideReason.Trim();
                if (reason.Length > MaxReasonLength)
                {
                    return $"The reason can be at most {MaxReasonLength} characters.";
                }

                return Banner.RequiresReason && reason.Length == 0 ? "This donor isn't eligible. Enter the reason for booking anyway." : null;
            default:
                return null;
        }
    }

    /// <summary>True when rescheduling and the date and time are still the appointment's own.</summary>
    private bool IsUnmovedOriginal(TimeOnly time) => !IsNew && Date == _baselineDate && time == _baselineTime;

    /// <summary>The slot at a local time on the day the list was loaded for, or null: a slot of any other day is never a match (FEAT-11).</summary>
    private SlotChoice? SlotAt(TimeOnly time) =>
        _slotsDate is { } day && day == Date ? Slots.FirstOrDefault(s => s.Local == time && RecordFormatting.LocalDate(s.StartUtc, _zone) == day) : null;

    private string? ValidateTime()
    {
        if (SelectedTime is not { } time)
        {
            return TimeText.Trim().Length > 0 ? "Use a time like 13:00 or 1:00 PM." : "Choose a time.";
        }

        if (IsUnmovedOriginal(time))
        {
            return null;
        }

        if (IsLoadingSlots)
        {
            return "The times of that day are still loading.";
        }

        if (SlotAt(time) is { } slot)
        {
            if (slot.IsTaken)
            {
                return "That time is already taken. Choose another.";
            }

            if (slot.IsPast)
            {
                return "That time has already passed. Choose a later time.";
            }

            return null;
        }

        if (_slotsKnown && _slotsDate == Date && Slots.Count > 0)
        {
            return "The clinic has no slot at that time. Choose one of the times shown.";
        }

        // No slot list to check against (it could not be loaded, or the time was kept from the old appointment): check the instant itself.
        if (Date is not { } day)
        {
            return null;
        }

        return TryToUtc(day, time) switch
        {
            null => "That time doesn't exist on that day. Choose another.",
            { } instant when instant <= _time.GetUtcNow().UtcDateTime => "That time has already passed. Choose a later time.",
            _ => null,
        };
    }

    /// <summary>The UTC instant of a local date and time, or null when that local time doesn't exist (a clock change).</summary>
    private DateTime? TryToUtc(DateOnly day, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(time), DateTimeKind.Unspecified);
        return _zone.IsInvalidTime(local) ? null : TimeZoneInfo.ConvertTimeToUtc(local, _zone);
    }

    /// <summary>The slot to send: the chosen slot's own instant, or the date and time converted to UTC (a reschedule that kept its time of day).</summary>
    private DateTime SlotUtc()
    {
        var time = SelectedTime!.Value;
        if (SlotAt(time) is { } slot)
        {
            return slot.StartUtc; // only a slot of the chosen day
        }

        return DateTime.SpecifyKind(TryToUtc(Date!.Value, time)!.Value, DateTimeKind.Utc);
    }

    // ---- sending ----------------------------------------------------------------------------------------------------

    /// <summary>The body of a booking.</summary>
    public BookAppointmentRequest BuildBooking() =>
        new(DonorId!.Value, SlotUtc(), Banner.RequiresReason ? OverrideReason.Trim() : null);

    /// <summary>The PATCH body of a reschedule: the row version and the slot, always date and time together.</summary>
    public RescheduleAppointmentRequest BuildReschedule() => new(_rowVersion, SlotUtc()) { OverrideReason = Banner.RequiresReason ? OverrideReason.Trim() : null };

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareSubmit()
    {
        if (_id is { } id)
        {
            if (!HasUnsavedChanges)
            {
                SavedRecord = _existing;
                return _ => Task.FromResult<ApiProblem?>(null); // nothing changed: nothing to send
            }

            var update = BuildReschedule();
            return async ct =>
            {
                var result = await _api.RescheduleAppointmentAsync(id, update, ct).ConfigureAwait(false);
                if (!result.IsOk)
                {
                    return Translate(result.Problem!);
                }

                SavedRecord = result.Value;
                _rowVersion = result.Value.RowVersion;
                return null;
            };
        }

        var booking = BuildBooking();
        return async ct =>
        {
            var result = await _api.BookAppointmentAsync(booking, ct).ConfigureAwait(false);
            if (!result.IsOk)
            {
                return Translate(result.Problem!);
            }

            SavedRecord = result.Value;
            return null;
        };
    }

    /// <summary>A taken slot or an ineligible donor becomes a message under the field it concerns, in plain words.</summary>
    private ApiProblem Translate(ApiProblem problem)
    {
        if (problem.Code == ProblemCodes.SlotTaken)
        {
            _slotWasTaken = true;
            return FieldProblem(problem, "slotStartUtc", "Someone else just took that time. Choose another.");
        }

        if (problem.Code == ProblemCodes.Ineligible)
        {
            _refusedReasons = problem.Errors is { } errors && errors.TryGetValue("reasons", out var reasons) ? [.. reasons] : [];
            return FieldProblem(problem, "overrideReason", "This donor isn't eligible. Enter the reason for booking anyway.");
        }

        return problem;
    }

    private static ApiProblem FieldProblem(ApiProblem problem, string field, string message) =>
        problem with { Code = ProblemCodes.Validation, Errors = new Dictionary<string, string[]> { [field] = [message] } };

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareReload()
    {
        var id = _id ?? 0;
        return async ct =>
        {
            var result = await _api.GetAppointmentAsync(id, ct).ConfigureAwait(false);
            _reloaded = result.IsOk ? result.Value : null;
            return result.Problem;
        };
    }

    protected override void ApplyReloaded()
    {
        if (_reloaded is { } appointment)
        {
            Load(appointment);
            PendingSlots = LoadSlotsAsync();
        }
    }

    protected override void AcceptAsBaseline()
    {
        _baselineDate = Date;
        _baselineTime = SelectedTime;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Banner.PropertyChanged -= OnBannerChanged;
            _slotsCts?.Cancel();
            _slotsCts?.Dispose();
            _slotsCts = null;
            _donorCts?.Cancel();
            _donorCts?.Dispose();
            _donorCts = null;
        }

        base.Dispose(disposing);
    }
}
