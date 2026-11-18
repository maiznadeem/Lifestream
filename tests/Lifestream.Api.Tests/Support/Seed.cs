using Lifestream.Api.Tests.Data;
using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests.Support;

/// <summary>Rows written straight to the database to set a scenario up (the API under test is not used to build its own fixtures).</summary>
public static class Seed
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A slot on a clinic day: <paramref name="daysAhead"/> days from today at hh:mm UTC (the default clinic zone).</summary>
    public static DateTime Slot(int daysAhead, int hour, int minute = 0) =>
        DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(daysAhead).AddHours(hour).AddMinutes(minute), DateTimeKind.Utc);

    public static async Task<Donor> DonorAsync(ApiFixture fx, string? name = null, BloodType type = BloodType.OPos, DateOnly? dob = null, DateOnly? lastDonated = null, Gender gender = Gender.Female, bool medicalHold = false)
    {
        await using var db = fx.Db.NewContext();
        var donor = TestRows.Donor(name ?? fx.NextName("Donor "), gender, type);
        donor.DateOfBirth = dob ?? DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-30));
        donor.LastDonated = lastDonated;
        donor.MedicalHold = medicalHold;
        db.Donors.Add(donor);
        await db.SaveChangesAsync(Ct);
        return donor;
    }

    public static async Task<Patient> PatientAsync(ApiFixture fx, string? name = null, BloodType type = BloodType.OPos)
    {
        await using var db = fx.Db.NewContext();
        var patient = TestRows.Patient(name ?? fx.NextName("Patient "), Gender.Male, type);
        db.Patients.Add(patient);
        await db.SaveChangesAsync(Ct);
        return patient;
    }

    public static async Task<Appointment> AppointmentAsync(ApiFixture fx, Donor donor, DateTime slotUtc, AppointmentStatus status = AppointmentStatus.Pending)
    {
        await using var db = fx.Db.NewContext();
        var appointment = new Appointment { DonorId = donor.Id, SlotStartUtc = slotUtc, Status = status };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(Ct);
        return appointment;
    }

    public static async Task<Request> RequestAsync(ApiFixture fx, Patient patient, int units, RequestStatus status = RequestStatus.Pending, DateTime? placedAtUtc = null)
    {
        await using var db = fx.Db.NewContext();
        var request = new Request
        {
            PatientId = patient.Id,
            BloodType = patient.BloodType,
            Units = units,
            RequiredBy = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
            Status = status,
        };
        if (placedAtUtc is { } placed)
        {
            request.PlacedAtUtc = placed;
        }

        db.Requests.Add(request);
        await db.SaveChangesAsync(Ct);
        return request;
    }

    /// <summary>Bags of one blood type, one per collected date, each tied to a (hidden) donation receipt as the schema requires.</summary>
    public static async Task<List<BloodBag>> BagsAsync(ApiFixture fx, BloodType type, params DateTime[] collectedAtUtc)
    {
        await using var db = fx.Db.NewContext();
        var staff = TestRows.Staff(fx.NextName("seeder"));
        var donor = TestRows.Donor(fx.NextName("Bag donor "), Gender.Male, type);
        var appointment = TestRows.Appointment(donor);
        appointment.Status = AppointmentStatus.Completed;
        var receipt = TestRows.Donation(staff, appointment, collectedAtUtc.Length);
        receipt.BloodType = type;
        var bags = collectedAtUtc.Select(c => TestRows.Bag(receipt, c)).ToList();
        db.AddRange(staff, donor, appointment, receipt);
        db.BloodBags.AddRange(bags);
        db.AuditEnabled = false;
        await db.SaveChangesAsync(Ct);
        return bags;
    }

    public static async Task<List<BloodBag>> AllBagsAsync(ApiFixture fx, BloodType type)
    {
        await using var db = fx.Db.NewContext();
        return await db.BloodBags.AsNoTracking().Where(b => b.BloodType == type).OrderBy(b => b.Id).ToListAsync(Ct);
    }

    /// <summary>Removes every bag of a type, so a test about stock of that type starts from nothing.</summary>
    public static async Task ClearBagsAsync(ApiFixture fx, BloodType type)
    {
        await using var db = fx.Db.NewContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM BloodBag WHERE BloodType = {0}", [type.ToString()], Ct);
    }

    public static async Task<Receipt> ReceiptAsync(ApiFixture fx, ReceiptKind kind, DateTime createdAtUtc, BloodType type = BloodType.OPos)
    {
        await using var db = fx.Db.NewContext();
        db.AuditEnabled = false;
        var staff = TestRows.Staff(fx.NextName("receipt-staff"));
        Receipt receipt;
        if (kind == ReceiptKind.Donation)
        {
            var donor = TestRows.Donor(fx.NextName("Receipt donor "), Gender.Male, type);
            var appointment = TestRows.Appointment(donor);
            appointment.Status = AppointmentStatus.Completed;
            receipt = TestRows.Donation(staff, appointment);
            db.AddRange(staff, donor, appointment);
        }
        else
        {
            var patient = TestRows.Patient(fx.NextName("Receipt patient "), Gender.Male, type);
            var request = TestRows.Request(patient);
            request.Status = RequestStatus.Fulfilled;
            receipt = TestRows.Issue(staff, request);
            db.AddRange(staff, patient, request);
        }

        receipt.CreatedAtUtc = createdAtUtc;
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync(Ct);
        return receipt;
    }
}
