using Lifestream.Data;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests.Data;

/// <summary>DB-09: every stored timestamp is UTC. A local or unspecified DateTime is refused on write; values read back are Kind Utc.</summary>
public sealed class UtcTimestampTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Tag() => "UT" + Guid.NewGuid().ToString("N")[..10];

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task Write_NonUtcDateTime_Throws_AndStoresNothing(DateTimeKind kind)
    {
        await using var db = fixture.NewContext(null);
        var donor = TestRows.Donor(Tag());
        var appointment = TestRows.Appointment(donor, DateTime.SpecifyKind(new DateTime(2026, 6, 1, 9, 0, 0), kind));
        db.Appointments.Add(appointment);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));

        Assert.Contains("Kind=Utc", Flatten(error), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM Donor WHERE Name = '{donor.Name}'", Ct));
    }

    [Fact]
    public async Task Write_NonUtcNullableDateTime_Throws()
    {
        await using var db = fixture.NewContext(null);
        var staff = TestRows.Staff(Tag());
        staff.Credential!.LockoutEndUtc = DateTime.Now.AddMinutes(15);
        db.Staff.Add(staff);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));

        Assert.Contains("Kind=Utc", Flatten(error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_LocalDateTime_Throws_OnTheSyncPathToo()
    {
        await using var db = fixture.NewContext(null);
        var donor = TestRows.Donor(Tag());
        db.Appointments.Add(TestRows.Appointment(donor, DateTime.Now));

        var error = Assert.ThrowsAny<Exception>(() => db.SaveChanges());

        Assert.Contains("Kind=Utc", Flatten(error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_ToALocalDateTime_Throws_AndTheStoredValueIsUnchanged()
    {
        var utc = new DateTime(2026, 7, 1, 8, 30, 0, DateTimeKind.Utc);
        await using var db = fixture.NewContext(null);
        var appointment = TestRows.Appointment(TestRows.Donor(Tag()), utc);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(Ct);

        appointment.SlotStartUtc = DateTime.SpecifyKind(utc.AddHours(3), DateTimeKind.Local);

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));
        Assert.Equal(
            "2026-07-01 08:30:00",
            await fixture.ScalarAsync<string>($"SELECT CONVERT(varchar(19), SlotStartUtc, 120) FROM Appointment WHERE Id = {appointment.Id}", Ct));
    }

    [Fact]
    public async Task Write_UtcDateTime_RoundTripsExactly_AndReadsBackAsUtc()
    {
        var utc = new DateTime(2026, 7, 1, 8, 30, 15, 123, DateTimeKind.Utc);
        var staff = TestRows.Staff(Tag());
        staff.Credential!.LockoutEndUtc = utc;
        await using (var db = fixture.NewContext(null))
        {
            db.Appointments.Add(TestRows.Appointment(TestRows.Donor(Tag()), utc));
            db.Staff.Add(staff);
            await db.SaveChangesAsync(Ct);
        }

        await using var read = fixture.NewContext(null);
        var appointment = await read.Appointments.AsNoTracking().OrderByDescending(a => a.Id).FirstAsync(a => a.SlotStartUtc == utc, Ct);
        var lockedOut = await read.StaffCredentials.AsNoTracking().SingleAsync(c => c.StaffId == staff.Id, Ct);

        Assert.Equal(DateTimeKind.Utc, appointment.SlotStartUtc.Kind);
        Assert.Equal(utc, appointment.SlotStartUtc);
        Assert.Equal(DateTimeKind.Utc, lockedOut.LockoutEndUtc!.Value.Kind);
        Assert.Equal(utc, lockedOut.LockoutEndUtc);
    }

    [Fact]
    public async Task Read_ServerDefaultTimestamps_AreKindUtc()
    {
        // PlacedAtUtc and the audit time are stamped by the database (SYSUTCDATETIME()), never by a client clock.
        await using var db = fixture.NewContext(null);
        var appointment = TestRows.Appointment(TestRows.Donor(Tag()));
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(Ct);

        await using var read = fixture.NewContext(null);
        var placed = (await read.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointment.Id, Ct)).PlacedAtUtc;
        var audited = (await read.AuditLogs.AsNoTracking().FirstAsync(a => a.Entity == "Appointment" && a.EntityId == appointment.Id, Ct)).OccurredAtUtc;

        Assert.Equal(DateTimeKind.Utc, placed.Kind);
        Assert.Equal(DateTimeKind.Utc, audited.Kind);
        var serverNow = await fixture.ScalarAsync<DateTime>("SELECT SYSUTCDATETIME()", Ct);
        Assert.InRange((serverNow - placed).TotalSeconds, -5, 120);
    }

    [Fact]
    public async Task Query_WithALocalDateTimeParameter_IsRefused()
    {
        await using var db = fixture.NewContext(null);
        var local = DateTime.Now;

        await Assert.ThrowsAnyAsync<Exception>(() => db.Appointments.Where(a => a.SlotStartUtc >= local).CountAsync(Ct));
    }

    [Fact]
    public void EveryDateTimeProperty_IsUtcConverted_Datetime2()
    {
        using var db = fixture.NewContext(null);
        var properties = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType) == typeof(DateTime))
            .ToList();

        Assert.True(properties.Count >= 10, "Too few DateTime properties found: the model scan is not working.");
        Assert.All(properties, p =>
        {
            Assert.True(p.GetValueConverter() is UtcDateTimeConverter or NullableUtcDateTimeConverter, $"{p.DeclaringType.DisplayName()}.{p.Name} has no UTC converter.");
            Assert.Equal("datetime2(3)", p.GetColumnType());
        });
    }

    private static string Flatten(Exception e) => string.Join(" | ", Chain(e).Select(x => x.Message));

    private static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }
}
