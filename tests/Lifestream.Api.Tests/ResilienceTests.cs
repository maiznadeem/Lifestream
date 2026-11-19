using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Health;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

public sealed class WakeApiFixture : ApiFixture
{
    protected override void Configure(Dictionary<string, string?> settings)
    {
        settings["Database:WakeBudgetSeconds"] = "3";
        settings["Database:MaxRetryDelaySeconds"] = "1";
    }
}

public sealed class RealProbeApiFixture : ApiFixture
{
    protected override bool UseFakeProbe => false;
}

/// <summary>Health, readiness, and a database that is still resuming (API-37, 38).</summary>
public sealed class ResilienceTests(WakeApiFixture fx) : IClassFixture<WakeApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Healthz_NeverOpensConnection()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        (await admin.Client.GetApi(ApiRoutes.Me)).Dispose();
        var before = fx.Connections.Opened;
        fx.Commands.Clear();
        fx.Commands.Recording = true;
        try
        {
            using var anonymous = fx.NewClient();
            for (var i = 0; i < 5; i++)
            {
                using var response = await anonymous.GetAsync(ApiRoutes.Healthz, Ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
        finally
        {
            fx.Commands.Recording = false;
        }

        Assert.Equal(before, fx.Connections.Opened);
        Assert.Empty(fx.Commands.Commands);
    }

    [Fact]
    public async Task Healthz_StillAnswers_WhileTheDatabaseIsUnavailable()
    {
        fx.Connections.FailuresRemaining = -1;
        try
        {
            using var client = fx.NewClient();
            using var response = await client.GetAsync(ApiRoutes.Healthz, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            fx.Connections.FailuresRemaining = 0;
        }
    }

    [Theory]
    [InlineData(40613, "waking")]
    [InlineData(-2, "waking")]
    [InlineData(42119, "resting")]
    public async Task Readyz_ReportsWakingAndRestingAsDistinctStates_AsProblemDetailsWithCodeAndCorrelationId(int sqlError, string state)
    {
        using var client = fx.NewClient();
        fx.Probe.FailWith = sqlError;
        try
        {
            using var response = await client.GetAsync(ApiRoutes.Readyz, Ct);

            // RFC 7807 like every other error: stable code, correlation id (the helper checks both and the media type), no server text.
            using var problem = await response.Problem(503, state == "waking" ? ProblemCodes.DatabaseWaking : ProblemCodes.DatabaseResting);
            Assert.Equal(state, problem.RootElement.GetProperty("database").GetString());
            Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
            Assert.True(response.Headers.Contains("Retry-After"));
            Assert.True(response.Headers.Contains(ApiHeaders.CorrelationId));
            Assert.DoesNotContain("simulated", problem.RootElement.GetRawText(), StringComparison.Ordinal);

            // The readiness contract still reads from it.
            var body = JsonSerializer.Deserialize<ReadinessResponse>(problem.RootElement.GetRawText(), ApiFixture.Json)!;
            Assert.Equal(state, body.Database);
        }
        finally
        {
            fx.Probe.FailWith = null;
        }

        using var ready = await client.GetAsync(ApiRoutes.Readyz, Ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(DatabaseStates.Ready, JsonSerializer.Deserialize<ReadinessResponse>(await ready.Content.ReadAsStringAsync(Ct), ApiFixture.Json)!.Database);
    }

    [Fact]
    public async Task Request_DuringWake_IsRetried_ThenSucceeds()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        (await admin.Client.GetApi(ApiRoutes.Me)).Dispose();
        var opensBefore = fx.Connections.Opened;
        fx.Connections.FailNumber = 40613;
        fx.Connections.FailuresRemaining = 2;
        try
        {
            using var response = await admin.Client.GetApi(ApiRoutes.Donors);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct) + " opens=" + (fx.Connections.Opened - opensBefore) + " " + string.Join(" // ", fx.Logs.Lines.TakeLast(5).Select(l => l[..Math.Min(400, l.Length)])));
        }
        finally
        {
            fx.Connections.FailuresRemaining = 0;
        }

        Assert.True(fx.Connections.Opened - opensBefore >= 3, "two failed opens and a successful one");
    }

    [Theory]
    [InlineData(40613)]
    [InlineData(-2)]
    public async Task Request_DatabaseStaysWaking_After_Budget_503WithRetryAfter_DatabaseWaking_Never500(int sqlError)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        (await admin.Client.GetApi(ApiRoutes.Me)).Dispose();
        fx.Connections.FailNumber = sqlError;
        fx.Connections.FailuresRemaining = -1;
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await admin.Client.GetApi(ApiRoutes.Donors);
            watch.Stop();
            using var problem = await response.Problem(503, ProblemCodes.DatabaseWaking);
            Assert.True(int.Parse(response.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) > 0);
            var text = problem.RootElement.GetRawText();
            Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SqlClient", text, StringComparison.Ordinal);
            Assert.DoesNotContain("simulated", text, StringComparison.Ordinal);
        }
        finally
        {
            fx.Connections.FailuresRemaining = 0;
        }

        // It kept trying for the whole (test-sized) budget instead of failing at once.
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(2.5), $"gave up after {watch.Elapsed}");

        using var recovered = await admin.Client.GetApi(ApiRoutes.Donors);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Request_FreeAllowanceUsedUp_503DatabaseResting_WithRetryAfter()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        (await admin.Client.GetApi(ApiRoutes.Me)).Dispose();
        fx.Connections.FailNumber = 42119;
        fx.Connections.FailuresRemaining = -1;
        try
        {
            using var response = await admin.Client.GetApi(ApiRoutes.Donors);
            using var problem = await response.Problem(503, ProblemCodes.DatabaseResting);
            Assert.True(response.Headers.Contains("Retry-After"));
        }
        finally
        {
            fx.Connections.FailuresRemaining = 0;
            fx.Connections.FailNumber = 40613;
        }
    }

    [Fact]
    public async Task UnexpectedFailure_Is500Problem_WithNoExceptionOrSqlOrSchemaText_AndALogLineWithoutTheMessage()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        (await admin.Client.GetApi(ApiRoutes.Me)).Dispose();
        fx.Connections.ThrowGeneric = true;
        try
        {
            using var response = await admin.Client.GetApi(ApiRoutes.Donors);
            using var problem = await response.Problem(500, "server_error");
            var text = problem.RootElement.GetRawText();
            foreach (var forbidden in new[] { "secret-detail", "SELECT", "[Donor]", "FK_Secret", "prod-sql-01", "InvalidOperation", "Exception", "   at " })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }
        finally
        {
            fx.Connections.ThrowGeneric = false;
        }

        // The server-side log has the exception type and stack, not the message (which can carry data).
        Assert.DoesNotContain(fx.Logs.Lines, l => l.Contains("secret-detail", StringComparison.Ordinal));
        Assert.Contains(fx.Logs.Lines, l => l.Contains("InvalidOperationException", StringComparison.Ordinal));
    }
}

/// <summary>The real readiness probe against a real database.</summary>
public sealed class ReadinessRealDatabaseTests(RealProbeApiFixture fx) : IClassFixture<RealProbeApiFixture>
{
    [Fact]
    public async Task Readyz_RealProbe_Ready_OnARealSqlServer()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        using var client = fx.NewClient();
        using var response = await client.GetAsync(ApiRoutes.Readyz, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DatabaseStates.Ready, JsonSerializer.Deserialize<ReadinessResponse>(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), ApiFixture.Json)!.Database);
    }
}
