using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Lifestream.Api.Auth;
using Lifestream.Api.Tests.Data;
using Lifestream.Contracts;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lifestream.Api.Tests.Support;

/// <summary>A clock the tests can move. Starts at the real time so it agrees with the database's own clock defaults.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Raised after the clock moved (a fixture uses it to keep the once-a-day demo reset from firing because a test crossed midnight).</summary>
    public event Action? Changed;

    public void Advance(TimeSpan by)
    {
        _now += by;
        Changed?.Invoke();
    }

    public void Set(DateTime utc)
    {
        _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        Changed?.Invoke();
    }

    public DateOnly Today => DateOnly.FromDateTime(_now.UtcDateTime);
}

/// <summary>Records every SQL command the context runs while <see cref="Recording"/> is true.</summary>
public sealed class CommandLog : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    public bool Recording { get; set; }

    public IReadOnlyList<string> Commands => [.. _commands];

    public void Clear() => _commands.Clear();

    private void Add(CommandEventData data, System.Data.Common.DbCommand command)
    {
        if (Recording && data.Context is not null)
        {
            _commands.Enqueue(command.CommandText);
        }
    }

    public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Add(eventData, command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Add(eventData, command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>
/// Records every query the API sends (text and a copy of each parameter) while <see cref="Recording"/> is true, so a test can replay
/// the exact statements under SET STATISTICS XML and read the actual execution plans.
/// </summary>
public sealed class StatementCapture : DbCommandInterceptor
{
    private readonly ConcurrentQueue<(string Sql, List<SqlParameter> Parameters)> _statements = new();

    public bool Recording { get; set; }

    /// <summary>Returns what was recorded since the last call and clears it.</summary>
    public List<(string Sql, List<SqlParameter> Parameters)> Drain()
    {
        var list = new List<(string, List<SqlParameter>)>();
        while (_statements.TryDequeue(out var item))
        {
            list.Add(item);
        }

        return list;
    }

    // Recorded after execution, so the text is what was really sent (interceptors that run later, such as the search-plan one, have
    // already rewritten it).
    public override ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(System.Data.Common.DbCommand command, CommandExecutedEventData eventData, System.Data.Common.DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (Recording)
        {
            _statements.Enqueue((command.CommandText, [.. command.Parameters.Cast<SqlParameter>().Select(p => (SqlParameter)((ICloneable)p).Clone())]));
        }

        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>
/// Fault injection (DB-10): while <see cref="FailWhenSqlContains"/> is set, the first command whose text contains it throws, as if the
/// database had failed at exactly that step of a multi-step write.
/// </summary>
public sealed class FaultInjector : DbCommandInterceptor
{
    public string? FailWhenSqlContains { get; set; }

    /// <summary>How many commands were failed on purpose.</summary>
    public int Injected { get; private set; }

    private void Check(System.Data.Common.DbCommand command)
    {
        if (FailWhenSqlContains is { } marker && command.CommandText.Contains(marker, StringComparison.Ordinal))
        {
            Injected++;
            throw new InvalidOperationException("injected fault: " + marker);
        }
    }

    public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
    {
        Check(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Check(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>Counts connections opened and can make opening fail the way a resuming serverless database does (error 40613).</summary>
public sealed class ConnectionProbe : DbConnectionInterceptor
{
    private int _opened;

    /// <summary>How many more opens fail: 0 none, -1 all of them.</summary>
    public int FailuresRemaining { get; set; }

    public int Opened => Volatile.Read(ref _opened);

    /// <summary>The SQL error number simulated while failing (40613 resuming, -2 connect timeout, 42119 free allowance used up).</summary>
    public int FailNumber { get; set; } = 40613;

    /// <summary>When set, the failure is this exception (for an error with its real text and severity, or with several errors).</summary>
    public Func<SqlException>? FailWith { get; set; }

    /// <summary>When set, opening throws this non-SQL exception instead (an unexpected failure).</summary>
    public bool ThrowGeneric { get; set; }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(System.Data.Common.DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _opened);
        if (ThrowGeneric)
        {
            throw new InvalidOperationException("secret-detail: SELECT * FROM [Donor] violated FK_Secret on server prod-sql-01");
        }

        if (FailuresRemaining != 0)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
            }

            throw FailWith?.Invoke() ?? SqlExceptions.Create(FailNumber);
        }

        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }
}

public static class SqlExceptions
{
    /// <summary>Builds a real SqlException with the given error number (the type has no public constructor).</summary>
    public static SqlException Create(int number) => Create([number]);

    /// <summary>A SqlException that carries several errors, in this order (<see cref="SqlException.Number"/> is the first one's).</summary>
    public static SqlException Create(int[] numbers, string message = "simulated", byte severity = 20)
    {
        var errorType = typeof(SqlError);
        var errorCtor = errorType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).OrderByDescending(c => c.GetParameters().Length).First();
        object?[] args(int number) => errorCtor.GetParameters().Select(p => p.Name switch
        {
            "infoNumber" => (object?)number,
            "errorState" => (byte)0,
            "errorClass" => severity,
            "server" => "test",
            "errorMessage" => message,
            "procedure" => "",
            "lineNumber" => 0,
            "win32ErrorCode" => 0,
            _ => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
        }).ToArray();
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        foreach (var number in numbers)
        {
            typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(collection, [errorCtor.Invoke(args(number))]);
        }

        var create = typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.Name == "CreateException").OrderBy(m => m.GetParameters().Length).First();
        var createArgs = create.GetParameters().Select(p =>
            p.ParameterType == typeof(SqlErrorCollection) ? collection
            : p.ParameterType == typeof(string) ? "12.0.0"
            : p.ParameterType == typeof(Guid) ? Guid.NewGuid()
            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        return (SqlException)create.Invoke(null, createArgs)!;
    }
}

/// <summary>Replaces the readiness probe so tests can simulate a database that is resuming or paused.</summary>
public sealed class FakeProbe : IDatabaseProbe
{
    public int? FailWith { get; set; }

    public Task CheckAsync(CancellationToken ct) => FailWith is { } number ? throw SqlExceptions.Create(number) : Task.CompletedTask;
}

/// <summary>Captures log lines (message plus structured state plus scopes) so tests can search them.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scopeText = new System.Text.StringBuilder();
            owner._scopes.ForEachScope(
                (scope, sb) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var p in pairs)
                        {
                            sb.Append(p.Key).Append('=').Append(p.Value).Append(' ');
                        }
                    }
                },
                scopeText);
            var structured = state is IEnumerable<KeyValuePair<string, object?>> kv ? string.Join(' ', kv.Select(p => $"{p.Key}={p.Value}")) : "";
            owner.Lines.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)} | {structured} | {scopeText} | {exception}");
        }
    }
}

