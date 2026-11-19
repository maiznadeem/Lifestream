using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Data.Seed;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>
/// The once-a-day demo reset decides "is it a new day" from ONE clock: the API's (TimeProvider), in UTC. The database's clock is not consulted
/// for it (only the 10-minute manual cooldown uses the database's own clock, for both its stamp and its check). These tests set the API clock
/// explicitly around midnight and show the reset follows it, whatever the database's real date is.
/// </summary>
public sealed class DemoDailyResetClockTests(DemoResetApiFixture fx) : IClassFixture<DemoResetApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int, int, int> Days => new()
    {
        { 2030, 6, 15 }, // an app date years away from the database's real date
        { 2031, 12, 31 }, // across a year end
        { 2026, 11, 18 }, // the real date of this run, in case the machine and the database agree
    };

    [Theory]
    [MemberData(nameof(Days))]
    public async Task DailyReset_FollowsTheApiClock_NoResetAt235959_OneResetAt000001_NoSecondReset(int year, int month, int day)
    {
        var lastSecond = new DateTime(year, month, day, 23, 59, 59, DateTimeKind.Utc);
        var today = DateOnly.FromDateTime(lastSecond);
        var tomorrow = today.AddDays(1);

        // The last reset was today (by the API clock), and the API clock says 23:59:59.
        fx.Clock.Set(lastSecond);
        await fx.Db.ExecuteAsync("DELETE FROM DemoState", Ct);
        await fx.Db.ExecuteAsync($"INSERT INTO DemoState (Id, LastResetDate) VALUES (1, '{today:yyyy-MM-dd}')", Ct);
        var owner = await fx.AddStaffAsync(Role.Admin, change: s => s.IsProtected = true);
        await Seed.DonorAsync(fx, $"Before midnight {year}");

        using (var beforeMidnight = await owner.Client.GetApi(ApiRoutes.Donors))
        {
            Assert.Equal(HttpStatusCode.OK, beforeMidnight.StatusCode);
        }

        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Donor WHERE Name = 'Before midnight {year}'", Ct));
        Assert.Equal(today, await MarkerAsync());

        // Two seconds later it is 00:00:01 tomorrow by the API clock: the first request resets, whatever day the database thinks it is.
        fx.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(tomorrow, fx.Clock.Today);
        var serverToday = DateOnly.FromDateTime(await fx.Db.ScalarAsync<DateTime>("SELECT CAST(SYSUTCDATETIME() AS datetime)", Ct));
        if (year == 2030)
        {
            Assert.NotEqual(tomorrow, serverToday); // the server clock and the app clock really are different here
        }

        using (var afterMidnight = await owner.Client.GetApi(ApiRoutes.Donors))
        {
            Assert.Equal(HttpStatusCode.OK, afterMidnight.StatusCode);
        }

        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Donor WHERE Name = 'Before midnight {year}'", Ct));
        Assert.Equal(SeedSizes.Donors, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor", Ct));
        Assert.Equal(tomorrow, await MarkerAsync());
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Staff WHERE Id = {owner.Id} AND IsProtected = 1", Ct));

        // Later the same API day: no second reset.
        await Seed.DonorAsync(fx, $"After midnight {year}");
        using (var again = await owner.Client.GetApi(ApiRoutes.Donors))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Donor WHERE Name = 'After midnight {year}'", Ct));
    }

    private async Task<DateOnly> MarkerAsync() =>
        DateOnly.FromDateTime(await fx.Db.ScalarAsync<DateTime>("SELECT CAST(LastResetDate AS datetime) FROM DemoState", Ct));
}
