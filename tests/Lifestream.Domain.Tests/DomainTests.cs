namespace Lifestream.Domain.Tests;

public sealed class DomainTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);
    private static readonly DateOnly Adult = new(1990, 1, 1);

    [Fact]
    public void BloodType_DisplayRoundTrips_AllEight()
    {
        Assert.Equal(8, BloodTypeExtensions.All.Count);
        Assert.Equal(["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"], BloodTypeExtensions.All.Select(b => b.ToDisplay()));

        foreach (var bloodType in BloodTypeExtensions.All)
        {
            Assert.True(BloodTypeExtensions.TryParseDisplay(bloodType.ToDisplay(), out var parsed));
            Assert.Equal(bloodType, parsed);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C+")]
    [InlineData("A")]
    public void BloodType_TryParseDisplay_RejectsUnknown(string? text)
    {
        Assert.False(BloodTypeExtensions.TryParseDisplay(text, out _));
    }

    [Fact]
    public void Eligibility_Age17_NotEligible()
    {
        var dob = Today.AddYears(-17);

        var result = Eligibility.Evaluate(dob, null, Today);

        Assert.False(result.IsEligible);
        Assert.Single(result.Reasons);
        Assert.Null(result.NextEligibleDate);
    }

    [Fact]
    public void Eligibility_MedicalHold_NotEligible_WithTheHoldAsTheReason_EvenWhenAgeAndDonationAreFine()
    {
        var result = Eligibility.Evaluate(Adult, null, Today, medicalHold: true);

        Assert.False(result.IsEligible);
        Assert.Equal([Eligibility.MedicalHoldReason], result.Reasons);
        Assert.True(Eligibility.Evaluate(Adult, null, Today, medicalHold: false).IsEligible);
    }

    [Fact]
    public void Eligibility_MedicalHoldAndRecentDonation_ListsBothReasons()
    {
        var result = Eligibility.Evaluate(Adult, Today.AddDays(-10), Today, medicalHold: true);

        Assert.False(result.IsEligible);
        Assert.Equal(2, result.Reasons.Count);
        Assert.Equal(Today.AddDays(80), result.NextEligibleDate);
    }

    [Fact]
    public void Eligibility_Age18Today_Eligible()
    {
        Assert.True(Eligibility.Evaluate(Today.AddYears(-18), null, Today).IsEligible);
    }

    [Fact]
    public void Eligibility_89DaysSinceDonation_NotEligible_NextDateIsDay90()
    {
        var last = Today.AddDays(-89);

        var result = Eligibility.Evaluate(Adult, last, Today);

        Assert.False(result.IsEligible);
        Assert.Equal(last.AddDays(90), result.NextEligibleDate);
        Assert.Equal(Today.AddDays(1), result.NextEligibleDate);
    }

    [Fact]
    public void Eligibility_90DaysSinceDonation_Eligible()
    {
        var result = Eligibility.Evaluate(Adult, Today.AddDays(-90), Today);

        Assert.True(result.IsEligible);
        Assert.Null(result.NextEligibleDate);
    }

    [Fact]
    public void Eligibility_Age60_Eligible_Age61_NotEligible()
    {
        // Aged 60 until the day before the 61st birthday.
        var turns61Tomorrow = Today.AddYears(-61).AddDays(1);
        var turned61Today = Today.AddYears(-61);

        Assert.True(Eligibility.Evaluate(turns61Tomorrow, null, Today).IsEligible);
        Assert.False(Eligibility.Evaluate(turned61Today, null, Today).IsEligible);
    }

    [Fact]
    public void Eligibility_TooYoungAndRecentDonation_ReportsBothReasons()
    {
        var result = Eligibility.Evaluate(Today.AddYears(-17), Today.AddDays(-10), Today);

        Assert.Equal(2, result.Reasons.Count);
    }

    [Fact]
    public void BloodRules_UseSpecifiedConstants()
    {
        Assert.Equal(42, BloodRules.ShelfLifeDays);
        Assert.Equal(7, BloodRules.ExpiringSoonDays);
    }
}
