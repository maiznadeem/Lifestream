using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests.Data;

/// <summary>DB-16: each insert, update and delete on the six business tables writes exactly one audit row.</summary>
public sealed class AuditTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Donor")]
    [InlineData("Patient")]
    [InlineData("Request")]
    [InlineData("Receipt")]
    [InlineData("Appointment")]
    [InlineData("Staff")]
    public async Task InsertUpdateDelete_EachWriteExactlyOneAuditRow(string entity)
    {
        // The acting user is a real staff row (the audit user id comes from the token in the API).
        await using var setup = fixture.NewContext();
        var actor = TestRows.Staff($"actor{Guid.NewGuid():N}"[..20]);
        var donor = TestRows.Donor();
        var patient = TestRows.Patient();
        setup.AddRange(actor, donor, patient);
        await setup.SaveChangesAsync(Ct);

        await using var db = fixture.NewContext(actor.Id);
        IEntity row = entity switch
        {
            "Donor" => TestRows.Donor("Audit Donor"),
            "Patient" => TestRows.Patient("Audit Patient"),
            "Request" => new Request { PatientId = patient.Id, BloodType = BloodType.APos, Units = 1, RequiredBy = new DateOnly(2026, 8, 1), Status = RequestStatus.Pending },
            "Appointment" => new Appointment { DonorId = donor.Id, SlotStartUtc = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc), Status = AppointmentStatus.Pending },
            "Staff" => TestRows.Staff($"audited{Guid.NewGuid():N}"[..20]),
            "Receipt" => await NewIssueReceiptAsync(actor.Id, patient.Id),
            _ => throw new InvalidOperationException(entity),
        };

        // Insert
        db.Add(row);
        await db.SaveChangesAsync(Ct);
        await AssertOneAuditRowAsync(entity, row.Id, AuditActions.Insert, actor.Id);

        // Update
        switch (row)
        {
            case Donor d: d.Name = "Audit Donor Renamed"; break;
            case Patient p: p.Name = "Audit Patient Renamed"; break;
            case Request r: r.Units = 3; break;
            case Appointment a: a.Status = AppointmentStatus.Cancelled; break;
            case Staff s: s.DisplayName = "Audited Renamed"; break;
            case Receipt rc: rc.Notes = "corrected"; break;
        }

        await db.SaveChangesAsync(Ct);
        await AssertOneAuditRowAsync(entity, row.Id, AuditActions.Update, actor.Id);

        // Delete
        var id = row.Id;
        db.Remove(row);
        await db.SaveChangesAsync(Ct);
        await AssertOneAuditRowAsync(entity, id, AuditActions.Delete, actor.Id);

        Assert.Equal(
            3,
            await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM AuditLog WHERE Entity = '{entity}' AND EntityId = {id}", Ct));
    }

    [Fact]
    public async Task NoChangeSave_WritesNoAuditRow()
    {
        await using var db = fixture.NewContext(1);
        var before = await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM AuditLog", Ct);

        _ = await db.Donors.ToListAsync(Ct);
        await db.SaveChangesAsync(Ct);

        Assert.Equal(before, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM AuditLog", Ct));
    }

    [Fact]
    public async Task AuditRow_HoldsUserActionEntityIdAndServerUtcTime()
    {
        await using var db = fixture.NewContext();
        var actor = TestRows.Staff($"time{Guid.NewGuid():N}"[..20]);
        db.Staff.Add(actor);
        await db.SaveChangesAsync(Ct);
        await using var acting = fixture.NewContext(actor.Id);
        var donor = TestRows.Donor("Timed Donor");
        acting.Donors.Add(donor);
        await acting.SaveChangesAsync(Ct);

        var row = (await fixture.RowsAsync(
            $"SELECT StaffId, Action, Entity, EntityId, ABS(DATEDIFF_BIG(SECOND, OccurredAtUtc, SYSUTCDATETIME())) FROM AuditLog WHERE Entity = 'Donor' AND EntityId = {donor.Id}",
            Ct)).Single();

        Assert.Equal(actor.Id, (int)row[0]!);
        Assert.Equal("Insert", (string)row[1]!);
        Assert.Equal("Donor", (string)row[2]!);
        Assert.Equal(donor.Id, (int)row[3]!);
        Assert.InRange(Convert.ToInt64(row[4], System.Globalization.CultureInfo.InvariantCulture), 0, 60);
    }

    [Fact]
    public async Task UnauditedTables_WriteNoAuditRows()
    {
        await using var db = fixture.NewContext(1);
        var staff = TestRows.Staff($"quiet{Guid.NewGuid():N}"[..20]);
        db.Add(staff);
        await db.SaveChangesAsync(Ct);
        db.RefreshTokens.Add(new RefreshToken { StaffId = staff.Id, FamilyId = Guid.NewGuid(), TokenHash = Guid.NewGuid().ToString("N"), ExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM AuditLog WHERE Entity = 'RefreshToken'", Ct));
    }

    private async Task<Receipt> NewIssueReceiptAsync(int staffId, int patientId)
    {
        await using var setup = fixture.NewContext();
        var request = new Request { PatientId = patientId, BloodType = BloodType.APos, Units = 1, RequiredBy = new DateOnly(2026, 8, 1), Status = RequestStatus.Pending };
        setup.Requests.Add(request);
        await setup.SaveChangesAsync(Ct);
        return new Receipt { StaffId = staffId, RequestId = request.Id, Kind = ReceiptKind.Issue, BloodType = BloodType.APos, Units = 1 };
    }

    private async Task AssertOneAuditRowAsync(string entity, int id, string action, int staffId)
    {
        var rows = await fixture.RowsAsync(
            $"SELECT StaffId FROM AuditLog WHERE Entity = '{entity}' AND EntityId = {id} AND Action = '{action}'", Ct);
        var row = Assert.Single(rows);
        Assert.Equal(staffId, (int)row[0]!);
    }
}
