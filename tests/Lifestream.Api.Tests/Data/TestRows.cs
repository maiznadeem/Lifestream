using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Data.Security;
using Lifestream.Domain;

namespace Lifestream.Api.Tests.Data;

/// <summary>Valid rows for tests that need a row to exist (or to break one value of).</summary>
internal static class TestRows
{
    /// <summary>One real hash, computed once; hashing at 600,000 iterations is deliberately slow.</summary>
    public static readonly string ValidHash = PasswordHashing.Hash("not-a-real-password-for-tests");

    public static Staff Staff(string username = "tester") => new()
    {
        DisplayName = "Test Staff",
        Username = username,
        Credential = new StaffCredential { PasswordHash = ValidHash },
        Gender = Gender.Female,
        DateOfBirth = new DateOnly(1990, 1, 1),
        Designation = Designation.Doctor,
        Shift = Shift.Morning,
        Role = Role.Staff,
        Phone = "+1-555-0100",
    };

    public static Donor Donor(string name = "Test Donor", Gender gender = Gender.Male, BloodType bloodType = BloodType.OPos) => new()
    {
        Name = name,
        DateOfBirth = new DateOnly(1985, 5, 5),
        Gender = gender,
        BloodType = bloodType,
        Phone = "+1-555-0101",
        Address = "1 Sample Street, Demoville",
        Frequency = DonorFrequency.Occasional,
    };

    public static Patient Patient(string name = "Test Patient", Gender gender = Gender.Male, BloodType bloodType = BloodType.OPos) => new()
    {
        Name = name,
        DateOfBirth = new DateOnly(1970, 2, 2),
        Gender = gender,
        BloodType = bloodType,
        Phone = "+1-555-0102",
        Address = "2 Sample Road, Demoville",
    };

    public static Appointment Appointment(Donor donor, DateTime? slot = null) => new()
    {
        Donor = donor,
        SlotStartUtc = slot ?? new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
        Status = AppointmentStatus.Pending,
    };

    public static Request Request(Patient patient, int units = 2) => new()
    {
        Patient = patient,
        BloodType = patient.BloodType,
        Units = units,
        RequiredBy = new DateOnly(2026, 6, 20),
        Status = RequestStatus.Pending,
    };

    public static Receipt Donation(Staff staff, Appointment appointment, int units = 1) => new()
    {
        Kind = ReceiptKind.Donation,
        BloodType = appointment.Donor?.BloodType ?? BloodType.OPos,
        Units = units,
        Staff = staff,
        Appointment = appointment,
    };

    public static Receipt Issue(Staff staff, Request request, int units = 1) => new()
    {
        Kind = ReceiptKind.Issue,
        BloodType = request.BloodType,
        Units = units,
        Staff = staff,
        Request = request,
    };

    public static BloodBag Bag(Receipt donation, DateTime collectedAtUtc) => new()
    {
        BloodType = donation.BloodType,
        CollectedAtUtc = collectedAtUtc,
        ExpiresOn = StockRules.ExpiryFor(collectedAtUtc),
        Status = BagStatus.Available,
        DonationReceipt = donation,
    };
}
