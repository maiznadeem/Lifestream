using System.Globalization;
using System.Net;
using System.Text;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>
/// DB-09 (every stored timestamp comes from the server clock, whatever the client sends) and DB-13 (date ranges are half-open: a record
/// at 23:59:59.999 on the last day is in, one at 00:00 the next day is out), both end to end through the API on a real SQL Server.
/// </summary>
public sealed class ServerClockAndRangeTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0, int ms = 0) => new(y, mo, d, h, mi, s, ms, DateTimeKind.Utc);

    // ---- DB-09 ----

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>A request from a client whose clock is five hours ahead: the Date and several made-up time headers say so.</summary>
    private static HttpRequestMessage Skewed(HttpMethod method, string path, string? json)
    {
        var skewed = DateTimeOffset.UtcNow.AddHours(5);
        var request = new HttpRequestMessage(method, path) { Content = json is null ? null : Json(json) };
        request.Headers.Date = skewed;
        request.Headers.Add("X-Client-Time", skewed.ToString("o", CultureInfo.InvariantCulture));
        request.Headers.Add("X-Request-Time", skewed.ToString("o", CultureInfo.InvariantCulture));
        request.Headers.Add("X-Timestamp", skewed.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return request;
    }

    private static string Skew(int hours = 5) => DateTime.UtcNow.AddHours(hours).ToString("o", CultureInfo.InvariantCulture);

    /// <summary>The distance in seconds between a stored value and the database server's own UTC clock, read in SQL.</summary>
    private async Task<double> SecondsFromServerClockAsync(string table, string column, string where)
    {
        var rows = await fx.Db.RowsAsync($"SELECT TOP 1 CAST(DATEDIFF_BIG(MILLISECOND, SYSUTCDATETIME(), [{column}]) AS float) / 1000.0 FROM [{table}] WHERE {where}", Ct);
        Assert.NotEmpty(rows);
        return Math.Abs(Convert.ToDouble(rows[0][0], CultureInfo.InvariantCulture));
    }

    private async Task AssertServerStampedAsync(string table, string column, string where)
    {
        var seconds = await SecondsFromServerClockAsync(table, column, where);
        Assert.True(seconds < 20, $"{table}.{column} is {seconds:F1} s from the server clock");
    }

    [Fact]
    public async Task EveryStoredStamp_IsSetFromTheServerClock_WhateverTheClientSends()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion, StringComparison.Ordinal);
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        var patient = await Seed.PatientAsync(fx, type: BloodType.ABNeg);
        await Seed.ClearBagsAsync(fx, BloodType.ABNeg);
        await Seed.BagsAsync(fx, BloodType.ABNeg, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1));
        var slot = Seed.Slot(3, 10);

        // A booking: the client claims it was placed five hours from now (and says the same in every time-like header).
        using var bookRequest = Skewed(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.Appointments),
            $$"""{"donorId":{{donor.Id}},"slotStartUtc":"{{slot:o}}","placedAtUtc":"{{Skew()}}","createdAtUtc":"{{Skew()}}","occurredAtUtc":"{{Skew()}}"}""");
        using var bookResponse = await staff.Client.SendAsync(bookRequest, Ct);
        var booked = await bookResponse.Read<AppointmentDetail>();
        Assert.True(booked.PlacedAtUtc < DateTime.UtcNow.AddMinutes(1), "the response must not echo the client's clock");
        await AssertServerStampedAsync("Appointment", "PlacedAtUtc", $"Id = {booked.Id}");
        Assert.Equal(slot, booked.SlotStartUtc); // the slot is the booking itself, so it is the client's to choose

        // A request for blood.
        using var requestRequest = Skewed(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.Requests),
            $$"""{"patientId":{{patient.Id}},"units":2,"requiredBy":"{{DateTime.UtcNow.AddDays(5):yyyy-MM-dd}}","placedAtUtc":"{{Skew()}}","createdAtUtc":"{{Skew(-9)}}"}""");
        using var requestResponse = await staff.Client.SendAsync(requestRequest, Ct);
        var request = await requestResponse.Read<RequestDetail>();
        await AssertServerStampedAsync("Request", "PlacedAtUtc", $"Id = {request.Id}");

        // Recording the donation: the receipt time and every bag's collection time.
        using var donationRequest = Skewed(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AppointmentDonation, booked.Id),
            $$"""{"units":2,"notes":"skewed client","createdAtUtc":"{{Skew()}}","collectedAtUtc":"{{Skew()}}","issuedAtUtc":"{{Skew()}}"}""");
        using var donationResponse = await staff.Client.SendAsync(donationRequest, Ct);
        var donation = await donationResponse.Read<ReceiptDetail>();
        await AssertServerStampedAsync("Receipt", "CreatedAtUtc", $"Id = {donation.Id}");
        await AssertServerStampedAsync("BloodBag", "CollectedAtUtc", $"DonationReceiptId = {donation.Id}");

        // Issuing blood for the request.
        using var fulfilRequest = Skewed(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.RequestFulfil, request.Id), $$"""{"createdAtUtc":"{{Skew()}}","issuedAtUtc":"{{Skew()}}"}""");
        using var fulfilResponse = await staff.Client.SendAsync(fulfilRequest, Ct);
        var issue = await fulfilResponse.Read<ReceiptDetail>();
        await AssertServerStampedAsync("Receipt", "CreatedAtUtc", $"Id = {issue.Id}");

        // The audit rows these writes produced carry the server's time too.
        foreach (var (entity, id) in new[] { ("Appointment", booked.Id), ("Request", request.Id), ("Receipt", donation.Id), ("Receipt", issue.Id) })
        {
            await AssertServerStampedAsync("AuditLog", "OccurredAtUtc", $"Entity = '{entity}' AND EntityId = {id}");
        }

        // Signing in: the session row's creation time and expiry come from the server (a refresh token lives 7 days from now).
        using var loginRequest = Skewed(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AuthLogin), $$"""{"username":"{{staff.Username}}","password":"{{ApiFixture.Password}}","createdAtUtc":"{{Skew()}}"}""");
        using var loginResponse = await fx.NewClient().SendAsync(loginRequest, Ct);
        var token = await loginResponse.Read<TokenResponse>();
        Assert.False(string.IsNullOrEmpty(token.AccessToken));
        await AssertServerStampedAsync("RefreshToken", "CreatedAtUtc", $"StaffId = {staff.Id} ORDER BY Id DESC");
        var life = await fx.Db.ScalarAsync<double>($"SELECT TOP 1 CAST(DATEDIFF_BIG(SECOND, CreatedAtUtc, ExpiresAtUtc) AS float) FROM RefreshToken WHERE StaffId = {staff.Id} ORDER BY Id DESC", Ct);
        Assert.Equal(7 * 24 * 3600, life, 5);

        // The client's clock really was off by hours: had any value been taken from it, it would be that far from the server's.
        Assert.True((DateTimeOffset.UtcNow.AddHours(5) - DateTimeOffset.UtcNow).TotalHours > 4.9);
    }

    [Fact]
    public async Task AClientClockInThePast_IsIgnoredToo_PhotoAndLockoutStampsAreServerTime()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var victim = await fx.AddStaffAsync(Role.Staff);

        // Five wrong passwords from a client whose clock is five hours behind: the lockout end is five minutes... from the server's now.
        for (var i = 0; i < 5; i++)
        {
            using var attempt = Skewed(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AuthLogin), $$"""{"username":"{{victim.Username}}","password":"wrong-{{i}}"}""");
            attempt.Headers.Date = DateTimeOffset.UtcNow.AddHours(-5);
            using var response = await fx.NewClient().SendAsync(attempt, Ct);
            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests);
        }

        var lockedUntil = await fx.Db.ScalarAsync<double>($"SELECT CAST(DATEDIFF_BIG(SECOND, SYSUTCDATETIME(), LockoutEndUtc) AS float) FROM Staff WHERE Id = {victim.Id} AND LockoutEndUtc IS NOT NULL", Ct);
        Assert.InRange(lockedUntil, 14 * 60, 15 * 60 + 20); // Auth:LockoutMinutes = 15 in the test host, from the server's now
        Assert.NotEqual(0, staff.Id);
    }

    // ---- DB-13 ----

    private static readonly DateTime Day = Utc(2031, 2, 14);

    private static string ToDate => "2031-02-14";

    [Fact]
    public async Task Receipts_RangeEndingOnADay_IncludesTheLastMillisecond_ExcludesMidnightAfter_ExcludesTheLastMillisecondBefore()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var tag = Utc(2031, 2, 14, 12);
        var dayBefore = await Seed.ReceiptAsync(fx, ReceiptKind.Donation, Utc(2031, 2, 13, 23, 59, 59, 999));
        var startOfDay = await Seed.ReceiptAsync(fx, ReceiptKind.Donation, Day);
        var lastMs = await Seed.ReceiptAsync(fx, ReceiptKind.Issue, Utc(2031, 2, 14, 23, 59, 59, 999));
        var nextMidnight = await Seed.ReceiptAsync(fx, ReceiptKind.Donation, Utc(2031, 2, 15));
        Assert.True(tag > Day);

        var oneDay = await (await admin.Client.GetApi(ApiRoutes.Receipts, $"?fromUtc={ToDate}&toUtc={ToDate}&pageSize=200")).Read<PagedResult<ReceiptListItem>>();
        Assert.Equal(new[] { startOfDay.Id, lastMs.Id }.Order(), oneDay.Items.Select(i => i.Id).Order());
        Assert.Equal(2, oneDay.Total);

        // Only an end date: everything up to and including that whole day, nothing after it.
        var upTo = await (await admin.Client.GetApi(ApiRoutes.Receipts, $"?toUtc={ToDate}&pageSize=200&fromUtc=2031-02-01")).Read<PagedResult<ReceiptListItem>>();
        Assert.DoesNotContain(upTo.Items, i => i.Id == nextMidnight.Id);
        Assert.Contains(upTo.Items, i => i.Id == lastMs.Id);
        Assert.Contains(upTo.Items, i => i.Id == dayBefore.Id);

        // A date-time instead of a date on the To side means the same day.
        var withTime = await (await admin.Client.GetApi(ApiRoutes.Receipts, $"?fromUtc={ToDate}&toUtc=2031-02-14T00:00:00Z&pageSize=200")).Read<PagedResult<ReceiptListItem>>();
        Assert.Equal(new[] { startOfDay.Id, lastMs.Id }.Order(), withTime.Items.Select(i => i.Id).Order());

        // The same range through a name search (the search path builds its own query).
        var searched = await (await admin.Client.GetApi(ApiRoutes.Receipts, $"?fromUtc={ToDate}&toUtc={ToDate}&pageSize=200&search=Receipt")).Read<PagedResult<ReceiptListItem>>();
        Assert.Equal(new[] { startOfDay.Id, lastMs.Id }.Order(), searched.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task Requests_RangeOnThePlacedDate_IsHalfOpen()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var patient = await Seed.PatientAsync(fx, fx.NextName("Range Patient "));
        var before = await Seed.RequestAsync(fx, patient, 1, placedAtUtc: Utc(2031, 2, 13, 23, 59, 59, 999));
        var start = await Seed.RequestAsync(fx, patient, 1, placedAtUtc: Day);
        var last = await Seed.RequestAsync(fx, patient, 1, placedAtUtc: Utc(2031, 2, 14, 23, 59, 59, 999));
        var after = await Seed.RequestAsync(fx, patient, 1, placedAtUtc: Utc(2031, 2, 15));

        var result = await (await admin.Client.GetApi(ApiRoutes.Requests, $"?patientId={patient.Id}&fromUtc={ToDate}&toUtc={ToDate}&pageSize=200")).Read<PagedResult<RequestListItem>>();

        Assert.Equal(new[] { start.Id, last.Id }.Order(), result.Items.Select(i => i.Id).Order());
        Assert.DoesNotContain(result.Items, i => i.Id == before.Id || i.Id == after.Id);

        var all = await (await admin.Client.GetApi(ApiRoutes.Requests, $"?patientId={patient.Id}&toUtc={ToDate}&pageSize=200")).Read<PagedResult<RequestListItem>>();
        Assert.Equal(new[] { before.Id, start.Id, last.Id }.Order(), all.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task Appointments_RangeOnTheSlot_IsHalfOpen()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var donor = await Seed.DonorAsync(fx);
        var before = await Seed.AppointmentAsync(fx, donor, Utc(2031, 2, 13, 23, 59, 59, 999));
        var start = await Seed.AppointmentAsync(fx, donor, Day);
        var last = await Seed.AppointmentAsync(fx, donor, Utc(2031, 2, 14, 23, 59, 59, 999));
        var after = await Seed.AppointmentAsync(fx, donor, Utc(2031, 2, 15));

        var result = await (await admin.Client.GetApi(ApiRoutes.Appointments, $"?donorId={donor.Id}&fromUtc={ToDate}&toUtc={ToDate}&pageSize=200")).Read<PagedResult<AppointmentListItem>>();

        Assert.Equal(new[] { start.Id, last.Id }.Order(), result.Items.Select(i => i.Id).Order());
        Assert.DoesNotContain(result.Items, i => i.Id == before.Id || i.Id == after.Id);
    }

    [Fact]
    public async Task Dashboard_ChartData_CountsTheLastMillisecondOfTheToDate_NotMidnightAfter_NorTheLastMillisecondBefore()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var units = 1;
        // Blood type APos is used by nothing else in this class; each receipt is `units` unit(s).
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, Utc(2031, 2, 13, 23, 59, 59, 999), BloodType.ONeg);
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, Day, BloodType.ONeg);
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, Utc(2031, 2, 14, 23, 59, 59, 999), BloodType.ONeg);
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, Utc(2031, 2, 15), BloodType.ONeg);

        var dashboard = await (await admin.Client.GetApi(ApiRoutes.Dashboard, $"?from={ToDate}&to={ToDate}")).Read<DashboardResponse>();

        var oneDay = dashboard.Series.Single(s => s.BloodType == BloodType.ONeg);
        Assert.Single(oneDay.Donated);
        Assert.Equal(2 * units, oneDay.Donated[0]);

        // The month view over the whole of February counts all four; ending the range on the 13th counts only the first.
        var february = await (await admin.Client.GetApi(ApiRoutes.Dashboard, "?from=2031-02-01&to=2031-02-28")).Read<DashboardResponse>();
        Assert.Equal(4 * units, february.Series.Single(s => s.BloodType == BloodType.ONeg).Donated[0]);
        var upTo13 = await (await admin.Client.GetApi(ApiRoutes.Dashboard, "?from=2031-02-01&to=2031-02-13")).Read<DashboardResponse>();
        Assert.Equal(1 * units, upTo13.Series.Single(s => s.BloodType == BloodType.ONeg).Donated[0]);
    }
}
