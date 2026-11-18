using Lifestream.Domain;

namespace Lifestream.Contracts.Profile;

public sealed record ProfileResponse(
    int StaffId,
    string DisplayName,
    string Username,
    Designation Designation,
    Shift Shift,
    string? Phone,
    bool HasPhoto,
    string RowVersion);

public sealed record UpdateProfileRequest(
    string RowVersion,
    Optional<string> DisplayName = default,
    Optional<string?> Phone = default);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
