using System.Net;
using System.Text.Json;
using Lifestream.Api.Auth;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Api.Tests;

/// <summary>
/// API-14 (and API-01): the sign-in state of a person lives in StaffCredential, so a failed login, a lockout, a password change or a reset never
/// changes the Staff row and never invalidates a record somebody has open (the verifier's probe E18). All of these run against the real database.
/// </summary>
public sealed class StaffCredentialRowVersionTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<long> StaffVersionAsync(int id) => fx.Db.ScalarAsync<long>($"SELECT CAST(RowVersion AS bigint) FROM Staff WHERE Id = {id}", Ct);

    private Task<long> CredentialVersionAsync(int id) => fx.Db.ScalarAsync<long>($"SELECT CAST(RowVersion AS bigint) FROM StaffCredential WHERE StaffId = {id}", Ct);

    [Fact]
    public async Task RealDatabase_StaffCredentialIsOneToOneWithStaff_WithItsOwnRowVersion()
    {
        Assert.StartsWith("Lifestream_Test_", fx.Db.DatabaseName, StringComparison.Ordinal);
        var user = await fx.AddStaffAsync(Role.Staff);
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM StaffCredential WHERE StaffId = {user.Id}", Ct));
        Assert.Equal(0, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('Staff') AND name IN ('PasswordHash','FailedLoginCount','LockoutEndUtc','MustChangePassword')", Ct));
        Assert.Equal(6, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('StaffCredential') AND name IN ('PasswordHash','FailedCount','LockoutEndUtc','MustChangePassword','SecurityStamp','RowVersion')", Ct));
    }

    [Fact]
    public async Task E18_ResetPassword_ThenSaveWithTheRecordHeld_Succeeds_AndTheStaffRowVersionDidNotMove()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        var held = await (await admin.Client.GetApi(ApiRoutes.StaffById, worker.Id)).Read<StaffDetail>();
        var credentialBefore = await CredentialVersionAsync(worker.Id);

        using var reset = await admin.Client.PostApi(ApiRoutes.StaffResetPassword, new ResetPasswordRequest("temporary-pass-77"), worker.Id);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        Assert.Equal((await (await admin.Client.GetApi(ApiRoutes.StaffById, worker.Id)).Read<StaffDetail>()).RowVersion, held.RowVersion);
        Assert.NotEqual(credentialBefore, await CredentialVersionAsync(worker.Id));
        using var save = await admin.Client.PatchApi(ApiRoutes.StaffById, worker.Id, new UpdateStaffRequest(held.RowVersion, DisplayName: "Edited After Reset"));
        Assert.Equal("Edited After Reset", (await save.Read<StaffDetail>()).DisplayName);
    }

    [Fact]
    public async Task E18_ChangePassword_ThenSaveTheProfileHeld_Succeeds_AndTheStaffRowVersionDidNotMove()
    {
        var person = await fx.AddStaffAsync(Role.Staff);
        var held = await (await person.Client.GetApi(ApiRoutes.Me)).Read<ProfileResponse>();
        var staffBefore = await StaffVersionAsync(person.Id);

        using var change = await person.Client.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest(ApiFixture.Password, "a-brand-new-password-5"));
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        Assert.Equal(staffBefore, await StaffVersionAsync(person.Id));
        Assert.Equal(held.RowVersion, (await (await person.Client.GetApi(ApiRoutes.Me)).Read<ProfileResponse>()).RowVersion);
        using var save = await person.Client.PatchMe(new UpdateProfileRequest(held.RowVersion, DisplayName: "Own Edit After Change"));
        Assert.Equal("Own Edit After Change", (await save.Read<ProfileResponse>()).DisplayName);
    }

    [Fact]
    public async Task E18_FailedLoginsAndALockout_NeverChangeTheStaffRowVersion_SoAnAdminsHeldRecordStillSaves()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var other = await fx.AddStaffAsync(Role.Staff);
        var held = await (await admin.Client.GetApi(ApiRoutes.StaffById, other.Id)).Read<StaffDetail>();
        var staffBefore = await StaffVersionAsync(other.Id);
        var credentialBefore = await CredentialVersionAsync(other.Id);

        (await fx.NewClient().PostApi(ApiRoutes.AuthLogin, new LoginRequest(other.Username, "a-typo-password"))).Dispose();
        Assert.Equal(staffBefore, await StaffVersionAsync(other.Id));
        Assert.NotEqual(credentialBefore, await CredentialVersionAsync(other.Id));

        for (var i = 0; i < 6; i++)
        {
            (await fx.NewClient().PostApi(ApiRoutes.AuthLogin, new LoginRequest(other.Username, "wrong-" + i))).Dispose();
        }

        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM StaffCredential WHERE StaffId = {other.Id} AND LockoutEndUtc IS NOT NULL", Ct));
        Assert.Equal(staffBefore, await StaffVersionAsync(other.Id));
        using var save = await admin.Client.PatchApi(ApiRoutes.StaffById, other.Id, new UpdateStaffRequest(held.RowVersion, DisplayName: "Edited After Typos"));
        Assert.Equal("Edited After Typos", (await save.Read<StaffDetail>()).DisplayName);
    }

    [Fact]
    public async Task AProfileEdit_StillChangesTheStaffRowVersion_SoARealConflictIsStillCaught()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        var held = await (await admin.Client.GetApi(ApiRoutes.StaffById, worker.Id)).Read<StaffDetail>();

        using var first = await admin.Client.PatchApi(ApiRoutes.StaffById, worker.Id, new UpdateStaffRequest(held.RowVersion, DisplayName: "First Edit"));
        var saved = await first.Read<StaffDetail>();
        Assert.NotEqual(held.RowVersion, saved.RowVersion);

        using var stale = await admin.Client.PatchApi(ApiRoutes.StaffById, worker.Id, new UpdateStaffRequest(held.RowVersion, DisplayName: "Second Edit"));
        using var problem = await stale.Problem(409, "concurrency");
    }

    [Fact]
    public async Task PasswordChangeAndReset_StillSetTheNewPassword_AndStillAuditAStaffUpdate()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        var stampBefore = await fx.Db.ScalarAsync<string>($"SELECT CAST(SecurityStamp AS varchar(36)) FROM StaffCredential WHERE StaffId = {worker.Id}", Ct);
        var auditBefore = await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM AuditLog WHERE Entity = 'Staff' AND EntityId = {worker.Id} AND Action = 'Update'", Ct);

        using var reset = await admin.Client.PostApi(ApiRoutes.StaffResetPassword, new ResetPasswordRequest("temporary-pass-77"), worker.Id);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        using var login = await fx.NewClient().PostApi(ApiRoutes.AuthLogin, new LoginRequest(worker.Username, "temporary-pass-77"));
        Assert.True((await login.Read<TokenResponse>()).User.MustChangePassword);
        Assert.NotEqual(stampBefore, await fx.Db.ScalarAsync<string>($"SELECT CAST(SecurityStamp AS varchar(36)) FROM StaffCredential WHERE StaffId = {worker.Id}", Ct));
        Assert.Equal(auditBefore + 1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM AuditLog WHERE Entity = 'Staff' AND EntityId = {worker.Id} AND Action = 'Update'", Ct));
    }
}

