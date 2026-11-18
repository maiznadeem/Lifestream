namespace Lifestream.Domain;

public sealed record EligibilityResult(bool IsEligible, IReadOnlyList<string> Reasons, DateOnly? NextEligibleDate);

/// <summary>Donor eligibility: age 18-60 inclusive and at least 90 days since the last donation.</summary>
public static class Eligibility
{
    public const int MinAgeYears = 18;
    public const int MaxAgeYears = 60;
    public const int MinDaysBetweenDonations = 90;

    public static EligibilityResult Evaluate(DateOnly dateOfBirth, DateOnly? lastDonation, DateOnly today)
    {
        var reasons = new List<string>();
        var age = AgeOn(dateOfBirth, today);

        if (age < MinAgeYears)
        {
            reasons.Add($"Donors must be at least {MinAgeYears} years old.");
        }
        else if (age > MaxAgeYears)
        {
            reasons.Add($"Donors must be {MaxAgeYears} years old or younger.");
        }

        DateOnly? next = null;
        if (lastDonation is { } last)
        {
            var nextDate = last.AddDays(MinDaysBetweenDonations);
            if (nextDate > today)
            {
                next = nextDate;
                reasons.Add($"At least {MinDaysBetweenDonations} days are needed between donations; next eligible on {nextDate:dd MMM yyyy}.");
            }
        }

        return new EligibilityResult(reasons.Count == 0, reasons, next);
    }

    public static int AgeOn(DateOnly dateOfBirth, DateOnly today)
    {
        var age = today.Year - dateOfBirth.Year;
        if (today < dateOfBirth.AddYears(age))
        {
            age--;
        }

        return age;
    }
}
