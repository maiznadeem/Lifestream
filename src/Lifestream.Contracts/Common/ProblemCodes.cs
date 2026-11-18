namespace Lifestream.Contracts;

/// <summary>Stable machine-readable codes carried in the "code" member of every RFC 7807 problem response.</summary>
public static class ProblemCodes
{
    public const string Validation = "validation";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string Concurrency = "concurrency";
    public const string InsufficientStock = "insufficient_stock";
    public const string AlreadyReceipted = "already_receipted";
    public const string SlotTaken = "slot_taken";
    public const string Ineligible = "ineligible";
    public const string LockedOut = "locked_out";
    public const string RateLimited = "rate_limited";
    public const string DatabaseWaking = "database_waking";
    public const string DatabaseResting = "database_resting";
    public const string DemoForbidden = "demo_forbidden";
    public const string DemoResetCooldown = "demo_reset_cooldown";
}
