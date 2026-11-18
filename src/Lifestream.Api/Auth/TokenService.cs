using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts.Auth;
using Lifestream.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Lifestream.Api.Auth;

public static class Claims
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Role = "role";
    public const string Demo = "demo";
    public const string Family = "sid";
    public const string Use = "use";
}

/// <summary>Issues and validates the short-lived access token and the stateless demo refresh token.</summary>
public sealed class TokenService
{
    private const string RefreshUse = "refresh";
    private readonly JwtSettings _settings;
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(IOptions<JwtSettings> settings, TimeProvider time)
    {
        _settings = settings.Value;
        _time = time;
        var secret = _settings.SigningKey is { Length: >= 32 } key ? Encoding.UTF8.GetBytes(key) : RandomNumberGenerator.GetBytes(48);
        Key = new SymmetricSecurityKey(secret);
    }

    public SecurityKey Key { get; }

    public TokenValidationParameters AccessParameters => new()
    {
        ValidIssuer = _settings.Issuer,
        ValidAudience = _settings.Audience,
        IssuerSigningKey = Key,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.Zero,
        NameClaimType = Claims.Name,
        RoleClaimType = Claims.Role,
        LifetimeValidator = (notBefore, expires, _, _) => expires is not null && expires > _time.GetUtcNow().UtcDateTime && (notBefore is null || notBefore <= _time.GetUtcNow().UtcDateTime),
    };

    public (string Token, DateTime ExpiresUtc) CreateAccess(int staffId, string displayName, Role role, bool isDemo, Guid family)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var expires = now + _settings.AccessLifetime;
        var claims = new Dictionary<string, object>
        {
            [Claims.Subject] = staffId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [Claims.Name] = displayName,
            [Claims.Role] = role.ToString(),
            [Claims.Family] = family.ToString("N"),
        };
        if (isDemo)
        {
            claims[Claims.Demo] = "1";
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _settings.Issuer,
            Audience = _settings.Audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    /// <summary>A demo session's refresh token: signed, carries its own family id, needs no database row (so a daily reset never logs a visitor out).</summary>
    public string CreateDemoRefresh(Guid family)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _settings.Issuer,
            Audience = _settings.Audience + "-refresh",
            Claims = new Dictionary<string, object> { [Claims.Family] = family.ToString("N"), [Claims.Use] = RefreshUse },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddHours(_settings.DemoRefreshHours),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>The family id inside a valid, unexpired demo refresh token, or null when the text is not one.</summary>
    public async Task<Guid?> ReadDemoRefreshAsync(string token)
    {
        var parameters = AccessParameters;
        parameters.ValidAudience = _settings.Audience + "-refresh";
        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid || result.ClaimsIdentity.FindFirst(Claims.Use)?.Value != RefreshUse)
        {
            return null;
        }

        return Guid.TryParseExact(result.ClaimsIdentity.FindFirst(Claims.Family)?.Value, "N", out var family) ? family : null;
    }

    public static string NewRefreshSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string HashRefresh(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static SessionUser Session(int staffId, string displayName, Role role, bool isDemo) =>
        new(staffId, displayName, role, isDemo, CanManageStaff: role == Role.Admin && !isDemo);
}

/// <summary>The caller, read from the validated token. Staff ids always come from here, never from a request body.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor, DemoDirectory demo, IServiceProvider services)
{
    private ClaimsPrincipal Principal => accessor.HttpContext?.User ?? new ClaimsPrincipal();

    public bool IsAuthenticated => Principal.Identity?.IsAuthenticated == true;
    public bool IsDemo => Principal.HasClaim(Claims.Demo, "1");
    public bool IsAdmin => Principal.HasClaim(Claims.Role, nameof(Role.Admin)) && !IsDemo;
    public Role Role => IsAdmin ? Role.Admin : Role.Staff;
    public string DisplayName => Principal.FindFirst(Claims.Name)?.Value ?? "";

    public Guid? Family => Guid.TryParseExact(Principal.FindFirst(Claims.Family)?.Value, "N", out var f) ? f : null;

    public int TokenStaffId => int.TryParse(Principal.FindFirst(Claims.Subject)?.Value, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0;

    /// <summary>The staff row id to record on receipts and audit rows. For the shared demo account this is the current demo row, because ids change at every reset.</summary>
    public Task<int> StaffIdAsync(CancellationToken ct) => IsDemo ? demo.DemoStaffIdAsync(services.GetRequiredService<Lifestream.Data.LifestreamDbContext>(), ct) : Task.FromResult(TokenStaffId);
}