/// <summary>TestServer has no network peer, so this sets one: the address in X-Test-Remote, or loopback (a trusted proxy) when absent.</summary>
public sealed class RemoteAddressFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue("X-Test-Remote", out var remote)
                ? System.Net.IPAddress.Parse(remote.ToString())
                : System.Net.IPAddress.Loopback;
            await nextMiddleware();
        });
        next(app);
    };
}

public sealed record TestUser(int Id, string Username, Role Role, HttpClient Client);

/// <summary>
/// One real SQL Server database (created by the EF migrations, dropped afterwards) plus the API hosted in memory on top of it.
/// Tests that need the 100x data use <see cref="ScaleApiFixture"/>.
/// </summary>
public class ApiFixture : IAsyncLifetime
{
    public const string Password = TestSecrets.Demo;

    private int _counter;
    private readonly List<HttpClient> _clients = [];

    public SqlServerDatabaseFixture Db { get; private set; } = null!;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public TestClock Clock { get; } = new();
    public CommandLog Commands { get; } = new();
    public StatementCapture Statements { get; } = new();
    public FaultInjector Faults { get; } = new();
    public ConnectionProbe Connections { get; } = new();
    public FakeProbe Probe { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Per-test-class configuration overrides applied before the host starts (set in a derived fixture or via <see cref="Configure"/>).</summary>
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Demo:Enabled"] = "true",
        ["Limits:GlobalPerMinute"] = "100000",
        ["Limits:LoginPerMinute"] = "100000",
        ["Limits:DemoPerMinute"] = "100000",
        ["Dashboard:CacheSeconds"] = "0",
        ["Database:MaxRetryDelaySeconds"] = "1",
        ["Auth:LockoutMinutes"] = "15",
        ["Logging:LogLevel:Default"] = "Warning",
    };

