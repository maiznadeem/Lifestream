using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Common;
using Lifestream.Contracts.Donors;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Appointments;

/// <summary>What the eligibility banner shows before a donor is booked or a donation is recorded (FEAT-09).</summary>
public enum BannerState
{
    /// <summary>No donor chosen yet: nothing to show.</summary>
    Hidden,

    Checking,

    Eligible,

    NotEligible,

    /// <summary>The check could not be made (the server was unreachable); the server still decides when the form is sent.</summary>
    Unavailable,
}

/// <summary>
/// The eligibility banner: a headline (eligible or not), the facts that decide it (age, days since the last donation) and the reasons.
/// The rule is the shared Domain rule the server applies; the banner only explains it. An ineligible donor can still be booked or
/// recorded, but only with a reason (<see cref="RequiresReason"/>). The medical hold is one of the facts it shows.
/// </summary>
public sealed partial class EligibilityBannerViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(RequiresReason), nameof(Headline), nameof(IsEligible))]
    public partial BannerState State { get; set; } = BannerState.Hidden;

    /// <summary>The facts and reasons, one line each.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Lines { get; set; } = [];

    public bool IsVisible => State != BannerState.Hidden;

    public bool IsEligible => State == BannerState.Eligible;

    /// <summary>True for an ineligible donor: the form then asks for an override reason.</summary>
    public bool RequiresReason => State == BannerState.NotEligible;

    public string Headline => State switch
    {
        BannerState.Checking => "Checking eligibility...",
        BannerState.Eligible => "Eligible to donate",
        BannerState.NotEligible => "Not eligible to donate",
        BannerState.Unavailable => "Eligibility could not be checked",
        _ => string.Empty,
    };

    public void Hide()
    {
        State = BannerState.Hidden;
        Lines = [];
    }

    public void ShowChecking()
    {
        State = BannerState.Checking;
        Lines = [];
    }

    public void ShowUnavailable()
    {
        State = BannerState.Unavailable;
        Lines = ["The server will check again when you send this."];
    }

    /// <summary>The server refused a booking or a donation because the donor is not eligible: show its reasons and ask for an override reason.</summary>
    public void ShowRefused(IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        Lines = [.. reasons];
        State = BannerState.NotEligible;
    }

    /// <summary>Shows the result for a donor as of a day, using the shared rule (age, days since the last donation and the medical hold).</summary>
    public void Show(DonorDetail donor, DateOnly asOf, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(donor);
        Show(donor, Eligibility.Evaluate(donor.DateOfBirth, donor.LastDonated, asOf, donor.MedicalHold), today, asOf);
    }

    /// <summary>Shows a result the server computed (the eligibility endpoint).</summary>
    public void Show(DonorDetail donor, DonorEligibility server, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(donor);
        ArgumentNullException.ThrowIfNull(server);
        Show(donor, new EligibilityResult(server.IsEligible, server.Reasons, server.NextEligibleDate), today, today);
    }

    private void Show(DonorDetail donor, EligibilityResult result, DateOnly today, DateOnly asOf)
    {
        var lines = new List<string> { $"Age: {Eligibility.AgeOn(donor.DateOfBirth, asOf)} years" };
        lines.Add(donor.LastDonated is { } last
            ? $"Last donation: {Formatting.Date(last)} ({DaysText(today.DayNumber - last.DayNumber)})"
            : "Last donation: none recorded");
        lines.Add(donor.MedicalHold ? "Medical hold: yes" : "Medical hold: none");
        lines.AddRange(result.Reasons);
        if (result.NextEligibleDate is { } next && !result.Reasons.Any(r => r.Contains(Formatting.Date(next), StringComparison.Ordinal)))
        {
            lines.Add($"Next eligible: {Formatting.Date(next)}");
        }

        Lines = lines;
        State = result.IsEligible ? BannerState.Eligible : BannerState.NotEligible;
    }

    private static string DaysText(int days) => days switch
    {
        < 0 => "in the future",
        0 => "today",
        1 => "1 day ago",
        _ => $"{days} days ago",
    };
}
