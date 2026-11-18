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
    DateOnly? LastDonated);

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
    string RowVersion);

public sealed record CreateDonorRequest(
    string Name,
    DateOnly DateOfBirth,
    Gender Gender,
    BloodType BloodType,
    string? Phone,
    string? Address,
    DonorFrequency Frequency);

/// <summary>PATCH body: only supplied fields change; an explicit null clears an optional field.</summary>
public sealed record UpdateDonorRequest(
    string RowVersion,
    Optional<string> Name = default,
    Optional<DateOnly> DateOfBirth = default,
    Optional<Gender> Gender = default,
    Optional<BloodType> BloodType = default,
    Optional<string?> Phone = default,
    Optional<string?> Address = default,
    Optional<DonorFrequency> Frequency = default);

public sealed record DonorQuery(
    string? Search = null,
    BloodType? BloodType = null,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize,
    string? Sort = null);

/// <summary>Result of GET donors/{id}/eligibility.</summary>
public sealed record DonorEligibility(bool IsEligible, IReadOnlyList<string> Reasons, DateOnly? NextEligibleDate);
