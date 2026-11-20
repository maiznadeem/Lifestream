using System.Diagnostics;
using System.Globalization;
using Lifestream.Api.Infrastructure;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Domain;
using Microsoft.Data.SqlClient;

namespace Lifestream.Api.Tests;

/// <summary>
/// The free database has a monthly allowance (100,000 vCore-seconds, about 100 wakes). When it is used up Azure keeps the database paused
/// until the 1st of the next month and refuses every login with error 42119, severity 16 (Microsoft Learn, "Database Engine events and
/// errors (41400 to 49999)"). That is not a wake: waiting does not end it. The API must say so at once and plainly, and must not knock on the
/// database again. The error is simulated here with its real number, severity and text; the product's own retry settings are used.
/// </summary>
public sealed class FreeAllowanceUsedUpTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    /// <summary>The message as Microsoft Learn documents it, with the two placeholders filled in.</summary>
    private const string AzureMessage =
        "This database has reached the monthly free amount allowance for the month of November 2026 and is paused for the remainder of the month. "
        + "The free amount will renew at 12:00 AM (UTC) on December 01, 2026. To regain access immediately, open the Compute and Storage tab from the database menu on the Azure Portal "
        + "and select the \"Continue using database with additional charges\" option. This will resume the database and bill you for additional usage charges the rest of this month. "
        + "For more details, see https://go.microsoft.com/fwlink/?linkid=2243105&clcid=0x409.";

    private const string PlainTitle = "The demo database has used its free monthly allowance and is back on the 1st.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SqlException Azure42119() => SqlExceptions.Create([DatabaseErrors.FreeAllowanceExhausted], AzureMessage, severity: 16);

    /// <summary>A new process with the product's own wake budget (120 s) and retry pace (2 s): a retried error would be seen as a long wait.</summary>
    private static ColdHost Product(ApiFixture fx) => new(fx, s =>
    {
        s.Remove("Database:MaxRetryDelaySeconds");
        s.Remove("Database:WakeBudgetSeconds");
        s["Logging:LogLevel:Default"] = "Warning";
    });

    private static async Task AssertPlainRestingAsync(HttpResponseMessage response)
    {
        using var problem = await response.Problem(503, ProblemCodes.DatabaseResting);
        Assert.Equal(PlainTitle, problem.RootElement.GetProperty("title").GetString());

        // Retry-After is the time to the 1st of next month, 00:00 UTC: when Azure renews the allowance.
        var now = DateTime.UtcNow;
        var first = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        var retryAfter = int.Parse(response.Headers.GetValues("Retry-After").Single(), CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, (first - now).TotalSeconds - 30, (first - now).TotalSeconds + 30);

        // Azure's own text (how to switch on billing, a link) is not passed on, and nothing calls this "waking".
        var text = problem.RootElement.GetRawText();
        foreach (var forbidden in new[] { "Azure Portal", "additional charges", "fwlink", "waking", "SqlException" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ARequest_IsAnsweredAtOnce_Plainly_AndTheDatabaseIsTriedOnce()
    {
        var staff = await fx.AddStaffAsync(Role.Admin);
        await using var host = Product(fx);
        host.Client.DefaultRequestHeaders.Authorization = staff.Client.DefaultRequestHeaders.Authorization;
        (await host.Client.GetApi(ApiRoutes.Me)).Dispose();

        var opened = host.Connections.Opened;
        host.Connections.FailWith = Azure42119;
        host.Connections.FailuresRemaining = -1;
        var watch = Stopwatch.StartNew();
        using var response = await host.Client.GetApi(ApiRoutes.Donors);
        watch.Stop();

        await AssertPlainRestingAsync(response);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"answered after {watch.Elapsed}: the error was waited on");
        Assert.Equal(1, host.Connections.Opened - opened);
        Assert.Contains(host.Logs.Lines, l => l.Contains("free allowance is used up", StringComparison.Ordinal) && l.Contains("42119", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Lines, l => l.Contains("Azure Portal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASignIn_IsAnsweredAtOnce_Plainly()
    {
        await fx.AddDemoAsync();
        await using var host = Product(fx);
        host.Connections.FailWith = Azure42119;
        host.Connections.FailuresRemaining = -1;

        var watch = Stopwatch.StartNew();
        using var response = await host.Client.PostAsync(ApiRoutes.Path(ApiRoutes.AuthDemo), null, Ct);
        watch.Stop();

        await AssertPlainRestingAsync(response);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"answered after {watch.Elapsed}");
        Assert.Equal(1, host.Connections.Opened);
    }

    [Fact]
    public async Task Readyz_SaysResting_Plainly_AndStartsNothing()
    {
        await using var host = Product(fx);
        host.Connections.FailWith = Azure42119;
        host.Connections.FailuresRemaining = -1;
        host.Probe.FailWith = DatabaseErrors.FreeAllowanceExhausted;

        using var response = await host.Client.GetAsync(ApiRoutes.Readyz, Ct);

        await AssertPlainRestingAsync(response);
        using var problem = await response.Problem(503, ProblemCodes.DatabaseResting);
        Assert.Equal("resting", problem.RootElement.GetProperty("database").GetString());
        Assert.Equal(0, host.WarmUp.Runs);
        Assert.Equal(0, host.Connections.Opened);
    }

    [Fact]
    public async Task TheError_IsRecognised_WhereverItSits_AndIsNeverTriedAgain()
    {
        // Among other errors of one failure, after an error that on its own would mean "waking", and inside another exception.
        var mixed = SqlExceptions.Create([40613, DatabaseErrors.FreeAllowanceExhausted]);
        Assert.Equal(40613, mixed.Number);
        Assert.True(DatabaseErrors.IsResting(mixed));
        Assert.False(DatabaseErrors.IsWaking(mixed));
        Assert.True(DatabaseErrors.IsResting(new InvalidOperationException("wrapped", Azure42119())));
        Assert.False(DatabaseErrors.IsResting(SqlExceptions.Create(40613)));
        Assert.True(DatabaseErrors.IsWaking(SqlExceptions.Create(40613)));

        await fx.AddDemoAsync();
        await using var host = Product(fx);
        host.Connections.FailWith = () => SqlExceptions.Create([40613, DatabaseErrors.FreeAllowanceExhausted]);
        host.Connections.FailuresRemaining = -1;
        var watch = Stopwatch.StartNew();
        using var response = await host.Client.PostAsync(ApiRoutes.Path(ApiRoutes.AuthDemo), null, Ct);
        watch.Stop();

        using var problem = await response.Problem(503, ProblemCodes.DatabaseResting);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"answered after {watch.Elapsed}: a retryable error beside it made the API wait");
        Assert.Equal(1, host.Connections.Opened);
    }
}
