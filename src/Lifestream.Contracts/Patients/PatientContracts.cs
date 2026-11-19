using Lifestream.Domain;

namespace Lifestream.Contracts.Patients;

public sealed record PatientListItem(
    int Id,
    string Name,
    DateOnly DateOfBirth,
    Gender Gender,
    BloodType BloodType,
    string? Phone,
    int PendingRequests);

public sealed record PatientDetail(
    int Id,
    string Name,
    DateOnly DateOfBirth,
    Gender Gender,
    BloodType BloodType,
    string? Phone,
    string? Address,
    string RowVersion);

public sealed record CreatePatientRequest(
    string Name,
    DateOnly DateOfBirth,
    Gender Gender,
    BloodType BloodType,
    string? Phone,
    string? Address);

public sealed record UpdatePatientRequest(
    string RowVersion,
    Optional<string> Name = default,
    Optional<DateOnly> DateOfBirth = default,
    Optional<Gender> Gender = default,
    Optional<BloodType> BloodType = default,
    Optional<string?> Phone = default,
    Optional<string?> Address = default);

public sealed record PatientQuery(
    string? Search = null,
    BloodType? BloodType = null,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize,
    string? Sort = null);