/// <summary>
/// API-01: failure counting for a name with no account survives a restart and matches names the way the database does, so a real account and an
/// unknown name are indistinguishable in every state (the verifier's probes E01 and E19). The counter is a database table keyed under the same collation.
/// </summary>
public sealed class UnknownLoginPersistenceTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(int Status, string? Code, string? RetryAfter)> LoginAsync(HttpClient client, string username, string password)
    {
        using var response = await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(username, password));
        var text = await response.Content.ReadAsStringAsync(Ct);
        var code = text.StartsWith('{') && JsonElement.Parse(text).TryGetProperty("code", out var c) ? c.GetString() : null;
        return ((int)response.StatusCode, code, response.Headers.RetryAfter?.ToString());
    }

    public static TheoryData<string> Spellings() => new() { "soft hyphen", "fullwidth", "NUL", "upper case", "eszett" };

    private static string Spell(string kind, string name) => kind switch
    {
        "soft hyphen" => name + "­",
        "fullwidth" => new string([.. name.Select(ch => ch is >= '!' and <= '~' ? (char)(ch - 0x21 + 0xFF01) : ch)]),
        "NUL" => name + "\u0000",
        "upper case" => name.ToUpperInvariant(),
        "eszett" => name.Replace("ss", "ß", StringComparison.Ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task E01_AfterFiveWrongGuesses_ASpellingTheCollationTreatsAsEqual_IsLockedForARealAndAnUnknownName_Identically(string kind)
    {
        // "ss" is in both names so the eszett spelling is a different string with the same collation value.
        var user = await fx.AddStaffAsync(Role.Staff, username: fx.NextName("glassman"));
        var ghost = "ghostglassman" + Guid.NewGuid().ToString("N")[..10];
        using var client = fx.NewClient();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(401, (await LoginAsync(client, user.Username, "wrong-" + i)).Status);
            Assert.Equal(401, (await LoginAsync(client, ghost, "wrong-" + i)).Status);
        }

        var known = await LoginAsync(client, Spell(kind, user.Username), "wrong-6");
        var unknown = await LoginAsync(client, Spell(kind, ghost), "wrong-6");

        Assert.Equal(429, known.Status);
        Assert.Equal(known.Status, unknown.Status);
        Assert.Equal(known.Code, unknown.Code);
        Assert.Equal("locked_out", unknown.Code);
        Assert.NotNull(unknown.RetryAfter);

        // And the plain spelling of the unknown name is the same locked name.
        Assert.Equal(429, (await LoginAsync(client, ghost, "wrong-7")).Status);

        // One row per name as the database sees it, whichever spelling reached it first.
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM UnknownLogin WHERE Name = N'{ghost}'", Ct));
    }

    [Fact]
    public async Task E19_AfterARestart_AnUnknownNameStaysLocked_ExactlyLikeARealAccount()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        var ghost = "ghost" + Guid.NewGuid().ToString("N")[..8];
        var partial = "ghostpartial" + Guid.NewGuid().ToString("N")[..8];
        using var client = fx.NewClient();
        for (var i = 0; i < 5; i++)
        {
            await LoginAsync(client, user.Username, "w" + i);
            await LoginAsync(client, ghost, "w" + i);
        }

        // Three guesses of a name that is not locked yet: the count must survive too (the fifth guess locks it).
        for (var i = 0; i < 3; i++)
        {
            await LoginAsync(client, partial, "w" + i);
        }

        await using var restarted = fx.NewHostOverSameDatabase();
        using var after = restarted.CreateClient();
        var known6 = await LoginAsync(after, user.Username, "w6");
        var ghost6 = await LoginAsync(after, ghost, "w6");
        var ghostFullwidth = await LoginAsync(after, Spell("fullwidth", ghost), "w6");

        Assert.Equal(429, known6.Status);
        Assert.Equal(known6.Status, ghost6.Status);
        Assert.Equal(known6.Code, ghost6.Code);
        Assert.Equal(429, ghostFullwidth.Status);
        Assert.Equal(401, (await LoginAsync(after, partial, "w3")).Status); // the fourth guess: the three before the restart were remembered, not forgotten
        Assert.Equal(401, (await LoginAsync(after, partial, "w4")).Status); // the fifth guess is checked, and starts the lock
        Assert.Equal(429, (await LoginAsync(after, partial, "w5")).Status);
    }

    [Fact]
    public async Task UnknownNames_UseTheSameCollationAsUsernames_ByConstruction()
    {
        var collations = await fx.Db.RowsAsync("SELECT t.name, c.collation_name FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE (t.name = 'UnknownLogin' AND c.name = 'Name') OR (t.name = 'Staff' AND c.name = 'Username') ORDER BY t.name DESC", Ct);
        Assert.Equal(2, collations.Count);
        Assert.Equal((string)collations[0][1]!, (string)collations[1][1]!);
    }

    [Fact]
    public async Task UnknownLoginRows_ExpireAndArePurgedWhenTheTableIsUsed_WithNoTimer()
    {
        var expiredName = "expired" + Guid.NewGuid().ToString("N")[..8];
        await fx.Db.ExecuteAsync($"INSERT INTO UnknownLogin (Name, FailedCount, LockoutEndUtc, ExpiresAtUtc) VALUES (N'{expiredName}', 4, NULL, DATEADD(day, -1, SYSUTCDATETIME()))", Ct);
        fx.Clock.Advance(TimeSpan.FromMinutes(1));
        try
        {
            using var client = fx.NewClient();
            await LoginAsync(client, "ghost" + Guid.NewGuid().ToString("N")[..8], "x");
        }
        finally
        {
            fx.Clock.Advance(TimeSpan.FromMinutes(-1));
        }

        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM UnknownLogin WHERE Name = N'{expiredName}'", Ct));
    }

    [Fact]
    public async Task AnExpiredCounterStartsFromZero_AfterThePurge()
    {
        var name = "stale" + Guid.NewGuid().ToString("N")[..8];
        using var client = fx.NewClient();
        for (var i = 0; i < 4; i++)
        {
            await LoginAsync(client, name, "w" + i);
        }

        fx.Clock.Advance(UnknownLoginRetention + TimeSpan.FromDays(1));
        try
        {
            // Four guesses were counted before the retention time ran out; they are gone, so this is guess one, not guess five.
            Assert.Equal(401, (await LoginAsync(client, name, "w4")).Status);
            Assert.Equal(401, (await LoginAsync(client, name, "w5")).Status);
        }
        finally
        {
            fx.Clock.Advance(-(UnknownLoginRetention + TimeSpan.FromDays(1)));
        }
    }

    [Fact]
    public async Task ARealAccountAndAnUnknownName_WithThreeFailuresThenThirtyOneIdleDays_BehaveIdentically_NeitherIsLockedAfterTwoMore()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        var ghost = "ghostidle" + Guid.NewGuid().ToString("N")[..8];
        using var client = fx.NewClient();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(401, (await LoginAsync(client, user.Username, "w" + i)).Status);
            Assert.Equal(401, (await LoginAsync(client, ghost, "w" + i)).Status);
        }

        var idle = TimeSpan.FromDays(31);
        fx.Clock.Advance(idle);
        try
        {
            var known = new List<(int, string?, string?)>();
            var unknown = new List<(int, string?, string?)>();
            for (var i = 3; i < 5; i++)
            {
                known.Add(await LoginAsync(client, user.Username, "w" + i));
                unknown.Add(await LoginAsync(client, ghost, "w" + i));
            }

            Assert.Equal(known, unknown);
            Assert.All(known, r => Assert.Equal(401, r.Item1)); // the old three were forgotten: two guesses are not five
            Assert.Equal(2, await fx.Db.ScalarAsync<int>($"SELECT FailedCount FROM StaffCredential WHERE StaffId = {user.Id}", Ct));

            // Three more reach five in this run, for both, and both then lock the same way.
            for (var i = 5; i < 8; i++)
            {
                Assert.Equal(401, (await LoginAsync(client, user.Username, "w" + i)).Status);
                Assert.Equal(401, (await LoginAsync(client, ghost, "w" + i)).Status);
            }

            Assert.Equal((await LoginAsync(client, user.Username, "w9")).Item1, (await LoginAsync(client, ghost, "w9")).Item1);
            Assert.Equal(429, (await LoginAsync(client, ghost, "w10")).Item1);
        }
        finally
        {
            fx.Clock.Advance(-idle);
        }
    }

    private static TimeSpan UnknownLoginRetention => Lifestream.Data.Entities.UnknownLogin.Retention;
}

