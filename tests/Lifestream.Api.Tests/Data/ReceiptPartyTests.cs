using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Data.Entities;
using Lifestream.Data.Seed;
using Lifestream.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Lifestream.Api.Tests.Data;

/// <summary>What every receipt must satisfy: its party is its appointment's donor or its request's patient, and nothing else.</summary>
internal static class ReceiptParty
{
    /// <summary>Counts the receipts whose stored party is missing, on the wrong side, or not the one of their appointment or request. Must be 0.</summary>
    public const string Wrong =
        """
        SELECT COUNT(*) FROM Receipt r
        LEFT JOIN Appointment a ON a.Id = r.AppointmentId
        LEFT JOIN Request q ON q.Id = r.RequestId
        WHERE (r.Kind = 'Donation' AND (r.DonorId IS NULL OR a.Id IS NULL OR r.DonorId <> a.DonorId OR r.PatientId IS NOT NULL))
           OR (r.Kind = 'Issue' AND (r.PatientId IS NULL OR q.Id IS NULL OR r.PatientId <> q.PatientId OR r.DonorId IS NOT NULL))
        """;
}

/// <summary>
/// The party a receipt stores (<see cref="Receipt.DonorId"/>, <see cref="Receipt.PatientId"/>): set on every way a receipt is added through the
/// context, and kept right by the database whatever writes the row (the composite foreign keys and CK_Receipt_Party), tested by trying to break it.
/// </summary>
public sealed class ReceiptPartyTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    private async Task<(Staff Staff, Appointment Appointment, Request Request, Donor OtherDonor, Patient OtherPatient)> RowsAsync()
    {
        await using var db = fixture.NewContext();
        var staff = TestRows.Staff(Unique("party"));
        var appointment = TestRows.Appointment(TestRows.Donor());
        var request = TestRows.Request(TestRows.Patient());
        var otherDonor = TestRows.Donor("Somebody Else");
        var otherPatient = TestRows.Patient("Somebody Else");
        db.AddRange(staff, appointment, request, otherDonor, otherPatient);
        await db.SaveChangesAsync(Ct);
        return (staff, appointment, request, otherDonor, otherPatient);
    }

    private async Task<(int? DonorId, int? PatientId)> PartyAsync(int receiptId)
    {
        var row = (await fixture.RowsAsync($"SELECT DonorId, PatientId FROM Receipt WHERE Id = {receiptId}", Ct)).Single();
        return (row[0] as int?, row[1] as int?);
    }

    private static async Task<SqlException> Rejected(Func<Task> action)
    {
        var exception = await Assert.ThrowsAnyAsync<DbUpdateException>(action);
        return Assert.IsType<SqlException>(exception.InnerException);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AReceiptAddedByItsAppointmentOrRequestId_GetsTheParty_FromTheDatabase(bool audited, bool async)
    {
        var rows = await RowsAsync();
        await using var db = fixture.NewContext(); // nothing tracked: the party is looked up
        db.AuditEnabled = audited;
        var donation = new Receipt { StaffId = rows.Staff.Id, AppointmentId = rows.Appointment.Id, Kind = ReceiptKind.Donation, BloodType = BloodType.OPos, Units = 1 };
        var issue = new Receipt { StaffId = rows.Staff.Id, RequestId = rows.Request.Id, Kind = ReceiptKind.Issue, BloodType = BloodType.OPos, Units = 1 };
        db.Receipts.AddRange(donation, issue);
        if (async)
        {
            await db.SaveChangesAsync(Ct);
        }
        else
        {
            db.SaveChanges();
        }

        Assert.Equal((rows.Appointment.DonorId, null), await PartyAsync(donation.Id));
        Assert.Equal((null, rows.Request.PatientId), await PartyAsync(issue.Id));
        Assert.Equal(rows.Appointment.DonorId, donation.DonorId); // and on the object the caller holds
        Assert.Equal(rows.Request.PatientId, issue.PatientId);
    }

    [Fact]
    public async Task AReceiptAddedById_WithItsAppointmentTrackedByTheSameContext_GetsTheParty()
    {
        var rows = await RowsAsync();
        await using var db = fixture.NewContext();
        var appointment = await db.Appointments.SingleAsync(a => a.Id == rows.Appointment.Id, Ct); // as the record-donation endpoint holds it
        var request = await db.Requests.SingleAsync(r => r.Id == rows.Request.Id, Ct);
        var donation = new Receipt { StaffId = rows.Staff.Id, AppointmentId = appointment.Id, Kind = ReceiptKind.Donation, BloodType = BloodType.OPos, Units = 1 };
        var issue = new Receipt { StaffId = rows.Staff.Id, RequestId = request.Id, Kind = ReceiptKind.Issue, BloodType = BloodType.OPos, Units = 1 };
        db.Receipts.AddRange(donation, issue);
        await db.SaveChangesAsync(Ct);

        Assert.Equal((appointment.DonorId, null), await PartyAsync(donation.Id));
        Assert.Equal((null, request.PatientId), await PartyAsync(issue.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReceiptAddedWithEverythingNew_AsObjects_GetsTheParty_InOneSave(bool audited)
    {
        await using var db = fixture.NewContext();
        db.AuditEnabled = audited; // the demo data and the 100x data are saved unaudited
        var staff = TestRows.Staff(Unique("new"));
        var appointment = TestRows.Appointment(TestRows.Donor());
        var request = TestRows.Request(TestRows.Patient());
        var donation = TestRows.Donation(staff, appointment);
        var issue = TestRows.Issue(staff, request);
        db.AddRange(donation, issue);
        await db.SaveChangesAsync(Ct);

        Assert.True(appointment.DonorId > 0 && request.PatientId > 0);
        Assert.Equal((appointment.DonorId, null), await PartyAsync(donation.Id));
        Assert.Equal((null, request.PatientId), await PartyAsync(issue.Id));
    }

    [Fact]
    public async Task TheDemoData_HasEveryReceiptsParty_Right()
    {
        await using var db = fixture.NewContext();
        db.AuditEnabled = false;
        new DemoDataGenerator().Generate(DateTime.UtcNow).AddTo(db); // what the demo reset and the migrate job save
        await db.SaveChangesAsync(Ct);

        Assert.True(await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Receipt WHERE DonorId IS NOT NULL", Ct) > 50);
        Assert.True(await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Receipt WHERE PatientId IS NOT NULL", Ct) > 20);
        Assert.Equal(0, await fixture.ScalarAsync<int>(ReceiptParty.Wrong, Ct));
    }

    // ---- the database refuses a wrong or a missing party, whatever writes the row ----

    [Fact]
    public async Task ADonationWithAnotherDonor_AndAnIssueWithAnotherPatient_AreRefused_ByTheForeignKey()
    {
        var rows = await RowsAsync();
        await using var db = fixture.NewContext();
        db.Receipts.Add(new Receipt { StaffId = rows.Staff.Id, AppointmentId = rows.Appointment.Id, DonorId = rows.OtherDonor.Id, Kind = ReceiptKind.Donation, BloodType = BloodType.OPos, Units = 1 });
        var donation = await Rejected(() => db.SaveChangesAsync(Ct));
        Assert.Equal(547, donation.Number);
        Assert.Contains("FK_Receipt_Appointment_AppointmentId_DonorId", donation.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();

        db.Receipts.Add(new Receipt { StaffId = rows.Staff.Id, RequestId = rows.Request.Id, PatientId = rows.OtherPatient.Id, Kind = ReceiptKind.Issue, BloodType = BloodType.OPos, Units = 1 });
        var issue = await Rejected(() => db.SaveChangesAsync(Ct));
        Assert.Equal(547, issue.Number);
        Assert.Contains("FK_Receipt_Request_RequestId_PatientId", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("'Donation', {appointment}, NULL, NULL, NULL")] // a donation without its donor
    [InlineData("'Issue', NULL, {request}, NULL, NULL")] // an issue without its patient
    [InlineData("'Donation', {appointment}, NULL, {donor}, {patient}")] // a donation that also names a patient
    [InlineData("'Issue', NULL, {request}, {donor}, {patient}")] // an issue that also names a donor
    public async Task ARowWrittenPastTheContext_WithoutItsParty_OrWithBoth_IsRefused_ByTheCheck(string values)
    {
        var rows = await RowsAsync();
        var sql = "INSERT INTO Receipt (StaffId, BloodType, Units, Kind, AppointmentId, RequestId, DonorId, PatientId) VALUES ("
            + $"{rows.Staff.Id}, 'OPos', 1, "
            + values.Replace("{appointment}", $"{rows.Appointment.Id}", StringComparison.Ordinal).Replace("{request}", $"{rows.Request.Id}", StringComparison.Ordinal)
                .Replace("{donor}", $"{rows.Appointment.DonorId}", StringComparison.Ordinal).Replace("{patient}", $"{rows.Request.PatientId}", StringComparison.Ordinal)
            + ")";

        var error = await Assert.ThrowsAsync<SqlException>(() => fixture.ExecuteAsync(sql, Ct));

        Assert.Equal(547, error.Number);
        Assert.Contains("CK_Receipt_Party", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDonorOfAReceiptedAppointment_AndThePatientOfAnIssuedRequest_CannotBeChanged_SoTheCopyCannotGoStale()
    {
        var rows = await RowsAsync();
        await using var db = fixture.NewContext();
        db.Receipts.AddRange(
            new Receipt { StaffId = rows.Staff.Id, AppointmentId = rows.Appointment.Id, Kind = ReceiptKind.Donation, BloodType = BloodType.OPos, Units = 1 },
            new Receipt { StaffId = rows.Staff.Id, RequestId = rows.Request.Id, Kind = ReceiptKind.Issue, BloodType = BloodType.OPos, Units = 1 });
        await db.SaveChangesAsync(Ct);

        var appointment = await Assert.ThrowsAsync<SqlException>(() => fixture.ExecuteAsync($"UPDATE Appointment SET DonorId = {rows.OtherDonor.Id} WHERE Id = {rows.Appointment.Id}", Ct));
        Assert.Equal(547, appointment.Number);
        var request = await Assert.ThrowsAsync<SqlException>(() => fixture.ExecuteAsync($"UPDATE Request SET PatientId = {rows.OtherPatient.Id} WHERE Id = {rows.Request.Id}", Ct));
        Assert.Equal(547, request.Number);
        var receipt = await Assert.ThrowsAsync<SqlException>(() => fixture.ExecuteAsync($"UPDATE Receipt SET DonorId = {rows.OtherDonor.Id} WHERE AppointmentId = {rows.Appointment.Id}", Ct));
        Assert.Equal(547, receipt.Number);
        Assert.Equal(0, await fixture.ScalarAsync<int>(ReceiptParty.Wrong, Ct));
    }
}

/// <summary>The two write paths of the API that create a receipt: recording a donation and fulfilling a request.</summary>
public sealed class ReceiptPartyApiTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RecordingADonation_AndFulfillingARequest_StoreTheParty()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        var appointment = await Seed.AppointmentAsync(fx, donor, DateTime.UtcNow.AddHours(-1));
        var patient = await Seed.PatientAsync(fx, type: BloodType.ABPos);
        await Seed.BagsAsync(fx, BloodType.ABPos, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-2));
        var request = await Seed.RequestAsync(fx, patient, 1);

        using var donated = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(1, null), appointment.Id);
        using var fulfilled = await staff.Client.PostApi(ApiRoutes.RequestFulfil, id: request.Id);

        Assert.True(donated.IsSuccessStatusCode, await donated.Content.ReadAsStringAsync(Ct));
        Assert.True(fulfilled.IsSuccessStatusCode, await fulfilled.Content.ReadAsStringAsync(Ct));
        Assert.Equal(donor.Id, await fx.Db.ScalarAsync<int>($"SELECT DonorId FROM Receipt WHERE AppointmentId = {appointment.Id} AND PatientId IS NULL", Ct));
        Assert.Equal(patient.Id, await fx.Db.ScalarAsync<int>($"SELECT PatientId FROM Receipt WHERE RequestId = {request.Id} AND DonorId IS NULL", Ct));
        Assert.Equal(0, await fx.Db.ScalarAsync<int>(ReceiptParty.Wrong, Ct));
    }
}

/// <summary>
/// The ReceiptParty migration on a database at the schema before it (2.1.6) that holds receipts: Up fills every receipt's party and adds what
/// keeps it right, Down takes all of it away again and leaves the schema and the rows as they were, and Up works again afterwards.
/// </summary>
public sealed class ReceiptPartyMigrationTests(EmptyDatabaseFixture empty) : IClassFixture<EmptyDatabaseFixture>
{
    private const string Before = "20261119150000_StaffCredentialAndSecurityState";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<int> CountAsync(string sql) => empty.ScalarAsync<int>(sql, Ct);

    [Fact]
    public async Task Up_FillsEveryReceiptsParty_Down_RestoresTheSchemaBefore_AndUpWorksAgain()
    {
        await using var db = empty.NewContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        Assert.Equal("ReceiptParty", all[^1][(all[^1].IndexOf('_', StringComparison.Ordinal) + 1)..]);
        Assert.Equal(Before, all[^2]);

        // A database as 2.1.6 has it, with people, appointments and requests (those tables are the same in both schemas, so the context writes
        // them) and receipts written as 2.1.6 wrote them: no party columns.
        await migrator.MigrateAsync(Before, Ct);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('Receipt') AND name IN ('DonorId', 'PatientId')"));
        var staff = TestRows.Staff("party.migration");
        var appointments = Enumerable.Range(0, 3).Select(i => TestRows.Appointment(TestRows.Donor($"Migrated Donor {i}"))).ToList();
        var requests = Enumerable.Range(0, 2).Select(i => TestRows.Request(TestRows.Patient($"Migrated Patient {i}"))).ToList();
        db.Add(staff);
        db.AddRange(appointments);
        db.AddRange(requests);
        await db.SaveChangesAsync(Ct);
        foreach (var appointment in appointments.Take(2))
        {
            await empty.ExecuteAsync($"INSERT INTO Receipt (StaffId, BloodType, Units, Kind, AppointmentId) VALUES ({staff.Id}, 'OPos', 1, 'Donation', {appointment.Id})", Ct);
        }

        await empty.ExecuteAsync($"INSERT INTO Receipt (StaffId, BloodType, Units, Kind, RequestId) VALUES ({staff.Id}, 'OPos', 1, 'Issue', {requests[0].Id})", Ct);

        await migrator.MigrateAsync(cancellationToken: Ct);
        await AssertMigratedAsync(receipts: 3);
        Assert.Equal(appointments[0].DonorId, await CountAsync($"SELECT DonorId FROM Receipt WHERE AppointmentId = {appointments[0].Id}"));
        Assert.Equal(appointments[1].DonorId, await CountAsync($"SELECT DonorId FROM Receipt WHERE AppointmentId = {appointments[1].Id}"));
        Assert.Equal(requests[0].PatientId, await CountAsync($"SELECT PatientId FROM Receipt WHERE RequestId = {requests[0].Id}"));

        await migrator.MigrateAsync(Before, Ct);
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM Receipt")); // the rows are kept
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('Receipt') AND name IN ('DonorId', 'PatientId')"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM sys.key_constraints WHERE name IN ('AK_Appointment_Id_DonorId', 'AK_Request_Id_PatientId')"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Receipt_Party'"));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('Receipt') AND name IN ('IX_Receipt_AppointmentId', 'IX_Receipt_RequestId')"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('Receipt') AND name = 'IX_Receipt_AppointmentId' AND is_unique = 1"));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM sys.foreign_keys WHERE name IN ('FK_Receipt_Appointment_AppointmentId', 'FK_Receipt_Request_RequestId')"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('Receipt') AND name IN ('IX_Receipt_AppointmentId_DonorId', 'IX_Receipt_RequestId_PatientId', 'IX_Receipt_DonorId_CreatedAtUtc', 'IX_Receipt_PatientId_CreatedAtUtc')"));
        Assert.Equal([Before], (await db.Database.GetAppliedMigrationsAsync(Ct)).TakeLast(1));

        // 2.1.6 works on it again: a receipt written the old way, and the rule that an appointment is receipted once.
        await empty.ExecuteAsync($"INSERT INTO Receipt (StaffId, BloodType, Units, Kind, AppointmentId) VALUES ({staff.Id}, 'OPos', 1, 'Donation', {appointments[2].Id})", Ct);
        var twice = await Assert.ThrowsAsync<SqlException>(() =>
            empty.ExecuteAsync($"INSERT INTO Receipt (StaffId, BloodType, Units, Kind, AppointmentId) VALUES ({staff.Id}, 'OPos', 1, 'Donation', {appointments[2].Id})", Ct));
        Assert.Contains("IX_Receipt_AppointmentId", twice.Message, StringComparison.Ordinal);

        await migrator.MigrateAsync(cancellationToken: Ct);
        await AssertMigratedAsync(receipts: 4);
        Assert.Equal(appointments[2].DonorId, await CountAsync($"SELECT DonorId FROM Receipt WHERE AppointmentId = {appointments[2].Id}"));
    }

    private async Task AssertMigratedAsync(int receipts)
    {
        await using var db = empty.NewContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(receipts, await CountAsync("SELECT COUNT(*) FROM Receipt"));
        Assert.Equal(0, await CountAsync(ReceiptParty.Wrong));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Receipt_Party' AND (is_not_trusted = 1 OR is_disabled = 1)"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Receipt_Party'"));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM sys.foreign_keys WHERE name IN ('FK_Receipt_Appointment_AppointmentId_DonorId', 'FK_Receipt_Request_RequestId_PatientId') AND is_not_trusted = 0 AND is_disabled = 0"));
        Assert.Equal(4, await CountAsync("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('Receipt') AND name IN ('IX_Receipt_AppointmentId_DonorId', 'IX_Receipt_RequestId_PatientId', 'IX_Receipt_DonorId_CreatedAtUtc', 'IX_Receipt_PatientId_CreatedAtUtc')"));
    }
}
