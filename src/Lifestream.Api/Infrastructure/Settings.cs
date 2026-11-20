namespace Lifestream.Api.Infrastructure;

/// <summary>Every configuration section the API reads, with its defaults. Bound once in <see cref="ApiSetup"/>.</summary>
public sealed class JwtSettings
{
    public const string Section = "Jwt";

    /// <summary>Signing key (a Container Apps secret in Azure). When absent the API generates a throwaway key at start-up.</summary>
    public string? SigningKey { get; set; }

    public string Issuer { get; set; } = "lifestream-api";
    public string Audience { get; set; } = "lifestream-client";

    /// <summary>Capped at 15 minutes whatever is configured.</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 7;
    public int DemoRefreshHours { get; set; } = 12;

    public TimeSpan AccessLifetime => TimeSpan.FromMinutes(Math.Clamp(AccessTokenMinutes, 1, 15));
}

public sealed class AuthSettings
{
    public const string Section = "Auth";
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}

public sealed class DemoSettings
{
    public const string Section = "Demo";
    public bool Enabled { get; set; } = true;
    public int WriteCapPerIpPerDay { get; set; } = 200;

    /// <summary>A table may grow this many rows beyond its seed size before demo creates are refused.</summary>
    public int TableGrowthCap { get; set; } = 500;
}

public sealed class ClinicSettings
{
    public const string Section = "Clinic";
    public string Hours { get; set; } = "09:00-17:00";
    public int SlotMinutes { get; set; } = 30;
    public string TimeZone { get; set; } = "UTC";
    public int MaxDonationUnits { get; set; } = 2;
    public int MaxRequestUnits { get; set; } = 10;
}

public sealed class StockSettings
{
    public const string Section = "Stock";
    public int Threshold { get; set; } = 10;
}

public sealed class DashboardSettings
{
    public const string Section = "Dashboard";

    /// <summary>Cache lifetime in seconds; never more than 60.</summary>
    public int CacheSeconds { get; set; } = 30;
}

public sealed class DatabaseSettings
{
    public const string Section = "Database";

    /// <summary>How long a request keeps retrying while the database resumes, before answering 503.</summary>
    public int WakeBudgetSeconds { get; set; } = 120;

    public int MaxRetryDelaySeconds { get; set; } = 10;

    /// <summary>The readiness probe waits this long for one connection before reporting "waking".</summary>
    public int ReadinessTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// How many pooled connections the sign-in warm-up leaves open (1 to 8). The first screen sends several requests at once and the
    /// pool opens connections one after another, so without this the last of them waits for all the others' connections first.
    /// </summary>
    public int WarmConnections { get; set; } = 4;
}

public sealed class LimitSettings
{
    public const string Section = "Limits";
    public int GlobalPerMinute { get; set; } = 600;
    public int LoginPerMinute { get; set; } = 10;
    public int DemoPerMinute { get; set; } = 6;
    public int MaxBodyBytes { get; set; } = 64 * 1024;
    public int LoginBodyBytes { get; set; } = 2 * 1024;
    public int DemoBodyBytes { get; set; } = 1024;

    /// <summary>Photo uploads are capped at 1 MB; the request body limit allows a little slack for headers in multipart clients.</summary>
    public int PhotoBodyBytes { get; set; } = (1024 * 1024) + 1024;
}

/// <summary>Problem codes the frozen contract does not list. Kept next to the API so the contract stays untouched.</summary>
public static class ExtraProblemCodes
{
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string InvalidCredentials = "invalid_credentials";
    public const string PayloadTooLarge = "payload_too_large";
    public const string ServerError = "server_error";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string PasswordChangeRequired = "password_change_required";
}
