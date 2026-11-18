using Lifestream.Domain;

namespace Lifestream.Contracts.Auth;

public sealed record LoginRequest(string Username, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record SessionUser(int StaffId, string DisplayName, Role Role, bool IsDemo, bool CanManageStaff);

public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresUtc, SessionUser User);
