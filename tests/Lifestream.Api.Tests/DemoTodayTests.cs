using System.Globalization;
using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Stock;
using Lifestream.Data.Seed;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>
/// The demo's "today" through the real API. The daily reset runs at a pinned clock; then the three places that show today are read as the client reads them
/// (the Today view and its count with <c>utcOffsetMinutes</c>; the dashboard's panel and the sidebar count with the same view and offset beside the local day as
/// <c>fromUtc</c>/<c>toUtc</c>, see the client's LocalDay) for callers at UTC-8, UTC, UTC+5 and UTC+13, at the reset and at later hours of the same UTC day up to its last second.
/// </summary>
public sealed class DemoTodayTests(DemoResetApiFixture fx) : IClassFixture<DemoResetApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly int[] Offsets = [-480, 0, 300, 780];

    private static readonly TimeSpan Donation = TimeSpan.FromMinutes(DemoClinicDay.DonationMinutes);

    /// <summary>The UTC day and the time of day of the reset: just after midnight, before the clinic opens, in its morning, in its afternoon, after it closed, and at the day's last minute.</summary>
    public static TheoryData<int, int, int, int, int> Resets => new()
    {
        { 2031, 3, 10, 0, 1 },
        { 2031, 3, 11, 5, 30 },
        { 2031, 3, 12, 10, 50 },
        { 2031, 3, 13, 13, 20 },
        { 2031, 3, 14, 18, 5 },
        { 2031, 3, 15, 23, 58 },
    };

    [Theory]
    [MemberData(nameof(Resets))]
    public async Task AfterAReset_TheTodayView_TheDashboardPanel_AndTheSidebarCount_ShowAClinicDay_AtEveryOffsetAndHour(int year, int month, int day, int hour, int minute)
    {
        var resetAt = new DateTime(year, month, day, hour, minute, 0, 500, DateTimeKind.Utc);
        fx.Clock.Set(resetAt);
        var owner = await fx.AddStaffAsync(Role.Admin, change: s => s.IsProtected = true);
        await fx.Db.ExecuteAsync("DELETE FROM DemoState", Ct); // AddStaffAsync marks today as reset; without the mark the next request is the day's first

        // The first authenticated request of this UTC day resets the demo, with the clinic as it stands at the API clock's instant (not at 00:00).
        using (var first = await owner.Client.GetApi(ApiRoutes.Donors))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var expected = new DemoDataGenerator().Generate(resetAt);
        var around = (await ListAsync(owner.Client, $"?fromUtc={Iso(resetAt.Date.AddDays(-1))}&toUtc={Iso(resetAt.Date.AddDays(1))}&pageSize=200&sort=slot")).Select(a => (a.SlotStartUtc, a.Status)).ToList();
        Assert.Equal(3 * DemoClinicDay.Appointments, around.Count);
        Assert.Equal(
            expected.Appointments.Where(a => a.SlotStartUtc >= resetAt.Date.AddDays(-1) && a.SlotStartUtc < resetAt.Date.AddDays(2)).OrderBy(a => a.SlotStartUtc).Select(a => (a.SlotStartUtc, a.Status)),
            around);

        var problems = new List<string>();
        foreach (var now in ViewingTimes(resetAt))
        {
            fx.Clock.Set(now);
            var client = await SignInAsync(owner);
            foreach (var offset in Offsets)
            {
                var at = $"reset {resetAt:HH:mm}, seen {now:HH:mm} UTC at {offset:+0;-0} min";
                var localToday = DateOnly.FromDateTime(now.AddMinutes(offset));
                var dayStart = DateTime.SpecifyKind(localToday.ToDateTime(TimeOnly.MinValue).AddMinutes(-offset), DateTimeKind.Utc);
                var range = $"view=Today&utcOffsetMinutes={offset}&fromUtc={Iso(dayStart)}&toUtc={Iso(dayStart.AddDays(1))}";

                // The Today view, as the Appointments screen asks for it, and its count.
                var view = await ListAsync(client, $"?view=Today&utcOffsetMinutes={offset}&pageSize=50");
                var counts = await (await client.GetApi(ApiRoutes.AppointmentViews, $"?utcOffsetMinutes={offset}")).Read<AppointmentViewCounts>();
                // The dashboard's "Today's appointments" (it drops the cancelled ones itself) and the sidebar's count of those still to come.
                var panel = (await ListAsync(client, $"?{range}&pageSize=50&sort=slot")).Where(a => a.Status != AppointmentStatus.Cancelled).ToList();
                var badge = (await (await client.GetApi(ApiRoutes.Appointments, $"?status=Pending&{range}&pageSize=1")).Read<PagedResult<AppointmentListItem>>()).Total;

                // fromUtc/toUtc alone are whole UTC days (two of them for any caller), which is why the client names the view beside them.
                var twoDays = (await (await client.GetApi(ApiRoutes.Appointments, $"?fromUtc={Iso(dayStart)}&toUtc={Iso(dayStart.AddDays(1))}&pageSize=1")).Read<PagedResult<AppointmentListItem>>()).Total;
                if (twoDays < 2 * DemoClinicDay.Appointments)
                {
                    problems.Add($"{at}: fromUtc/toUtc alone returned {twoDays} appointments; this test expects the two whole UTC days");
                }

                if (view.Count < DemoClinicDay.Appointments - 1)
                {
                    problems.Add($"{at}: the Today view has {view.Count} appointments");
                }

                if (counts.Today != view.Count || !panel.Select(a => a.Id).SequenceEqual(view.Select(a => a.Id)) || badge != view.Count(a => a.Status == AppointmentStatus.Pending))
                {
                    problems.Add($"{at}: view {view.Count}, its count {counts.Today}, the dashboard panel {panel.Count}, the sidebar {badge} of {view.Count(a => a.Status == AppointmentStatus.Pending)} pending");
                }

                // In time order, inside the caller's day, and nothing finished before it could be.
                if (!view.Select(a => a.SlotStartUtc).SequenceEqual(view.Select(a => a.SlotStartUtc).Order()) || view.Any(a => a.SlotStartUtc < dayStart || a.SlotStartUtc >= dayStart.AddDays(1)))
                {
                    problems.Add($"{at}: the Today view is out of order or outside the caller's day");
                }

                problems.AddRange(view.Where(a => a.Status != AppointmentStatus.Pending && a.SlotStartUtc + Donation > now).Select(a => $"{at}: the {a.SlotStartUtc:HH:mm} appointment is {a.Status}"));
                problems.AddRange(view.Where(a => a.Status == AppointmentStatus.Pending && a.SlotStartUtc + Donation <= resetAt).Select(a => $"{at}: the {a.SlotStartUtc:HH:mm} appointment was over at the reset and is pending"));
            }
        }

        Assert.True(problems.Count == 0, $"{problems.Count} problems, first: {string.Join("; ", problems.Take(5))}");

        // Seen from UTC at the reset: the clinic's own day, with the first-time mark on someone still to come whenever anyone is.
        fx.Clock.Set(resetAt);
        var atReset = await SignInAsync(owner);
        var clinicDay = await ListAsync(atReset, "?view=Today&utcOffsetMinutes=0&pageSize=50");
        var toCome = clinicDay.Where(a => a.Status == AppointmentStatus.Pending).ToList();
        Assert.Equal(expected.Appointments.Count(a => DateOnly.FromDateTime(a.SlotStartUtc) == DateOnly.FromDateTime(resetAt) && a.Status == AppointmentStatus.Pending), toCome.Count);
        if (toCome.Count > 0)
        {
            Assert.Contains(toCome, a => a.DonorFirstTime);
        }

        // Stock is what the generator's bags say: today's donations are in it.
        var stock = await (await atReset.GetApi(ApiRoutes.Stock)).Read<List<StockItem>>();
        var today = DateOnly.FromDateTime(resetAt);
        Assert.Equal(expected.BloodBags.Count(b => b.Status == BagStatus.Available && b.ExpiresOn >= today), stock.Sum(s => s.Available));
        var donatedToday = (await (await atReset.GetApi(ApiRoutes.Receipts, $"?kind=Donation&fromUtc={Iso(resetAt.Date)}&pageSize=50")).Read<PagedResult<Lifestream.Contracts.Receipts.ReceiptListItem>>()).Total;
        Assert.Equal(clinicDay.Count(a => a.Status == AppointmentStatus.Completed), donatedToday);
    }

    [Fact]
    public async Task AManualReset_LaterInTheDay_MovesTheClinicDayOn()
    {
        // Daily reset before the clinic opens: all of today is to come. "Reset demo data" in the afternoon: the morning is done.
        var morning = new DateTime(2031, 4, 2, 6, 0, 0, 500, DateTimeKind.Utc);
        fx.Clock.Set(morning);
        var owner = await fx.AddStaffAsync(Role.Admin, change: s => s.IsProtected = true);
        await fx.Db.ExecuteAsync("DELETE FROM DemoState", Ct); // AddStaffAsync marks today as reset; without the mark the next request is the day's first
        using (var first = await owner.Client.GetApi(ApiRoutes.Donors))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var before = await ListAsync(owner.Client, "?view=Today&utcOffsetMinutes=0&pageSize=50");
        Assert.All(before, a => Assert.Equal(AppointmentStatus.Pending, a.Status));

        var afternoon = morning.Date.AddHours(14).AddMinutes(10);
        fx.Clock.Set(afternoon);
        using var anonymous = fx.NewClient();
        var token = await (await anonymous.PostApi(ApiRoutes.AuthDemo)).Read<Lifestream.Contracts.Auth.TokenResponse>();
        using var demo = fx.NewClient(token.AccessToken);
        using (var reset = await demo.PostApi(ApiRoutes.DemoReset))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        var after = await ListAsync(await SignInAsync(owner), "?view=Today&utcOffsetMinutes=0&pageSize=50");
        Assert.Equal(before.Select(a => a.SlotStartUtc), after.Select(a => a.SlotStartUtc));
        Assert.All(after, a => Assert.Equal(a.SlotStartUtc + Donation <= afternoon, a.Status != AppointmentStatus.Pending));
        Assert.Contains(after, a => a.Status == AppointmentStatus.Completed);
        Assert.Contains(after, a => a.Status == AppointmentStatus.Pending);
    }

    private static async Task<List<AppointmentListItem>> ListAsync(HttpClient client, string query) =>
        [.. (await (await client.GetApi(ApiRoutes.Appointments, query)).Read<PagedResult<AppointmentListItem>>()).Items];

    /// <summary>A fresh session for the protected owner at the clock's present time (an access token lives 15 minutes, and these tests move the clock by hours).</summary>
    private async Task<HttpClient> SignInAsync(TestUser owner)
    {
        await using var db = fx.Db.NewContext();
        return fx.NewClient(await fx.SessionAsync(db, owner.Id, $"Test {owner.Username}", owner.Role));
    }

    private static string Iso(DateTime utc) => Uri.EscapeDataString(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));

    /// <summary>The reset itself, three later moments of the same UTC day, and its last second (just before the next reset).</summary>
    private static IEnumerable<DateTime> ViewingTimes(DateTime resetAt)
    {
        yield return resetAt;
        var nextDay = resetAt.Date.AddDays(1);
        foreach (var hours in new[] { 1.5, 5, 11 })
        {
            if (resetAt.AddHours(hours) < nextDay)
            {
                yield return resetAt.AddHours(hours);
            }
        }

        yield return nextDay.AddSeconds(-1);
    }
}
