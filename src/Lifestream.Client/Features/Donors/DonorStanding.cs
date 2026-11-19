using System.Globalization;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Donors;

/// <summary>Where a donor stands today, as one short word for the list's eligibility column and the side panel's card.</summary>
public enum DonorStandingKind
{
    /// <summary>Eligible and has donated before.</summary>
    Eligible,

    /// <summary>Eligible and has never donated.</summary>
    FirstTime,

    /// <summary>Waiting for the gap since the last donation to pass; <see cref="DonorStanding.NextDate"/> says until when.</summary>
    Waiting,

    /// <summary>A clinician has put the donor on hold.</summary>
    OnHold,

    /// <summary>Not eligible for another reason (too young or too old).</summary>
    NotEligible,
}

/// <summary>
/// A donor's eligibility today, worked out with the same rules as the API (Domain <see cref="Eligibility"/>) from what the list already carries, so the list can show it
/// on every row without a call per donor. The side panel still asks the API for the authoritative answer.
/// </summary>
public sealed record DonorStanding(DonorStandingKind Kind, string Text, DateOnly? NextDate)
{
    public static DonorStanding Of(DateOnly dateOfBirth, DateOnly? lastDonated, bool medicalHold, DateOnly today)
    {
        if (medicalHold)
        {
            return new(DonorStandingKind.OnHold, "On hold", null);
        }

        var result = Eligibility.Evaluate(dateOfBirth, lastDonated, today);
        if (result.IsEligible)
        {
            return lastDonated is null ? new(DonorStandingKind.FirstTime, "First-time", null) : new(DonorStandingKind.Eligible, "Eligible", null);
        }

        // Only the gap since the last donation stands in the way: the date it ends ("29 Jan"; never more than 90 days away, so the day and month are enough).
        return result is { NextEligibleDate: { } next, Reasons.Count: 1 }
            ? new(DonorStandingKind.Waiting, "Until " + next.ToString("dd MMM", CultureInfo.InvariantCulture), next)
            : new(DonorStandingKind.NotEligible, "Not eligible", null);
    }

    /// <summary>The donor's code as the screens show it, "D-0175".</summary>
    public static string Code(int id) => $"D-{id:0000}";
}