/// <summary>API-02: a revoked demo session stays revoked after a restart, and the daily demo reset does not forget it (the verifier's probe E19, second half).</summary>
public sealed class DemoRevocationPersistenceTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TokenResponse> DemoLoginAsync()
    {
        await fx.AddDemoAsync();
        using var anonymous = fx.NewClient();
        return await (await anonymous.PostApi(ApiRoutes.AuthDemo)).Read<TokenResponse>();
    }

    [Fact]
    public async Task E19_ALoggedOutDemoSession_StaysRevokedAfterARestart_RefreshAndAccessTokenBoth()
    {
        var session = await DemoLoginAsync();
        using var anonymous = fx.NewClient();
        (await anonymous.PostApi(ApiRoutes.AuthLogout, new RefreshRequest(session.RefreshToken))).Dispose();
        using var before = await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(session.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
        Assert.True(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM DemoRevocation WHERE [Key] LIKE 'f:%'", Ct) > 0);

        await using var restarted = fx.NewHostOverSameDatabase();
        using var c2 = restarted.CreateClient();
        using var after = await c2.PostAsJsonAsync(ApiRoutes.Path(ApiRoutes.AuthRefresh), new RefreshRequest(session.RefreshToken), ApiFixture.Json, Ct);
        using var problem = await after.Problem(401, "unauthorized");

        c2.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var me = await c2.GetAsync(ApiRoutes.Path(ApiRoutes.Me), Ct);
        using var meProblem = await me.Problem(401, "unauthorized");
    }

    [Fact]
    public async Task E19_ARotatedRefreshToken_CannotBeReplayedAfterARestart_AndTheReplayEndsTheSession()
    {
        var session = await DemoLoginAsync();
        using var anonymous = fx.NewClient();
        var rotated = await (await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(session.RefreshToken))).Read<TokenResponse>();

        await using var restarted = fx.NewHostOverSameDatabase();
        using var c2 = restarted.CreateClient();
        using var replay = await c2.PostAsJsonAsync(ApiRoutes.Path(ApiRoutes.AuthRefresh), new RefreshRequest(session.RefreshToken), ApiFixture.Json, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The replay ended the whole session, also for the newest token, and that too is remembered by yet another restart.
        await using var again = fx.NewHostOverSameDatabase();
        using var c3 = again.CreateClient();
        using var newest = await c3.PostAsJsonAsync(ApiRoutes.Path(ApiRoutes.AuthRefresh), new RefreshRequest(rotated.RefreshToken), ApiFixture.Json, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, newest.StatusCode);
    }

    [Fact]
    public async Task TheDailyDemoReset_DoesNotForgetARevokedSession()
    {
        var session = await DemoLoginAsync();
        using var anonymous = fx.NewClient();
        (await anonymous.PostApi(ApiRoutes.AuthLogout, new RefreshRequest(session.RefreshToken))).Dispose();
        var rowsBefore = await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM DemoRevocation", Ct);
        Assert.True(rowsBefore > 0);

        await using (var db = fx.Db.NewContext())
        {
            Assert.True(await Lifestream.Data.Seed.DemoReset.ForceAsync(db, fx.Clock.Today, Ct));
        }

        Assert.Equal(rowsBefore, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM DemoRevocation", Ct));
        using var refresh = await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(session.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task DemoRevocationRows_ExpireWithTheToken_AndArePurgedWhenTheTableIsUsed_WithNoTimer()
    {
        var key = "f:" + Guid.NewGuid().ToString("N");
        await fx.Db.ExecuteAsync($"INSERT INTO DemoRevocation ([Key], ExpiresAtUtc) VALUES ('{key}', DATEADD(day, -1, SYSUTCDATETIME()))", Ct);
        fx.Clock.Advance(TimeSpan.FromMinutes(6));
        try
        {
            using var scope = fx.Factory.Services.CreateScope();
            var denylist = scope.ServiceProvider.GetRequiredService<DemoSessionDenylist>();
            Assert.False(await denylist.IsRevokedAsync(Guid.NewGuid(), Ct));
        }
        finally
        {
            fx.Clock.Advance(TimeSpan.FromMinutes(-6));
        }

        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM DemoRevocation WHERE [Key] = '{key}'", Ct));
    }
}
