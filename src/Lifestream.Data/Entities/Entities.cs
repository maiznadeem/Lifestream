using Lifestream.Domain;

namespace Lifestream.Data.Entities;

/// <summary>Every table has an int identity <c>Id</c> (one id type across the schema).</summary>
public interface IEntity
{
    int Id { get; }
}

/// <summary>A person who gives blood.</summary>
public sealed class Donor : IEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateOnly DateOfBirth { get; set; }
    public Gender Gender { get; set; }
    public BloodType BloodType { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public DonorFrequency Frequency { get; set; }

    /// <summary>Set by the API when a donation is recorded (replaces the old trigger).</summary>
    public DateOnly? LastDonated { get; set; }

    /// <summary>A clinician has put this donor on hold (illness, medication, deferral). A donor with a hold is not eligible (API-11).</summary>
    public bool MedicalHold { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A person who receives blood.</summary>
public sealed class Patient : IEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateOnly DateOfBirth { get; set; }
    public Gender Gender { get; set; }
    public BloodType BloodType { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A staff member. The profile fields live here; the password hash and lockout state live in <see cref="StaffCredential"/> and the photo in <see cref="StaffPhoto"/>, so a login or password write never invalidates an open record.</summary>
public sealed class Staff : IEntity
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string Username { get; set; } = "";
    public Gender Gender { get; set; }
    public DateOnly DateOfBirth { get; set; }
    public Designation Designation { get; set; }
    public Shift Shift { get; set; }
    public Role Role { get; set; }
    public int? Salary { get; set; }
    public string? Phone { get; set; }

    /// <summary>The owner admin: survives the demo reset.</summary>
    public bool IsProtected { get; set; }

    /// <summary>The shared public demo account.</summary>
    public bool IsDemo { get; set; }

    /// <summary>True when a <see cref="StaffPhoto"/> row exists. Kept here so a Staff query never reads the photo table.</summary>
    public bool HasPhoto { get; set; }

    /// <summary>The password hash and sign-in security state. Lives in its own row so its writes never change this row's <see cref="RowVersion"/>.</summary>
    public StaffCredential? Credential { get; set; }

    /// <summary>When the account was created (server time, default <c>SYSUTCDATETIME()</c>); the profile shows it as "Member since" (FEAT-24).</summary>
    public DateTime CreatedAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Sign-in state of one staff member (1:1 with <see cref="Staff"/>), with its own row version: failed logins, lockouts, password changes
/// and resets change this row and never the <see cref="Staff"/> row. The password column holds only a salted PBKDF2 hash.
/// </summary>
public sealed class StaffCredential
{
    public int StaffId { get; set; }
    public Staff? Staff { get; set; }
    public string PasswordHash { get; set; } = "";
    public int FailedCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }

    /// <summary>When the last wrong guess was counted; a partial count idle for <see cref="UnknownLogin.Retention"/> counts as zero, exactly as for an unknown name.</summary>
    public DateTime? LastFailureUtc { get; set; }

    /// <summary>Set when an administrator resets the password: the person must choose their own before anything else is allowed.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Replaced whenever the password changes or is reset.</summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// One counter of wrong guesses for a login name that has no password account (or belongs to the demo account), so such a name locks
/// exactly like a real one. <see cref="Name"/> is stored under the same collation as <c>Staff.Username</c>, so every spelling the database
/// treats as one name shares one row. Rows expire <see cref="ExpiresAtUtc"/> and are purged when the table is queried.
/// </summary>
public sealed class UnknownLogin : IEntity
{
    public const int MaxNameLength = 200;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int FailedCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>A demo session or a demo refresh token that must stay ended, kept until the longest token it could affect has expired. Not touched by the daily demo reset.</summary>
public sealed class DemoRevocation
{
    /// <summary>"f:" + family id (a signed-out session) or "t:" + token id (a refresh token already used).</summary>
    public string Key { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>Photo thumbnail, at most 30 KB and 256 px, outside the Staff row.</summary>
public sealed class StaffPhoto
{
    public const int MaxBytes = 30 * 1024;
    public const int MaxPixels = 256;

    public int StaffId { get; set; }
    public Staff? Staff { get; set; }
    public byte[] Content { get; set; } = [];
    public string ContentType { get; set; } = "image/jpeg";
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A booked donation slot.</summary>
public sealed class Appointment : IEntity
{
    public int Id { get; set; }
    public int DonorId { get; set; }
    public Donor? Donor { get; set; }

    /// <summary>
    /// A copy of the donor's blood type, so "appointments of donors with blood type X, by date" is one index seek instead of a
    /// join. Never set it by hand: <see cref="LifestreamDbContext"/> fills it when the appointment is added and rewrites it when
    /// the donor's blood type changes.
    /// </summary>
    public BloodType DonorBloodType { get; set; }

    /// <summary>Server time (default <c>SYSUTCDATETIME()</c>) unless a generator sets history.</summary>
    public DateTime PlacedAtUtc { get; set; }

    public DateTime SlotStartUtc { get; set; }
    public AppointmentStatus Status { get; set; }
    public string? OverrideReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A hospital's request for blood for a patient.</summary>
public sealed class Request : IEntity
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient? Patient { get; set; }
    public BloodType BloodType { get; set; }
    public int Units { get; set; }
    public DateTime PlacedAtUtc { get; set; }
    public DateOnly RequiredBy { get; set; }
    public RequestStatus Status { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One table for both directions: a donation (needs an appointment) or an issue (needs a request).</summary>
public sealed class Receipt : IEntity
{
    public int Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public ReceiptKind Kind { get; set; }
    public BloodType BloodType { get; set; }
    public int Units { get; set; }
    public int StaffId { get; set; }
    public Staff? Staff { get; set; }
    public int? AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }
    public int? RequestId { get; set; }
    public Request? Request { get; set; }
    public string? Notes { get; set; }
}

/// <summary>One unit of blood. Expiry is collection date plus <see cref="StockRules.ShelfLifeDays"/>.</summary>
public sealed class BloodBag : IEntity
{
    public int Id { get; set; }
    public BloodType BloodType { get; set; }
    public DateTime CollectedAtUtc { get; set; }
    public DateOnly ExpiresOn { get; set; }
    public BagStatus Status { get; set; }
    public int DonationReceiptId { get; set; }
    public Receipt? DonationReceipt { get; set; }
    public int? IssueReceiptId { get; set; }
    public Receipt? IssueReceipt { get; set; }
}

/// <summary>Hashed refresh token. Each login (including each demo session) has its own family.</summary>
public sealed class RefreshToken : IEntity
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public Staff? Staff { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}

/// <summary>Who changed what and when. <c>OccurredAtUtc</c> is written by the server clock.</summary>
public sealed class AuditLog : IEntity
{
    public int Id { get; set; }

    /// <summary>The acting staff id; null for system work such as the demo reset. Deliberately not a foreign key so history survives deleting the user.</summary>
    public int? StaffId { get; set; }

    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public int EntityId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

/// <summary>
/// What a deleted donor or patient looked like, so Undo can put the same record back with the same id (FEAT-06). A row is written in
/// the transaction of the delete and removed by a restore; rows older than the restore window are dropped by the next delete.
/// </summary>
public sealed class DeletedRecord : IEntity
{
    public int Id { get; set; }

    /// <summary>The table the record came from: "Donor" or "Patient".</summary>
    public string Entity { get; set; } = "";

    public int EntityId { get; set; }

    /// <summary>The row as JSON (columns only, no navigations).</summary>
    public string Json { get; set; } = "";

    public DateTime DeletedAtUtc { get; set; }
}

/// <summary>Single row (Id = 1) holding the demo reset bookkeeping.</summary>
public sealed class DemoState : IEntity
{
    public int Id { get; set; } = 1;
    public DateOnly? LastResetDate { get; set; }
    public DateTime? LastForcedResetAtUtc { get; set; }
}

public static class AuditActions
{
    public const string Insert = "Insert";
    public const string Update = "Update";
    public const string Delete = "Delete";
    public static readonly string[] All = [Insert, Update, Delete];
}
