using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Data.Security;
using Lifestream.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lifestream.Api.Tests.Data;

/// <summary>Rules the database itself enforces (DB-02, 03, 04, 07, 11), tested by trying to break them.</summary>
public sealed class ConstraintTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly int[] UniqueViolations = [2601, 2627];

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    private static async Task<SqlException> Rejected(Func<Task> action)
    {
        var exception = await Assert.ThrowsAnyAsync<DbUpdateException>(action);
        return Assert.IsType<SqlException>(exception.InnerException);
    }

    // ---- DB-02: only salted hashes in the password column ----

    [Fact]
    public async Task Staff_PasswordHashColumn_Length256()
    {
        var length = await fixture.ScalarAsync<int>(
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Staff' AND COLUMN_NAME = 'PasswordHash'", Ct);
        Assert.Equal(256, length);
        Assert.False(await HasColumn("Staff", "Password"), "The old plain-text 'password' column must not exist.");
    }

    [Fact]
    public async Task Staff_RealHash_IsAccepted_AndVerifies()
    {
        await using var db = fixture.NewContext();
        var staff = TestRows.Staff(Unique("hash"));
        staff.PasswordHash = PasswordHashing.Hash("correct horse battery staple");
        db.Staff.Add(staff);
        await db.SaveChangesAsync(Ct);

        Assert.True(PasswordHashing.IsValidHash(staff.PasswordHash));
        Assert.True(PasswordHashing.Verify(staff.PasswordHash, "correct horse battery staple"));
        Assert.False(PasswordHashing.Verify(staff.PasswordHash, "wrong"));
    }

    [Theory]
    [InlineData("Password123")]
    [InlineData("")]
    [InlineData("AQAAAAI")]
    [InlineData("hunter2hunter2hunter2hunter2hunter2hunter2hunter2hunter2hunter2hunter2")]
    public async Task Staff_ValueThatIsNotAHash_IsRejectedByTheDatabase(string notAHash)
    {
        await using var db = fixture.NewContext();
        var staff = TestRows.Staff(Unique("plain"));
        staff.PasswordHash = notAHash;
        db.Staff.Add(staff);

        var error = await Rejected(() => db.SaveChangesAsync(Ct));

        Assert.Equal(547, error.Number);
        Assert.Contains("CK_Staff_PasswordHash", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staff_HashWithWrongCase_IsRejected_CheckIsCaseSensitive()
    {
        await using var db = fixture.NewContext();
        var staff = TestRows.Staff(Unique("case"));
        staff.PasswordHash = TestRows.ValidHash.ToLowerInvariant();
        db.Staff.Add(staff);

        Assert.Equal(547, (await Rejected(() => db.SaveChangesAsync(Ct))).Number);
    }

    [Fact]
    public void PasswordHashing_ValidatesFormat_PlainTextAndWeakHashesAreNotValid()
    {
        Assert.True(PasswordHashing.IsValidHash(TestRows.ValidHash));
        Assert.False(PasswordHashing.IsValidHash("Password123"));
        Assert.False(PasswordHashing.IsValidHash(null));
        Assert.False(PasswordHashing.IsValidHash(""));
        Assert.False(PasswordHashing.IsValidHash(TestRows.ValidHash[..40]));
        // Same layout but 100,000 iterations (0x000186A0): below the minimum.
        var bytes = Convert.FromBase64String(TestRows.ValidHash);
        bytes[5] = 0;
        bytes[6] = 1;
        bytes[7] = 0x86;
        bytes[8] = 0xA0;
        Assert.False(PasswordHashing.IsValidHash(Convert.ToBase64String(bytes)));
    }

    // ---- DB-03 ----

    [Fact]
    public async Task Staff_UsernameUniqueCaseInsensitive()
    {
        var name = Unique("admin");
        await using (var first = fixture.NewContext())
        {
            first.Staff.Add(TestRows.Staff(name));
            await first.SaveChangesAsync(Ct);
        }

        await using var db = fixture.NewContext();
        db.Staff.Add(TestRows.Staff(name.ToUpperInvariant()));

        var error = await Rejected(() => db.SaveChangesAsync(Ct));

        Assert.Contains(error.Number, UniqueViolations);
        Assert.Contains("IX_Staff_Username", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staff_UsernameIsCaseInsensitive_EvenIfTheServerDefaultIsNot()
    {
        // The collation is pinned on the column, so uniqueness never depends on the server or database default.
        Assert.Equal(
            "Latin1_General_100_CI_AS",
            await fixture.ScalarAsync<string>("SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID('Staff') AND name = 'Username'", Ct));
    }

    // ---- DB-07: every enum and status has a CHECK ----

    [Theory]
    [InlineData("Donor.BloodType")]
    [InlineData("Donor.Gender")]
    [InlineData("Donor.Frequency")]
    [InlineData("Patient.BloodType")]
    [InlineData("Patient.Gender")]
    [InlineData("Staff.Gender")]
    [InlineData("Staff.Designation")]
    [InlineData("Staff.Shift")]
    [InlineData("Staff.Role")]
    [InlineData("Appointment.Status")]
    [InlineData("Request.BloodType")]
    [InlineData("Request.Status")]
    [InlineData("Receipt.Kind")]
    [InlineData("Receipt.BloodType")]
    [InlineData("BloodBag.BloodType")]
    [InlineData("BloodBag.Status")]
    public async Task CheckConstraint_ValueOutsideTheList_Rejected(string column)
    {
        const int unknown = 99;
        await using var db = fixture.NewContext();
        var (staff, donor, patient) = (TestRows.Staff(Unique("chk")), TestRows.Donor(), TestRows.Patient());
        var appointment = TestRows.Appointment(donor);
        var request = TestRows.Request(patient);
        db.AddRange(staff, donor, patient, appointment, request);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        Func<Task> insertBad = column switch
        {
            "Donor.BloodType" => () => Insert(db, Bad(TestRows.Donor(), d => d.BloodType = (BloodType)unknown)),
            "Donor.Gender" => () => Insert(db, Bad(TestRows.Donor(), d => d.Gender = (Gender)unknown)),
            "Donor.Frequency" => () => Insert(db, Bad(TestRows.Donor(), d => d.Frequency = (DonorFrequency)unknown)),
            "Patient.BloodType" => () => Insert(db, Bad(TestRows.Patient(), p => p.BloodType = (BloodType)unknown)),
            "Patient.Gender" => () => Insert(db, Bad(TestRows.Patient(), p => p.Gender = (Gender)unknown)),
            "Staff.Gender" => () => Insert(db, Bad(TestRows.Staff(Unique("g")), s => s.Gender = (Gender)unknown)),
            "Staff.Designation" => () => Insert(db, Bad(TestRows.Staff(Unique("d")), s => s.Designation = (Designation)unknown)),
            "Staff.Shift" => () => Insert(db, Bad(TestRows.Staff(Unique("s")), s => s.Shift = (Shift)unknown)),
            "Staff.Role" => () => Insert(db, Bad(TestRows.Staff(Unique("r")), s => s.Role = (Role)unknown)),
            "Appointment.Status" => () => Insert(db, new Appointment { DonorId = donor.Id, SlotStartUtc = DateTime.UtcNow, Status = (AppointmentStatus)unknown }),
            "Request.BloodType" => () => Insert(db, new Request { PatientId = patient.Id, BloodType = (BloodType)unknown, Units = 1, RequiredBy = new DateOnly(2026, 7, 1), Status = RequestStatus.Pending }),
            "Request.Status" => () => Insert(db, new Request { PatientId = patient.Id, BloodType = BloodType.APos, Units = 1, RequiredBy = new DateOnly(2026, 7, 1), Status = (RequestStatus)unknown }),
            "Receipt.Kind" => () => Insert(db, new Receipt { StaffId = staff.Id, AppointmentId = appointment.Id, Kind = (ReceiptKind)unknown, BloodType = BloodType.APos, Units = 1 }),
            "Receipt.BloodType" => () => Insert(db, new Receipt { StaffId = staff.Id, AppointmentId = appointment.Id, Kind = ReceiptKind.Donation, BloodType = (BloodType)unknown, Units = 1 }),
            "BloodBag.BloodType" => () => InsertBag(db, staff.Id, appointment.Id, b => b.BloodType = (BloodType)unknown),
            "BloodBag.Status" => () => InsertBag(db, staff.Id, appointment.Id, b => b.Status = (BagStatus)unknown),
            _ => throw new InvalidOperationException(column),
        };

        var error = await Rejected(insertBad);

        Assert.Equal(547, error.Number);
        Assert.Contains("CHECK constraint", error.Message, StringComparison.Ordinal);
        Assert.Contains($"CK_{column.Split('.')[0]}_", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckConstraint_EveryEnumColumnHasOne_ListingExactlyTheEnumNames()
    {
        var checks = (await fixture.RowsAsync("SELECT name, definition FROM sys.check_constraints", Ct)).ToDictionary(r => (string)r[0]!, r => (string)r[1]!);
        var expected = new Dictionary<string, string[]>
        {
            ["CK_Donor_BloodType"] = Enum.GetNames<BloodType>(),
            ["CK_Donor_Gender"] = Enum.GetNames<Gender>(),
            ["CK_Donor_Frequency"] = Enum.GetNames<DonorFrequency>(),
            ["CK_Patient_BloodType"] = Enum.GetNames<BloodType>(),
            ["CK_Patient_Gender"] = Enum.GetNames<Gender>(),
            ["CK_Staff_Gender"] = Enum.GetNames<Gender>(),
            ["CK_Staff_Designation"] = Enum.GetNames<Designation>(),
            ["CK_Staff_Shift"] = Enum.GetNames<Shift>(),
            ["CK_Staff_Role"] = Enum.GetNames<Role>(),
            ["CK_Appointment_Status"] = Enum.GetNames<AppointmentStatus>(),
            ["CK_Request_BloodType"] = Enum.GetNames<BloodType>(),
            ["CK_Request_Status"] = Enum.GetNames<RequestStatus>(),
            ["CK_Receipt_Kind"] = Enum.GetNames<ReceiptKind>(),
            ["CK_Receipt_BloodType"] = Enum.GetNames<BloodType>(),
            ["CK_BloodBag_BloodType"] = Enum.GetNames<BloodType>(),
            ["CK_BloodBag_Status"] = Enum.GetNames<BagStatus>(),
        };
        foreach (var (name, values) in expected)
        {
            Assert.True(checks.TryGetValue(name, out var definition), $"Missing {name}.");
            Assert.All(values, v => Assert.Contains($"'{v}'", definition, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Donor_GenderOther_IsAllowed()
    {
        await using var db = fixture.NewContext();
        db.Donors.Add(TestRows.Donor("Other Donor", Gender.Other));
        await db.SaveChangesAsync(Ct);
    }

    // ---- DB-04 / plan: receipt rules ----

    [Fact]
    public async Task Receipt_DonationWithoutAppointment_Rejected()
    {
        await using var db = fixture.NewContext();
        var staff = TestRows.Staff(Unique("rcpt"));
        db.Staff.Add(staff);
        await db.SaveChangesAsync(Ct);
        db.Receipts.Add(new Receipt { StaffId = staff.Id, Kind = ReceiptKind.Donation, BloodType = BloodType.APos, Units = 1 });

        var error = await Rejected(() => db.SaveChangesAsync(Ct));

        Assert.Equal(547, error.Number);
        Assert.Contains("CK_Receipt_Links", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receipt_IssueWithRequest_Accepted()
    {
        await using var db = fixture.NewContext();
        var staff = TestRows.Staff(Unique("issue"));
        var request = TestRows.Request(TestRows.Patient());
        var receipt = TestRows.Issue(staff, request);
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync(Ct);

        Assert.True(receipt.Id > 0);
        Assert.Equal(request.Id, receipt.RequestId);
    }

    [Fact]
    public async Task Receipt_IssueWithAppointment_AndDonationWithRequest_BothRejected()
    {
        await using var db = fixture.NewContext();
        var staff = TestRows.Staff(Unique("mix"));
        var appointment = TestRows.Appointment(TestRows.Donor());
        var request = TestRows.Request(TestRows.Patient());
        db.AddRange(staff, appointment, request);
        await db.SaveChangesAsync(Ct);

        db.Receipts.Add(new Receipt { StaffId = staff.Id, AppointmentId = appointment.Id, Kind = ReceiptKind.Issue, BloodType = BloodType.APos, Units = 1 });
        Assert.Equal(547, (await Rejected(() => db.SaveChangesAsync(Ct))).Number);
        db.ChangeTracker.Clear();

        db.Receipts.Add(new Receipt { StaffId = staff.Id, RequestId = request.Id, Kind = ReceiptKind.Donation, BloodType = BloodType.APos, Units = 1 });
        Assert.Equal(547, (await Rejected(() => db.SaveChangesAsync(Ct))).Number);
    }

    [Fact]
    public async Task Receipt_SecondDonationForSameAppointment_Rejected()
    {
        await using var db = fixture.NewContext();
        var staff = TestRows.Staff(Unique("twice"));
        var appointment = TestRows.Appointment(TestRows.Donor());
        db.Receipts.Add(TestRows.Donation(staff, appointment));
        await db.SaveChangesAsync(Ct);

        db.Receipts.Add(new Receipt { StaffId = staff.Id, AppointmentId = appointment.Id, Kind = ReceiptKind.Donation, BloodType = BloodType.OPos, Units = 1 });
        var error = await Rejected(() => db.SaveChangesAsync(Ct));

        Assert.Contains(error.Number, UniqueViolations);
        Assert.Contains("IX_Receipt_AppointmentId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receipt_UnknownStaff_Rejected_ForeignKeyWorks()
    {
        await using var db = fixture.NewContext();
        var appointment = TestRows.Appointment(TestRows.Donor());
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(Ct);
        db.Receipts.Add(new Receipt { StaffId = int.MaxValue, AppointmentId = appointment.Id, Kind = ReceiptKind.Donation, BloodType = BloodType.APos, Units = 1 });

        var error = await Rejected(() => db.SaveChangesAsync(Ct));

        Assert.Equal(547, error.Number);
        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.Ordinal);
    }

    // ---- DB-11: photos ----

    [Fact]
    public async Task StaffPhoto_AtLimits_Accepted_OverSizeOrPixels_Rejected()
    {
        await using var db = fixture.NewContext();
        var staff = TestRows.Staff(Unique("photo"));
        db.Staff.Add(staff);
        await db.SaveChangesAsync(Ct);

        db.StaffPhotos.Add(new StaffPhoto { StaffId = staff.Id, Content = new byte[StaffPhoto.MaxBytes], Width = StaffPhoto.MaxPixels, Height = StaffPhoto.MaxPixels });
        await db.SaveChangesAsync(Ct);

        await using var tooWide = fixture.NewContext();
        var other = TestRows.Staff(Unique("photo2"));
        tooWide.Staff.Add(other);
        await tooWide.SaveChangesAsync(Ct);
        tooWide.StaffPhotos.Add(new StaffPhoto { StaffId = other.Id, Content = new byte[100], Width = StaffPhoto.MaxPixels + 1, Height = 10 });
        var pixelError = await Rejected(() => tooWide.SaveChangesAsync(Ct));
        Assert.Contains("CK_StaffPhoto_Pixels", pixelError.Message, StringComparison.Ordinal);

        await using var tooBig = fixture.NewContext();
        tooBig.StaffPhotos.Add(new StaffPhoto { StaffId = other.Id, Content = new byte[StaffPhoto.MaxBytes + 1], Width = 10, Height = 10 });
        await Rejected(() => tooBig.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task StaffQuery_DoesNotReadPhotoTable()
    {
        var capture = new CommandCapture();
        await using var seed = fixture.NewContext();
        var staff = TestRows.Staff(Unique("nophoto"));
        seed.Staff.Add(staff);
        await seed.SaveChangesAsync(Ct);
        seed.StaffPhotos.Add(new StaffPhoto { StaffId = staff.Id, Content = new byte[1000], Width = 64, Height = 64 });
        await seed.SaveChangesAsync(Ct);

        await using var db = fixture.NewContext(null, capture);
        _ = await db.Staff.ToListAsync(Ct);
        _ = await db.Staff.AsNoTracking().Select(s => new { s.Id, s.DisplayName, s.HasPhoto }).ToListAsync(Ct);
        _ = await db.Staff.SingleAsync(s => s.Username == staff.Username, Ct);
        _ = await db.Staff.OrderBy(s => s.DisplayName).Take(50).ToListAsync(Ct);
        Assert.NotEmpty(capture.Commands);
        Assert.All(capture.Commands, c => Assert.DoesNotContain("StaffPhoto", c, StringComparison.Ordinal));

        // The listener works: reading the photo table is visible to it.
        _ = await db.StaffPhotos.Select(p => p.StaffId).ToListAsync(Ct);
        Assert.Contains(capture.Commands, c => c.Contains("StaffPhoto", StringComparison.Ordinal));
    }

    // ---- plan: Review Focus 5, non-Latin and long names ----

    [Fact]
    public async Task Donor_NameWithDiacriticsAnd60Chars_RoundTrips()
    {
        var diacritics = "Ayşe Nur Öztürk-Abdulrahman";
        var sixty = string.Concat(Enumerable.Repeat("Öztürk", 10));
        Assert.Equal(60, sixty.Length);

        await using (var db = fixture.NewContext())
        {
            db.Donors.Add(TestRows.Donor(diacritics));
            db.Donors.Add(TestRows.Donor(sixty));
            await db.SaveChangesAsync(Ct);
        }

        await using var read = fixture.NewContext();
        var names = await read.Donors.Select(d => d.Name).ToListAsync(Ct);
        Assert.Contains(diacritics, names);
        Assert.Contains(sixty, names);
        // Accent- and case-insensitive match comes from the column collation, so no function wraps the column in a query.
        Assert.Equal(1, await read.Donors.CountAsync(d => d.Name == "AYSE NUR OZTURK-ABDULRAHMAN", Ct));
    }

    // ---- OWN-10 ----

    [Fact]
    public async Task Fixture_DropsDatabase()
    {
        await using var own = new SqlServerDatabaseFixture();
        await own.InitializeAsync();
        var name = own.DatabaseName;
        Assert.NotNull(await DbId(own, name));

        await own.DisposeAsync();

        Assert.Null(await DbId(own, name));
        Assert.DoesNotContain(
            name,
            (await fixture.RowsAsync($"SELECT name FROM sys.databases WHERE name = '{name}'", Ct)).Select(r => (string)r[0]!));
    }

    private static async Task<object?> DbId(SqlServerDatabaseFixture f, string name)
    {
        await using var connection = new SqlConnection(f.MasterConnection);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand("SELECT DB_ID(@n)", connection);
        command.Parameters.AddWithValue("@n", name);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull ? null : value;
    }

    private async Task<bool> HasColumn(string table, string column) =>
        await fixture.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('{table}') AND LOWER(name) = LOWER('{column}')", Ct) > 0;

    private static T Bad<T>(T entity, Action<T> corrupt)
    {
        corrupt(entity);
        return entity;
    }

    private static async Task Insert(LifestreamDbContext db, object entity)
    {
        db.Add(entity);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task InsertBag(LifestreamDbContext db, int staffId, int appointmentId, Action<BloodBag> corrupt)
    {
        var receipt = new Receipt { StaffId = staffId, AppointmentId = appointmentId, Kind = ReceiptKind.Donation, BloodType = BloodType.APos, Units = 1 };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync(Ct);
        var bag = TestRows.Bag(receipt, new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc));
        bag.DonationReceipt = null;
        bag.DonationReceiptId = receipt.Id;
        corrupt(bag);
        await Insert(db, bag);
    }
}

/// <summary>Records the SQL text of every command a context runs.</summary>
internal sealed class CommandCapture : DbCommandInterceptor
{
    public List<string> Commands { get; } = [];

    public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<System.Data.Common.DbDataReader> result)
    {
        Commands.Add(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<System.Data.Common.DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }
}
