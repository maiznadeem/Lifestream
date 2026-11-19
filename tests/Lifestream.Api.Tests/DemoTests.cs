using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Donors;
using Lifestream.Data.Seed;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests;

public sealed class DemoCapApiFixture : ApiFixture
{
    protected override void Configure(Dictionary<string, string?> settings)
    {
        settings["Demo:WriteCapPerIpPerDay"] = "3";
        settings["Demo:TableGrowthCap"] = "2";
    }
}

public sealed class DemoOffApiFixture : ApiFixture
{
    protected override void Configure(Dictionary<string, string?> settings) => settings["Demo:Enabled"] = "false";
}

/// <summary>The public demo: daily reset without a timer, manual reset, write caps (API-39, 40, 05, 04; OWN-06).</summary>
public sealed class DemoResetTests(DemoResetApiFixture fx) : IClassFixture<DemoResetApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SetLastResetAsync(DateOnly? day)
    {
        await fx.Db.ExecuteAsync("DELETE FROM DemoState", Ct);
        if (day is { } d)
        {
            await fx.Db.ExecuteAsync($"INSERT INTO DemoState (Id, LastResetDate) VALUES (1, '{d:yyyy-MM-dd}')", Ct);
        }
    }

    /// <summary>
    /// Moves the test clock to the next UTC day and marks the last reset as the day before, so the next authenticated request is
    /// "the first request of the day" again (the process remembers having checked today). Sessions must be made after this call.
    /// </summary>
    private async Task NewDayAsync()
    {
        fx.Clock.Advance(TimeSpan.FromDays(1));
        await SetLastResetAsync(fx.Clock.Today.AddDays(-1));
    }

    private async Task<Dictionary<string, (int Rows, int Checksum)>> SnapshotAsync()
    {
        // Row counts and content checksums (never ids: identity values are not restarted by a reset).
        var queries = new Dictionary<string, string>
        {
            ["Donor"] = "SELECT COUNT(*), ISNULL(CHECKSUM_AGG(CHECKSUM(Name, DateOfBirth, Gender, BloodType, Phone, Address, Frequency, LastDonated)), 0) FROM Donor",
            ["Patient"] = "SELECT COUNT(*), ISNULL(CHECKSUM_AGG(CHECKSUM(Name, DateOfBirth, Gender, BloodType, Phone, Address)), 0) FROM Patient",
            ["Appointment"] = "SELECT COUNT(*), ISNULL(CHECKSUM_AGG(CHECKSUM(SlotStartUtc, Status, OverrideReason)), 0) FROM Appointment",
            ["Request"] = "SELECT COUNT(*), ISNULL(CHECKSUM_AGG(CHECKSUM(BloodType, Units, Status, RequiredBy)), 0) FROM Request",
            ["Receipt"] = "SELECT COUNT(*), ISNULL(CHECKSUM_AGG(CHECKSUM(Kind, BloodType, Units, CreatedAtUtc)), 0) FROM Receipt",
            ["BloodBag"] = "SELECT COUNT(*), ISNULL(CHECKSUM_AGG(CHECKSUM(BloodType, CollectedAtUtc, ExpiresOn, Status)), 0) FROM BloodBag",
            ["Staff"] = "SELECT COUNT(*), ISNULL(CHECKSUM_AGG(CHECKSUM(DisplayName, Username, Role, Salary, IsDemo)), 0) FROM Staff WHERE IsProtected = 0",
            ["StaffPhoto"] = "SELECT COUNT(*), 0 FROM StaffPhoto WHERE StaffId IN (SELECT Id FROM Staff WHERE IsProtected = 0)",
        };
        var result = new Dictionary<string, (int, int)>();
        foreach (var (table, sql) in queries)
        {
            var row = (await fx.Db.RowsAsync(sql, Ct)).Single();
            result[table] = ((int)row[0]!, (int)row[1]!);
        }

        return result;
    }

    [Fact]
    public async Task FirstRequestOfTheDay_ResetsExactlyOnce_UnderConcurrentRequests_NoTimer()
    {
        await NewDayAsync();
        var owner = await fx.AddStaffAsync(Role.Admin, change: s => s.IsProtected = true);
        await Seed.DonorAsync(fx, "Visitor leftover");
        fx.Clock.Advance(TimeSpan.FromSeconds(2));

        fx.Commands.Clear();
        fx.Commands.Recording = true;
        HttpStatusCode[] results;
        try
        {
            results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
            {
                using var response = await owner.Client.GetApi(ApiRoutes.Donors);
                return response.StatusCode;
            }));
        }
        finally
        {
            fx.Commands.Recording = false;
        }

        Assert.All(results, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(1, fx.Commands.Commands.Count(c => c.Contains("DELETE", StringComparison.Ordinal) && c.Contains("[BloodBag] AS", StringComparison.Ordinal)));
        Assert.Equal(fx.Clock.Today, DateOnly.FromDateTime(await fx.Db.ScalarAsync<DateTime>("SELECT CAST(LastResetDate AS datetime) FROM DemoState", Ct)));
        Assert.Equal(SeedSizes.Donors, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor", Ct));
        Assert.Equal(0, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor WHERE Name = 'Visitor leftover'", Ct));
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Staff WHERE IsProtected = 1 AND Id = {owner.Id}", Ct));

        // The same day again: no second reset, however many requests come.
        fx.Commands.Clear();
        fx.Commands.Recording = true;
        try
        {
            (await owner.Client.GetApi(ApiRoutes.Donors)).Dispose();
        }
        finally
        {
            fx.Commands.Recording = false;
        }

        Assert.DoesNotContain(fx.Commands.Commands, c => c.Contains("DELETE FROM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_AfterReset_CarriesDemoResetHeader_OnlyForSessionsOlderThanTheReset()
    {
        await NewDayAsync();
        var owner = await fx.AddStaffAsync(Role.Admin, change: s => s.IsProtected = true);
        fx.Clock.Advance(TimeSpan.FromSeconds(2));

        // This session began before the reset, so the request that triggers it (and every later one) says so.
        using var first = await owner.Client.GetApi(ApiRoutes.Me);
        Assert.Equal("true", first.Headers.GetValues(ApiHeaders.DemoReset).Single());
        using var second = await owner.Client.GetApi(ApiRoutes.Donors);
        Assert.Equal("true", second.Headers.GetValues(ApiHeaders.DemoReset).Single());

        // A session that begins after the reset does not.
        fx.Clock.Advance(TimeSpan.FromSeconds(3));
        await using var db = fx.Db.NewContext();
        var staff = await db.Staff.AsNoTracking().SingleAsync(s => s.Id == owner.Id, Ct);
        using var fresh = fx.NewClient(await fx.SessionAsync(db, staff.Id, staff.DisplayName, Role.Admin));
        using var third = await fresh.GetApi(ApiRoutes.Donors);
        Assert.False(third.Headers.Contains(ApiHeaders.DemoReset));
    }

    [Fact]
    public async Task ManualReset_RestoresSeedRowCountsAndChecksums_VisitorChangesAndPhotosAreGone()
    {
        await NewDayAsync();
        var owner = await fx.AddStaffAsync(Role.Admin, change: s => s.IsProtected = true);
        (await owner.Client.GetApi(ApiRoutes.Me)).Dispose(); // daily reset runs here
        var demo = await fx.AddDemoAsync();
        var seed = await SnapshotAsync();
        Assert.Equal(SeedSizes.Donors, seed["Donor"].Rows);
        Assert.Equal(SeedSizes.Patients, seed["Patient"].Rows);
        Assert.Equal(SeedSizes.Appointments, seed["Appointment"].Rows);
        Assert.Equal(SeedSizes.Requests, seed["Request"].Rows);
        Assert.Equal(SeedSizes.Receipts, seed["Receipt"].Rows);
        Assert.Equal(SeedSizes.Staff, seed["Staff"].Rows);

        // A visitor makes a mess: creates, edits, deletes, and a staff photo is uploaded by an admin account.
        var created = await (await demo.Client.PostApi(ApiRoutes.Donors, new CreateDonorRequest("Visitor Made This", new DateOnly(1990, 1, 1), Gender.Male, BloodType.APos, "+1-555-0150", null, DonorFrequency.Regular))).Read<DonorDetail>();
        var list = await (await demo.Client.GetApi(ApiRoutes.Donors, "?pageSize=5")).Read<PagedResult<DonorListItem>>();
        (await demo.Client.PatchApi(ApiRoutes.DonorById, list.Items[0].Id, new UpdateDonorRequest((await (await demo.Client.GetApi(ApiRoutes.DonorById, list.Items[0].Id)).Read<DonorDetail>()).RowVersion, Name: "Vandalised"))).Dispose();
        await fx.Db.ExecuteAsync("INSERT INTO StaffPhoto (StaffId, Content, ContentType, Width, Height, UpdatedAtUtc) SELECT TOP 1 Id, 0x01, 'image/jpeg', 1, 1, SYSUTCDATETIME() FROM Staff WHERE IsProtected = 0", Ct);
        Assert.NotEqual(seed["Donor"], (await SnapshotAsync())["Donor"]);
        Assert.Equal(1, (await SnapshotAsync())["StaffPhoto"].Rows);

        // The demo account's own "Reset demo data" action.
        using var reset = await demo.Client.PostApi(ApiRoutes.DemoReset);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var after = await SnapshotAsync();
        foreach (var (table, expected) in seed)
        {
            Assert.True(expected == after[table], $"{table}: seed {expected} but after the reset {after[table]}");
        }

        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Donor WHERE Name = 'Visitor Made This' OR Name = 'Vandalised' OR Id = {created.Id}", Ct));
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Staff WHERE IsProtected = 1 AND Id = {owner.Id}", Ct));

        // The reset touches the one database it is connected to and nothing else.
        Assert.DoesNotContain(fx.Commands.Commands, c => c.Contains("master", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DemoResetEndpoint_CooldownTenMinutes_Globally_429WithRetryAfter()
    {
        var demo = await fx.AddDemoAsync();
        await fx.Db.ExecuteAsync("UPDATE DemoState SET LastForcedResetAtUtc = NULL", Ct);
        using var first = await demo.Client.PostApi(ApiRoutes.DemoReset);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        using var second = await demo.Client.PostApi(ApiRoutes.DemoReset);
        using var problem = await second.Problem(429, ProblemCodes.DemoResetCooldown);
        var wait = int.Parse(second.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(wait, 1, 600);

        // Another visitor (another session) is held to the same global cooldown.
        using var other = await (await fx.AddDemoAsync(ip: "198.51.100.9")).Client.PostApi(ApiRoutes.DemoReset);
        using var p2 = await other.Problem(429, ProblemCodes.DemoResetCooldown);

        // After ten minutes it works again.
        await fx.Db.ExecuteAsync("UPDATE DemoState SET LastForcedResetAtUtc = DATEADD(minute, -11, SYSUTCDATETIME())", Ct);
        using var later = await demo.Client.PostApi(ApiRoutes.DemoReset);
        Assert.Equal(HttpStatusCode.NoContent, later.StatusCode);
    }

    [Fact]
    public async Task DemoSession_SurvivesAReset_NoErrors_HeaderTellsTheClient()
    {
        var demo = await fx.AddDemoAsync();
        await fx.Db.ExecuteAsync("UPDATE DemoState SET LastForcedResetAtUtc = NULL", Ct);
        using var reset = await demo.Client.PostApi(ApiRoutes.DemoReset);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // The old demo staff row was replaced (new id), yet the same token still works and shows the reset header.
        using var me = await demo.Client.GetApi(ApiRoutes.Me);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("true", me.Headers.GetValues(ApiHeaders.DemoReset).Single());

        // Refreshing the session ends the header, and the refresh token (a signed token) survived the reset too.
        using var anonymous = fx.NewClient();
        var tokens = await (await anonymous.PostApi(ApiRoutes.AuthDemo)).Read<TokenResponse>();
        fx.Clock.Advance(TimeSpan.FromSeconds(3));
        var refreshed = await (await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(tokens.RefreshToken))).Read<TokenResponse>();
        using var newClient = fx.NewClient(refreshed.AccessToken);
        using var clean = await newClient.GetApi(ApiRoutes.Me);
        Assert.False(clean.Headers.Contains(ApiHeaders.DemoReset));
    }

    [Fact]
    public async Task DemoWrites_AuditAndReceiptsUseTheCurrentDemoStaffRow()
    {
        var demo = await fx.AddDemoAsync();
        await fx.Db.ExecuteAsync("UPDATE DemoState SET LastForcedResetAtUtc = NULL", Ct);
        (await demo.Client.PostApi(ApiRoutes.DemoReset)).Dispose();

        var donor = await (await demo.Client.PostApi(ApiRoutes.Donors, new CreateDonorRequest("After Reset Donor", new DateOnly(1990, 1, 1), Gender.Male, BloodType.APos, null, null, DonorFrequency.Regular))).Read<DonorDetail>();
        var currentDemoId = await fx.Db.ScalarAsync<int>("SELECT Id FROM Staff WHERE IsDemo = 1", Ct);
        Assert.NotEqual(demo.Id, currentDemoId);
        Assert.Equal(currentDemoId, await fx.Db.ScalarAsync<int>($"SELECT StaffId FROM AuditLog WHERE Entity = 'Donor' AND EntityId = {donor.Id} AND Action = 'Insert'", Ct));
    }
}

public sealed class DemoCapTests(DemoCapApiFixture fx) : IClassFixture<DemoCapApiFixture>
{
    private static CreateDonorRequest Donor(string name) => new(name, new DateOnly(1990, 1, 1), Gender.Male, BloodType.APos, null, null, DonorFrequency.Regular);

    [Fact]
    public async Task DemoWrites_CappedPerIp_PerDay_OtherAddressesAndRealStaffAreNotAffected()
    {
        var demoA = await fx.AddDemoAsync(ip: "203.0.113.50");
        var demoB = await fx.AddDemoAsync(ip: "203.0.113.51");
        var admin = await fx.AddStaffAsync(Role.Admin, ip: "203.0.113.50");

        for (var i = 0; i < 3; i++)
        {
            using var ok = await demoA.Client.PostApi(ApiRoutes.Donors, Donor("Cap " + i));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var capped = await demoA.Client.PostApi(ApiRoutes.Donors, Donor("Cap over"));
        using var problem = await capped.Problem(429, ProblemCodes.RateLimited);
        Assert.True(capped.Headers.Contains("Retry-After"));

        using var otherIp = await demoB.Client.PostApi(ApiRoutes.Donors, Donor("Other address"));
        Assert.Equal(HttpStatusCode.Created, otherIp.StatusCode);
        using var staffSameIp = await admin.Client.PostApi(ApiRoutes.Donors, Donor("Real staff"));
        Assert.Equal(HttpStatusCode.Created, staffSameIp.StatusCode);

        // Reads are never capped.
        using var read = await demoA.Client.GetApi(ApiRoutes.Donors);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task DemoCreates_RefusedOnceATableHasGrownPastItsSeedSizePlusTheCap()
    {
        var demo = await fx.AddDemoAsync(ip: "203.0.113.60");
        await using (var db = fx.Db.NewContext())
        {
            db.AuditEnabled = false;
            var have = await db.Patients.CountAsync(TestContext.Current.CancellationToken);
            db.Patients.AddRange(Enumerable.Range(0, SeedSizes.Patients + 2 - have).Select(i => Data.TestRows.Patient($"Fill {i}")));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var full = await demo.Client.PostApi(ApiRoutes.Patients, new Lifestream.Contracts.Patients.CreatePatientRequest("One too many", new DateOnly(1980, 1, 1), Gender.Male, BloodType.APos, null, null));
        using var problem = await full.Problem(409, ProblemCodes.Conflict);
        Assert.Contains("Reset demo data", problem.RootElement.GetProperty("title").GetString(), StringComparison.Ordinal);
    }
}

public sealed class DemoDisabledTests(DemoOffApiFixture fx) : IClassFixture<DemoOffApiFixture>
{
    [Fact]
    public async Task DemoDisabled_NoDemoLogin_NoDailyReset()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        await fx.Db.ExecuteAsync("DELETE FROM DemoState", TestContext.Current.CancellationToken);
        using var client = fx.NewClient();
        using var demo = await client.PostApi(ApiRoutes.AuthDemo);
        using var p = await demo.Problem(404, ProblemCodes.NotFound);
        using var response = await admin.Client.GetApi(ApiRoutes.Donors);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM DemoState", TestContext.Current.CancellationToken));
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Staff WHERE Id = {admin.Id}", TestContext.Current.CancellationToken));
    }
}
