using Lifestream.Domain;

namespace Lifestream.Contracts.Staff;

/// <summary>Never carries photo bytes or any credential; the photo is fetched lazily from staff/{id}/photo.</summary>
public sealed record StaffListItem(
    int Id,
    string DisplayName,
    string Username,
    Designation Designation,
    Shift Shift,
    Role Role,
    bool HasPhoto,
    int? Salary,
    string? Phone);

public sealed record StaffDetail(
    int Id,
    string DisplayName,
    string Username,
    Gender Gender,
    DateOnly DateOfBirth,
    Designation Designation,
    Shift Shift,
    Role Role,
    bool HasPhoto,
    int? Salary,
    string? Phone,
    bool IsProtected,
    string RowVersion);

public sealed record CreateStaffRequest(
    string DisplayName,
    string Username,
    Gender Gender,
    DateOnly DateOfBirth,
    Designation Designation,
    Shift Shift,
    Role Role,
    int? Salary,
    string? Phone,
    string InitialPassword);

public sealed record UpdateStaffRequest(
    string RowVersion,
    Optional<string> DisplayName = default,
    Optional<Gender> Gender = default,
    Optional<DateOnly> DateOfBirth = default,
    Optional<Designation> Designation = default,
    Optional<Shift> Shift = default,
    Optional<Role> Role = default,
    Optional<int?> Salary = default,
    Optional<string?> Phone = default);

public sealed record ResetPasswordResponse(string TemporaryPassword);
