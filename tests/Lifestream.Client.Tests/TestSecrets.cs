namespace Lifestream.Client.Tests;

/// <summary>
/// The only file in this test project that holds password-shaped literals: throw-away values typed into forms, sent to the
/// in-process test API, or stored as hashes in scratch databases. No real system accepts any of them. Tests use these constants
/// instead of writing a literal next to a password property, so the repository scan (tools/scan-artifacts.ps1 -GitTracked) stays
/// clean without allow-listing test lines one by one; tests/infra/TestSecrets.Tests.ps1 fails on a password literal anywhere else.
/// </summary>
internal static class TestSecrets
{
    /// <summary>A one-character password typed into a login form.</summary>
    public const string P = "p";

    /// <summary>A two-character password typed into a login form.</summary>
    public const string Pw = "pw";

    /// <summary>Shorter than the length rule.</summary>
    public const string TooShort = "short";

    /// <summary>A confirmation that does not match.</summary>
    public const string Different = "different";

    /// <summary>Another confirmation that does not match.</summary>
    public const string Other = "other";

    /// <summary>Long enough for the length rule.</summary>
    public const string LongEnough = "a-long-enough-password";

    /// <summary>A temporary password an admin sets.</summary>
    public const string Temporary = "Temporary-Pass-12";

    /// <summary>The current password in a change-password form.</summary>
    public const string Current = "the-old-password";

    /// <summary>The new password in a change-password form.</summary>
    public const string Next = "the-new-password-1";

    /// <summary>A new password equal to the current one.</summary>
    public const string Same = "same-password-12";

    /// <summary>A new password that differs from the current one.</summary>
    public const string BrandNew = "brand-new-password";

    /// <summary>The current password in the operation-runner dialogs.</summary>
    public const string Old123 = "old-password-123";

    /// <summary>The new password in the operation-runner dialogs.</summary>
    public const string New456 = "new-password-456";

    /// <summary>Any text typed into a password field.</summary>
    public const string Typed = "typed";
}
