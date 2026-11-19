using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Api.Tests;

/// <summary>
/// API-01 (security review): concurrent wrong logins under spellings the name column's collation treats as one name must be serialised by the
/// database (key-range locking on the row), never by a lock name made from a checksum. Two equal spellings that took different locks both
/// inserted a counter row and one hit the unique key (a 500: a difference a real account never shows). All tests run against a real SQL Server.
/// </summary>
public sealed class LoginCollationLockTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Rounds = 6; // a race is a matter of timing: each round uses fresh names
    private const int Groups = 8;
    private const int AttemptsPerGroup = 4; // fewer than the five that lock a name, so every attempt must answer 401 and be counted
    private const int NamesPerAttempt = 2;

    /// <summary>Pairs of spellings of one name that the collation (Latin1_General_100_CI_AS) treats as equal, each built on a fresh stem.</summary>
    private static readonly Func<string, (string A, string B)>[] Pairs =
    [
        s => (s, s.ToUpperInvariant()),                                     // case
        s => (s + "pq", s + "ｐｑ"),                                        // fullwidth letters
        s => (s + "ab", s + "a­b"),                                    // soft hyphen
        s => (s + "strasse", s + "straße"),                            // ss and the sharp s
        s => (s, s + "\0"),                                                 // trailing NUL
        s => (s + "xy", s.ToUpperInvariant() + "ｘｙ"),                    // case and fullwidth together
        s => (s + "ss", s.ToUpperInvariant() + "ß"),                   // case and the sharp s
        s => (s + "q", s + "q\0"),                                          // trailing NUL after a letter
    ];

    private async Task<int> CountRowsAsync(string spelling) =>
        await ScalarAsync("SELECT COUNT(*) FROM UnknownLogin WHERE Name = @n", spelling);

    private async Task<int> FailedCountOfAsync(string spelling) =>
        await ScalarAsync("SELECT COALESCE(MAX(FailedCount), -1) FROM UnknownLogin WHERE Name = @n", spelling);

    private async Task<int> ScalarAsync(string sql, string name)
    {
        await using var connection = new SqlConnection(fx.Db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@n", System.Data.SqlDbType.NVarChar, 100) { Value = name });
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Login_32ConcurrentWrongLoginsOverPairsOfCollationEqualSpellings_NeverAnswer5xxOr409_AndEveryAttemptIsCounted()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        for (var round = 0; round < Rounds; round++)
        {
            await RunRoundAsync();
        }
    }

    private async Task RunRoundAsync()
    {
        var stem = "ghost-coll-" + Guid.NewGuid().ToString("N")[..8];
        var groups = Pairs.Select(p => p(stem + Random.Shared.Next(100, 999))).ToArray();
        Assert.Equal(Groups, groups.Length);

        // 32 requests: every group gets 4 wrong guesses, alternating between its two spellings, all released at once.
        var requests = new List<(int Group, string Name)>();
        for (var attempt = 0; attempt < AttemptsPerGroup; attempt++)
        {
            for (var g = 0; g < Groups; g++)
            {
                requests.Add((g, attempt % NamesPerAttempt == 0 ? groups[g].A : groups[g].B));
            }
        }

        Assert.Equal(32, requests.Count);
        using var gate = new ManualResetEventSlim(false);
        var clients = requests.Select(_ => fx.NewClient()).ToList();
        var tasks = requests.Select((r, i) => Task.Run(async () =>
        {
            gate.Wait(Ct);
            using var response = await clients[i].PostApi(ApiRoutes.AuthLogin, new LoginRequest(r.Name, "wrong-" + i));
            return (r.Group, Status: (int)response.StatusCode);
        }, Ct)).ToList();
        await Task.Delay(500, Ct); // every request is waiting at the gate
        gate.Set();
        var results = await Task.WhenAll(tasks);

        var bad = results.Where(r => r.Status != 401).Select(r => $"group {r.Group}: {r.Status}").ToList();
        Assert.True(bad.Count == 0, "expected only 401 (no 5xx, no 409, no early 429): " + string.Join(", ", bad));

        for (var g = 0; g < Groups; g++)
        {
            Assert.Equal(1, await CountRowsAsync(groups[g].A));
            Assert.Equal(1, await CountRowsAsync(groups[g].B));
            Assert.Equal(AttemptsPerGroup, await FailedCountOfAsync(groups[g].A));
            Assert.Equal(AttemptsPerGroup, await FailedCountOfAsync(groups[g].B));
        }
    }
    private static async Task<int> StatusAsync(HttpClient client, string name, string password)
    {
        using var response = await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(name, password));
        return (int)response.StatusCode;
    }

    [Fact]
    public async Task Login_EqualSpellingOfAHeldName_WaitsForTheDatabasesKeyRangeLock_NotForALockNamedAfterTheName()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        var stem = "ghost-range-" + Guid.NewGuid().ToString("N")[..8];
        var (a, b) = ("ghost-range-" + stem[^8..] + "ab", "GHOST-RANGE-" + stem[^8..] + "a­b"); // equal under the collation, not byte for byte

        await using var connection = new SqlConnection(fx.Db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Ct);
        await using (var hold = new SqlCommand("SELECT FailedCount FROM UnknownLogin WITH (UPDLOCK, HOLDLOCK) WHERE Name = @n", connection, transaction))
        {
            hold.Parameters.Add(new SqlParameter("@n", System.Data.SqlDbType.NVarChar, 100) { Value = a });
            Assert.Null(await hold.ExecuteScalarAsync(Ct)); // no row yet: the lock is on the gap the name would be inserted into
        }

        using var client = fx.NewClient();
        var blocked = StatusAsync(client, b, "w");
        await using var observer = new SqlConnection(fx.Db.ConnectionString);
        await observer.OpenAsync(Ct);
        string? waiting = null;
        for (var i = 0; i < 100 && waiting is null; i++)
        {
            await Task.Delay(50, Ct);
            await using var probe = new SqlCommand(
                "SELECT TOP (1) request_mode FROM sys.dm_tran_locks WHERE resource_database_id = DB_ID() AND resource_type = 'KEY' AND request_status = 'WAIT'", observer);
            waiting = (string?)await probe.ExecuteScalarAsync(Ct);
        }

        Assert.False(blocked.IsCompleted, "the login for an equal spelling should be waiting");
        Assert.Equal("RangeS-U", waiting); // it waits at the unknown-name read (UPDLOCK + HOLDLOCK), before it writes anything
        await transaction.CommitAsync(Ct);
        Assert.Equal(401, await blocked);
        Assert.Equal(1, await CountRowsAsync(a));
        Assert.Equal(1, await FailedCountOfAsync(b));
    }

    [Fact]
    public async Task Login_RealAndUnknownName_SendNoChecksumQuery_AndReadTheNameRowWithUpdlockAndHoldlock()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        var user = await fx.AddStaffAsync(Lifestream.Domain.Role.Staff);
        var ghost = "ghost-shape-" + Guid.NewGuid().ToString("N")[..8];
        foreach (var name in new[] { user.Username, ghost })
        {
            using var client = fx.NewClient();
            fx.Commands.Clear();
            fx.Commands.Recording = true;
            try
            {
                Assert.Equal(401, await StatusAsync(client, name, "w"));
            }
            finally
            {
                fx.Commands.Recording = false;
            }

            var sql = fx.Commands.Commands;
            Assert.DoesNotContain(sql, c => c.Contains("CHECKSUM", StringComparison.OrdinalIgnoreCase));
            Assert.Single(sql, c => c.Contains("UPDLOCK, HOLDLOCK", StringComparison.Ordinal) && c.Contains("FROM UnknownLogin", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Login_ADuplicateKeyOnTheFirstInsert_IsRetriedOnce_AndAnswersLikeARealAccount()
    {
        await using var fixture = new LoginDuplicateRetryFixture();
        await fixture.InitializeAsync();
        var ghost = "ghost-dup-" + Guid.NewGuid().ToString("N")[..8];
        using var client = fixture.NewClient();
        Assert.Equal(401, await StatusAsync(client, ghost, "w"));
        Assert.Equal(1, fixture.Duplicate.Thrown);

        await using var connection = new SqlConnection(fixture.Db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new SqlCommand("SELECT FailedCount FROM UnknownLogin WHERE Name = @n", connection);
        count.Parameters.Add(new SqlParameter("@n", System.Data.SqlDbType.NVarChar, 100) { Value = ghost });
        Assert.Equal(1, (int)(await count.ExecuteScalarAsync(Ct))!);
    }
}

/// <summary>Fails the first unknown-name write with a unique-key violation (error 2627), as if an equal spelling had got its row in first.</summary>
public sealed class DuplicateOnceInterceptor : DbCommandInterceptor
{
    private int _thrown;

    public int Thrown => Volatile.Read(ref _thrown);

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("INSERT INTO UnknownLogin", StringComparison.Ordinal) && Interlocked.CompareExchange(ref _thrown, 1, 0) == 0)
        {
            throw SqlExceptions.Create(2627);
        }

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

public sealed class LoginDuplicateRetryFixture : ApiFixture
{
    public DuplicateOnceInterceptor Duplicate { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services) => services.AddSingleton<IInterceptor>(Duplicate);
}
