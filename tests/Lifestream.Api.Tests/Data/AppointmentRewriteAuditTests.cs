using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests.Data;

/// <summary>
/// DB-16 for the appointments a donor's blood-type change rewrites (<c>Appointment.DonorBloodType</c> is a copy of the donor's type):
/// every rewritten appointment gets its Update audit row in the same transaction, appointments that the context tracks stay consistent
/// (no concurrency error on the next edit), a failed save leaves no audit row and no half-changed entity, and a bulk update of the
/// donors' blood type keeps the copy in sync too. Real SQL Server (the fixture asserts the server version).
/// </summary>
public sealed class AppointmentRewriteAuditTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Tag() => "RW" + Guid.NewGuid().ToString("N")[..10];

    private async Task<Staff> ActorAsync()
    {
        await using var db = fixture.NewContext();
        var actor = TestRows.Staff("rw" + Guid.NewGuid().ToString("N")[..12]);
        db.Staff.Add(actor);
        await db.SaveChangesAsync(Ct);
        return actor;
    }

    private async Task<(Donor Donor, List<Appointment> Appointments)> DonorWithAppointmentsAsync(int count, BloodType type = BloodType.APos)
    {
        await using var db = fixture.NewContext();
        var donor = TestRows.Donor(Tag(), bloodType: type);
        var appointments = Enumerable.Range(0, count).Select(i => TestRows.Appointment(donor, new DateTime(2027, 3, 1 + i, 9, 0, 0, DateTimeKind.Utc))).ToList();
        db.Appointments.AddRange(appointments);
        await db.SaveChangesAsync(Ct);
        return (donor, appointments);
    }

    private async Task<List<(string Action, int? StaffId, DateTime At)>> AuditAsync(string entity, int id)
    {
        var rows = await fixture.RowsAsync($"SELECT Action, StaffId, OccurredAtUtc FROM AuditLog WHERE Entity = '{entity}' AND EntityId = {id} ORDER BY Id", Ct);
        return rows.Select(r => ((string)r[0]!, r[1] as int?, (DateTime)r[2]!)).ToList();
    }

    [Fact]
    public async Task RanAgainstARealSqlServer()
    {
        Assert.Contains("Microsoft SQL Server", fixture.ServerVersion, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM sys.databases WHERE name = DB_NAME()", Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BloodTypeChange_WritesAnUpdateAuditRowForEveryRewrittenAppointment_InTheSameTransaction(bool useAsync)
    {
        var actor = await ActorAsync();
        var (donor, appointments) = await DonorWithAppointmentsAsync(3);
        var other = await DonorWithAppointmentsAsync(1, BloodType.BPos);

        await using (var db = fixture.NewContext(actor.Id))
        {
            var tracked = await db.Donors.SingleAsync(d => d.Id == donor.Id, Ct);
            tracked.BloodType = BloodType.ONeg;
            if (useAsync)
            {
                await db.SaveChangesAsync(Ct);
            }
            else
            {
                db.SaveChanges();
            }
        }

        foreach (var appointment in appointments)
        {
            var audit = await AuditAsync("Appointment", appointment.Id);
            Assert.Equal(["Insert", "Update"], audit.Select(a => a.Action));
            var update = audit.Last();
            Assert.Equal(actor.Id, update.StaffId);
            Assert.InRange((DateTime.UtcNow - update.At).TotalSeconds, -30, 30);
            Assert.Equal("ONeg", await fixture.ScalarAsync<string>($"SELECT DonorBloodType FROM Appointment WHERE Id = {appointment.Id}", Ct));
        }

        // The donor's own update is audited too, and another donor's appointment is neither changed nor audited.
        Assert.Contains(await AuditAsync("Donor", donor.Id), a => a.Action == "Update" && a.StaffId == actor.Id);
        Assert.Equal(["Insert"], (await AuditAsync("Appointment", other.Appointments[0].Id)).Select(a => a.Action));
        Assert.Equal("BPos", await fixture.ScalarAsync<string>($"SELECT DonorBloodType FROM Appointment WHERE Id = {other.Appointments[0].Id}", Ct));
    }

    [Fact]
    public async Task BloodTypeChange_AppointmentsAlreadyOfThatType_AreNotRewrittenOrAudited()
    {
        var actor = await ActorAsync();
        var (donor, appointments) = await DonorWithAppointmentsAsync(1);

        await using (var db = fixture.NewContext(actor.Id))
        {
            var tracked = await db.Donors.SingleAsync(d => d.Id == donor.Id, Ct);
            tracked.Name = Tag(); // a rename is not a blood-type change
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(["Insert"], (await AuditAsync("Appointment", appointments[0].Id)).Select(a => a.Action));
    }

    [Fact]
    public async Task TrackedAppointments_AfterTheirDonorsBloodTypeChanges_AreCurrent_AndTheNextEditIsNotAConcurrencyError()
    {
        var actor = await ActorAsync();
        var (donor, appointments) = await DonorWithAppointmentsAsync(2);

        await using var db = fixture.NewContext(actor.Id);
        var trackedDonor = await db.Donors.SingleAsync(d => d.Id == donor.Id, Ct);
        var trackedAppointments = await db.Appointments.Where(a => a.DonorId == donor.Id).OrderBy(a => a.Id).ToListAsync(Ct);
        var versionsBefore = trackedAppointments.Select(a => a.RowVersion.ToArray()).ToList();

        trackedDonor.BloodType = BloodType.BNeg;
        await db.SaveChangesAsync(Ct);

        Assert.All(trackedAppointments, a => Assert.Equal(BloodType.BNeg, a.DonorBloodType));
        Assert.All(trackedAppointments, a => Assert.Equal(EntityState.Unchanged, db.Entry(a).State));

        // The next edit of a tracked appointment goes through (it used to throw DbUpdateConcurrencyException: stale RowVersion).
        trackedAppointments[0].Status = AppointmentStatus.Cancelled;
        trackedAppointments[1].SlotStartUtc = new DateTime(2027, 4, 10, 9, 30, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync(Ct);

        foreach (var appointment in appointments)
        {
            Assert.Equal(["Insert", "Update", "Update"], (await AuditAsync("Appointment", appointment.Id)).Select(a => a.Action));
        }

        Assert.Equal("Cancelled", await fixture.ScalarAsync<string>($"SELECT Status FROM Appointment WHERE Id = {appointments[0].Id}", Ct));
        Assert.NotEmpty(versionsBefore);
    }

    [Fact]
    public async Task TrackedAppointment_ModifiedInTheSameSave_IsSavedWithTheBloodTypeChange()
    {
        var actor = await ActorAsync();
        var (donor, appointments) = await DonorWithAppointmentsAsync(1);

        await using var db = fixture.NewContext(actor.Id);
        var trackedDonor = await db.Donors.SingleAsync(d => d.Id == donor.Id, Ct);
        var trackedAppointment = await db.Appointments.SingleAsync(a => a.Id == appointments[0].Id, Ct);
        trackedDonor.BloodType = BloodType.ABPos;
        trackedAppointment.Status = AppointmentStatus.NoShow;

        await db.SaveChangesAsync(Ct);

        Assert.Equal("ABPos", await fixture.ScalarAsync<string>($"SELECT DonorBloodType FROM Appointment WHERE Id = {appointments[0].Id}", Ct));
        Assert.Equal("NoShow", await fixture.ScalarAsync<string>($"SELECT Status FROM Appointment WHERE Id = {appointments[0].Id}", Ct));
        Assert.Equal(["Insert", "Update", "Update"], (await AuditAsync("Appointment", appointments[0].Id)).Select(a => a.Action));
    }

    [Fact]
    public async Task AppointmentChangedByAnotherSessionMeanwhile_StillFailsItsOwnSave()
    {
        var actor = await ActorAsync();
        var (donor, appointments) = await DonorWithAppointmentsAsync(1);

        await using var db = fixture.NewContext(actor.Id);
        var trackedDonor = await db.Donors.SingleAsync(d => d.Id == donor.Id, Ct);
        var trackedAppointment = await db.Appointments.SingleAsync(a => a.Id == appointments[0].Id, Ct);
        await fixture.ExecuteAsync($"UPDATE Appointment SET Status = 'Cancelled' WHERE Id = {appointments[0].Id}", Ct); // someone else
        trackedDonor.BloodType = BloodType.ABNeg;
        trackedAppointment.SlotStartUtc = new DateTime(2027, 5, 1, 9, 0, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task FailedSave_LeavesNoAuditRowAndNoRewrite_AndTheSameContextCanSaveAgain()
    {
        var actor = await ActorAsync();
        var (donor, appointments) = await DonorWithAppointmentsAsync(2);

        await using var db = fixture.NewContext(actor.Id);
        var trackedDonor = await db.Donors.SingleAsync(d => d.Id == donor.Id, Ct);
        var trackedAppointments = await db.Appointments.Where(a => a.DonorId == donor.Id).ToListAsync(Ct);
        await fixture.ExecuteAsync($"UPDATE Donor SET Phone = '+1-555-0199' WHERE Id = {donor.Id}", Ct); // the donor row version moves on
        trackedDonor.BloodType = BloodType.ONeg;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));

        foreach (var appointment in appointments)
        {
            Assert.Equal("APos", await fixture.ScalarAsync<string>($"SELECT DonorBloodType FROM Appointment WHERE Id = {appointment.Id}", Ct));
            Assert.Equal(["Insert"], (await AuditAsync("Appointment", appointment.Id)).Select(a => a.Action));
        }

        // The tracked appointments are as they were; after the donor is reloaded the same context saves cleanly.
        Assert.All(trackedAppointments, a => Assert.Equal(BloodType.APos, a.DonorBloodType));
        await db.Entry(trackedDonor).ReloadAsync(Ct);
        trackedDonor.BloodType = BloodType.ONeg;
        await db.SaveChangesAsync(Ct);
        foreach (var appointment in appointments)
        {
            Assert.Equal(["Insert", "Update"], (await AuditAsync("Appointment", appointment.Id)).Select(a => a.Action));
        }
    }

    [Fact]
    public async Task BulkUpdate_OfDonorBloodType_RewritesTheirAppointments_WithAuditRows_InOneTransaction()
    {
        var actor = await ActorAsync();
        var (first, firstAppointments) = await DonorWithAppointmentsAsync(2);
        var (second, secondAppointments) = await DonorWithAppointmentsAsync(1, BloodType.BNeg);
        var (_, untouchedAppointments) = await DonorWithAppointmentsAsync(1, BloodType.OPos);

        await using var db = fixture.NewContext(actor.Id);
        var trackedAppointment = await db.Appointments.SingleAsync(a => a.Id == firstAppointments[0].Id, Ct);
        var changed = await db.BulkUpdateAsync(db.Donors.Where(d => d.Id == first.Id || d.Id == second.Id), s => s.SetProperty(d => d.BloodType, BloodType.ABNeg), Ct);

        Assert.Equal(2, changed);
        foreach (var appointment in firstAppointments.Concat(secondAppointments))
        {
            Assert.Equal("ABNeg", await fixture.ScalarAsync<string>($"SELECT DonorBloodType FROM Appointment WHERE Id = {appointment.Id}", Ct));
            var audit = await AuditAsync("Appointment", appointment.Id);
            Assert.Equal(["Insert", "Update"], audit.Select(a => a.Action));
            Assert.Equal(actor.Id, audit.Last().StaffId);
        }

        Assert.Contains(await AuditAsync("Donor", first.Id), a => a.Action == "Update");
        Assert.Contains(await AuditAsync("Donor", second.Id), a => a.Action == "Update");
        Assert.Equal("OPos", await fixture.ScalarAsync<string>($"SELECT DonorBloodType FROM Appointment WHERE Id = {untouchedAppointments[0].Id}", Ct));
        Assert.Equal(["Insert"], (await AuditAsync("Appointment", untouchedAppointments[0].Id)).Select(a => a.Action));

        // The tracked appointment is current: editing it next works.
        Assert.Equal(BloodType.ABNeg, trackedAppointment.DonorBloodType);
        trackedAppointment.Status = AppointmentStatus.Cancelled;
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task BulkUpdate_OfSomethingElseOnDonors_DoesNotTouchOrAuditAppointments()
    {
        var actor = await ActorAsync();
        var (donor, appointments) = await DonorWithAppointmentsAsync(1);

        await using var db = fixture.NewContext(actor.Id);
        await db.BulkUpdateAsync(db.Donors.Where(d => d.Id == donor.Id), s => s.SetProperty(d => d.Address, "1 Quiet Street"), Ct);

        Assert.Equal(["Insert"], (await AuditAsync("Appointment", appointments[0].Id)).Select(a => a.Action));
        Assert.Contains(await AuditAsync("Donor", donor.Id), a => a.Action == "Update");
    }

    [Fact]
    public async Task BulkUpdate_AsSystemWork_AuditOff_StillKeepsTheCopyInSync_WithoutAuditRows()
    {
        var (donor, appointments) = await DonorWithAppointmentsAsync(1);

        await using var db = fixture.NewContext();
        db.AuditEnabled = false;
        await db.BulkUpdateAsync(db.Donors.Where(d => d.Id == donor.Id), s => s.SetProperty(d => d.BloodType, BloodType.BPos), Ct);

        Assert.Equal("BPos", await fixture.ScalarAsync<string>($"SELECT DonorBloodType FROM Appointment WHERE Id = {appointments[0].Id}", Ct));
        Assert.Equal(["Insert"], (await AuditAsync("Appointment", appointments[0].Id)).Select(a => a.Action));
    }
}
