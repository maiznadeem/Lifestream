using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Api.Tests;

/// <summary>The shipped limits (login 10 per minute per address) instead of the generous test ones.</summary>
public sealed class DefaultLimitsFixture : ApiFixture
{
    protected override void Configure(Dictionary<string, string?> settings)
    {
        settings["Limits:LoginPerMinute"] = "10";
        settings["Limits:GlobalPerMinute"] = "600";
    }
}

/// <summary>A lock threshold so high that only the counting itself is observable.</summary>
public sealed class HighLockoutFixture : ApiFixture
{
    protected override void Configure(Dictionary<string, string?> settings) => settings["Auth:MaxFailedAttempts"] = "1000";
}

/// <summary>
/// A lock threshold of 1000 and a password check that holds the thread it runs on until <see cref="ReleaseChecks"/>: on a loaded machine a 0.3 s
/// PBKDF2 check takes seconds (the full suite measured up to 5 s), and this makes that state exact instead of depending on the machine. Every guess
/// sent to this host is wrong, so a released check answers false without spending PBKDF2 time on it.
/// </summary>
public sealed class HeldChecksFixture : ApiFixture
{
    private readonly ManualResetEventSlim _checks = new();

    public void ReleaseChecks() => _checks.Set();

    protected override void Configure(Dictionary<string, string?> settings) => settings["Auth:MaxFailedAttempts"] = "1000";

    protected override void ConfigureTestServices(IServiceCollection services) => services.AddSingleton<Lifestream.Api.Auth.ICredentialHasher>(new HeldHasher(_checks));

    private sealed class HeldHasher(ManualResetEventSlim release) : Lifestream.Api.Auth.ICredentialHasher
    {
        public string Hash(string password) => SharedPbkdf2Work.Instance.Hash(password);

        public bool Verify(string hash, string password)
        {
            release.Wait(TimeSpan.FromMinutes(2));
            return false;
        }
    }
}

/// <summary>Makes every write of a sign-in counter (the UPDATE of a StaffCredential row) fail while <see cref="Mode"/> is set: a clash with another writer, or a plain failure.</summary>
public sealed class StaffWriteFailer : DbCommandInterceptor
{
    public enum FailMode
    {
        None,
        ConcurrencyClash,
        Failure,
    }

    public FailMode Mode { get; set; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Mode != FailMode.None && command.CommandText.StartsWith("UPDATE StaffCredential", StringComparison.Ordinal) && command.Parameters.Cast<System.Data.Common.DbParameter>().Any(p => p.ParameterName == "@write" && (int)p.Value! == 1))
        {
            throw Mode == FailMode.ConcurrencyClash ? new DbUpdateConcurrencyException("simulated clash") : new InvalidOperationException("simulated write failure");
        }

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

public sealed class FailingStaffWriteFixture : ApiFixture
{
    public StaffWriteFailer Failer { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services) => services.AddSingleton<IInterceptor>(Failer);
}

/// <summary>API-03, API-36: lockout and failure counting hold under concurrency. Replays the verifier's sequences P05, P17 and P20 against a real SQL Server.</summary>
[Collection(Lifestream.Api.Tests.Support.Alone.Name)] // a burst of concurrent requests: runs after the parallel classes, alone
public sealed class LoginLockoutConcurrencyTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static Task<(int Status, string? Code, string? RetryAfter)[]> BurstAsync(ApiFixture fx, string username, IReadOnlyList<string> passwords, Func<int, string> ipFor) =>
        BurstAsync(fx, [.. passwords.Select((password, i) => (username, password, ipFor(i)))]);

    internal static async Task<(int Status, string? Code, string? RetryAfter)[]> BurstAsync(ApiFixture fx, IReadOnlyList<(string Username, string Password, string Ip)> guesses)
    {
        // Clients are created up front: the fixture's client list is not thread safe.
        var clients = guesses.Select(g => fx.NewClient(ip: g.Ip)).ToList();
        return await Task.WhenAll(guesses.Select((guess, i) => Task.Run(
            async () =>
            {
                using var response = await clients[i].PostApi(ApiRoutes.AuthLogin, new LoginRequest(guess.Username, guess.Password));
                var text = await response.Content.ReadAsStringAsync(Ct);
                var code = response.IsSuccessStatusCode ? null : JsonElement.Parse(text).GetProperty("code").GetString();
                return ((int)response.StatusCode, code, response.Headers.TryGetValues("Retry-After", out var v) ? v.Single() : null);
            },
            Ct)));
    }

