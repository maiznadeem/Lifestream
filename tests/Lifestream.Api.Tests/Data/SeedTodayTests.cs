using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Data.Seed;
using Lifestream.Domain;

namespace Lifestream.Api.Tests.Data;

/// <summary>
/// The demo's "today". The data of a reset at instant t0 is seen while the UTC date is still t0's, by viewers whose local day is any 24-hour window around
/// "now". These tests pin what such a viewer finds: a clinic day in their own today, whatever their offset and the hour; nothing that is over before its time;
/// and donations made today that are whole (receipt, bags, donor history). No database needed.
/// </summary>
public sealed class SeedTodayTests
{
    private static readonly DateOnly Day = new(2026, 11, 19);

    private static readonly TimeSpan Donation = TimeSpan.FromMinutes(DemoClinicDay.DonationMinutes);

    /// <summary>Times of day a reset can happen at: just after midnight, before the clinic opens, during its day, after it closed, and the last minute of the day.</summary>
    private static readonly TimeSpan[] ResetTimes =
    [
        new(0, 0, 5), new(5, 30, 0), new(9, 20, 0), new(10, 45, 0), new(12, 10, 0), new(13, 0, 0), new(14, 44, 59), new(16, 50, 0), new(17, 15, 0), new(23, 59, 30),
    ];

    /// <summary>UTC offsets in minutes: the four of the brief (-8, 0, +5, +13), the ends of the range, and the half and quarter hours where local midnight can fall between two slots.</summary>
    private static readonly int[] Offsets = [-720, -570, -480, -210, 0, 300, 330, 345, 780, 840];

    private static DateTime At(DateOnly day, TimeSpan time) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + time;

    /// <summary>The caller's local day as a half-open UTC range, as the API works it out from <c>utcOffsetMinutes</c> (ListScreenEndpoints.LocalDayUtc).</summary>
    private static (DateTime Start, DateTime End) LocalDay(DateTime nowUtc, int offsetMinutes)
    {
        var localToday = DateOnly.FromDateTime(nowUtc.AddMinutes(offsetMinutes));
        var start = DateTime.SpecifyKind(localToday.ToDateTime(TimeOnly.MinValue).AddMinutes(-offsetMinutes), DateTimeKind.Utc);
        return (start, start.AddDays(1));
    }

    /// <summary>The moments a dataset reset at <paramref name="resetAt"/> is looked at: the reset itself, every full hour after it, and the last second before the next UTC day.</summary>
    private static IEnumerable<DateTime> ViewingTimes(DateTime resetAt)
    {
        yield return resetAt;
        var nextDay = resetAt.Date.AddDays(1);
        for (var t = resetAt.Date.AddHours(resetAt.Hour + 1); t < nextDay; t = t.AddHours(1))
        {
            yield return t;
        }

        yield return nextDay.AddSeconds(-1);
    }

