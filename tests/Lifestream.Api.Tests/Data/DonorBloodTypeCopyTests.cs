using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Lifestream.Api.Tests.Data;

/// <summary>
/// <c>Appointment.DonorBloodType</c> (DB-05) is a copy of the donor's blood type so that "appointments by donor blood type" is an
/// index seek. It is correct on insert, follows the donor, and existing rows are filled by the migration that adds it.
/// </summary>
public sealed class DonorBloodTypeCopyTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Tag() => "BT" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task NewAppointment_ByDonorId_GetsTheDonorsBloodType()
    {
        await using var setup = fixture.NewContext(null);
        var donor = TestRows.Donor(Tag(), bloodType: BloodType.BNeg);
        setup.Donors.Add(donor);
        await setup.SaveChangesAsync(Ct);

        await using var db = fixture.NewContext(null);
        var appointment = new Appointment { DonorId = donor.Id, SlotStartUtc = DateTime.UtcNow.AddDays(3), Status = AppointmentStatus.Pending };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(Ct);

        Assert.Equal(BloodType.BNeg, appointment.DonorBloodType);
        Assert.Equal("BNeg", await fixture.ScalarAsync<string>($"SELECT DonorBloodType FROM Appointment WHERE Id = {appointment.Id}", Ct));
    }

    [Fact]
    public async Task NewAppointment_WithNewDonor_GetsTheDonorsBloodType()
    {
        await using var db = fixture.NewContext(null);
        var appointment = TestRows.Appointment(TestRows.Donor(Tag(), bloodType: BloodType.ABPos));
        db.Appointments.Add(appointment);

        db.SaveChanges();

        Assert.Equal("ABPos", await fixture.ScalarAsync<string>($"SELECT DonorBloodType FROM Appointment WHERE Id = {appointment.Id}", Ct));
    }

    [Fact]
    public async Task ChangingTheDonorsBloodType_RewritesTheirAppointments()
    {
        await using var db = fixture.NewContext(null);
        var donor = TestRows.Donor(Tag(), bloodType: BloodType.APos);
        var first = TestRows.Appointment(donor, new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        var second = TestRows.Appointment(donor, new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc));
        db.Appointments.AddRange(first, second);
        await db.SaveChangesAsync(Ct);

        donor.BloodType = BloodType.ONeg;
        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM Appointment WHERE DonorId = {donor.Id} AND DonorBloodType = 'ONeg'", Ct));
        Assert.Equal(0, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM Appointment WHERE DonorId = {donor.Id} AND DonorBloodType <> 'ONeg'", Ct));
    }

    [Fact]
    public async Task EveryAppointment_CarriesItsDonorsBloodType()
    {
        Assert.Equal(
            0,
            await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Appointment a JOIN Donor d ON d.Id = a.DonorId WHERE a.DonorBloodType <> d.BloodType", Ct));
    }

    [Fact]
    public async Task Migration_FillsDonorBloodType_OfAppointmentsThatAlreadyExist()
    {
        await using var empty = new EmptyDatabaseFixture();
        await empty.InitializeAsync();
        await using (var db = empty.NewContext(null))
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261118212125_InitialSchema", Ct);
            await empty.ExecuteAsync(
                $"""
                INSERT INTO Donor (Name, DateOfBirth, Gender, BloodType, Frequency, Phone, Address) VALUES (N'Old Donor', '1980-01-01', 'Male', 'ONeg', 'Regular', '+1-555-0100', 'x');
                INSERT INTO Appointment (DonorId, SlotStartUtc, Status) SELECT Id, '2026-01-01T09:00:00', 'Pending' FROM Donor;
                INSERT INTO Staff (DisplayName, Username, PasswordHash, Gender, DateOfBirth, Designation, Shift, Role, IsProtected, IsDemo, HasPhoto, FailedLoginCount)
                VALUES (N'Old', N'old', '{TestRows.ValidHash}', 'Male', '1990-01-01', 'Doctor', 'Morning', 'Staff', 0, 0, 0, 0);
                """,
                Ct);

            await migrator.MigrateAsync(null, Ct);
        }

        Assert.Equal("ONeg", await empty.ScalarAsync<string>("SELECT DonorBloodType FROM Appointment", Ct));
        Assert.Equal(1, await empty.ScalarAsync<int>("SELECT COUNT(*) FROM Staff", Ct));
    }
}