    [Fact]
    public async Task Login_40ConcurrentWrongGuessesFrom40Addresses_OnlyFiveAreChecked_TheRestAre429LockedOut()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        var results = await BurstAsync(fx, user.Username, [.. Enumerable.Range(0, 40).Select(i => "wrong-guess-" + i)], i => $"203.0.113.{i + 1}");

        var seen = string.Join(", ", results.GroupBy(r => $"{r.Status} {r.Code}").Select(g => $"{g.Key} x{g.Count()}"));
        Assert.True(results.Count(r => r.Status == 401) == 5, $"expected 5 checked guesses (401); got {seen}");
        Assert.True(results.Count(r => r.Status == 429 && r.Code == ProblemCodes.LockedOut) == 35, $"expected 35 locked out (429 {ProblemCodes.LockedOut}); got {seen}");
        Assert.All(results.Where(r => r.Status == 429), r => Assert.True(int.Parse(r.RetryAfter!, System.Globalization.CultureInfo.InvariantCulture) > 0));

        // The real database holds the lock; counting restarted when it began.
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        var row = (await fx.Db.RowsAsync($"SELECT FailedCount, LockoutEndUtc FROM StaffCredential WHERE StaffId = {user.Id}", Ct)).Single();
        Assert.Equal(0, Convert.ToInt32(row[0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotNull(row[1]);

        // The right password is refused while the lock lasts.
        var after = await BurstAsync(fx, user.Username, [ApiFixture.Password], _ => "203.0.113.200");
        Assert.Equal(429, after[0].Status);
    }

    [Fact]
    public async Task Login_BurstOf24WrongGuessesAndTheRightPassword_NeverChecksMoreThanOneWindowOfGuesses()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        var passwords = Enumerable.Range(0, 24).Select(i => "wrong-guess-" + i).Append(ApiFixture.Password).ToList();
        var results = await BurstAsync(fx, user.Username, passwords, i => $"198.18.0.{i + 1}");

        var signedIn = results.Count(r => r.Status == 200);
        var wrongChecked = results.Count(r => r.Status == 401);
        Assert.True(signedIn <= 1);

        // Without the right password reaching the front of the queue, no more than five guesses may be checked; a sign-in
        // starts a new window, so at most one more window of five can follow it.
        Assert.True(wrongChecked <= (signedIn == 0 ? 5 : 10), $"{wrongChecked} wrong guesses were checked (signed in: {signedIn})");
        Assert.Equal(25, results.Length);
        Assert.All(results, r => Assert.True(r.Status is 200 or 401 or 429));
    }

    [Fact]
    public async Task Login_AllConcurrentFailuresAreCounted_NoneAreDropped()
    {
        await using var fixture = new HighLockoutFixture();
        await fixture.InitializeAsync();
        var user = await fixture.AddStaffAsync(Role.Staff);
        var results = await BurstAsync(fixture, user.Username, [.. Enumerable.Range(0, 30).Select(i => "lost-write-" + i)], i => $"192.0.2.{i + 1}");

        Assert.All(results, r => Assert.Equal(401, r.Status));
        Assert.Equal(30, await fixture.Db.ScalarAsync<int>($"SELECT FailedCount FROM StaffCredential WHERE StaffId = {user.Id}", Ct));
    }

    [Fact]
    public async Task Login_ConcurrentGuessesAreAllCounted_WhilePasswordChecksAreSlow()
    {
        // Thirty wrong guesses at one account and ninety at unknown names, all at once, while every password check is held. Counting a guess
        // must not wait for anyone's password check: all thirty at the account are counted before a single check is let go, and then every
        // guess is a 401 (no 409 "busy" because the account's lock queue outlasted its wait).
        await using var fixture = new HeldChecksFixture();
        await fixture.InitializeAsync();
        var user = await fixture.AddStaffAsync(Role.Staff);
        List<(string, string, string)> guesses =
        [
            .. Enumerable.Range(0, 30).Select(i => (user.Username, "held-guess-" + i, $"192.0.2.{i + 1}")),
            .. Enumerable.Range(0, 90).Select(i => (fixture.NextName("nobody"), "held-guess-" + i, $"198.51.100.{i + 1}")),
        ];

        var counted = 0;
        (int Status, string? Code, string? RetryAfter)[] results;
        var sent = BurstAsync(fixture, guesses);
        try
        {
            var deadline = Stopwatch.StartNew();
            while ((counted = await fixture.Db.ScalarAsync<int>($"SELECT FailedCount FROM StaffCredential WHERE StaffId = {user.Id}", Ct)) < 30 && deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(50, Ct);
            }
        }
        finally
        {
            fixture.ReleaseChecks();
            results = await sent;
        }

        var seen = string.Join(", ", results.GroupBy(r => $"{r.Status} {r.Code}").Select(g => $"{g.Key} x{g.Count()}"));
        Assert.True(results.All(r => r.Status == 401), $"expected 120 x 401; got {seen}");
        Assert.True(counted == 30, $"only {counted} of 30 guesses at the account were counted while the password checks were held; got {seen}");
        Assert.Equal(30, await fixture.Db.ScalarAsync<int>($"SELECT FailedCount FROM StaffCredential WHERE StaffId = {user.Id}", Ct));
    }

    [Fact]
    public async Task Login_WhileTheAccountLockIsHeldLongerThanOtherLocksWait_TheGuessWaitsAndIsCounted()
    {
        // On a busy server the queue for one account's lock can take longer than any fixed wait; a guess in it is counted (401) or refused as
        // locked (429), never answered 409 "busy". Here the account's lock is held from outside for longer than the 15 s other locks wait.
        var user = await fx.AddStaffAsync(Role.Staff);
        await using var holder = new SqlConnection(fx.Db.ConnectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync(Ct);
        await using (var take = new SqlCommand("EXEC sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction'", holder, transaction))
        {
            take.Parameters.AddWithValue("@resource", $"login:{user.Id}");
            await take.ExecuteNonQueryAsync(Ct);
        }

        using var client = fx.NewClient(ip: "203.0.113.150");
        var guess = client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(user.Username, "wrong-while-held"));
        await Task.Delay(TimeSpan.FromSeconds(16), Ct);
        if (guess.IsCompleted)
        {
            using var early = await guess;
            Assert.Fail($"the guess stopped waiting for the account's lock: {(int)early.StatusCode} {await early.Content.ReadAsStringAsync(Ct)}");
        }

        await transaction.RollbackAsync(Ct);
        using var response = await guess;
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT FailedCount FROM StaffCredential WHERE StaffId = {user.Id}", Ct));
    }

    [Fact]
    public async Task Login_OneAddressSendingTwelveConcurrentGuesses_AtMostFiveAreChecked_WithTheShippedLimits()
    {
        await using var fixture = new DefaultLimitsFixture();
        await fixture.InitializeAsync();
        var user = await fixture.AddStaffAsync(Role.Staff);
        var results = await BurstAsync(fixture, user.Username, [.. Enumerable.Range(0, 12).Select(i => "single-ip-guess-" + i)], _ => "203.0.113.77");

        Assert.True(results.Count(r => r.Status == 401) <= 5, string.Join(",", results.Select(r => r.Status)));
        Assert.Equal(12, results.Count(r => r.Status is 401 or 429));
    }
}

/// <summary>API-36: a failed security write is an error response, never a quiet 401.</summary>
public sealed class LoginWriteFailureTests(FailingStaffWriteFixture fx) : IClassFixture<FailingStaffWriteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(StaffWriteFailer.FailMode.ConcurrencyClash, 409, "concurrency")]
    [InlineData(StaffWriteFailer.FailMode.Failure, 500, "server_error")]
    public async Task Login_WhenTheFailureCannotBeWritten_AnswersAnErrorNotA401(StaffWriteFailer.FailMode mode, int status, string code)
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        fx.Failer.Mode = mode;
        try
        {
            using var client = fx.NewClient();
            using var response = await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(user.Username, "wrong-password-1"));
            using var problem = await response.Problem(status, code);
        }
        finally
        {
            fx.Failer.Mode = StaffWriteFailer.FailMode.None;
        }

        // Nothing was half-written: the account still counts from where it was.
        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT FailedCount FROM StaffCredential WHERE StaffId = {user.Id}", Ct));
    }
}

