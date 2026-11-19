using System.Diagnostics;
using System.Text.Json;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>A database of its own: the throwaway names of the ageing test must not leak into other tests.</summary>
public sealed class LoginAgeingFixture : ApiFixture
{
}

/// <summary>
/// API-01 (final): an unknown name and a real account answer alike after the idle time has passed (also when the purge has not deleted
/// the old counter row), and run the same database work in every state. Ports the verifier's probes V01, V01b, V02 and T02 (v-04c-api).
/// All tests here run against a real SQL Server.
/// </summary>
public sealed class LoginExpiryEnumerationTests(LoginAgeingFixture fx) : IClassFixture<LoginAgeingFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> AnswerAsync(string username, string password)
    {
        using var client = fx.NewClient();
        using var response = await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(username, password));
        var text = await response.Content.ReadAsStringAsync(Ct);
        var code = text.StartsWith('{') && JsonElement.Parse(text).TryGetProperty("code", out var c) ? c.GetString() : null;
        return $"{(int)response.StatusCode}/{code}/RA={(response.Headers.RetryAfter is null ? "none" : "set")}";
    }

    private async Task<(List<string> Known, List<string> Unknown)> GuessAfterIdleAsync(int extraNames)
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        var user = await fx.AddStaffAsync(Role.Staff);
        var ghost = "ghost-age-" + Guid.NewGuid().ToString("N")[..8];
        var prefix = "filler" + Guid.NewGuid().ToString("N")[..6];
        await Parallel.ForEachAsync(Enumerable.Range(0, extraNames), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Ct }, async (i, _) => await AnswerAsync(prefix + i, "x-wrong"));
        fx.Clock.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(await AnswerAsync(user.Username, "w" + i), await AnswerAsync(ghost, "w" + i));
        }

        var idle = TimeSpan.FromDays(30) + TimeSpan.FromHours(1);
        fx.Clock.Advance(idle);
        try
        {
            var known = new List<string>();
            var unknown = new List<string>();
            for (var i = 4; i < 8; i++)
            {
                known.Add(await AnswerAsync(user.Username, "w" + i));
                unknown.Add(await AnswerAsync(ghost, "w" + i));
            }

            return (known, unknown);
        }
        finally
        {
            fx.Clock.Advance(-idle);
        }
    }

    [Fact]
    public async Task Login_PartialCountIdleFor30Days_IsForgotten_ForRealAndUnknownAlike_AlsoWhen250OtherExpiredRowsExceedThePurgeBatch()
    {
        var (known, unknown) = await GuessAfterIdleAsync(extraNames: 250);

        // Four old failures are forgotten: the four new guesses are wrong guesses 1 to 4 for both, so none of them locks.
        Assert.Equal(["401/invalid_credentials/RA=none", "401/invalid_credentials/RA=none", "401/invalid_credentials/RA=none", "401/invalid_credentials/RA=none"], known);
        Assert.Equal(known, unknown);
    }

    [Fact]
    public async Task Login_PartialCountIdleFor30Days_IsForgotten_ForRealAndUnknownAlike_WithoutOtherRows()
    {
        var (known, unknown) = await GuessAfterIdleAsync(extraNames: 0);
        Assert.Equal(known, unknown);
        Assert.All(unknown, a => Assert.Equal("401/invalid_credentials/RA=none", a));
    }

    [Fact]
    public async Task Login_UnknownNameIdleFor31Days_CountsFromZero_FiveGuessesThenTheLock()
    {
        var ghost = "ghost-order-" + Guid.NewGuid().ToString("N")[..8];
        await AnswerAsync(ghost, "a");
        await AnswerAsync(ghost, "b");
        var idle = TimeSpan.FromDays(31);
        fx.Clock.Advance(idle);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal("401/invalid_credentials/RA=none", await AnswerAsync(ghost, "c" + i));
            }

            Assert.Equal("429/locked_out/RA=set", await AnswerAsync(ghost, "c5"));
        }
        finally
        {
            fx.Clock.Advance(-idle);
        }
    }
}

