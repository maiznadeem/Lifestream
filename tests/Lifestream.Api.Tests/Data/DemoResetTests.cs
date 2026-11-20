using System.Security.Cryptography;
using System.Text;
using Lifestream.Data.Seed;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests.Data;

/// <summary>The demo reset (plan Task 1; Review Focus 3).</summary>
[Collection(Lifestream.Api.Tests.Support.Bursts.Name)] // a burst of concurrent requests: runs after the parallel classes, alone
public sealed class DemoResetTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateOnly Day = new(2026, 11, 19);

    [Fact]
    public async Task DemoReset_RunsOncePerDay_UnderTenConcurrentCalls()
    {
        await fixture.ClearAllAsync(Ct);

        var first = await RunConcurrently(Day);
        var again = await RunConcurrently(Day);
        var nextDay = await RunConcurrently(Day.AddDays(1));

        Assert.Equal(1, first.Count(r => r));
        Assert.Equal(10, first.Length);
        Assert.Equal(0, again.Count(r => r));
        Assert.Equal(1, nextDay.Count(r => r));
        Assert.Equal(SeedSizes.Receipts, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Receipt", Ct));
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM DemoState", Ct));
    }

    private async Task<bool[]> RunConcurrently(DateOnly day)
    {
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var db = fixture.NewContext();
            return await DemoReset.RunIfDueAsync(db, day, Ct);
        });
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task DemoReset_RestoresSeedRowCountsAndChecksums_KeepsProtectedStaff()
    {
        await fixture.ClearAllAsync(Ct);
        await using var owner = fixture.NewContext();
        var admin = TestRows.Staff("owner.admin");
        admin.IsProtected = true;
        admin.Role = Role.Admin;
        owner.Staff.Add(admin);
        await owner.SaveChangesAsync(Ct);
        var protectedHash = admin.Credential!.PasswordHash;

        await using (var first = fixture.NewContext())
        {
            Assert.True(await DemoReset.RunIfDueAsync(first, Day, Ct));
        }

        var seededCounts = await Counts();
        var seededChecksum = await Checksum();
        Assert.Equal(SeedSizes.Staff, seededCounts["Staff"] - 1);
        Assert.Equal(SeedSizes.Donors, seededCounts["Donor"]);
        Assert.Equal(SeedSizes.Receipts, seededCounts["Receipt"]);

        // A visitor makes a mess: renames, deletes, adds, and the owner makes real audited changes.
        await using (var mess = fixture.NewContext(admin.Id))
        {
            mess.Donors.Add(TestRows.Donor("Vandal Donor"));
            await mess.SaveChangesAsync(Ct);
        }

        await fixture.ExecuteAsync(
            "UPDATE Donor SET Name = N'Renamed'; DELETE FROM BloodBag; DELETE FROM Receipt; UPDATE Staff SET DisplayName = N'Changed' WHERE IsDemo = 1;",
            Ct);
        await using (var someone = fixture.NewContext(await fixture.ScalarAsync<int>("SELECT MIN(Id) FROM Staff WHERE IsProtected = 0", Ct)))
        {
            someone.Patients.Add(TestRows.Patient("Vandal Patient"));
            await someone.SaveChangesAsync(Ct);
        }

        Assert.NotEqual(seededChecksum, await Checksum());

        await using (var forced = fixture.NewContext())
        {
            Assert.True(await DemoReset.ForceAsync(forced, Day, Ct));
        }

        Assert.Equal(seededCounts, await Counts());
        Assert.Equal(seededChecksum, await Checksum());

        // The protected admin is untouched (same row, same hash) and its audit rows survive; other people's audit rows are gone.
        await using var read = fixture.NewContext();
        var kept = await read.Staff.Include(s => s.Credential).SingleAsync(s => s.IsProtected, Ct);
        Assert.Equal(admin.Id, kept.Id);
        Assert.Equal(protectedHash, kept.Credential!.PasswordHash);
        Assert.True(await read.AuditLogs.AnyAsync(a => a.StaffId == admin.Id, Ct));
        Assert.False(await read.AuditLogs.AnyAsync(a => a.StaffId != admin.Id && a.StaffId != null, Ct));
        Assert.Equal(0, await read.AuditLogs.CountAsync(a => a.StaffId == null && a.Entity == "Receipt", Ct));
    }

    [Fact]
    public async Task DemoReset_Force_AllowedOncePerTenMinutes()
    {
        await fixture.ClearAllAsync(Ct);
        await using var db = fixture.NewContext();

        Assert.True(await DemoReset.ForceAsync(db, Day, Ct));
        Assert.False(await DemoReset.ForceAsync(db, Day, Ct));
        await using var other = fixture.NewContext();
        Assert.False(await DemoReset.ForceAsync(other, Day, Ct));

        // Ten minutes later it works again (the stored time is moved back; the cooldown is measured on the server clock).
        await fixture.ExecuteAsync("UPDATE DemoState SET LastForcedResetAtUtc = DATEADD(MINUTE, -11, SYSUTCDATETIME())", Ct);
        Assert.True(await DemoReset.ForceAsync(other, Day, Ct));
    }

    [Fact]
    public async Task DemoReset_WritesNoAuditRowsForItsOwnWork()
    {
        await fixture.ClearAllAsync(Ct);
        await using var db = fixture.NewContext(null);
        Assert.True(await DemoReset.RunIfDueAsync(db, Day, Ct));

        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM AuditLog", Ct));
        Assert.True(db.AuditEnabled);
    }

    private async Task<Dictionary<string, int>> Counts()
    {
        var result = new Dictionary<string, int>();
        foreach (var table in new[] { "Staff", "Donor", "Patient", "Appointment", "Request", "Receipt", "BloodBag" })
        {
            result[table] = await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM {table}", Ct);
        }

        return result;
    }

    /// <summary>A hash of every non-protected row's content, with names in place of ids (ids differ after a reset).</summary>
    private async Task<string> Checksum()
    {
        await using var db = fixture.NewContext();
        var lines = new List<string>();
        lines.AddRange((await db.Staff.AsNoTracking().Include(x => x.Credential).Where(x => !x.IsProtected).ToListAsync(Ct))
            .Select(x => $"S|{x.Username}|{x.DisplayName}|{x.Gender}|{x.DateOfBirth:O}|{x.Designation}|{x.Shift}|{x.Role}|{x.Salary}|{x.Phone}|{x.IsDemo}|{x.Credential!.PasswordHash}"));
        lines.AddRange((await db.Donors.AsNoTracking().ToListAsync(Ct))
            .Select(x => $"D|{x.Name}|{x.DateOfBirth:O}|{x.Gender}|{x.BloodType}|{x.Phone}|{x.Address}|{x.Frequency}|{x.LastDonated:O}"));
        lines.AddRange((await db.Patients.AsNoTracking().ToListAsync(Ct))
            .Select(x => $"P|{x.Name}|{x.DateOfBirth:O}|{x.Gender}|{x.BloodType}|{x.Phone}|{x.Address}"));
        lines.AddRange((await db.Appointments.AsNoTracking().Include(x => x.Donor).ToListAsync(Ct))
            .Select(x => $"A|{x.Donor!.Name}|{x.SlotStartUtc:O}|{x.PlacedAtUtc:O}|{x.Status}|{x.OverrideReason}"));
        lines.AddRange((await db.Requests.AsNoTracking().Include(x => x.Patient).ToListAsync(Ct))
            .Select(x => $"R|{x.Patient!.Name}|{x.BloodType}|{x.Units}|{x.PlacedAtUtc:O}|{x.RequiredBy:O}|{x.Status}"));
        lines.AddRange((await db.Receipts.AsNoTracking().Include(x => x.Staff).Include(x => x.Appointment!.Donor).Include(x => x.Request!.Patient).ToListAsync(Ct))
            .Select(x => $"C|{x.Kind}|{x.CreatedAtUtc:O}|{x.BloodType}|{x.Units}|{x.Staff!.Username}|{x.Appointment?.SlotStartUtc:O}|{x.Appointment?.Donor?.Name}|{x.Request?.PlacedAtUtc:O}|{x.Request?.Patient?.Name}"));
        lines.AddRange((await db.BloodBags.AsNoTracking().Include(x => x.DonationReceipt).Include(x => x.IssueReceipt).ToListAsync(Ct))
            .Select(x => $"B|{x.BloodType}|{x.CollectedAtUtc:O}|{x.ExpiresOn:O}|{x.Status}|{x.DonationReceipt!.CreatedAtUtc:O}|{x.IssueReceipt?.CreatedAtUtc:O}"));
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }
}
