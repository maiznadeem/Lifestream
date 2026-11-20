using System.Net;
using Lifestream.Api.Features;
using Lifestream.Api.Tests.Data;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>
/// Direction A's list-screen additions (fix-a): a donor's donations with their bags, the Donations count and the Eligibility and Last donated filters of the donors list,
/// the Appointments and Requests saved views and their counts, the first-time flag, and the bags a fulfil would issue. All read-only.
/// </summary>
public sealed class ListScreenApiTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => fx.Clock.Today;

    private DateTime Now => fx.Clock.GetUtcNow().UtcDateTime;

    // ---- donors/{id}/donations ----

    [Fact]
    public async Task Donations_NewestFirst_WithEachBagAndWhereItWent_AndTheTotals()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        await using var db = fx.Db.NewContext();
        db.AuditEnabled = false;
        var seeder = TestRows.Staff(fx.NextName("history-staff"));
        var donor = TestRows.Donor(fx.NextName("History donor "), Gender.Female, BloodType.BNeg);
        var older = Completed(donor, Now.AddDays(-200));
        var newer = Completed(donor, Now.AddDays(-20));
        var oldReceipt = Donation(seeder, older, 1, Now.AddDays(-200));
        var newReceipt = Donation(seeder, newer, 2, Now.AddDays(-20));
        var patient = TestRows.Patient(fx.NextName("History patient "), Gender.Male, BloodType.BNeg);
        var request = TestRows.Request(patient);
        request.Status = RequestStatus.Fulfilled;
        var issue = TestRows.Issue(seeder, request);
        issue.CreatedAtUtc = Now.AddDays(-190);
        var issued = TestRows.Bag(oldReceipt, Now.AddDays(-200));
        issued.Status = BagStatus.Issued;
        issued.IssueReceipt = issue;
        var inStock = TestRows.Bag(newReceipt, Now.AddDays(-20));
        var discarded = TestRows.Bag(newReceipt, Now.AddDays(-20));
        discarded.Status = BagStatus.Discarded;
        db.AddRange(seeder, donor, older, newer, oldReceipt, newReceipt, patient, request, issue, issued, inStock, discarded);
        await db.SaveChangesAsync(Ct);

        var history = await (await staff.Client.GetApi(ApiRoutes.DonorDonations, donor.Id)).Read<DonorDonations>();

        Assert.Equal(2, history.Total);
        Assert.Equal(3, history.TotalUnits);
        Assert.Equal([newReceipt.Id, oldReceipt.Id], history.Items.Select(i => i.ReceiptId));
        Assert.Equal(newer.Id, history.Items[0].AppointmentId);
        Assert.Equal(2, history.Items[0].Units);
        Assert.Equal([BagStatus.Available, BagStatus.Discarded], history.Items[0].Bags.Select(b => b.Status));
        Assert.All(history.Items[0].Bags, b => Assert.Null(b.PatientName));
        var bag = Assert.Single(history.Items[1].Bags);
        Assert.Equal(issued.Id, bag.Id);
        Assert.Equal(BagStatus.Issued, bag.Status);
        Assert.Equal(request.Id, bag.RequestId);
        Assert.Equal(patient.Id, bag.PatientId);
        Assert.Equal(patient.Name, bag.PatientName);
        Assert.Equal(issue.CreatedAtUtc, bag.IssuedAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(StockRules.ExpiryFor(Now.AddDays(-200)), bag.ExpiresOn);
    }

    [Fact]
    public async Task Donations_AtMostTwentyListed_TotalsCountAll_InAFixedNumberOfStatements()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        await using var db = fx.Db.NewContext();
        db.AuditEnabled = false;
        var seeder = TestRows.Staff(fx.NextName("many-staff"));
        var donor = TestRows.Donor(fx.NextName("Many donor "), Gender.Male, BloodType.APos);
        db.AddRange(seeder, donor);
        for (var i = 0; i < DonorDonations.MaxItems + 3; i++)
        {
            var appointment = Completed(donor, Now.AddDays(-100 - (i * 91)));
            var receipt = Donation(seeder, appointment, 1, appointment.SlotStartUtc);
            db.AddRange(appointment, receipt, TestRows.Bag(receipt, appointment.SlotStartUtc));
        }

        await db.SaveChangesAsync(Ct);
        (await staff.Client.GetApi(ApiRoutes.Me)).Dispose();
        fx.Commands.Clear();
        fx.Commands.Recording = true;
        DonorDonations history;
        try
        {
            history = await (await staff.Client.GetApi(ApiRoutes.DonorDonations, donor.Id)).Read<DonorDonations>();
        }
        finally
        {
            fx.Commands.Recording = false;
        }

        Assert.Equal(DonorDonations.MaxItems + 3, history.Total);
        Assert.Equal(DonorDonations.MaxItems, history.Items.Count);
        Assert.All(history.Items, i => Assert.Single(i.Bags));
        Assert.True(history.Items.Zip(history.Items.Skip(1)).All(p => p.First.DonatedAtUtc >= p.Second.DonatedAtUtc), "newest first");
        var bagReads = fx.Commands.Commands.Count(c => c.Contains("FROM [BloodBag]", StringComparison.Ordinal));
        Assert.Equal(1, bagReads); // the bags of all twenty donations in one statement, not one per donation
        Assert.True(fx.Commands.Commands.Count <= 4, string.Join(" || ", fx.Commands.Commands));
    }

    [Fact]
    public async Task Donations_UnknownDonor_404_AndNoDonationsIsEmpty()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        using var missing = await staff.Client.GetApi(ApiRoutes.DonorDonations, 987_654);
        using var problem = await missing.Problem(404, ProblemCodes.NotFound);
        var donor = await Seed.DonorAsync(fx);
        var empty = await (await staff.Client.GetApi(ApiRoutes.DonorDonations, donor.Id)).Read<DonorDonations>();
        Assert.Equal(0, empty.Total);
        Assert.Equal(0, empty.TotalUnits);
        Assert.Empty(empty.Items);
    }

    // ---- donors list: Donations, eligible, lastDonated ----

    [Fact]
    public async Task DonorList_Donations_CountsCompletedAppointmentsOnly()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, fx.NextName("Counted donor "));
        await Seed.AppointmentAsync(fx, donor, Now.AddDays(-200), AppointmentStatus.Completed);
        await Seed.AppointmentAsync(fx, donor, Now.AddDays(-100), AppointmentStatus.Completed);
        await Seed.AppointmentAsync(fx, donor, Now.AddDays(-50), AppointmentStatus.Cancelled);
        await Seed.AppointmentAsync(fx, donor, Now.AddDays(5));
        var page = await (await staff.Client.GetApi(ApiRoutes.Donors, "?search=" + Uri.EscapeDataString(donor.Name))).Read<PagedResult<DonorListItem>>();
        Assert.Equal(2, Assert.Single(page.Items).Donations);
    }

    [Fact]
    public async Task DonorList_EligibleFilter_AgreesWithTheEligibilityRules()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var prefix = fx.NextName("Elig ");
        var cases = new (string Name, DateOnly Dob, DateOnly? Last, bool Hold)[]
        {
            ("a17", Today.AddYears(-18).AddDays(1), null, false),
            ("b18", Today.AddYears(-18), null, false),
            ("c60", Today.AddYears(-61).AddDays(1), null, false),
            ("d61", Today.AddYears(-61), null, false),
            ("e hold", Today.AddYears(-30), null, true),
            ("f recent", Today.AddYears(-30), Today.AddDays(-89), false),
            ("g 90 days", Today.AddYears(-30), Today.AddDays(-90), false),
            ("h leap", new DateOnly(2004, 2, 29), null, false),
        };
        foreach (var c in cases)
        {
            await Seed.DonorAsync(fx, $"{prefix} {c.Name}", dob: c.Dob, lastDonated: c.Last, medicalHold: c.Hold);
        }

        var expected = cases.Where(c => Eligibility.Evaluate(c.Dob, c.Last, Today, c.Hold).IsEligible).Select(c => $"{prefix} {c.Name}").Order().ToList();
        var notExpected = cases.Select(c => $"{prefix} {c.Name}").Except(expected).Order().ToList();
        var eligible = await (await staff.Client.GetApi(ApiRoutes.Donors, $"?search={Uri.EscapeDataString(prefix)}&eligible=true")).Read<PagedResult<DonorListItem>>();
        var ineligible = await (await staff.Client.GetApi(ApiRoutes.Donors, $"?search={Uri.EscapeDataString(prefix)}&eligible=false")).Read<PagedResult<DonorListItem>>();
        Assert.Equal(expected, eligible.Items.Select(d => d.Name).Order().ToList());
        Assert.Equal(notExpected, ineligible.Items.Select(d => d.Name).Order().ToList());
        Assert.Equal(expected.Count, eligible.Total);
    }

    [Theory]
    [InlineData(2004, 2, 29, 2022, 2, 28)]
    [InlineData(2004, 2, 29, 2022, 3, 1)]
    [InlineData(2008, 2, 29, 2026, 2, 28)]
    [InlineData(1990, 6, 15, 2026, 6, 14)]
    public void LatestBirthDateForAge_MatchesAgeOn(int by, int bm, int bd, int ty, int tm, int td)
    {
        var today = new DateOnly(ty, tm, td);
        var latest = ListScreenEndpoints.LatestBirthDateForAge(today, 18);
        Assert.True(Eligibility.AgeOn(latest, today) >= 18);
        Assert.True(Eligibility.AgeOn(latest.AddDays(1), today) < 18);
        var birth = new DateOnly(by, bm, bd);
        Assert.Equal(Eligibility.AgeOn(birth, today) >= 18, birth <= latest);
    }

    [Fact]
    public async Task DonorList_LastDonatedFilter_Windows()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var prefix = fx.NextName("Recency ");
        await Seed.DonorAsync(fx, prefix + " recent", lastDonated: Today.AddDays(-20));
        await Seed.DonorAsync(fx, prefix + " months", lastDonated: Today.AddMonths(-6));
        await Seed.DonorAsync(fx, prefix + " years", lastDonated: Today.AddMonths(-20));
        await Seed.DonorAsync(fx, prefix + " never");

        async Task<string> One(string value)
        {
            var page = await (await staff.Client.GetApi(ApiRoutes.Donors, $"?search={Uri.EscapeDataString(prefix)}&lastDonated={value}")).Read<PagedResult<DonorListItem>>();
            return Assert.Single(page.Items).Name;
        }

        Assert.Equal(prefix + " recent", await One(nameof(DonationRecency.Within3Months)));
        Assert.Equal(prefix + " months", await One(nameof(DonationRecency.Within12Months)));
        Assert.Equal(prefix + " years", await One(nameof(DonationRecency.Over12Months)));
        Assert.Equal(prefix + " never", await One(nameof(DonationRecency.Never)));
        using var bad = await staff.Client.GetApi(ApiRoutes.Donors, "?lastDonated=Soon");
        using var problem = await bad.Problem(400, ProblemCodes.Validation);
    }

    // ---- appointments: views, counts, first-time ----

    /// <summary>
    /// Puts the API clock at noon UTC of its current day for a test that reasons about a caller's local day, and puts it back when the test ends.
    /// This fixture's clock starts at the real time, so without the pin the outcome depends on the hour the test runs at: the test below failed in
    /// every run between 00:00 and 10:00 UTC (found by running the suite at 00:18 UTC). Same idiom as ServerClockAndRangeTests.PinClocks.
    /// </summary>
    private Restore PinClockAtNoonUtc()
    {
        var before = fx.Clock.GetUtcNow().UtcDateTime;
        fx.Clock.Set(before.Date.AddHours(12));
        return new Restore(() => fx.Clock.Set(before));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [Fact]
    public async Task AppointmentViews_SplitTheDay_AndTheCountsMatchTheLists()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, fx.NextName("Views donor "));
        var yesterday = await Seed.AppointmentAsync(fx, donor, Now.Date.AddDays(-1).AddHours(10), AppointmentStatus.Completed);
        var today = await Seed.AppointmentAsync(fx, donor, Now.Date.AddHours(15));
        var todayDone = await Seed.AppointmentAsync(fx, donor, Now.Date.AddHours(9), AppointmentStatus.Completed);
        await Seed.AppointmentAsync(fx, donor, Now.Date.AddHours(11), AppointmentStatus.Cancelled);
        var tomorrow = await Seed.AppointmentAsync(fx, donor, Now.Date.AddDays(1).AddHours(10));
        await Seed.AppointmentAsync(fx, donor, Now.Date.AddDays(2).AddHours(10), AppointmentStatus.Cancelled);
        var lapsed = await Seed.AppointmentAsync(fx, donor, Now.Date.AddDays(-3).AddHours(10)); // never recorded: in Past, not in Upcoming

        async Task<List<int>> View(AppointmentView view) =>
            [.. (await (await staff.Client.GetApi(ApiRoutes.Appointments, $"?donorId={donor.Id}&view={view}&utcOffsetMinutes=0&sort=slot")).Read<PagedResult<AppointmentListItem>>()).Items.Select(a => a.Id)];

        Assert.Equal([todayDone.Id, today.Id], await View(AppointmentView.Today));
        var unsorted = await (await staff.Client.GetApi(ApiRoutes.Appointments, $"?donorId={donor.Id}&view=Today&utcOffsetMinutes=0")).Read<PagedResult<AppointmentListItem>>();
        Assert.Equal([todayDone.Id, today.Id], unsorted.Items.Select(a => a.Id)); // the agenda reads in time order without a sort
        Assert.Equal([tomorrow.Id], await View(AppointmentView.Upcoming));
        Assert.Equal([lapsed.Id, yesterday.Id], await View(AppointmentView.Past));
        Assert.Equal(2, (await View(AppointmentView.Cancelled)).Count);
        var counts = await (await staff.Client.GetApi(ApiRoutes.AppointmentViews, $"?donorId={donor.Id}&utcOffsetMinutes=0")).Read<AppointmentViewCounts>();
        Assert.Equal(new AppointmentViewCounts(2, 1, 2, 2), counts);
    }

    [Fact]
    public async Task AppointmentViews_FollowTheCallersLocalDay()
    {
        using var pinned = PinClockAtNoonUtc();
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, fx.NextName("Offset donor "));
        // The API clock is at 12:00 UTC: at UTC+14 it is already 02:00 tomorrow there, so tomorrow 09:00 UTC is "today" for that caller.
        var tomorrow = await Seed.AppointmentAsync(fx, donor, Now.Date.AddDays(1).AddHours(9));
        var utc = await (await staff.Client.GetApi(ApiRoutes.AppointmentViews, $"?donorId={donor.Id}")).Read<AppointmentViewCounts>();
        var east = await (await staff.Client.GetApi(ApiRoutes.AppointmentViews, $"?donorId={donor.Id}&utcOffsetMinutes=840")).Read<AppointmentViewCounts>();
        Assert.Equal(new AppointmentViewCounts(0, 1, 0, 0), utc);
        Assert.Equal(new AppointmentViewCounts(1, 0, 0, 0), east);
        var list = await (await staff.Client.GetApi(ApiRoutes.Appointments, $"?donorId={donor.Id}&view=Today&utcOffsetMinutes=840")).Read<PagedResult<AppointmentListItem>>();
        Assert.Equal(tomorrow.Id, Assert.Single(list.Items).Id);
        using var bad = await staff.Client.GetApi(ApiRoutes.AppointmentViews, "?utcOffsetMinutes=900");
        using var p1 = await bad.Problem(400, ProblemCodes.Validation);
        using var badView = await staff.Client.GetApi(ApiRoutes.Appointments, "?view=Soon");
        using var p2 = await badView.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task AppointmentList_DonorFirstTime_UntilTheFirstCompletedDonation()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, fx.NextName("First donor "));
        var first = await Seed.AppointmentAsync(fx, donor, Now.Date.AddDays(-30).AddHours(10), AppointmentStatus.Completed);
        var second = await Seed.AppointmentAsync(fx, donor, Now.Date.AddDays(3).AddHours(10));
        var newcomer = await Seed.DonorAsync(fx, fx.NextName("New donor "));
        var only = await Seed.AppointmentAsync(fx, newcomer, Now.Date.AddDays(3).AddHours(11));
        var rows = (await (await staff.Client.GetApi(ApiRoutes.Appointments, $"?donorId={donor.Id}")).Read<PagedResult<AppointmentListItem>>()).Items.ToDictionary(a => a.Id);
        Assert.True(rows[first.Id].DonorFirstTime);
        Assert.False(rows[second.Id].DonorFirstTime);
        var newRows = (await (await staff.Client.GetApi(ApiRoutes.Appointments, $"?donorId={newcomer.Id}")).Read<PagedResult<AppointmentListItem>>()).Items;
        Assert.True(Assert.Single(newRows, a => a.Id == only.Id).DonorFirstTime);
    }

    // ---- requests: views, counts, suggested bags ----

    [Fact]
    public async Task RequestViews_OpenSortsOverdueFirst_DueSoonIsWithin48Hours_CountsMatch()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx, fx.NextName("Views patient "));
        await using var db = fx.Db.NewContext();
        Request Add(int days, RequestStatus status = RequestStatus.Pending)
        {
            var r = TestRows.Request(patient);
            r.Patient = null;
            r.PatientId = patient.Id;
            r.RequiredBy = Today.AddDays(days);
            r.Status = status;
            db.Requests.Add(r);
            return r;
        }

        var later = Add(10);
        var today = Add(0);
        var overdue = Add(-2);
        var dayAfter = Add(2);
        Add(1, RequestStatus.Fulfilled);
        Add(-1, RequestStatus.Cancelled);
        await db.SaveChangesAsync(Ct);

        async Task<List<int>> View(RequestView view) =>
            [.. (await (await staff.Client.GetApi(ApiRoutes.Requests, $"?patientId={patient.Id}&view={view}&utcOffsetMinutes=0")).Read<PagedResult<RequestListItem>>()).Items.Select(r => r.Id)];

        Assert.Equal([overdue.Id, today.Id, dayAfter.Id, later.Id], await View(RequestView.Open));
        Assert.Equal([overdue.Id, today.Id, dayAfter.Id], await View(RequestView.DueSoon));
        Assert.Single(await View(RequestView.Fulfilled));
        Assert.Single(await View(RequestView.Cancelled));
        var counts = await (await staff.Client.GetApi(ApiRoutes.RequestViews, $"?patientId={patient.Id}&utcOffsetMinutes=0")).Read<RequestViewCounts>();
        Assert.Equal(new RequestViewCounts(4, 3, 1, 1), counts);
        // A sort the caller chose still wins.
        var placed = await (await staff.Client.GetApi(ApiRoutes.Requests, $"?patientId={patient.Id}&view=Open&sort=-requiredBy")).Read<PagedResult<RequestListItem>>();
        Assert.Equal(later.Id, placed.Items[0].Id);
    }

    [Fact]
    public async Task RequestBags_AreTheOldestUnexpiredBags_ExactlyThoseTheFulfilIssues()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        await Seed.ClearBagsAsync(fx, BloodType.ABNeg);
        var bags = await Seed.BagsAsync(fx, BloodType.ABNeg, Now.AddDays(-30), Now.AddDays(-10), Now.AddDays(-20), Now.AddDays(-45));
        var patient = await Seed.PatientAsync(fx, fx.NextName("Bags patient "), BloodType.ABNeg);
        var request = await Seed.RequestAsync(fx, patient, 2);

        var preview = await (await staff.Client.GetApi(ApiRoutes.RequestBags, request.Id)).Read<FulfilPreview>();

        Assert.Equal(2, preview.Units);
        Assert.Equal(3, preview.Available); // the 45-day-old bag has expired
        Assert.Equal([bags[0].Id, bags[2].Id], preview.Bags.Select(b => b.Id));
        Assert.All(preview.Bags, b => Assert.NotNull(b.DonorName));
        using var fulfil = await staff.Client.PostApi(ApiRoutes.RequestFulfil, id: request.Id);
        Assert.Equal(HttpStatusCode.Created, fulfil.StatusCode);
        var receipt = await fulfil.Read<ReceiptDetail>();
        Assert.Equal(preview.Bags.Select(b => b.Id).Order(), receipt.BagIds.Order());
    }

    [Fact]
    public async Task RequestBags_ShortStock_ListsWhatThereIs_UnknownRequest404()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        await Seed.ClearBagsAsync(fx, BloodType.BPos);
        await Seed.BagsAsync(fx, BloodType.BPos, Now.AddDays(-5));
        var patient = await Seed.PatientAsync(fx, fx.NextName("Short patient "), BloodType.BPos);
        var request = await Seed.RequestAsync(fx, patient, 3);
        var preview = await (await staff.Client.GetApi(ApiRoutes.RequestBags, request.Id)).Read<FulfilPreview>();
        Assert.Equal(1, preview.Available);
        Assert.Single(preview.Bags);
        using var missing = await staff.Client.GetApi(ApiRoutes.RequestBags, 987_654);
        using var problem = await missing.Problem(404, ProblemCodes.NotFound);
    }

    [Fact]
    public async Task NewRoutes_NeedASignedInUser()
    {
        using var anonymous = fx.NewClient();
        foreach (var path in new[] { ApiRoutes.Path(ApiRoutes.DonorDonations, 1), ApiRoutes.Path(ApiRoutes.AppointmentViews), ApiRoutes.Path(ApiRoutes.RequestViews), ApiRoutes.Path(ApiRoutes.RequestBags, 1) })
        {
            using var response = await anonymous.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private static Appointment Completed(Donor donor, DateTime slotUtc)
    {
        var appointment = TestRows.Appointment(donor, slotUtc);
        appointment.Status = AppointmentStatus.Completed;
        return appointment;
    }

    private static Receipt Donation(Staff staff, Appointment appointment, int units, DateTime createdAtUtc)
    {
        var receipt = TestRows.Donation(staff, appointment, units);
        receipt.CreatedAtUtc = createdAtUtc;
        return receipt;
    }
}