/// <summary>API-01 (final): the SQL a login runs is the same for a real and an unknown name in every state, and so is the time it takes.</summary>
public sealed class LoginSameWorkTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private async Task<(int Status, List<string> Sql)> RecordedAsync(string username, string password)
    {
        using var client = fx.NewClient();
        fx.Commands.Clear();
        fx.Commands.Recording = true;
        try
        {
            using var response = await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(username, password));
            return ((int)response.StatusCode, [.. fx.Commands.Commands]);
        }
        finally
        {
            fx.Commands.Recording = false;
        }
    }

    [Fact]
    public async Task Login_RealAndUnknownName_RunTheSameSqlCommands_InOpenCountingLockedAndExpiredState()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        var user = await fx.AddStaffAsync(Role.Staff);
        var ghost = "ghost-sql-" + Guid.NewGuid().ToString("N")[..8];
        var sequences = new List<string>();

        async Task StepAsync(string state, string password)
        {
            var (kStatus, known) = await RecordedAsync(user.Username, password);
            var (uStatus, unknown) = await RecordedAsync(ghost, password);
            Assert.True(known.Count >= 4, $"{state}: only {known.Count} commands were recorded; the interceptor is not seeing the login");
            Assert.True(kStatus == uStatus, $"{state}: status {kStatus} vs {uStatus}");
            Assert.True(known.SequenceEqual(unknown), $"{state}: the SQL differs.\nreal:\n{string.Join("\n--\n", known)}\nunknown:\n{string.Join("\n--\n", unknown)}");
            sequences.Add($"{state}: status {kStatus}, {known.Count} commands");
        }

        await StepAsync("open (first wrong guess)", "w1");
        await StepAsync("counting (second wrong guess)", "w2");
        await StepAsync("counting (third wrong guess)", "w3");
        await StepAsync("counting (fourth wrong guess)", "w4");
        await StepAsync("locking (fifth wrong guess)", "w5");
        await StepAsync("locked", "w6");
        await StepAsync("locked (right password)", ApiFixture.Password);

        fx.Clock.Advance(TimeSpan.FromMinutes(16));
        try
        {
            await StepAsync("lock ended (counting again)", "w7");
        }
        finally
        {
            fx.Clock.Advance(TimeSpan.FromMinutes(-16));
        }

        // Expired partial count: two guesses, then 30 days and an hour of silence.
        var user2 = await fx.AddStaffAsync(Role.Staff);
        var ghost2 = "ghost-sql2-" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 2; i++)
        {
            await RecordedAsync(user2.Username, "e" + i);
            await RecordedAsync(ghost2, "e" + i);
        }

        var idle = TimeSpan.FromDays(30) + TimeSpan.FromHours(1);
        fx.Clock.Advance(idle);
        try
        {
            var (kStatus, known) = await RecordedAsync(user2.Username, "e-after");
            var (uStatus, unknown) = await RecordedAsync(ghost2, "e-after");
            Assert.Equal(kStatus, uStatus);
            Assert.True(known.SequenceEqual(unknown), $"expired: the SQL differs.\nreal:\n{string.Join("\n--\n", known)}\nunknown:\n{string.Join("\n--\n", unknown)}");
            sequences.Add($"expired: status {kStatus}, {known.Count} commands");
        }
        finally
        {
            fx.Clock.Advance(-idle);
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "lifestream-login-sql-sequence.txt"), sequences);
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    [Fact]
    [Trait("Category", "Perf")] // a timing comparison: run in the perf pass, not in the loaded default run
    public async Task Login_LockedRealAndLockedUnknownName_TakeTheSameTime()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        var ghost = "ghost-time-" + Guid.NewGuid().ToString("N")[..8];
        using var client = fx.NewClient();
        for (var i = 0; i < 6; i++)
        {
            (await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(user.Username, "w" + i))).Dispose();
            (await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(ghost, "w" + i))).Dispose();
        }

        async Task<double> TimeAsync(string name)
        {
            var watch = Stopwatch.StartNew();
            (await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(name, "w-locked"))).Dispose();
            return watch.Elapsed.TotalMilliseconds;
        }

        for (var i = 0; i < 20; i++)
        {
            await TimeAsync(user.Username);
            await TimeAsync(ghost);
        }

        var known = new List<double>();
        var unknown = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            known.Add(await TimeAsync(user.Username));
            unknown.Add(await TimeAsync(ghost));
        }

        var (mk, mu) = (Median(known), Median(unknown));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "lifestream-login-locked-timing.txt"), $"locked real median {mk:F2} ms, locked unknown median {mu:F2} ms (n=200 each, interleaved)");
        // The verifier saw 0.59 ms against 1.85 ms (a factor of three): the medians must now be within 25% of each other.
        Assert.InRange(Math.Max(mk, mu) / Math.Min(mk, mu), 1.0, 1.25);
    }
}
