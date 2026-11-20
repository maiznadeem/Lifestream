using System.Diagnostics;
using System.Net;
using Lifestream.Api.Infrastructure;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>
/// Z3 of the cold-start work, server side: how often a request tries again while the database resumes. Up to 2.1.2 the wait between two
/// tries grew to 10 s, so a request sat about 5 s (measured: 4.5 s) on a database that was already back. These tests run with the product's
/// own default (the other fixtures lower it to 1 s to be quick).
/// </summary>
public sealed class WakeRetryPaceTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ColdHost WithProductDefaults(ApiFixture fx) => new(fx, s => s.Remove("Database:MaxRetryDelaySeconds"));

    [Fact]
    public void TheDefault_IsTwoSeconds()
    {
        Assert.Equal(2, new DatabaseSettings().MaxRetryDelaySeconds);
        Assert.Equal(TimeSpan.FromSeconds(2), DatabaseProblems.ReadinessRetryAfter(new DatabaseSettings()));
    }

    [Fact]
    public async Task ARequest_WhileTheDatabaseResumes_TriesAgainEveryTwoSeconds_NotSooner_NotLater()
    {
        var staff = await fx.AddStaffAsync(Role.Admin);
        await using var host = WithProductDefaults(fx);
        host.Client.DefaultRequestHeaders.Authorization = staff.Client.DefaultRequestHeaders.Authorization;
        (await host.Client.GetApi(ApiRoutes.Me)).Dispose(); // everything of a first request is done; what is timed below is the waiting

        var opened = host.Connections.Opened;
        host.Connections.FailNumber = 40613;
        host.Connections.FailuresRemaining = 3;
        var watch = Stopwatch.StartNew();
        using var response = await host.Client.GetApi(ApiRoutes.Donors);
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, host.Connections.Opened - opened); // three refused, the fourth answered

        // Three waits of 2 s. 2.1.2 waited 5, 6 and 8 s here (19 s). Under 5.5 s would mean a paused database is tried more often than every 2 s.
        Assert.InRange(watch.Elapsed.TotalSeconds, 5.5, 9);
    }

    [Fact]
    public async Task Readyz_OfAWakingDatabase_AsksTheClientBackInTwoSeconds()
    {
        await using var host = WithProductDefaults(fx);
        host.Connections.FailuresRemaining = -1; // the warm-up it starts must not reach the fixture's database while it is "waking"
        host.Probe.FailWith = 40613;

        using var response = await host.Client.GetAsync(ApiRoutes.Readyz, Ct);

        using var problem = await response.Problem(503, ProblemCodes.DatabaseWaking);
        Assert.Equal("2", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task ARequest_ThatGaveUpAfterTheWholeBudget_StillAsksForFifteenSeconds()
    {
        // Unchanged: after two minutes without a database, the answer is the slower advice.
        var staff = await fx.AddStaffAsync(Role.Admin);
        await using var host = new ColdHost(fx, s => s["Database:WakeBudgetSeconds"] = "1");
        host.Client.DefaultRequestHeaders.Authorization = staff.Client.DefaultRequestHeaders.Authorization;
        (await host.Client.GetApi(ApiRoutes.Me)).Dispose();
        host.Connections.FailuresRemaining = -1;

        using var response = await host.Client.GetApi(ApiRoutes.Donors);

        using var problem = await response.Problem(503, ProblemCodes.DatabaseWaking);
        Assert.Equal("15", response.Headers.GetValues("Retry-After").Single());
    }
}
