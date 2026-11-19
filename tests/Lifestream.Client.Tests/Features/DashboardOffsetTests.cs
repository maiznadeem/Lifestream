using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Dashboard;

namespace Lifestream.Client.Tests.Features;

/// <summary>FEAT-27 / API: the dashboard sends <c>utcOffsetMinutes</c>, so a record at 23:30 local time lands on the user's own day and month.</summary>
public sealed class DashboardOffsetTests
{
    private sealed class ZonedClock(TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 19, 20, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    [Fact]
    public async Task Dashboard_SendsTheLocalOffsetInMinutes_FakeApi()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(Samples.Dashboard()));
        var vm = new DashboardViewModel(rig.Api, new FakeNavigation());
        vm.Attach(null, new ZonedClock(TimeZoneInfo.CreateCustomTimeZone("plus530", TimeSpan.FromMinutes(330), "plus530", "plus530")));

        await vm.LoadAsync();

        var call = rig.Recorder.Calls.Single(c => c.Name == "GetDashboardAsync");
        Assert.Equal(330, call.Args[2]); // +05:30 is 330 minutes ahead of UTC
        Assert.Equal(new DateOnly(2026, 11, 20), call.Args[1]); // and "today" is the local day, which is already the 20th there
    }

    [Fact]
    public async Task TheRealClient_PutsUtcOffsetMinutesInTheQuery_AndOmitsItWhenNotGiven()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, "{}"));
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy(), new ServerStatus(), handler)) { BaseAddress = new Uri("https://api.test/") };
        using var api = new LifestreamApiClient(http, new Lifestream.Client.Session.SessionService());

        _ = await api.GetDashboardAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 11, 19), -300);
        _ = await api.GetDashboardAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 11, 19));

        Assert.Contains("utcOffsetMinutes=-300", handler.Requests[0].Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("utcOffsetMinutes", handler.Requests[1].Uri, StringComparison.Ordinal);
    }
}
