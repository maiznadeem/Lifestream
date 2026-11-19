namespace Lifestream.Desktop.Tests;

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

    /// <summary>Long enough for the length rule.</summary>
    public const string LongEnough = "a-long-enough-password";

    /// <summary>A value the view model pushes into the password box.</summary>
    public const string FromViewModel = "from-view-model";

    /// <summary>A value the user types into the password box.</summary>
    public const string TypedByUser = "typed-by-user";

    /// <summary>A value shown by the reveal toggle.</summary>
    public const string Shown = "shown";

    /// <summary>A value typed into a dialog that is cancelled.</summary>
    public const string TypedNotSaved = "typed-but-not-saved";

    /// <summary>The current password in a change-password dialog.</summary>
    public const string Old123 = "old-password-123";

    /// <summary>The new password in a change-password dialog.</summary>
    public const string New456 = "new-password-456";
}