    protected virtual SqlServerDatabaseFixture CreateDb() => new();

    /// <summary>
    /// False (the default): the once-a-day demo reset can never fire during a test, whatever the wall clock is. The test clock starts at the real time
    /// and tests move it by minutes, so near midnight UTC the "first request of the day" would arrive in the middle of a test and wipe the staff it
    /// just added (404 on GET me). The marker of the last reset is therefore moved forward whenever the clock moves. A fixture for tests that ARE about the
    /// daily reset returns true: its clock starts at noon UTC (far from midnight) and the marker is left to the test.
    /// </summary>
    protected virtual bool DailyResetIsUnderTest => false;

    /// <summary>False leaves the real readiness probe in place (it opens a real connection).</summary>
    protected virtual bool UseFakeProbe => true;

    protected virtual void Configure(Dictionary<string, string?> settings)
    {
    }

    /// <summary>Extra services for one test class (for example an interceptor that makes a write fail).</summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    public TokenService Tokens => Factory.Services.GetRequiredService<TokenService>();

    public async ValueTask InitializeAsync()
    {
        Db = CreateDb();
        await Db.InitializeAsync();
        if (DailyResetIsUnderTest)
        {
            Clock.Set(DateTime.UtcNow.Date.AddHours(12).AddMilliseconds(500)); // not on a whole second: token issue times are whole seconds, and the reset header compares them with the reset time
        }

        Clock.Changed += KeepDailyResetAway;
        Configure(Settings);
        Settings["ConnectionStrings:Lifestream"] = Db.ConnectionString;
        Settings["Jwt:SigningKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));
            builder.ConfigureLogging(l => l.AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<IInterceptor>(Commands);
                services.AddSingleton<IInterceptor>(Statements);
                services.AddSingleton<IInterceptor>(Faults);
                services.AddSingleton<IInterceptor>(Connections);
                if (UseFakeProbe)
                {
                    services.AddSingleton<IDatabaseProbe>(Probe);
                }

                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, RemoteAddressFilter>();
                services.AddSingleton<ICredentialHasher>(SharedPbkdf2Work.Instance); // real PBKDF2, each password/hash pair verified once per run
                services.AddSingleton(SharedPbkdf2Work.Instance.Dummy); // one dummy hash for the run instead of one per host
                ConfigureTestServices(services);
            });
        });
        _ = Factory.Server;
    }

    /// <summary>Moves the last-reset marker up to the clock's day (never back), so the clock crossing midnight does not start a reset.</summary>
    private void KeepDailyResetAway()
    {
        if (DailyResetIsUnderTest)
        {
            return;
        }

        using var connection = new SqlConnection(Db.ConnectionString);
        connection.Open();
        using var command = new SqlCommand("UPDATE DemoState SET LastResetDate = @today WHERE LastResetDate < @today", connection);
        command.Parameters.Add(new SqlParameter("@today", System.Data.SqlDbType.Date) { Value = Clock.Today.ToDateTime(TimeOnly.MinValue) });
        command.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await Factory.DisposeAsync();
        await Db.DisposeAsync();
    }

    public HttpClient NewClient(string? bearer = null, string? ip = null)
    {
        var client = Factory.CreateClient();
        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (ip is not null)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        }

        _clients.Add(client);
        return client;
    }

    /// <summary>
    /// A second app host over the same database, with the same settings, signing key and clock but empty memory: what a scale-to-zero restart
    /// of the API looks like. Anything that must survive a restart has to be in the database for this host to know it.
    /// </summary>
    public WebApplicationFactory<Program> NewHostOverSameDatabase() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<IDatabaseProbe>(Probe);
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, RemoteAddressFilter>();
                services.AddSingleton<ICredentialHasher>(SharedPbkdf2Work.Instance); // a restarted host computes its own dummy hash, as a real start does
            });
        });

    public string NextName(string prefix) => $"{prefix}{Interlocked.Increment(ref _counter)}";

    /// <summary>Adds a staff row with a real hash of <see cref="Password"/> and returns a client signed in as them (token minted directly, session row stored).</summary>
    public async Task<TestUser> AddStaffAsync(Role role, string? username = null, Action<Staff>? change = null, string? ip = null)
    {
        username ??= NextName(role == Role.Admin ? "admin" : "staff");
        await MarkResetTodayAsync();
        await using var db = Db.NewContext();
        var staff = TestRows.Staff(username);
        staff.Role = role;
        staff.Designation = role == Role.Admin ? Designation.Admin : Designation.Doctor;
        staff.DisplayName = $"Test {username}";
        staff.Salary = 52000;
        change?.Invoke(staff);
        db.Staff.Add(staff);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new TestUser(staff.Id, username, role, NewClient(await SessionAsync(db, staff.Id, staff.DisplayName, role), ip));
    }

    /// <summary>The demo staff row plus today's reset marker (so the daily reset does not run), and a demo client via the real demo login.</summary>
    public async Task<TestUser> AddDemoAsync(string? ip = null)
    {
        await using var db = Db.NewContext();
        var demo = await db.Staff.FirstOrDefaultAsync(s => s.IsDemo, TestContext.Current.CancellationToken);
        if (demo is null)
        {
            demo = TestRows.Staff("demo");
            demo.IsDemo = true;
            demo.DisplayName = "Demo Staff";
            db.Staff.Add(demo);
        }

        if (!await db.DemoState.AnyAsync(TestContext.Current.CancellationToken))
        {
            db.DemoState.Add(new DemoState { LastResetDate = Clock.Today });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var anonymous = NewClient(ip: ip);
        var response = await anonymous.PostAsync(ApiRoutes.Path(ApiRoutes.AuthDemo), null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<Lifestream.Contracts.Auth.TokenResponse>(Json, TestContext.Current.CancellationToken);
        return new TestUser(demo.Id, "demo", Role.Staff, NewClient(token!.AccessToken, ip));
    }

    public async Task<string> SessionAsync(LifestreamDbContext db, int staffId, string displayName, Role role)
    {
        var family = Guid.NewGuid();
        db.RefreshTokens.Add(new RefreshToken
        {
            StaffId = staffId,
            FamilyId = family,
            TokenHash = TokenService.HashRefresh(TokenService.NewRefreshSecret()),
            CreatedAtUtc = Clock.GetUtcNow().UtcDateTime,
            ExpiresAtUtc = Clock.GetUtcNow().UtcDateTime.AddDays(7),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return Tokens.CreateAccess(staffId, displayName, role, false, family).Token;
    }

    public static JsonSerializerOptions Json { get; } = ContractJson.CreateOptions();

    /// <summary>Today's DemoState row so the once-per-day reset does not run (for tests that enable the demo but do not test the reset).</summary>
    public async Task MarkResetTodayAsync()
    {
        await using var db = Db.NewContext();
        if (!await db.DemoState.AnyAsync(TestContext.Current.CancellationToken))
        {
            db.DemoState.Add(new DemoState { LastResetDate = Clock.Today });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        KeepDailyResetAway();
    }
}

/// <summary>For the tests about the once-a-day demo reset itself: the clock starts at noon UTC and the reset marker belongs to the test.</summary>
public sealed class DemoResetApiFixture : ApiFixture
{
    protected override bool DailyResetIsUnderTest => true;
}

/// <summary>The same host over a database that holds the 100x dataset (27,000 receipts, 3,500 donors).</summary>
public sealed class ScaleApiFixture : ApiFixture
{
    protected override SqlServerDatabaseFixture CreateDb() => new ScaleDatabaseFixture();
}

/// <summary>The same host over a database that holds the 1x demo dataset (270 receipts, 300 appointments, 100 donors).</summary>
public sealed class DemoSizeApiFixture : ApiFixture
{
    protected override SqlServerDatabaseFixture CreateDb() => new DemoSizeDatabaseFixture();
}

public static class HttpExtensions
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Task<HttpResponseMessage> GetApi(this HttpClient client, string template, string query = "") =>
        client.GetAsync(ApiRoutes.Path(template) + query, Ct);

    public static Task<HttpResponseMessage> GetApi(this HttpClient client, string template, int id, string query = "") =>
        client.GetAsync(ApiRoutes.Path(template, id) + query, Ct);

    public static async Task<HttpResponseMessage> PostApi(this HttpClient client, string template, object? body = null, int? id = null, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, id is { } i ? ApiRoutes.Path(template, i) : ApiRoutes.Path(template))
        {
            Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: ApiFixture.Json),
        };
        if (key is not null)
        {
            request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        }

        return await client.SendAsync(request, Ct);
    }

    public static Task<HttpResponseMessage> PatchApi(this HttpClient client, string template, int id, object body) =>
        client.PatchAsync(ApiRoutes.Path(template, id), JsonContent.Create(body, body.GetType(), options: ApiFixture.Json), Ct);

    public static Task<HttpResponseMessage> PatchRaw(this HttpClient client, string template, int id, string json) =>
        client.PatchAsync(ApiRoutes.Path(template, id), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

    public static Task<HttpResponseMessage> PostRaw(this HttpClient client, string template, string json) =>
        client.PostAsync(ApiRoutes.Path(template), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

    public static Task<HttpResponseMessage> PatchMe(this HttpClient client, object body) =>
        client.PatchAsync(ApiRoutes.Path(ApiRoutes.Me), JsonContent.Create(body, body.GetType(), options: ApiFixture.Json), Ct);

    public static Task<HttpResponseMessage> PutBytes(this HttpClient client, string template, int? id, byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return client.PutAsync(id is { } i ? ApiRoutes.Path(template, i) : ApiRoutes.Path(template), content, Ct);
    }

    public static Task<HttpResponseMessage> PutApi(this HttpClient client, string template, object body, int? id = null) =>
        client.PutAsync(id is { } i ? ApiRoutes.Path(template, i) : ApiRoutes.Path(template), JsonContent.Create(body, body.GetType(), options: ApiFixture.Json), Ct);

    public static Task<HttpResponseMessage> DeleteApi(this HttpClient client, string template, int id) =>
        client.DeleteAsync(ApiRoutes.Path(template, id), Ct);

    public static async Task<T> Read<T>(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<T>(text, ApiFixture.Json)!;
    }

    public static async Task<JsonDocument> Problem(this HttpResponseMessage response, int expectedStatus, string? expectedCode = null)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True((int)response.StatusCode == expectedStatus, $"expected {expectedStatus} got {(int)response.StatusCode}: {text}");
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.ToString());
        var doc = JsonDocument.Parse(text);
        if (expectedCode is not null)
        {
            Assert.Equal(expectedCode, doc.RootElement.GetProperty("code").GetString());
        }

        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("correlationId").GetString()));
        return doc;
    }
}
