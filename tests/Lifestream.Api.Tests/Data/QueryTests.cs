using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Data.Queries;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests.Data;

/// <summary>The shared counting and range queries on a small database (DB-12, DB-13, DB-14, API-28 data side).</summary>
public sealed class QueryTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- DB-12 ----

    [Fact]
    public async Task GenderCounts_EqualIndependentCountOfEachTable_NoJoin_EachTableReadOnce()
    {
        await fixture.ClearAllAsync(Ct);
        await using (var seed = fixture.NewContext())
        {
            seed.Donors.AddRange(Enumerable.Range(0, 17).Select(i => TestRows.Donor($"Male Donor {i}")));
            seed.Donors.AddRange(Enumerable.Range(0, 5).Select(i => TestRows.Donor($"Female Donor {i}", Gender.Female)));
            seed.Donors.Add(TestRows.Donor("Other Donor", Gender.Other));
            seed.Patients.AddRange(Enumerable.Range(0, 19).Select(i => TestRows.Patient($"Male Patient {i}")));
            seed.Patients.AddRange(Enumerable.Range(0, 2).Select(i => TestRows.Patient($"Other Patient {i}", Gender.Other)));
            await seed.SaveChangesAsync(Ct);
        }

        var capture = new CommandCapture();
        await using var db = fixture.NewContext(null, capture);
        var counts = await CountQueries.GenderCountsAsync(db, Ct);

        foreach (var gender in Enum.GetValues<Gender>())
        {
            Assert.Equal(await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM Donor WHERE Gender = '{gender}'", Ct), counts.Donors[gender]);
            Assert.Equal(await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM Patient WHERE Gender = '{gender}'", Ct), counts.Patients[gender]);
        }

        Assert.Equal(17, counts.Donors[Gender.Male]);
        Assert.Equal(19, counts.Patients[Gender.Male]);
        Assert.Equal(1, counts.Donors[Gender.Other]);
        Assert.Equal(2, counts.Patients[Gender.Other]);
        Assert.Equal(0, counts.Patients[Gender.Female]);

        // One command per table, each reading only its own table, with no join.
        Assert.Equal(2, capture.Commands.Count);
        Assert.Single(capture.Commands, c => c.Contains("[Donor]", StringComparison.Ordinal));
        Assert.Single(capture.Commands, c => c.Contains("[Patient]", StringComparison.Ordinal));
        Assert.All(capture.Commands, c =>
        {
            Assert.DoesNotContain("JOIN", c, StringComparison.OrdinalIgnoreCase);
            Assert.False(c.Contains("[Donor]", StringComparison.Ordinal) && c.Contains("[Patient]", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task GenderCounts_Plan_HasNoJoinOperator()
    {
        var plans = await PlanHelper.ExecuteAndCaptureAsync(fixture, db => CountQueries.GenderCountsAsync(db, Ct), Ct);

        Assert.Equal(2, plans.Count);
        Assert.All(plans, p => Assert.False(p.HasJoin(), p.Sql));
    }

    // ---- DB-13 ----

    [Fact]
    public async Task DayRange_IsHalfOpen_RecordAt2359OnEndDateIsCounted_NextMidnightIsNot()
    {
        await fixture.ClearAllAsync(Ct);
        var first = new DateOnly(2026, 3, 10);
        var last = new DateOnly(2026, 3, 12);
        DateTime At(int day, int hour, int minute, int second = 0, int ms = 0) => new(2026, 3, day, hour, minute, second, ms, DateTimeKind.Utc);
        var inside = new[] { At(10, 0, 0), At(11, 12, 0), At(12, 23, 59, 59, 997) };
        var outside = new[] { At(9, 23, 59, 59, 997), At(13, 0, 0) };

        await using (var seed = fixture.NewContext())
        {
            var staff = TestRows.Staff("range.staff");
            var donor = TestRows.Donor();
            var patient = TestRows.Patient();
            seed.AddRange(staff, donor, patient);
            foreach (var moment in inside.Concat(outside))
            {
                var appointment = new Appointment { Donor = donor, SlotStartUtc = moment, PlacedAtUtc = moment, Status = AppointmentStatus.Pending };
                var request = new Request { Patient = patient, BloodType = BloodType.APos, Units = 1, PlacedAtUtc = moment, RequiredBy = new DateOnly(2026, 4, 1), Status = RequestStatus.Pending };
                var donation = new Receipt { Staff = staff, Appointment = appointment, Kind = ReceiptKind.Donation, BloodType = BloodType.APos, Units = 1, CreatedAtUtc = moment };
                seed.AddRange(appointment, request, donation, TestRows.Bag(donation, moment));
            }

            await seed.SaveChangesAsync(Ct);
        }

        await using var db = fixture.NewContext();
        Assert.Equal(3, await db.Receipts.InDayRange(r => r.CreatedAtUtc, first, last).CountAsync(Ct));
        Assert.Equal(3, await db.Requests.InDayRange(r => r.PlacedAtUtc, first, last).CountAsync(Ct));
        Assert.Equal(3, await db.Appointments.InDayRange(a => a.PlacedAtUtc, first, last).CountAsync(Ct));
        Assert.Equal(3, await db.Appointments.InDayRange(a => a.SlotStartUtc, first, last).CountAsync(Ct));
        Assert.Equal(3, await db.BloodBags.InDayRange(b => b.CollectedAtUtc, first, last).CountAsync(Ct));

        var sql = db.Receipts.InDayRange(r => r.CreatedAtUtc, first, last).ToQueryString();
        Assert.Contains(">= @", sql, StringComparison.Ordinal);
        Assert.Contains("< @", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("BETWEEN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<=", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void HalfOpen_EndIsTheStartOfTheNextDay()
    {
        var (start, end) = DateRanges.HalfOpen(new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31));

        Assert.Equal(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), end);
    }

    // ---- DB-14 ----

    [Fact]
    public async Task Stock_ExcludesExpiredIssuedAndDiscarded_CountsExpiringSoon()
    {
        await fixture.ClearAllAsync(Ct);
        var today = new DateOnly(2026, 6, 1);
        await using (var seed = fixture.NewContext())
        {
            var staff = TestRows.Staff("stock.staff");
            var donation = TestRows.Donation(staff, TestRows.Appointment(TestRows.Donor(), new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc)), 1);
            var issue = TestRows.Issue(staff, TestRows.Request(TestRows.Patient()));
            seed.Receipts.AddRange(donation, issue);
            BloodBag Bag(BloodType type, DateOnly expires, BagStatus status) => new()
            {
                BloodType = type,
                CollectedAtUtc = expires.AddDays(-StockRules.ShelfLifeDays).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                ExpiresOn = expires,
                Status = status,
                DonationReceipt = donation,
                IssueReceipt = status == BagStatus.Issued ? issue : null,
            };

            seed.BloodBags.AddRange(
                Bag(BloodType.APos, today.AddDays(10), BagStatus.Available),
                Bag(BloodType.APos, today.AddDays(7), BagStatus.Available),
                Bag(BloodType.APos, today.AddDays(8), BagStatus.Available),
                Bag(BloodType.APos, today, BagStatus.Available),
                Bag(BloodType.APos, today.AddDays(-1), BagStatus.Available),
                Bag(BloodType.APos, today.AddDays(20), BagStatus.Issued),
                Bag(BloodType.APos, today.AddDays(20), BagStatus.Discarded),
                Bag(BloodType.ONeg, today.AddDays(30), BagStatus.Available));
            await seed.SaveChangesAsync(Ct);
        }

        await using var db = fixture.NewContext();
        var stock = await CountQueries.StockByBloodTypeAsync(db, today, BloodRules.ExpiringSoonDays, Ct);

        Assert.Equal(8, stock.Count);
        var aPos = stock.Single(s => s.BloodType == BloodType.APos);
        Assert.Equal(4, aPos.Available);
        Assert.Equal(2, aPos.ExpiringSoon);
        Assert.Equal(new StockRow(BloodType.ONeg, 1, 0), stock.Single(s => s.BloodType == BloodType.ONeg));
        Assert.Equal(new StockRow(BloodType.BPos, 0, 0), stock.Single(s => s.BloodType == BloodType.BPos));
    }
}
