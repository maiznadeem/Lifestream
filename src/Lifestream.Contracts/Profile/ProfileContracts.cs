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
    string RowVersion)
{
    /// <summary>The person's date of birth (read-only on the profile).</summary>
    public DateOnly DateOfBirth { get; init; }

    /// <summary>When the account was created, UTC (the profile shows "Member since").</summary>
    public DateTime MemberSinceUtc { get; init; }
}

public sealed record UpdateProfileRequest(
    string RowVersion,
    Optional<string> DisplayName = default,
    Optional<string?> Phone = default);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
