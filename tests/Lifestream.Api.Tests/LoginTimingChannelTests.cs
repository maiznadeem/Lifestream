using System.Diagnostics;
using Lifestream.Api.Auth;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Api.Tests;

/// <summary>Counts the PBKDF2 work the API does (hash and verify) while doing it for real.</summary>
public sealed class CountingHasher : ICredentialHasher
{
    private readonly Pbkdf2CredentialHasher _inner = new Pbkdf2CredentialHasher();
    private int _hashes;
    private int _verifies;

    public int Hashes => Volatile.Read(ref _hashes);

    public int Verifies => Volatile.Read(ref _verifies);

    public string Hash(string password)
    {
        Interlocked.Increment(ref _hashes);
        return _inner.Hash(password);
    }

    public bool Verify(string hash, string password)
    {
        Interlocked.Increment(ref _verifies);
        return _inner.Verify(hash, password);
    }
}

/// <summary>
/// API-01 (timing side channels the verifier measured, v-04d-api V19 and V20): the dummy hash for unknown names is computed at start, not by the
/// first unknown login; and every unknown name is serialised on its own key range, like a real account on its own lock. All tests here run against a real SQL Server.
/// </summary>
public sealed class LoginTimingChannelTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> LoginStatusAsync(HttpClient client, string username, string password)
    {
        using var response = await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(username, password));
        return (int)response.StatusCode;
    }

    /// <summary>A fresh host (empty memory, as after a scale-to-zero start) over the fixture's database, whose password hashing is counted.</summary>
    private (WebApplicationFactory<Program> Host, CountingHasher Counter) StartCountedHost()
    {
        var counter = new CountingHasher();
        var host = fx.NewHostOverSameDatabase().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ICredentialHasher>(counter)));
        _ = host.Server; // starts the host: the host builds its pipeline, which computes the dummy hash, before it accepts a request
        return (host, counter);
    }

    [Fact]
    public void FreshHost_ComputesTheDummyHashAtStartup_BeforeAnyRequest()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        var (host, counter) = StartCountedHost();
        using (host)
        {
            Assert.Equal(1, counter.Hashes);
            Assert.Equal(0, counter.Verifies);
        }
    }

    [Fact]
    public async Task FreshHost_FirstUnknownNameLogin_DoesTheSamePbkdf2WorkAsFirstWrongPasswordLoginOfARealAccount()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        var user = await fx.AddStaffAsync(Role.Staff);
        var ghost = "ghost-cold-" + Guid.NewGuid().ToString("N")[..8];

        var (unknownHost, unknownCounter) = StartCountedHost();
        using (unknownHost)
        {
            var (hashesAtReady, verifiesAtReady) = (unknownCounter.Hashes, unknownCounter.Verifies);
            using var client = unknownHost.CreateClient();
            Assert.Equal(401, await LoginStatusAsync(client, ghost, "wrong-1"));
            Assert.Equal((0, 1), (unknownCounter.Hashes - hashesAtReady, unknownCounter.Verifies - verifiesAtReady));
        }

        var (realHost, realCounter) = StartCountedHost();
        using (realHost)
        {
            var (hashesAtReady, verifiesAtReady) = (realCounter.Hashes, realCounter.Verifies);
            using var client = realHost.CreateClient();
            Assert.Equal(401, await LoginStatusAsync(client, user.Username, "wrong-1"));
            Assert.Equal((0, 1), (realCounter.Hashes - hashesAtReady, realCounter.Verifies - verifiesAtReady));
        }
    }

    /// <summary>Holds the key-range lock (as the login's own unknown-name read takes it) on a name, in a transaction of its own, until disposed.</summary>
    private async Task<IAsyncDisposable> HoldNameAsync(string name)
    {
        var connection = new SqlConnection(fx.Db.ConnectionString);
        await connection.OpenAsync(Ct);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Ct);
        await using (var take = new SqlCommand("SELECT FailedCount FROM UnknownLogin WITH (UPDLOCK, HOLDLOCK) WHERE Name = @name", connection, transaction))
        {
            take.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 100) { Value = name });
            await take.ExecuteScalarAsync(Ct);
        }

        return new Held(connection, transaction);
    }

    private sealed class Held(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.CommitAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Login_TheNameRowHeldByAnotherTransaction_BlocksEveryEqualSpellingOfThatName_ButNotAnotherName()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        // Three names with rows already, in order (aaa < mmm < zzz), so the key range of one name is not the range of another.
        var id = Guid.NewGuid().ToString("N")[..8];
        var held = "aaa-held-" + id;
        var other = "zzz-free-" + id;
        foreach (var name in new[] { held, "mmm-fence-" + id, other })
        {
            Assert.Equal(401, await LoginStatusAsync(fx.NewClient(), name, "seed"));
        }

        var holder = await HoldNameAsync(held);
        try
        {
            using var client = fx.NewClient();
            var blocked = LoginStatusAsync(client, held.ToUpperInvariant(), "w");
            Assert.Equal(401, await LoginStatusAsync(client, other, "w")); // another unknown name is not held up by it
            await Task.Delay(1500, Ct);
            Assert.False(blocked.IsCompleted, "a login for an equal spelling of the held name should wait for its key-range lock");
            await holder.DisposeAsync();
            holder = null!;
            Assert.Equal(401, await blocked);
        }
        finally
        {
            if (holder is not null)
            {
                await holder.DisposeAsync();
            }
        }
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    [Fact]
    [Trait("Category", "Perf")] // a timing comparison: run in the perf pass, not in the loaded default run
    public async Task Login_LockedRealAndLockedUnknownProbes_TakeTheSameTime_While16ConcurrentRequestsFloodLockedNames()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        const int Flooders = 16;
        var realProbe = await fx.AddStaffAsync(Role.Staff);
        var ghostProbe = "ghost-probe-" + Guid.NewGuid().ToString("N")[..8];
        var floodReal = new List<string>();
        for (var i = 0; i < Flooders / 2; i++)
        {
            floodReal.Add((await fx.AddStaffAsync(Role.Staff)).Username);
        }

        var floodGhosts = Enumerable.Range(0, Flooders / 2).Select(_ => "ghost-flood-" + Guid.NewGuid().ToString("N")[..8]).ToList();
        string[] everyone = [realProbe.Username, ghostProbe, .. floodReal, .. floodGhosts];
        using var setup = fx.NewClient();
        foreach (var name in everyone)
        {
            for (var i = 0; i < 6; i++)
            {
                await LoginStatusAsync(setup, name, "w" + i); // locks every name (five guesses, then the lock)
            }
        }

        var targets = floodReal.Concat(floodGhosts).ToList();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var floods = targets.Select(name => Task.Run(async () =>
        {
            using var client = fx.NewClient();
            while (!stop.IsCancellationRequested)
            {
                await LoginStatusAsync(client, name, "w-flood");
            }
        }, Ct)).ToList();

        var known = new List<double>();
        var unknown = new List<double>();
        try
        {
            using var probe = fx.NewClient();

            async Task<double> TimeAsync(string name)
            {
                var watch = Stopwatch.StartNew();
                Assert.Equal(429, await LoginStatusAsync(probe, name, "w-probe"));
                return watch.Elapsed.TotalMilliseconds;
            }

            await Task.Delay(500, Ct);
            for (var i = 0; i < 20; i++)
            {
                await TimeAsync(realProbe.Username);
                await TimeAsync(ghostProbe);
            }

            for (var i = 0; i < 100; i++)
            {
                known.Add(await TimeAsync(realProbe.Username));
                unknown.Add(await TimeAsync(ghostProbe));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(floods.Select(async t =>
            {
                try
                {
                    await t;
                }
                catch (OperationCanceledException)
                {
                }
            }));
        }

        var (mk, mu) = (Median(known), Median(unknown));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "lifestream-login-flood-timing.txt"), $"flood of {Flooders} concurrent: locked real probe median {mk:F2} ms, locked unknown probe median {mu:F2} ms (n=100 each, interleaved)");
        // The verifier saw 1.63 ms against 14.46 ms at 16 concurrent (a factor of nine, one lock shared by every unknown name).
        Assert.InRange(Math.Max(mk, mu) / Math.Min(mk, mu), 1.0, 1.5);
    }
}
