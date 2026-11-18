using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Receipts;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Appointments;

/// <summary>
/// The "Record donation" dialog, opened from an appointment (FEAT-16, FEAT-09). The donor, blood type and time come from the appointment and
/// can't be typed; the eligibility banner shows the server's verdict with the age and days since the last donation, and an ineligible donor
/// needs a reason before the donation can be recorded. A successful call issues the receipt, which is available as <see cref="Receipt"/>.
/// </summary>
public sealed partial class RecordDonationDialogViewModel : DialogViewModel
{
    /// <summary>The most units one donation may record (the API's setting <c>Clinic:MaxDonationUnits</c>, which defaults to 2).</summary>
    public const int MaxUnits = 2;

    public const int MaxNotesLength = 500;

    private readonly ILifestreamApiClient _api;
    private readonly TimeProvider _time;
    private readonly AppointmentDetail _appointment;
    private readonly TimeZoneInfo _zone;

    public RecordDonationDialogViewModel(ILifestreamApiClient api, AppointmentDetail appointment, TimeProvider? clock = null, TimeZoneInfo? zone = null)
        : base("Record donation", "Record donation", "Recording...")
    {
        _api = api;
        _appointment = appointment;
        _time = clock ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        UnitChoices = [.. Enumerable.Range(1, MaxUnits).Select(n => new Choice<int>(RecordFormatting.Units(n), n))];
        DeclareFields(nameof(Units), nameof(OverrideReason), nameof(Notes));
        PendingCheck = CheckEligibilityAsync();
    }

    public string DonorName => _appointment.DonorName;

    public string BloodTypeText => _appointment.DonorBloodType.ToDisplay();

    public string AppointmentText => RecordFormatting.When(_appointment.SlotStartUtc, _zone);

    public IReadOnlyList<Choice<int>> UnitChoices { get; }

    public EligibilityBannerViewModel Banner { get; } = new();

    /// <summary>The receipt the API issued; set after a successful call.</summary>
    public ReceiptDetail? Receipt { get; private set; }

    /// <summary>Completes when the eligibility check started when the dialog opened has finished (for tests).</summary>
    internal Task PendingCheck { get; private set; }

    [ObservableProperty]
    public partial int Units { get; set; } = 1;

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OverrideReason { get; set; } = string.Empty;

    protected override string RecordType => "donation";

    private DateOnly LocalToday => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private async Task CheckEligibilityAsync()
    {
        Banner.ShowChecking();
        var id = _appointment.DonorId;
        var donorTask = Task.Run(() => _api.GetDonorAsync(id));
        var eligibilityTask = Task.Run(() => _api.GetDonorEligibilityAsync(id));
        var donor = await donorTask.ConfigureAwait(true);
        var eligibility = await eligibilityTask.ConfigureAwait(true);
        if (!donor.IsOk || !eligibility.IsOk)
        {
            Banner.ShowUnavailable();
            return;
        }

        Banner.Show(donor.Value, eligibility.Value, LocalToday);
        if (Banner.RequiresReason && string.IsNullOrWhiteSpace(OverrideReason) && !string.IsNullOrWhiteSpace(_appointment.OverrideReason))
        {
            OverrideReason = _appointment.OverrideReason; // the reason given when it was booked
        }
    }

    protected override string? ValidateField(string field)
    {
        switch (field)
        {
            case nameof(Units):
                return Units is < 1 or > MaxUnits ? $"Choose 1 to {MaxUnits} units." : null;
            case nameof(OverrideReason):
                if (OverrideReason.Trim().Length > AppointmentEditorViewModel.MaxReasonLength)
                {
                    return $"The reason can be at most {AppointmentEditorViewModel.MaxReasonLength} characters.";
                }

                return Banner.RequiresReason && string.IsNullOrWhiteSpace(OverrideReason) ? "This donor isn't eligible. Enter the reason for recording the donation anyway." : null;
            case nameof(Notes):
                return ComposeNotes()?.Length > MaxNotesLength ? $"The notes and the reason together can be at most {MaxNotesLength} characters." : null;
            default:
                return null;
        }
    }

    /// <summary>The notes stored on the receipt: the override reason (when one is needed) first, then the typed notes.</summary>
    public string? ComposeNotes()
    {
        var parts = new List<string>();
        if (Banner.RequiresReason && !string.IsNullOrWhiteSpace(OverrideReason))
        {
            parts.Add($"Eligibility override: {OverrideReason.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(Notes))
        {
            parts.Add(Notes.Trim());
        }

        return parts.Count == 0 ? null : string.Join('\n', parts);
    }

    partial void OnUnitsChanged(int value) => FieldEdited(nameof(Units));

    partial void OnNotesChanged(string value) => FieldEdited(nameof(Notes));

    partial void OnOverrideReasonChanged(string value) => FieldEdited(nameof(OverrideReason));

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareSubmit()
    {
        var request = new RecordDonationRequest(Units, ComposeNotes());
        var id = _appointment.Id;
        return async ct =>
        {
            var result = await _api.RecordDonationAsync(id, request, ct).ConfigureAwait(false);
            if (!result.IsOk)
            {
                return result.Problem;
            }

            Receipt = result.Value;
            return null;
        };
    }
}
