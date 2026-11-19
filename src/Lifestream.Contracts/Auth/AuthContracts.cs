using Lifestream.Domain;

namespace Lifestream.Contracts.Auth;

public sealed record LoginRequest(string Username, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record SessionUser(int StaffId, string DisplayName, Role Role, bool IsDemo, bool CanManageStaff)
{
    /// <summary>True after an administrator reset the password: every call except sign-in, GET me and PUT me/password answers 403 password_change_required until the person sets their own.</summary>
    public bool MustChangePassword { get; init; }
}

public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresUtc, SessionUser User);