    public static TheoryData<DateOnly> Days => [Day, new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 1), new DateOnly(2027, 3, 1), new DateOnly(2026, 7, 31)];

    [Theory]
    [MemberData(nameof(Days))]
    public void EveryViewer_AtAnyOffsetAndHour_HasAClinicDayInTheirToday(DateOnly day)
    {
        var problems = new List<string>();
        var checkedViews = 0;
        foreach (var time in ResetTimes)
        {
            var resetAt = At(day, time);
            var set = new DemoDataGenerator().Generate(resetAt);
            foreach (var now in ViewingTimes(resetAt))
            {
                foreach (var offset in Offsets)
                {
                    var (start, end) = LocalDay(now, offset);
                    Assert.True(start <= now && now < end);
                    var today = set.Appointments.Where(a => a.SlotStartUtc >= start && a.SlotStartUtc < end && a.Status != AppointmentStatus.Cancelled).ToList();
                    checkedViews++;

                    // A whole clinic day, less at most the one appointment whose hour local midnight cuts in two.
                    if (today.Count < DemoClinicDay.Appointments - 1)
                    {
                        problems.Add($"reset {resetAt:O}, seen {now:O} at {offset:+0;-0} min: {today.Count} appointments today");
                    }

                    // Nothing is done before its time, for anyone: a finished appointment started at least a donation's length before the viewer's now.
                    problems.AddRange(today.Where(a => a.Status != AppointmentStatus.Pending && a.SlotStartUtc + Donation > now)
                        .Select(a => $"reset {resetAt:O}, seen {now:O} at {offset:+0;-0} min: the {a.SlotStartUtc:O} appointment is {a.Status} before it could be"));

                    // What is still pending was not over when the data was made: it is waiting since the reset at the earliest.
                    problems.AddRange(today.Where(a => a.Status == AppointmentStatus.Pending && a.SlotStartUtc + Donation <= resetAt)
                        .Select(a => $"reset {resetAt:O}, seen {now:O} at {offset:+0;-0} min: the {a.SlotStartUtc:O} appointment was over at the reset and is pending"));
                }
            }
        }

        Assert.True(problems.Count == 0, $"{problems.Count} problems, first: {string.Join("; ", problems.Take(5))}");
        Assert.True(checkedViews > ResetTimes.Length * Offsets.Length * 2);
    }

    [Fact]
    public void EveryViewer_FindsTheRegularEightAppointments_UnlessLocalMidnightCutsAnHourInTwo()
    {
        // Whole-hour offsets (and :30 offsets whose midnight falls outside clinic hours) see exactly one appointment per clinic hour.
        foreach (var time in ResetTimes)
        {
            var resetAt = At(Day, time);
            var set = new DemoDataGenerator().Generate(resetAt);
            foreach (var offset in new[] { -480, 0, 300, 330, 780 })
            {
                var (start, end) = LocalDay(resetAt, offset);
                var hours = set.Appointments.Where(a => a.SlotStartUtc >= start && a.SlotStartUtc < end && a.Status != AppointmentStatus.Cancelled).Select(a => a.SlotStartUtc.Hour).Order().ToList();
                Assert.Equal(Enumerable.Range(9, DemoClinicDay.Appointments), hours);
            }
        }
    }

    [Fact]
    public void NothingInTheDataset_IsStampedAfterTheReset_AndEveryStatusAgreesWithTheResetInstant()
    {
        foreach (var day in Days.Select(d => d.Data))
        {
            foreach (var time in ResetTimes)
            {
                var resetAt = At(day, time);
                var set = new DemoDataGenerator().Generate(resetAt);

                Assert.All(set.Appointments, a => Assert.True(a.PlacedAtUtc <= resetAt, $"{resetAt:O}: an appointment was booked at {a.PlacedAtUtc:O}"));
                Assert.All(set.Requests, r => Assert.True(r.PlacedAtUtc <= resetAt, $"{resetAt:O}: a request was placed at {r.PlacedAtUtc:O}"));
                Assert.All(set.Receipts, r => Assert.True(r.CreatedAtUtc <= resetAt, $"{resetAt:O}: a receipt is stamped {r.CreatedAtUtc:O}"));
                Assert.All(set.BloodBags, b => Assert.True(b.CollectedAtUtc <= resetAt, $"{resetAt:O}: a bag was collected at {b.CollectedAtUtc:O}"));

                // Bookings and requests are made while people are at work, not in the night (07:00 to 19:00 UTC: the clinic's day with two hours either side).
                Assert.All(set.Appointments.Select(a => a.PlacedAtUtc).Concat(set.Requests.Select(r => r.PlacedAtUtc)), t =>
                    Assert.True(t.TimeOfDay >= TimeSpan.FromHours(7) && t.TimeOfDay < TimeSpan.FromHours(19), $"{resetAt:O}: something was booked or requested at {t:O}"));

                // Completed and no-show: the slot started a donation's length or more before the reset. Pending: it had not.
                Assert.All(set.Appointments.Where(a => a.Status is AppointmentStatus.Completed or AppointmentStatus.NoShow), a =>
                    Assert.True(a.SlotStartUtc + Donation <= resetAt, $"{resetAt:O}: the {a.SlotStartUtc:O} appointment is {a.Status}"));
                Assert.All(set.Appointments.Where(a => a.Status == AppointmentStatus.Pending), a =>
                    Assert.True(a.SlotStartUtc + Donation > resetAt, $"{resetAt:O}: the {a.SlotStartUtc:O} appointment is still pending"));
            }
        }
    }

    [Fact]
    public void DuringTheClinicDay_SeveralAreDone_SeveralAreToCome_AndAFirstTimeDonorIsAmongThem()
    {
        // 12:40 UTC, seen from UTC: the morning is done, the afternoon is to come.
        var resetAt = At(Day, new TimeSpan(12, 40, 0));
        var set = new DemoDataGenerator().Generate(resetAt);
        var (start, end) = LocalDay(resetAt, 0);
        var today = set.Appointments.Where(a => a.SlotStartUtc >= start && a.SlotStartUtc < end && a.Status != AppointmentStatus.Cancelled).OrderBy(a => a.SlotStartUtc).ToList();

        var done = today.Where(a => a.Status == AppointmentStatus.Completed).ToList();
        var toCome = today.Where(a => a.Status == AppointmentStatus.Pending).ToList();
        Assert.True(done.Count >= 2, $"{done.Count} done");
        Assert.True(toCome.Count >= 3, $"{toCome.Count} to come");
        Assert.True(done.Max(a => a.SlotStartUtc) < toCome.Min(a => a.SlotStartUtc), "the done appointments are the earlier ones");
        Assert.Contains(toCome, a => IsFirstTime(set, a));
    }

    [Fact]
    public void WheneverTodayStillHasSomeoneToCome_OneOfThemIsAFirstTimeDonor_AndTomorrowAlwaysHasOne()
    {
        foreach (var time in ResetTimes)
        {
            var resetAt = At(Day, time);
            var set = new DemoDataGenerator().Generate(resetAt);
            var todayStart = resetAt.Date;
            var today = set.Appointments.Where(a => a.SlotStartUtc >= todayStart && a.SlotStartUtc < todayStart.AddDays(1) && a.Status == AppointmentStatus.Pending).ToList();
            var tomorrow = set.Appointments.Where(a => a.SlotStartUtc >= todayStart.AddDays(1) && a.SlotStartUtc < todayStart.AddDays(2) && a.Status == AppointmentStatus.Pending).ToList();

            if (today.Count > 0)
            {
                Assert.Contains(today, a => IsFirstTime(set, a));
            }

            Assert.Equal(DemoClinicDay.Appointments, tomorrow.Count);
            Assert.Contains(tomorrow, a => IsFirstTime(set, a));
        }
    }

    /// <summary>The API's rule for the first-time mark: the donor has no completed appointment before this one.</summary>
    private static bool IsFirstTime(DemoDataSet set, Appointment appointment) =>
        !set.Appointments.Any(o => o.Donor == appointment.Donor && o.Status == AppointmentStatus.Completed && o.SlotStartUtc < appointment.SlotStartUtc);

    [Fact]
    public void ADonationOfToday_IsAWholeDonation_ReceiptBagsStockAndDonorHistory()
    {
        var sawOne = false;
        foreach (var time in ResetTimes)
        {
            var resetAt = At(Day, time);
            var today = DateOnly.FromDateTime(resetAt);
            var set = new DemoDataGenerator().Generate(resetAt);
            var completedToday = set.Appointments.Where(a => a.Status == AppointmentStatus.Completed && DateOnly.FromDateTime(a.SlotStartUtc) == today).ToList();
            foreach (var appointment in completedToday)
            {
                sawOne = true;

                // As AppointmentEndpoints.RecordDonationAsync leaves it: one donation receipt for the appointment, by a member of staff, of the donor's group...
                var receipt = Assert.Single(set.Receipts, r => r.Appointment == appointment);
                Assert.Equal(ReceiptKind.Donation, receipt.Kind);
                Assert.Equal(appointment.Donor!.BloodType, receipt.BloodType);
                Assert.Equal(appointment.Donor.BloodType, appointment.DonorBloodType);
                Assert.InRange(receipt.Units, 1, 2);
                Assert.False(receipt.Staff!.IsDemo);
                Assert.Null(receipt.Request);
                Assert.True(receipt.CreatedAtUtc > appointment.SlotStartUtc && receipt.CreatedAtUtc <= appointment.SlotStartUtc + Donation);
                Assert.True(receipt.CreatedAtUtc <= resetAt);

                // ...one bag per unit, collected at that moment, in stock with the full shelf life...
                var bags = set.BloodBags.Where(b => b.DonationReceipt == receipt).ToList();
                Assert.Equal(receipt.Units, bags.Count);
                Assert.All(bags, b =>
                {
                    Assert.Equal(receipt.BloodType, b.BloodType);
                    Assert.Equal(receipt.CreatedAtUtc, b.CollectedAtUtc);
                    Assert.Equal(StockRules.ExpiryFor(b.CollectedAtUtc), b.ExpiresOn);
                    Assert.Equal(today.AddDays(StockRules.ShelfLifeDays), b.ExpiresOn);
                    Assert.Equal(BagStatus.Available, b.Status);
                    Assert.Null(b.IssueReceipt);
                });

                // ...and the donor's last donation is today, so they are not eligible again for 90 days.
                Assert.Equal(today, appointment.Donor.LastDonated);
                Assert.False(Eligibility.Evaluate(appointment.Donor.DateOfBirth, appointment.Donor.LastDonated, today.AddDays(1)).IsEligible);
            }

            // Stock, as the stock screen and the dashboard count it (available and not expired), holds today's bags: nothing of today was issued or written off.
            var inStock = set.BloodBags.Count(b => b.Status == BagStatus.Available && b.ExpiresOn >= today);
            var ofToday = set.BloodBags.Count(b => DateOnly.FromDateTime(b.CollectedAtUtc) == today);
            Assert.Equal(completedToday.Sum(a => set.Receipts.Single(r => r.Appointment == a).Units), ofToday);
            Assert.True(inStock >= ofToday);

            // Every donor's history agrees with their receipts, whenever the reset was.
            Assert.All(set.Donors, d =>
            {
                var donations = set.Receipts.Where(r => r.Kind == ReceiptKind.Donation && r.Appointment!.Donor == d).ToList();
                Assert.Equal(donations.Count == 0 ? null : DateOnly.FromDateTime(donations.Max(r => r.CreatedAtUtc)), d.LastDonated);
                Assert.Equal(donations.Count >= 3 ? DonorFrequency.Regular : DonorFrequency.Occasional, d.Frequency);
            });
        }

        Assert.True(sawOne, "no reset time of this test had a donation today");
    }

    [Fact]
    public void TodaysAndTomorrowsDonors_AreEligibleOnTheirDay_AndComeOnce()
    {
        foreach (var day in Days.Select(d => d.Data))
        {
            foreach (var time in ResetTimes)
            {
                var resetAt = At(day, time);
                var todayStart = resetAt.Date;
                var set = new DemoDataGenerator().Generate(resetAt);
                var around = set.Appointments.Where(a => a.SlotStartUtc >= todayStart.AddDays(-1) && a.SlotStartUtc < todayStart.AddDays(2)).ToList();

                // Nobody is booked twice in the three days, and none of those days has a cancelled row standing in for an appointment.
                Assert.Equal(around.Count, around.Select(a => a.Donor).Distinct().Count());
                Assert.DoesNotContain(around, a => a.Status == AppointmentStatus.Cancelled);

                foreach (var appointment in around.Where(a => a.SlotStartUtc >= todayStart))
                {
                    // Judged as the API judges a booking and a donation: on the appointment's day, from the donor's donations before it.
                    var on = DateOnly.FromDateTime(appointment.SlotStartUtc);
                    var before = set.Receipts.Where(r => r.Kind == ReceiptKind.Donation && r.Appointment!.Donor == appointment.Donor && r.Appointment != appointment).Select(r => (DateOnly?)DateOnly.FromDateTime(r.CreatedAtUtc)).Max();
                    var eligibility = Eligibility.Evaluate(appointment.Donor!.DateOfBirth, before, on);
                    Assert.True(eligibility.IsEligible, $"{resetAt:O}: {appointment.SlotStartUtc:O}: {string.Join(' ', eligibility.Reasons)}");
                    Assert.Null(appointment.OverrideReason);
                }
            }
        }
    }

    [Fact]
    public void TheCounts_AreTheSame_WhateverTheTimeOfTheReset()
    {
        foreach (var factor in new[] { 1, 3 })
        {
            foreach (var time in ResetTimes)
            {
                var set = ScaleDataGenerator.Generate(factor, At(Day, time));

                Assert.Equal(factor * SeedSizes.Appointments, set.Appointments.Count);
                Assert.Equal(factor * SeedSizes.CompletedAppointments, set.Appointments.Count(a => a.Status == AppointmentStatus.Completed));
                Assert.Equal(factor * SeedSizes.CancelledAppointments, set.Appointments.Count(a => a.Status == AppointmentStatus.Cancelled));
                Assert.Equal(factor * SeedSizes.NoShowAppointments, set.Appointments.Count(a => a.Status == AppointmentStatus.NoShow));
                Assert.Equal(factor * SeedSizes.PendingAppointments, set.Appointments.Count(a => a.Status == AppointmentStatus.Pending));
                Assert.Equal(factor * SeedSizes.Receipts, set.Receipts.Count);
                Assert.Equal(factor * SeedSizes.Requests, set.Requests.Count);
                Assert.Equal(factor * SeedSizes.PendingRequests, set.Requests.Count(r => r.Status == RequestStatus.Pending));
                Assert.Equal(factor * SeedSizes.Donors, set.Donors.Count);
                Assert.Equal(set.Appointments.Count, set.Appointments.Select(a => a.SlotStartUtc).Distinct().Count());
            }
        }
    }

    [Fact]
    public void TheDataset_IsDeterministicForAResetTime_AndADateMeansMidnight()
    {
        var morning = At(Day, new TimeSpan(10, 45, 0));
        var afternoon = At(Day, new TimeSpan(14, 50, 0));

        Assert.Equal(SeedTests.Canonical(new DemoDataGenerator().Generate(morning)), SeedTests.Canonical(new DemoDataGenerator().Generate(morning)));
        Assert.NotEqual(SeedTests.Canonical(new DemoDataGenerator().Generate(morning)), SeedTests.Canonical(new DemoDataGenerator().Generate(afternoon)));
        Assert.Equal(SeedTests.Canonical(new DemoDataGenerator().Generate(Day)), SeedTests.Canonical(new DemoDataGenerator().Generate(At(Day, TimeSpan.Zero))));
        Assert.Equal(SeedTests.Canonical(ScaleDataGenerator.Generate(1, morning)), SeedTests.Canonical(new DemoDataGenerator().Generate(morning)));

        // The time of day moves the clinic's day on; the people are the day's, whenever the reset happens.
        var a = new DemoDataGenerator().Generate(morning);
        var b = new DemoDataGenerator().Generate(afternoon);
        Assert.Equal(a.Donors.Select(d => $"{d.Name}|{d.BloodType}|{d.DateOfBirth:O}"), b.Donors.Select(d => $"{d.Name}|{d.BloodType}|{d.DateOfBirth:O}"));
        Assert.Equal(a.Patients.Select(p => p.Name), b.Patients.Select(p => p.Name));
        Assert.Equal(a.Staff.Select(s => s.Username), b.Staff.Select(s => s.Username));
        Assert.Equal(SlotsAround(a, morning), SlotsAround(b, afternoon));
    }

    private static List<string> SlotsAround(DemoDataSet set, DateTime resetAt) =>
        [.. set.Appointments.Where(a => a.SlotStartUtc >= resetAt.Date.AddDays(-1) && a.SlotStartUtc < resetAt.Date.AddDays(2)).OrderBy(a => a.SlotStartUtc).Select(a => $"{a.SlotStartUtc:O}|{a.Donor!.Name}")];

    [Fact]
    public void AtTheEndOfTheDay_EveryMonthOfTheDashboardStillHasDonationsAndIssues_AndNoStockIsNegative()
    {
        // The latest reset of a day moves the most donations from the history into today: 90 consecutive days at 23:59.
        var problems = new List<string>();
        for (var day = new DateOnly(2026, 10, 1); day < new DateOnly(2026, 12, 30); day = day.AddDays(1))
        {
            var set = new DemoDataGenerator().Generate(At(day, new TimeSpan(23, 59, 0)));
            var months = Enumerable.Range(0, 12).Select(i => new DateOnly(day.Year, day.Month, 1).AddMonths(i - 11)).Where(m => m != new DateOnly(day.Year, day.Month, 1)).ToList();
            foreach (var kind in new[] { ReceiptKind.Donation, ReceiptKind.Issue })
            {
                var perMonth = set.Receipts.Where(r => r.Kind == kind).GroupBy(r => new DateOnly(r.CreatedAtUtc.Year, r.CreatedAtUtc.Month, 1)).ToDictionary(g => g.Key, g => g.Count());
                problems.AddRange(months.Where(m => perMonth.GetValueOrDefault(m) == 0).Select(m => $"{day:yyyy-MM-dd}: no {kind} in {m:yyyy-MM}"));
            }

            // Every issued unit is a bag that was collected before it was issued and had not expired: stock never went below zero.
            problems.AddRange(set.BloodBags.Where(b => b.IssueReceipt is { } issue && (issue.CreatedAtUtc < b.CollectedAtUtc || DateOnly.FromDateTime(issue.CreatedAtUtc) > b.ExpiresOn))
                .Select(b => $"{day:yyyy-MM-dd}: a bag collected {b.CollectedAtUtc:O} was issued {b.IssueReceipt!.CreatedAtUtc:O}"));
            if (set.Receipts.Count != SeedSizes.Receipts)
            {
                problems.Add($"{day:yyyy-MM-dd}: {set.Receipts.Count} receipts");
            }
        }

        Assert.True(problems.Count == 0, $"{problems.Count} problems, first: {string.Join("; ", problems.Take(6))}");
    }
}
