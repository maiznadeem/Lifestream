using Lifestream.Domain;

namespace Lifestream.Contracts.Donors;

public sealed record DonorListItem(
    int Id,
    string Name,
    DateOnly DateOfBirth,
    Gender Gender,
    BloodType BloodType,
    string? Phone,
    DonorFrequency Frequency,
    DateOnly? LastDonated)
{
    /// <summary>True when a clinician has put the donor on hold; such a donor is not eligible (API-11).</summary>
    public bool MedicalHold { get; init; }

    /// <summary>How many donations the donor has made (completed appointments). Added for the Donations column; 0 from an older server.</summary>
    public int Donations { get; init; }
}

public sealed record DonorDetail(
    int Id,
    string Name,
    DateOnly DateOfBirth,
    Gender Gender,
    BloodType BloodType,
    string? Phone,
    string? Address,
    DonorFrequency Frequency,
    DateOnly? LastDonated,
    string RowVersion)
{
    /// <summary>True when a clinician has put the donor on hold; such a donor is not eligible (API-11).</summary>
    public bool MedicalHold { get; init; }
}

public sealed record CreateDonorRequest(
    string Name,
    DateOnly DateOfBirth,
    Gender Gender,
    BloodType BloodType,
    string? Phone,
    string? Address,
    DonorFrequency Frequency)
{
    /// <summary>Optional: put the new donor on hold from the start. Defaults to false.</summary>
    public bool MedicalHold { get; init; }
}

/// <summary>PATCH body: only supplied fields change; an explicit null clears an optional field.</summary>
public sealed record UpdateDonorRequest(
    string RowVersion,
    Optional<string> Name = default,
    Optional<DateOnly> DateOfBirth = default,
    Optional<Gender> Gender = default,
    Optional<BloodType> BloodType = default,
    Optional<string?> Phone = default,
    Optional<string?> Address = default,
    Optional<DonorFrequency> Frequency = default)
{
    /// <summary>Absent leaves the hold as it is; true or false sets or lifts it.</summary>
    public Optional<bool> MedicalHold { get; init; }
}

public sealed record DonorQuery(
    string? Search = null,
    BloodType? BloodType = null,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize,
    string? Sort = null)
{
    /// <summary>Optional: only donors who are (true) or are not (false) eligible to donate today.</summary>
    public bool? Eligible { get; init; }

    /// <summary>Optional: only donors who last donated in this window (or never).</summary>
    public DonationRecency? LastDonated { get; init; }
}

/// <summary>Result of GET donors/{id}/eligibility.</summary>
public sealed record DonorEligibility(bool IsEligible, IReadOnlyList<string> Reasons, DateOnly? NextEligibleDate);
