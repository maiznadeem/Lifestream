using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http.Headers;
using Lifestream.Api.Auth;
using Lifestream.Api.Infrastructure;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Data.Seed;
using Lifestream.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lifestream.Api.Tests;

/// <summary>Records every query EF has to compile (a query it has compiled before is not compiled again, so it does not show here).</summary>
public sealed class CompilationLog : IQueryExpressionInterceptor
{
    private readonly ConcurrentQueue<string> _queries = new();

    public IReadOnlyList<string> Queries => [.. _queries];

    public void Clear() => _queries.Clear();

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        _queries.Enqueue(queryExpression.ToString());
        return queryExpression;
    }
}

/// <summary>
/// A new API process over the fixture's database: what a container that just started is. The warm-up runs once per process, so every
/// test here starts its own.
/// </summary>
public sealed class ColdHost : IAsyncDisposable
{
    public ColdHost(ApiFixture fx, Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>(fx.Settings); // the same signing key: a token of the fixture's host is good here, as after a restart
        configure?.Invoke(settings);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureLogging(l => l.AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(fx.Clock);
                services.AddSingleton<IInterceptor>(Commands);
                services.AddSingleton<IInterceptor>(Connections);
                services.AddSingleton<IInterceptor>(Compiled);
                services.AddSingleton<IDatabaseProbe>(Probe);
                services.AddSingleton<IStartupFilter, RemoteAddressFilter>();
                services.AddSingleton<ICredentialHasher>(SharedPbkdf2Work.Instance);
                services.AddSingleton(SharedPbkdf2Work.Instance.Dummy);
            });
        });
        Client = Factory.CreateClient();
    }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    public CommandLog Commands { get; } = new() { Recording = true };

    public ConnectionProbe Connections { get; } = new();

    public CompilationLog Compiled { get; } = new();

    public FakeProbe Probe { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public SignInWarmUp WarmUp => Factory.Services.GetRequiredService<SignInWarmUp>();

    /// <summary>Calls /readyz and waits for the warm-up it started.</summary>
    public async Task<HttpStatusCode> ReadyAndWarmAsync()
    {
        using var response = await Client.GetAsync(ApiRoutes.Readyz, TestContext.Current.CancellationToken);
        await WarmUp.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>
/// Z1 of the cold-start work: /readyz starts, in the background and once, a warm-up of what the first sign-in needs. Process start and
/// /healthz never touch the database, and /readyz still answers at once and says only what its own connection found.
/// </summary>
public sealed class SignInWarmUpTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProcessStart_AndHealthz_OpenNoConnection_AndNeverStartTheDatabaseWarmUp()
    {
        await using var host = new ColdHost(fx, s => s["Logging:LogLevel:Lifestream.Api.Infrastructure.SignInWarmUp"] = "Information");
        Assert.False(host.WarmUp.OfflineStarted); // a process that has answered nothing has warmed nothing

        for (var i = 0; i < 5; i++)
        {
            using var response = await host.Client.GetAsync(ApiRoutes.Healthz, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The first answered /healthz starts the part that needs no database (the EF model, the token code); it runs to its end.
        for (var i = 0; i < 500 && !host.WarmUp.OfflineStarted; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(host.WarmUp.OfflineStarted);
        await host.WarmUp.OfflineCompletion.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Contains(host.Logs.Lines, l => l.Contains("Warm-up without the database finished", StringComparison.Ordinal));

        await Task.Delay(200, Ct); // anything a request had started in the background would have opened its connection by now
        Assert.Equal(0, host.WarmUp.Runs);
        Assert.False(host.WarmUp.Succeeded);
        Assert.Equal(0, host.Connections.Opened);
        Assert.Empty(host.Commands.Commands);
        Assert.DoesNotContain(host.Logs.Lines, l => l.Contains("warm-up stopped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Readyz_StartsTheWarmUpOnce_HoweverManyCallsArrive_AndNeverAgainAfterItSucceeded()
    {
        await fx.AddDemoAsync();
        await using var host = new ColdHost(fx, s => s["Logging:LogLevel:Lifestream.Api.Infrastructure.SignInWarmUp"] = "Information");

        var calls = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => host.Client.GetAsync(ApiRoutes.Readyz, Ct)));
        Assert.All(calls, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var call in calls)
        {
            call.Dispose();
        }

        await host.WarmUp.Completion.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.True(host.WarmUp.Succeeded, string.Join(" // ", host.Logs.Lines));
        Assert.Equal(1, host.WarmUp.Runs);

        var opened = host.Connections.Opened;
        Assert.Equal(HttpStatusCode.OK, await host.ReadyAndWarmAsync());
        Assert.Equal(1, host.WarmUp.Runs);
        Assert.Equal(opened, host.Connections.Opened); // a later /readyz costs its own probe and nothing else
        Assert.Contains(host.Logs.Lines, l => l.Contains("Sign-in warm-up finished", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheWarmUp_ADemoSignIn_HasToCompileItsQuery()
    {
        // The control for the next test: in a process whose /readyz was never called, the sign-in does this work itself.
        await fx.AddDemoAsync();
        await using var host = new ColdHost(fx);

        using var signIn = await host.Client.PostAsync(ApiRoutes.Path(ApiRoutes.AuthDemo), null, Ct);

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.NotEmpty(host.Compiled.Queries);
    }

    [Fact]
    public async Task AfterTheWarmUp_ADemoSignIn_CompilesNothing_AndItsFirstRequestCompilesOnlyItsOwnQuery()
    {
        await fx.AddDemoAsync();
        await using var host = new ColdHost(fx);
        Assert.Equal(HttpStatusCode.OK, await host.ReadyAndWarmAsync());
        Assert.True(host.WarmUp.Succeeded, string.Join(" // ", host.Logs.Lines));
        var connections = host.Connections.Opened;
        host.Compiled.Clear();

        using var signIn = await host.Client.PostAsync(ApiRoutes.Path(ApiRoutes.AuthDemo), null, Ct);
        var tokens = await signIn.Read<TokenResponse>();

        Assert.Empty(host.Compiled.Queries);

        // The first signed-in request: the session check and the daily-reset check were compiled by the warm-up; only the stock query is new.
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.Stock));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var first = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.DoesNotContain(host.Compiled.Queries, q => q.Contains("DemoRevocation", StringComparison.Ordinal) || q.Contains("DemoState", StringComparison.Ordinal) || q.Contains("Staff", StringComparison.Ordinal));
        Assert.True(host.Connections.Opened > connections, "the requests used the database");
    }

    [Fact]
    public async Task AfterTheWarmUp_APasswordSignIn_DoesNotCompileItsLookUp()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        await using var host = new ColdHost(fx);
        Assert.Equal(HttpStatusCode.OK, await host.ReadyAndWarmAsync());
        host.Compiled.Clear();

        using var signIn = await host.Client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(staff.Username, ApiFixture.Password));

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.DoesNotContain(host.Compiled.Queries, q => q.Contains("Username", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readyz_AnswersAtOnce_AndSaysOnlyWhatItsOwnConnectionFound_WhileTheWarmUpWaitsForAWakingDatabase()
    {
        await fx.AddDemoAsync();
        await using var host = new ColdHost(fx);
        host.Connections.FailuresRemaining = -1; // every connection of the warm-up fails as a resuming database does
        host.Probe.FailWith = 40613;

        var watch = Stopwatch.StartNew();
        using (var waking = await host.Client.GetAsync(ApiRoutes.Readyz, Ct))
        {
            watch.Stop();
            using var problem = await waking.Problem(503, ProblemCodes.DatabaseWaking);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"/readyz waited {watch.Elapsed} for the warm-up");
        Assert.False(host.WarmUp.Completion.IsCompleted); // still retrying in the background
        Assert.Equal(1, host.WarmUp.Runs);

        // The probe's own connection gets an answer: ready, whatever the warm-up is still doing.
        host.Probe.FailWith = null;
        using (var ready = await host.Client.GetAsync(ApiRoutes.Readyz, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        Assert.False(host.WarmUp.Succeeded);
        Assert.Equal(1, host.WarmUp.Runs);

        // The database is up: the same run finishes; no second one was needed.
        host.Connections.FailuresRemaining = 0;
        await host.WarmUp.Completion.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.True(host.WarmUp.Succeeded, string.Join(" // ", host.Logs.Lines));
        Assert.Equal(1, host.WarmUp.Runs);
    }

    [Fact]
    public async Task AWarmUpThatGaveUp_RunsAgain_OnlyOnceTheDatabaseAnswers()
    {
        await fx.AddDemoAsync();
        await using var host = new ColdHost(fx, s => s["Database:WakeBudgetSeconds"] = "1");
        host.Connections.FailuresRemaining = -1;
        host.Probe.FailWith = 40613;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await host.ReadyAndWarmAsync());
        Assert.False(host.WarmUp.Succeeded);
        Assert.Contains(host.Logs.Lines, l => l.Contains("warm-up stopped at step sign-in", StringComparison.Ordinal));

        // Still waking: asking again must not start another run (that would be one more caller knocking on a paused database).
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await host.ReadyAndWarmAsync());
        Assert.Equal(1, host.WarmUp.Runs);

        host.Connections.FailuresRemaining = 0;
        host.Probe.FailWith = null;
        Assert.Equal(HttpStatusCode.OK, await host.ReadyAndWarmAsync());
        Assert.Equal(2, host.WarmUp.Runs);
        Assert.True(host.WarmUp.Succeeded, string.Join(" // ", host.Logs.Lines));
    }

    [Fact]
    public async Task ARestingDatabase_StartsNoWarmUp()
    {
        await using var host = new ColdHost(fx);
        host.Connections.FailNumber = DatabaseErrors.FreeAllowanceExhausted;
        host.Connections.FailuresRemaining = -1;
        host.Probe.FailWith = DatabaseErrors.FreeAllowanceExhausted;

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await host.ReadyAndWarmAsync());
        }

        // Waiting does not bring back a database whose allowance is used up, so nothing is started for it.
        Assert.Equal(0, host.WarmUp.Runs);
        Assert.Equal(0, host.Connections.Opened);
    }

    [Fact]
    public async Task TheWarmUp_OnlyReads_AndLeavesADueDailyResetToTheFirstSignedInRequest()
    {
        await fx.AddDemoAsync();
        var yesterday = fx.Clock.Today.AddDays(-1);
        await using (var db = fx.Db.NewContext())
        {
            await db.DemoState.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastResetDate, (DateOnly?)yesterday), Ct);
        }

        try
        {
            int staffBefore, unknownBefore, tokensBefore, revocationsBefore;
            await using (var db = fx.Db.NewContext())
            {
                (staffBefore, unknownBefore, tokensBefore, revocationsBefore) = (await db.Staff.CountAsync(Ct), await db.UnknownLogins.CountAsync(Ct), await db.RefreshTokens.CountAsync(Ct), await db.DemoRevocations.CountAsync(Ct));
            }

            await using var host = new ColdHost(fx);
            Assert.Equal(HttpStatusCode.OK, await host.ReadyAndWarmAsync());
            Assert.True(host.WarmUp.Succeeded, string.Join(" // ", host.Logs.Lines));

            Assert.NotEmpty(host.Commands.Commands);
            Assert.All(host.Commands.Commands, sql =>
            {
                Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("UPDATE ", sql, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
            });

            await using var after = fx.Db.NewContext();
            Assert.Equal(yesterday, (await after.DemoState.AsNoTracking().SingleAsync(Ct)).LastResetDate); // due, and not done by the warm-up
            Assert.Equal((staffBefore, unknownBefore, tokensBefore, revocationsBefore), (await after.Staff.CountAsync(Ct), await after.UnknownLogins.CountAsync(Ct), await after.RefreshTokens.CountAsync(Ct), await after.DemoRevocations.CountAsync(Ct)));
            Assert.True(await DemoReset.IsDueAsync(after, fx.Clock.GetUtcNow().UtcDateTime, Ct));
        }
        finally
        {
            await fx.MarkResetTodayAsync(); // the other tests of this class must not meet a due reset
        }

        await using var today = fx.Db.NewContext();
        Assert.False(await DemoReset.IsDueAsync(today, fx.Clock.GetUtcNow().UtcDateTime, Ct));
    }
}
