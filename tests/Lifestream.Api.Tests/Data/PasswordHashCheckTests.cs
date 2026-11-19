using Lifestream.Data.Entities;
using Lifestream.Data.Security;
using Lifestream.Data.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace Lifestream.Api.Tests.Data;

/// <summary>
/// DB-02: the database itself refuses anything that is not a well-formed ASP.NET Core Identity v3 hash, as far as a T-SQL
/// CHECK can tell. Rows are inserted with raw SQL so the application's own validation is out of the way.
/// </summary>
public sealed class PasswordHashCheckTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Real => TestRows.ValidHash;

    public static TheoryData<string, string> Malformed() => new()
    {
        { "verifier counterexample: marker + plain text", "AQAAAAIthis-is-plain-text-not-a-hash-" + new string('x', 50) },
        { "marker + 80 letters", "AQAAAAI" + new string('a', 80) },
        { "marker + letters, length multiple of 4", "AQAAAAIA" + new string('a', 80) },
        { "right header, plain text body with dash", Real[..30] + "-" + Real[31..] },
        { "right header, url-safe base64 underscore", Real[..30] + "_" + Real[31..] },
        { "right header, space inside", Real[..30] + " " + Real[31..] },
        { "valid hash plus trailing space", Real + " " },
        { "valid hash plus newline", Real + "\n" },
        { "valid hash plus extra '='", Real + "=" },
        { "padding in the middle", Real[..40] + "=" + Real[41..] },
        { "three padding characters", Real[..^2] + "===" },
        { "truncated to 80 characters", Real[..80] },
        { "length not a multiple of four", Real[..^1] },
        { "version byte 2 instead of 1", "Ag" + Real[2..] },
        { "PRF HMAC-SHA1 (0) instead of SHA512 (2)", "AQAAAAAA" + Real[8..] },
        { "PRF HMAC-SHA256 (1)", "AQAAAAEA" + Real[8..] },
        { "iteration count above 2^24", "AQAAAAIB" + Real[8..] },
        { "salt length 8 instead of 16", Real[..12] + "AAAAC" + Real[17..] },
        { "salt length 32", Real[..12] + "AAAAI" + Real[17..] },
        { "all lower case", Real.ToLowerInvariant() },
        { "empty", "" },
        { "plain password", "Password123" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task Staff_MalformedHash_IsRejectedByTheDatabase(string why, string value)
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => InsertStaffAsync(value));

        Assert.True(error.Number == 547, $"{why}: expected a CHECK violation (547) but got {error.Number}: {error.Message}");
        Assert.Contains("CK_StaffCredential_PasswordHash", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staff_ValueOver256Characters_IsRejected_ByTheColumnLength()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => InsertStaffAsync(Real + new string('A', 200)));

        Assert.True(error.Number is 2628 or 8152, error.Message); // string or binary data would be truncated: the column is varchar(256)
    }

    [Fact]
    public async Task Staff_RealHashes_AreAccepted_ForEveryPlausibleLength()
    {
        // Identity v3 with the default 16-byte salt and 32-byte subkey is 84 characters; a longer subkey is longer.
        await InsertStaffAsync(Real);
        await InsertStaffAsync(PasswordHashing.Hash("another password"));
        var longer = Convert.ToBase64String(Convert.FromBase64String(Real).Concat(new byte[32]).ToArray());
        await InsertStaffAsync(longer);

        Assert.Equal(84, Real.Length);
        Assert.Equal(3, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM Staff WHERE Username LIKE '{Tag}%'", Ct));
    }

    [Fact]
    public async Task Staff_EveryRowOfASeededDatabase_IsAHashThePasswordHasherCanRead()
    {
        // A database seeded the way production is (the demo reset), plus one account whose password we know.
        await using (var seeding = fixture.NewContext())
        {
            await DemoReset.ForceAsync(seeding, DateOnly.FromDateTime(DateTime.UtcNow), Ct);
            var known = TestRows.Staff("knownpassword");
            known.Credential!.PasswordHash = PasswordHashing.Hash("known password 1");
            seeding.AuditEnabled = false;
            seeding.Staff.Add(known);
            await seeding.SaveChangesAsync(Ct);
        }

        var rows = await fixture.RowsAsync("SELECT s.Username, c.PasswordHash FROM Staff s JOIN StaffCredential c ON c.StaffId = s.Id", Ct);
        Assert.True(rows.Count >= 12, $"Only {rows.Count} staff rows; the seed did not run.");
        var hasher = new PasswordHasher<Staff>();
        foreach (var row in rows)
        {
            var username = (string)row[0]!;
            var hash = (string)row[1]!;
            Assert.True(PasswordHashing.IsValidHash(hash), $"{username} holds a value that is not a valid hash.");
            if (username == "knownpassword")
            {
                Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(new Staff(), hash, "known password 1"));
            }
            else
            {
                // Seed rows belong to a discarded password: the hasher reads them without error and rejects every guess.
                Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(new Staff(), hash, "demo"));
            }
        }
    }

    [Fact]
    public async Task StaffRowScan_FindsARowThatSlippedPastTheCheck()
    {
        // Proof that the scan in the test above can fail: in a scratch database with the CHECK switched off, a plain-text row is found.
        await using var scratch = new SqlServerDatabaseFixture();
        await scratch.InitializeAsync();
        await scratch.ExecuteAsync("ALTER TABLE StaffCredential NOCHECK CONSTRAINT CK_StaffCredential_PasswordHash", Ct);
        await scratch.ExecuteAsync(
            """
            INSERT INTO Staff (DisplayName, Username, Gender, DateOfBirth, Designation, Shift, Role, IsProtected, IsDemo, HasPhoto)
            VALUES (N'Raw', N'plain', 'Male', '1990-01-01', 'Doctor', 'Morning', 'Staff', 0, 0, 0);
            INSERT INTO StaffCredential (StaffId, PasswordHash) VALUES (SCOPE_IDENTITY(), 'AQAAAAIthis-is-plain-text-not-a-hash-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx');
            """,
            Ct);

        var rows = await scratch.RowsAsync("SELECT s.Username, c.PasswordHash FROM Staff s JOIN StaffCredential c ON c.StaffId = s.Id", Ct);

        Assert.Single(rows, r => !PasswordHashing.IsValidHash((string)r[1]!));
    }

    private static readonly string Tag = "hc" + Guid.NewGuid().ToString("N")[..8];

    private async Task InsertStaffAsync(string hash)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(
            """
            SET XACT_ABORT ON;
            BEGIN TRAN;
            INSERT INTO Staff (DisplayName, Username, Gender, DateOfBirth, Designation, Shift, Role, IsProtected, IsDemo, HasPhoto)
            VALUES (N'Raw', @u, 'Male', '1990-01-01', 'Doctor', 'Morning', 'Staff', 0, 0, 0);
            INSERT INTO StaffCredential (StaffId, PasswordHash) VALUES (SCOPE_IDENTITY(), @h);
            COMMIT;
            """,
            connection);
        command.Parameters.AddWithValue("@u", Tag + Guid.NewGuid().ToString("N")[..10]);
        command.Parameters.Add(new SqlParameter("@h", System.Data.SqlDbType.VarChar, -1) { Value = hash });
        await command.ExecuteNonQueryAsync(Ct);
    }
}
