using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Lifestream.Data;
using Lifestream.Data.Security;
using Lifestream.Data.Seed;
using Lifestream.Domain;

namespace Lifestream.Api.Tests.Data;

/// <summary>The generators: deterministic, synthetic, hashed, consistent (DB-02, 06, 14, 15). No database needed.</summary>
public sealed partial class SeedTests
{
    private static readonly DateOnly Day = new(2026, 11, 19);

    [Fact]
    public void DemoData_IsDeterministicForSameDate()
    {
        var first = Canonical(new DemoDataGenerator().Generate(Day));
        var second = Canonical(new DemoDataGenerator().Generate(Day));
        var otherDay = Canonical(new DemoDataGenerator().Generate(Day.AddDays(1)));

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherDay);
    }

    [Fact]
    public void DemoData_HasTheDocumentedCounts()
    {
        var set = new DemoDataGenerator().Generate(Day);

        Assert.Equal(SeedSizes.Staff, set.Staff.Count);
        Assert.Equal(SeedSizes.Donors, set.Donors.Count);
        Assert.Equal(SeedSizes.Patients, set.Patients.Count);
        Assert.Equal(SeedSizes.Appointments, set.Appointments.Count);
        Assert.Equal(SeedSizes.Requests, set.Requests.Count);
        Assert.Equal(SeedSizes.Receipts, set.Receipts.Count);
        Assert.Equal(270, set.Receipts.Count);
    }

    [Fact]
    public void DemoData_ContainsNoRealisticPhoneNumbers()
    {
        var set = new DemoDataGenerator().Generate(Day);
        var phones = set.Staff.Select(s => s.Phone).Concat(set.Donors.Select(d => d.Phone)).Concat(set.Patients.Select(p => p.Phone)).ToList();

        Assert.NotEmpty(phones);
        Assert.All(phones, p => Assert.Matches(FictionalPhone(), p!));
    }

    [Fact]
    public void DemoData_ContainsOnlySyntheticPeopleAndAddresses()
    {
        var set = new DemoDataGenerator().Generate(Day);

        Assert.All(set.Donors.Select(d => d.Name).Concat(set.Patients.Select(p => p.Name)), n => Assert.True(SyntheticVocabulary.IsSyntheticName(n), n));
        Assert.All(set.Staff.Where(s => !s.IsDemo), s => Assert.True(SyntheticVocabulary.IsSyntheticName(s.DisplayName), s.DisplayName));
        Assert.Equal(SyntheticVocabulary.DemoStaffName, Assert.Single(set.Staff, s => s.IsDemo).DisplayName);
        Assert.All(set.Donors.Select(d => d.Address).Concat(set.Patients.Select(p => p.Address)), a => Assert.EndsWith("Demoville", a!, StringComparison.Ordinal));
    }

    [Fact]
    public void DemoData_AllStaffPasswordsAreValidHashes()
    {
        var set = new DemoDataGenerator().Generate(Day);

        Assert.All(set.Staff, s => Assert.True(PasswordHashing.IsValidHash(s.Credential!.PasswordHash), $"{s.Username} does not hold a valid salted hash."));
        // The seed hash belongs to a password that was generated at random and discarded: nothing common verifies against it.
        Assert.False(PasswordHashing.Verify(set.Staff[0].Credential!.PasswordHash, "demo"));
        Assert.False(PasswordHashing.Verify(set.Staff[0].Credential!.PasswordHash, "Password123"));
    }

    [Fact]
    public void DemoData_UsernamesAreUniqueIgnoringCase_SlotsAreUnique()
    {
        var set = ScaleDataGenerator.Generate(10, Day);

        Assert.Equal(set.Staff.Count, set.Staff.Select(s => s.Username.ToUpperInvariant()).Distinct().Count());
        Assert.Equal(set.Donors.Count, set.Donors.Select(d => d.Name).Distinct().Count());
        var slots = set.Appointments.Select(a => a.SlotStartUtc).ToList();
        Assert.Equal(slots.Count, slots.Distinct().Count());
    }

    [Fact]
    public void DemoData_IsInternallyConsistent()
    {
        var set = new DemoDataGenerator().Generate(Day);
        var todayStart = Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        Assert.All(set.Receipts, r =>
        {
            if (r.Kind == ReceiptKind.Donation)
            {
                Assert.NotNull(r.Appointment);
                Assert.Null(r.Request);
                Assert.Equal(AppointmentStatus.Completed, r.Appointment.Status);
                Assert.Equal(r.Appointment.Donor!.BloodType, r.BloodType);
            }
            else
            {
                Assert.NotNull(r.Request);
                Assert.Null(r.Appointment);
                Assert.Equal(RequestStatus.Fulfilled, r.Request.Status);
                Assert.Equal(r.Request.Units, r.Units);
            }

            Assert.True(r.CreatedAtUtc < todayStart, "History must be in the past.");
        });

        // Every donated unit is a bag; every issued unit points at the issue receipt that took it.
        Assert.Equal(set.Receipts.Where(r => r.Kind == ReceiptKind.Donation).Sum(r => r.Units), set.BloodBags.Count);
        Assert.All(set.Receipts.Where(r => r.Kind == ReceiptKind.Issue), r =>
            Assert.Equal(r.Units, set.BloodBags.Count(b => b.IssueReceipt == r && b.BloodType == r.BloodType && b.Status == BagStatus.Issued)));
        Assert.All(set.BloodBags, b => Assert.Equal(StockRules.ExpiryFor(b.CollectedAtUtc), b.ExpiresOn));
        Assert.All(set.BloodBags.Where(b => b.IssueReceipt is not null), b =>
            Assert.True(DateOnly.FromDateTime(b.IssueReceipt!.CreatedAtUtc) <= b.ExpiresOn, "A bag must not be issued after it expired."));
        Assert.All(set.Appointments.Where(a => a.Status == AppointmentStatus.Pending), a => Assert.True(a.SlotStartUtc >= todayStart.AddDays(1)));
        Assert.All(set.Requests.Where(r => r.Status == RequestStatus.Pending), r => Assert.True(r.RequiredBy >= Day));
        Assert.Contains(set.Donors, d => d.Gender == Gender.Other);
        Assert.Equal(8, set.Donors.Select(d => d.BloodType).Distinct().Count());
        Assert.Equal(8, set.Patients.Select(p => p.BloodType).Distinct().Count());
    }

    /// <summary>The first day of each of the dashboard's twelve monthly buckets for <paramref name="today"/>, oldest first (the dashboard's last 12 months, this month included).</summary>
    private static List<DateOnly> DashboardMonths(DateOnly today) => [.. Enumerable.Range(0, 12).Select(i => new DateOnly(today.Year, today.Month, 1).AddMonths(i - 11))];

    private static Dictionary<DateOnly, int> PerMonth(IEnumerable<DateTime> dates) =>
        dates.GroupBy(d => new DateOnly(d.Year, d.Month, 1)).ToDictionary(g => g.Key, g => g.Count());

    public static TheoryData<DateOnly> ManyDays => [new DateOnly(2026, 11, 19), new DateOnly(2026, 3, 1), new DateOnly(2026, 12, 31), new DateOnly(2027, 2, 28), new DateOnly(2026, 7, 4)];

    [Theory]
    [MemberData(nameof(ManyDays))]
    public void DemoData_History_FillsEveryMonthOfTheDashboardsTwelveMonths_WithMonthToMonthVariation(DateOnly today)
    {
        var set = new DemoDataGenerator().Generate(today);
        var donations = PerMonth(set.Receipts.Where(r => r.Kind == ReceiptKind.Donation).Select(r => r.CreatedAtUtc));
        var issues = PerMonth(set.Receipts.Where(r => r.Kind == ReceiptKind.Issue).Select(r => r.CreatedAtUtc));
        var months = DashboardMonths(today);

        // Every bucket the chart draws (the current month too once it has a few days in it) has donations and issues.
        var drawn = months.Where(m => m != months[^1] || today.Day >= 10).ToList();
        Assert.All(drawn, m => Assert.True(donations.GetValueOrDefault(m) > 0, $"{today:yyyy-MM-dd}: no donations in {m:yyyy-MM}"));
        Assert.All(drawn, m => Assert.True(issues.GetValueOrDefault(m) > 0, $"{today:yyyy-MM-dd}: no issues in {m:yyyy-MM}"));

        // The months differ from each other: a flat histogram would look as made up as an empty one.
        var complete = months.Take(11).Select(m => donations.GetValueOrDefault(m)).ToList();
        Assert.True(complete.Max() >= complete.Min() * 1.3, $"{today:yyyy-MM-dd}: donations per month are {string.Join(", ", complete)}");
        Assert.True(complete.Distinct().Count() >= 6, $"{today:yyyy-MM-dd}: donations per month are {string.Join(", ", complete)}");

        // The history stays in the past and reaches back a full year.
        Assert.All(set.Receipts, r => Assert.True(DateOnly.FromDateTime(r.CreatedAtUtc) < today));
        Assert.True(DateOnly.FromDateTime(set.Receipts.Min(r => r.CreatedAtUtc)) <= months[0].AddDays(3), "the oldest receipt is newer than the first month of the chart");
    }

    [Fact]
    public void DemoData_EveryDayOfTwoYears_HasDonationsAndIssuesInEveryMonthOfTheDashboard()
    {
        // 730 consecutive dates (2026 and 2027, none of them a leap year): the oldest month used to have no issues on Jan 30 and 31.
        var problems = new List<string>();
        for (var today = new DateOnly(2026, 1, 1); today < new DateOnly(2028, 1, 1); today = today.AddDays(1))
        {
            var set = new DemoDataGenerator().Generate(today);
            var donations = PerMonth(set.Receipts.Where(r => r.Kind == ReceiptKind.Donation).Select(r => r.CreatedAtUtc));
            var issues = PerMonth(set.Receipts.Where(r => r.Kind == ReceiptKind.Issue).Select(r => r.CreatedAtUtc));
            foreach (var month in DashboardMonths(today).Where(m => m != new DateOnly(today.Year, today.Month, 1)))
            {
                if (donations.GetValueOrDefault(month) == 0)
                {
                    problems.Add($"{today:yyyy-MM-dd}: no donations in {month:yyyy-MM}");
                }

                if (issues.GetValueOrDefault(month) == 0)
                {
                    problems.Add($"{today:yyyy-MM-dd}: no issues in {month:yyyy-MM}");
                }
            }
        }

        Assert.True(problems.Count == 0, $"{problems.Count} empty months, first: {string.Join("; ", problems.Take(6))}");
    }

    [Fact]
    public void DemoData_Variation_IsDeterministicPerDate_AndDiffersFromOneDateToTheNext()
    {
        var a = PerMonth(new DemoDataGenerator().Generate(Day).Receipts.Select(r => r.CreatedAtUtc));
        var again = PerMonth(new DemoDataGenerator().Generate(Day).Receipts.Select(r => r.CreatedAtUtc));
        var other = PerMonth(new DemoDataGenerator().Generate(Day.AddDays(1)).Receipts.Select(r => r.CreatedAtUtc));

        Assert.Equal(a.OrderBy(x => x.Key), again.OrderBy(x => x.Key));
        Assert.NotEqual(a.OrderBy(x => x.Key), other.OrderBy(x => x.Key));
    }

    [Fact]
    public void ScaleData_Factor100_Has27000Receipts_AndIsDeterministic()
    {
        var first = ScaleDataGenerator.Generate(100, Day);

        Assert.Equal(27_000, first.Receipts.Count);
        Assert.Equal(100 * SeedSizes.Appointments, first.Appointments.Count);
        Assert.Equal(100 * SeedSizes.Requests, first.Requests.Count);
        Assert.Equal(100 * SeedSizes.Donors, first.Donors.Count);
        Assert.Equal(100 * SeedSizes.Staff, first.Staff.Count);
        Assert.All(first.Staff, s => Assert.True(PasswordHashing.IsValidHash(s.Credential!.PasswordHash)));
        Assert.Equal(Canonical(first), Canonical(ScaleDataGenerator.Generate(100, Day)));
    }

    [Fact]
    public void Scale_Factor1_EqualsTheDemoData()
    {
        Assert.Equal(Canonical(new DemoDataGenerator().Generate(Day)), Canonical(ScaleDataGenerator.Generate(1, Day)));
    }

    // ---- DB-15 / DB-14: nothing real in the repository, windows defined once ----

    [Fact]
    public void Repository_HoldsNoDatabaseBackupOrDump()
    {
        var root = SchemaTests.RepoFile("");
        var forbidden = new[] { ".bacpac", ".bak", ".mdf", ".ldf", ".rar" };
        var found = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => forbidden.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(found);
    }

    [Fact]
    public void FreshnessAndExpiryWindows_AreDefinedOnce()
    {
        Assert.Equal(42, StockRules.ShelfLifeDays);
        Assert.Equal(BloodRules.ShelfLifeDays, StockRules.ShelfLifeDays);
        Assert.Equal(3, StockRules.FreshDays);

        var dataRoot = Path.GetDirectoryName(SchemaTests.RepoFile("src/Lifestream.Data/Lifestream.Data.csproj"))!;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dataRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dataRoot, file);
            if (relative.StartsWith("Migrations", StringComparison.Ordinal) || relative.StartsWith("obj", StringComparison.Ordinal)
                || relative.StartsWith("bin", StringComparison.Ordinal) || relative == "StockRules.cs")
            {
                continue;
            }

            var code = string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
            if (WindowLiteral().IsMatch(code))
            {
                offenders.Add(relative);
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>One line per row, using names instead of database ids, so two runs (or a database and a generator) can be compared.</summary>
    internal static string Canonical(DemoDataSet s)
    {
        var lines = new List<string>();
        lines.AddRange(s.Staff.Select(x => $"S|{x.Username}|{x.DisplayName}|{x.Gender}|{x.DateOfBirth:O}|{x.Designation}|{x.Shift}|{x.Role}|{x.Salary}|{x.Phone}|{x.IsDemo}|{x.Credential!.PasswordHash}"));
        lines.AddRange(s.Donors.Select(x => $"D|{x.Name}|{x.DateOfBirth:O}|{x.Gender}|{x.BloodType}|{x.Phone}|{x.Address}|{x.Frequency}|{x.LastDonated:O}"));
        lines.AddRange(s.Patients.Select(x => $"P|{x.Name}|{x.DateOfBirth:O}|{x.Gender}|{x.BloodType}|{x.Phone}|{x.Address}"));
        lines.AddRange(s.Appointments.Select(x => $"A|{x.Donor!.Name}|{x.SlotStartUtc:O}|{x.PlacedAtUtc:O}|{x.Status}|{x.OverrideReason}"));
        lines.AddRange(s.Requests.Select(x => $"R|{x.Patient!.Name}|{x.BloodType}|{x.Units}|{x.PlacedAtUtc:O}|{x.RequiredBy:O}|{x.Status}"));
        lines.AddRange(s.Receipts.Select(x => $"C|{x.Kind}|{x.CreatedAtUtc:O}|{x.BloodType}|{x.Units}|{x.Staff!.Username}|{x.Appointment?.SlotStartUtc:O}|{x.Appointment?.Donor?.Name}|{x.Request?.PlacedAtUtc:O}|{x.Request?.Patient?.Name}"));
        lines.AddRange(s.BloodBags.Select(x => $"B|{x.BloodType}|{x.CollectedAtUtc:O}|{x.ExpiresOn:O}|{x.Status}|{x.DonationReceipt!.CreatedAtUtc:O}|{x.IssueReceipt?.CreatedAtUtc:O}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))) + $"|{lines.Count}";
    }

    [GeneratedRegex(@"^\+1-555-01\d\d$")]
    private static partial Regex FictionalPhone();

    [GeneratedRegex(@"\b42\b|AddDays\(\s*-?3\s*\)|\b3\s*\*\s*24")]
    private static partial Regex WindowLiteral();
}
