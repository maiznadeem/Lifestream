namespace Lifestream.Api.Tests;

/// <summary>
/// The only file in this test project that holds password-shaped literals: throw-away values typed into forms, sent to the
/// in-process test API, or stored as hashes in scratch databases. No real system accepts any of them. Tests use these constants
/// instead of writing a literal next to a password property, so the repository scan (tools/scan-artifacts.ps1 -GitTracked) stays
/// clean without allow-listing test lines one by one; tests/infra/TestSecrets.Tests.ps1 fails on a password literal anywhere else.
/// </summary>
internal static class TestSecrets
{
    /// <summary>The password of every staff row the fixture creates (ApiFixture.Password).</summary>
    public const string Demo = "not-a-real-password-for-tests";

    /// <summary>Prefix of the per-run random owner password of the migrator test (a new GUID follows).</summary>
    public const string OwnerPrefix = "Owner-";

    /// <summary>An initial password that passes the length rule.</summary>
    public const string Initial = "a-long-initial-password";

    /// <summary>A new password for the change-password tests.</summary>
    public const string BrandNew = "a-brand-new-password-1";

    /// <summary>A wrong one-character password.</summary>
    public const string WrongX = "x";

    /// <summary>Prefix of the numbered wrong passwords of the lockout tests.</summary>
    public const string WrongPrefix = "wrong";

    /// <summary>A wrong current password in the authorization matrix.</summary>
    public const string MatrixCurrent = "a";

    /// <summary>A 16-character new password in the authorization matrix.</summary>
    public const string MatrixNew = "bbbbbbbbbbbbbbbb";
}