/// <summary>API-01: an unknown username and a real one are indistinguishable in every state, including lockout.</summary>
public sealed class LoginEnumerationTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seen(int Status, string Type, string Title, string Code, string[] BodyKeys, string[] HeaderNames, int? RetryAfter);

    private async Task<Seen> AttemptAsync(string username, string password)
    {
        using var client = fx.NewClient();
        using var response = await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(username, password));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;
        return new Seen(
            (int)response.StatusCode,
            root.GetProperty("type").GetString()!,
            root.GetProperty("title").GetString()!,
            root.GetProperty("code").GetString()!,
            [.. root.EnumerateObject().Select(p => p.Name).Order()],
            [.. response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key)).Where(n => n is not "Date").Order()],
            response.Headers.TryGetValues("Retry-After", out var v) ? int.Parse(v.Single(), System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private static void AssertSame(Seen known, Seen unknown, string state)
    {
        Assert.True(known.Status == unknown.Status, $"{state}: status {known.Status} vs {unknown.Status}");
        Assert.Equal(known.Type, unknown.Type);
        Assert.Equal(known.Title, unknown.Title);
        Assert.Equal(known.Code, unknown.Code);
        Assert.Equal(known.BodyKeys, unknown.BodyKeys);
        Assert.Equal(known.HeaderNames, unknown.HeaderNames);
        Assert.Equal(known.RetryAfter is null, unknown.RetryAfter is null);
        if (known.RetryAfter is { } a)
        {
            Assert.InRange(Math.Abs(a - unknown.RetryAfter!.Value), 0, 2);
        }
    }

    [Fact]
    public async Task Login_KnownAndUnknownUsername_GetIdenticalResponses_InEveryState_IncludingLockout()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        var ghost = "nobody-" + user.Username;

        // Fresh failures, then the lockout itself (the sixth answer), then the right password while locked.
        for (var i = 1; i <= 5; i++)
        {
            AssertSame(await AttemptAsync(user.Username, "wrong-password-" + i), await AttemptAsync(ghost, "wrong-password-" + i), $"attempt {i}");
        }

        var knownLocked = await AttemptAsync(user.Username, "wrong-password-6");
        var unknownLocked = await AttemptAsync(ghost, "wrong-password-6");
        Assert.Equal(429, knownLocked.Status);
        AssertSame(knownLocked, unknownLocked, "locked");
        AssertSame(await AttemptAsync(user.Username, ApiFixture.Password), await AttemptAsync(ghost, ApiFixture.Password), "right password while locked");

        // The lock ends with the clock, for both.
        fx.Clock.Advance(TimeSpan.FromMinutes(16));
        try
        {
            AssertSame(await AttemptAsync(user.Username, "wrong-password-7"), await AttemptAsync(ghost, "wrong-password-7"), "after the lock expired");
        }
        finally
        {
            fx.Clock.Advance(TimeSpan.FromMinutes(-16));
        }
    }

    [Fact]
    public async Task Login_40ConcurrentGuessesAtAnUnknownUsername_BehaveLikeARealOne()
    {
        var ghost = "ghost-" + Guid.NewGuid().ToString("N")[..8];
        var results = await LoginLockoutConcurrencyTests.BurstAsync(fx, ghost, [.. Enumerable.Range(0, 40).Select(i => "wrong-guess-" + i)], i => $"203.0.114.{i + 1}");

        Assert.Equal(5, results.Count(r => r.Status == 401));
        Assert.Equal(35, results.Count(r => r.Status == 429 && r.Code == ProblemCodes.LockedOut));
    }

    [Fact]
    [Trait("Category", "Perf")] // a timing comparison: run in the perf pass, not in the loaded default run
    public async Task Login_ResponseTime_DoesNotTellAKnownUsernameFromAnUnknownOne_InEitherState()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        var ghost = "ghost-" + user.Username;
        var login = new Func<string, Task<double>>(async name =>
        {
            using var client = fx.NewClient();
            var watch = Stopwatch.StartNew();
            (await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(name, "wrong-password-timing"))).Dispose();
            return watch.Elapsed.TotalMilliseconds;
        });

        // Not locked yet: both verify a hash. Interleave the samples so machine noise hits both alike, and stay under the lock threshold.
        await login(user.Username);
        await login(ghost);
        var knownOpen = new List<double> { await login(user.Username) };
        var unknownOpen = new List<double> { await login(ghost) };
        knownOpen.Add(await login(user.Username));
        unknownOpen.Add(await login(ghost));
        Assert.InRange(Math.Abs(Median(knownOpen) - Median(unknownOpen)), 0, Math.Max(80, Math.Max(Median(knownOpen), Median(unknownOpen)) * 0.5));

        // Locked: neither verifies anything.
        for (var i = 0; i < 4; i++)
        {
            await login(user.Username);
            await login(ghost);
        }

        var knownLocked = new List<double>();
        var unknownLocked = new List<double>();
        for (var i = 0; i < 9; i++)
        {
            knownLocked.Add(await login(user.Username));
            unknownLocked.Add(await login(ghost));
        }

        Assert.InRange(Math.Abs(Median(knownLocked) - Median(unknownLocked)), 0, 25);
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
}
