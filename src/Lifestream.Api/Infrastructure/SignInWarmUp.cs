using System.Diagnostics;
using Lifestream.Api.Auth;
using Lifestream.Data;
using Lifestream.Data.Seed;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Infrastructure;

/// <summary>
/// Does, ahead of the first sign-in, the work a new process otherwise makes that sign-in wait for: the EF model, the token code, the
/// database connection with its managed-identity token, and the queries a sign-in and the first signed-in request run.
/// <para>
/// It has two parts. <see cref="StartOffline"/> touches no database at all (the model and the token code) and starts when the first
/// <c>/healthz</c> has been answered. <see cref="EnsureStarted"/> is called by <c>/readyz</c> only, which the client calls when its
/// sign-in window opens: that is the one place the database part may start. Nothing reaches the database at process start or from
/// <c>/healthz</c>: stray requests start the container, and every wake of the free database uses its monthly allowance.
/// </para>
/// <para>
/// The database part runs in the background and once: a second call while it runs, or after it has succeeded, does nothing. After a
/// failure (the database did not wake within the budget) it may run again, but only when asked to after the database has answered.
/// Everything it does is a read; it writes nothing and signs nobody in (the tokens it makes are discarded).
/// </para>
/// </summary>
public sealed partial class SignInWarmUp(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, IOptions<DatabaseSettings> database, ILogger<SignInWarmUp> logger)
{
    private readonly Lock _gate = new();
    private Task _offline = Task.CompletedTask;
    private bool _offlineStarted;
    private Task? _run;
    private bool _succeeded;
    private int _runs;

    /// <summary>How many times the database part was started (1 unless a run failed and a later one was allowed).</summary>
    public int Runs => Volatile.Read(ref _runs);

    /// <summary>True once the database part has finished without a failure.</summary>
    public bool Succeeded
    {
        get
        {
            lock (_gate)
            {
                return _succeeded;
            }
        }
    }

    /// <summary>The database part that is running or ran last (completed when none was started). It never faults: a failure is logged.</summary>
    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _run ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>The part without a database: completed when it has run (or was never started).</summary>
    public Task OfflineCompletion
    {
        get
        {
            lock (_gate)
            {
                return _offline;
            }
        }
    }

    /// <summary>True once the part without a database has been started.</summary>
    public bool OfflineStarted
    {
        get
        {
            lock (_gate)
            {
                return _offlineStarted;
            }
        }
    }

    /// <summary>Builds the EF model and warms the token code in the background, once. Opens no connection, so any request may start it.</summary>
    public void StartOffline()
    {
        lock (_gate)
        {
            if (_offlineStarted)
            {
                return;
            }

            _offlineStarted = true;
            _offline = Task.Run(OfflineAsync);
        }
    }

    /// <summary>
    /// Starts the database part unless it is running or has succeeded. With <paramref name="afterFailure"/> false it starts only the
    /// first time; with true (the caller has just seen the database answer) a run that failed is started again.
    /// </summary>
    public void EnsureStarted(bool afterFailure = false)
    {
        lock (_gate)
        {
            if (_succeeded || _run is { IsCompleted: false } || (_run is not null && !afterFailure))
            {
                return;
            }

            _runs++;
            _run = Task.Run(RunAsync);
        }
    }

    private async Task OfflineAsync()
    {
        try
        {
            var watch = Stopwatch.StartNew();
            await using var scope = scopes.CreateAsyncScope();
            Offline(scope.ServiceProvider);
            LogOffline(logger, watch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nothing depends on the warm-up: the first request does the same work itself. Type only, an exception message could carry a connection detail.
            LogFailed(logger, "offline", exception.GetType().Name, null, 0);
        }
    }

    /// <summary>The work that needs no database. Every step is cached by what it warms, so running it twice costs nothing.</summary>
    private static void Offline(IServiceProvider services)
    {
        _ = services.GetRequiredService<LifestreamDbContext>().Model;
        _ = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        services.GetRequiredService<AuthService>().WarmUpTokens();
    }

    private async Task RunAsync()
    {
        var ct = lifetime.ApplicationStopping;
        var watch = Stopwatch.StartNew();
        var step = "offline";
        try
        {
            await OfflineCompletion.ConfigureAwait(false);
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            Offline(services);
            var offlineMs = watch.ElapsedMilliseconds;

            // The first query is the one that waits for a resuming database (the execution strategy retries it within the wake budget).
            step = "sign-in";
            await services.GetRequiredService<AuthService>().WarmUpAsync(ct).ConfigureAwait(false);
            var signInMs = watch.ElapsedMilliseconds;

            // What the first signed-in request reads before its own work: is the session still good, and is the daily demo reset due.
            step = "session";
            var db = services.GetRequiredService<LifestreamDbContext>();
            var time = services.GetRequiredService<TimeProvider>();
            await RevocationCache.WarmUpAsync(db, time, ct).ConfigureAwait(false);
            await services.GetRequiredService<DemoSessionDenylist>().WarmUpAsync(ct).ConfigureAwait(false);
            if (services.GetRequiredService<IOptions<DemoSettings>>().Value.Enabled)
            {
                await DemoReset.IsDueAsync(db, time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            }

            // The first screen sends several requests at once and each needs a connection of its own; the pool opens them one after another.
            step = "connections";
            var extra = Math.Clamp(database.Value.WarmConnections, 1, 8) - 1;
            await Task.WhenAll(Enumerable.Range(0, extra).Select(_ => OpenOneAsync(ct))).ConfigureAwait(false);

            lock (_gate)
            {
                _succeeded = true;
            }

            LogFinished(logger, watch.ElapsedMilliseconds, offlineMs, signInMs - offlineMs, watch.ElapsedMilliseconds - signInMs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The process is stopping.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogFailed(logger, step, exception.GetType().Name, DatabaseErrors.NumberOf(exception), watch.ElapsedMilliseconds);
        }
    }

    private async Task OpenOneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LifestreamDbContext>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async token =>
            {
                await db.Database.OpenConnectionAsync(token).ConfigureAwait(false);
                await db.Database.CloseConnectionAsync().ConfigureAwait(false); // back to the pool, where the first requests find it
            },
            ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Warm-up without the database finished in {ElapsedMs} ms")]
    private static partial void LogOffline(ILogger logger, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in warm-up finished in {ElapsedMs} ms (model and tokens {OfflineMs} ms, sign-in queries {SignInMs} ms, session checks and connections {RestMs} ms)")]
    private static partial void LogFinished(ILogger logger, long elapsedMs, long offlineMs, long signInMs, long restMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in warm-up stopped at step {Step}: {ExceptionType} (sql {SqlNumber}) after {ElapsedMs} ms; the first request does the work instead")]
    private static partial void LogFailed(ILogger logger, string step, string exceptionType, int? sqlNumber, long elapsedMs);
}
