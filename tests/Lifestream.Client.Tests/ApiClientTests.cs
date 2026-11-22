using System.Net;
using System.Net.Sockets;
using Lifestream.Client.Api;
using Lifestream.Client.Session;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Client.Tests;

public sealed class ApiClientTests
{
    private const string DonorJson = """{"id":7,"name":"Ayşe","dateOfBirth":"1990-01-02","gender":"Female","bloodType":"APos","phone":null,"address":null,"frequency":"Regular","lastDonated":null,"rowVersion":"AAA="}""";

    private static (LifestreamApiClient Api, ServerStatus Status, ManualTimeProvider Clock, SessionService Session) Build(ScriptedHandler inner, WakePolicy? basePolicy = null)
    {
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var policy = (basePolicy ?? new WakePolicy()) with { Clock = clock };
        var http = new HttpClient(new WakeAwareHandler(policy, status, inner)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        var session = new SessionService();
        return (new LifestreamApiClient(http, session), status, clock, session);
    }

    /// <summary>Advances the manual clock a second at a time (letting continuations run) until the task finishes.</summary>
    private static async Task<T> Drive<T>(Task<T> task, ManualTimeProvider clock, int maxSeconds = 400)
    {
        for (var i = 0; i < maxSeconds && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2);
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ApiClient_RetriesWakingResponse_WithSameIdempotencyKey()
    {
        var handler = new ScriptedHandler((_, n) => n <= 2
            ? ScriptedHandler.Problem(HttpStatusCode.ServiceUnavailable, ProblemCodes.DatabaseWaking)
            : ScriptedHandler.Json(HttpStatusCode.Created, DonorJson));
        var (api, _, clock, _) = Build(handler);
        var request = new CreateDonorRequest("Ayşe", new DateOnly(1990, 1, 2), Gender.Female, BloodType.APos, null, null, DonorFrequency.Regular);

        var result = await Drive(api.CreateDonorAsync(request), clock);

        Assert.True(result.IsOk);
        Assert.Equal(7, result.Value.Id);
        var sent = handler.Requests;
        Assert.Equal(3, sent.Count);
        var keys = sent.Select(r => r.Header(ApiHeaders.IdempotencyKey)).Distinct().ToList();
        Assert.Single(keys);
        Assert.False(string.IsNullOrEmpty(keys[0]));
        Assert.Single(sent.Select(r => r.Body).Distinct()); // the same input, not lost or changed
    }

    [Fact]
    public async Task ApiClient_TwoSeparateWrites_UseDifferentIdempotencyKeys()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.Created, DonorJson));
        var (api, _, _, _) = Build(handler);
        var request = new CreateDonorRequest("A", new DateOnly(1990, 1, 2), Gender.Male, BloodType.APos, null, null, DonorFrequency.Regular);

        await api.CreateDonorAsync(request);
        await api.CreateDonorAsync(request);

        Assert.Equal(2, handler.Requests.Select(r => r.Header(ApiHeaders.IdempotencyKey)).Distinct().Count());
    }

    [Fact]
    public async Task ApiClient_GivesUpAfter120s_WithWakingState()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Problem(HttpStatusCode.ServiceUnavailable, ProblemCodes.DatabaseWaking));
        var (api, status, clock, _) = Build(handler);
        var states = new List<(TimeSpan At, ServerState State)>();
        status.Changed += (_, _) => states.Add((clock.Elapsed, status.State));

        var task = api.GetDonorAsync(7);
        var result = await Drive(task, clock);

        Assert.False(result.IsOk);
        Assert.True(result.Problem!.IsWaking, result.Problem.Code);
        Assert.InRange(clock.Elapsed.TotalSeconds, 100, 125);
        Assert.Equal(ServerState.Waking, states[0].State);
        Assert.InRange(states[0].At.TotalSeconds, 3, 4); // the waking state appears after 3 seconds, not before
        Assert.Equal(ServerState.Online, status.State); // and is cleared when the call ends
        Assert.True(handler.Requests.Count > 5, "should have retried with backoff");
    }

    [Fact]
    public async Task ApiClient_BackoffGrowsBetweenAttempts()
    {
        var times = new List<TimeSpan>();
        ManualTimeProvider? clockRef = null;
        var handler = new ScriptedHandler((_, _) =>
        {
            times.Add(clockRef!.Elapsed);
            return ScriptedHandler.Problem(HttpStatusCode.ServiceUnavailable, ProblemCodes.DatabaseWaking);
        });
        var (api, _, clock, _) = Build(handler);
        clockRef = clock;

        await Drive(api.GetDonorAsync(1), clock);

        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        Assert.True(gaps[1] > gaps[0], "second wait should be longer than the first");
        Assert.True(gaps.Max() <= TimeSpan.FromSeconds(16));
    }

    [Fact]
    public async Task ApiClient_NoWakingState_ForAFastCall()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, DonorJson));
        var (api, status, _, _) = Build(handler);
        var changes = 0;
        status.Changed += (_, _) => changes++;

        var result = await api.GetDonorAsync(7);

        Assert.True(result.IsOk);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ApiClient_ConnectionFailure_IsRetriedThenReportedAsUnreachable()
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("no route"));
        var (api, _, clock, _) = Build(handler);

        var result = await Drive(api.GetDonorAsync(7), clock);

        Assert.False(result.IsOk);
        Assert.True(result.Problem!.IsUnreachable);
        Assert.True(handler.Requests.Count > 3);
    }

    [Fact]
    public async Task ApiClient_UserCancel_StopsRetryingAtOnce()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Problem(HttpStatusCode.ServiceUnavailable, ProblemCodes.DatabaseWaking));
        var (api, status, clock, _) = Build(handler);
        using var cts = new CancellationTokenSource();

        var task = api.GetDonorAsync(7, cts.Token);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(20);
        var attemptsBefore = handler.Requests.Count;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        clock.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(20);
        Assert.Equal(attemptsBefore, handler.Requests.Count);
        Assert.Equal(ServerState.Online, status.State);
    }

    [Fact]
    public async Task ApiClient_RestingDatabase_IsNotRetried()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Problem(HttpStatusCode.ServiceUnavailable, ProblemCodes.DatabaseResting));
        var (api, _, _, _) = Build(handler);

        var result = await api.GetDonorAsync(7);

        Assert.True(result.Problem!.IsResting);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ApiClient_ProblemDetails_AreParsedWithCodeAndCorrelationId()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.Conflict, """{"title":"Changed by someone else","status":409,"code":"concurrency","correlationId":"abc"}""", (ApiHeaders.CorrelationId, "hdr-1")));
        var (api, _, _, _) = Build(handler);

        var result = await api.UpdateDonorAsync(7, new UpdateDonorRequest("AAA="));

        Assert.False(result.IsOk);
        Assert.Equal(ProblemCodes.Concurrency, result.Problem!.Code);
        Assert.Equal("hdr-1", result.Problem.CorrelationId);
        Assert.Equal(409, result.Problem.Status);
    }

    [Fact]
    public async Task ApiClient_DemoResetHeader_RaisesDemoResetObserved()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, DonorJson, (ApiHeaders.DemoReset, "1")));
        var (api, status, _, _) = Build(handler);
        var raised = 0;
        status.DemoResetObserved += (_, _) => raised++;

        await api.GetDonorAsync(7);

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// The server repeats X-Demo-Reset on every answer of a session that began before the reset, until the session is renewed. The app acts
    /// on each reset once: before 2.1.9 every answer raised it again, and the shell's reload of the screen at each one never let the dashboard finish.
    /// </summary>
    [Fact]
    public async Task ApiClient_DemoResetHeader_RepeatedForTheSameReset_RaisesItOnce_ANewResetRaisesItAgain()
    {
        var reset = "2026-11-22T02:40:11.0000000Z";
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, DonorJson, (ApiHeaders.DemoReset, reset)));
        var (api, status, _, _) = Build(handler);
        var raised = 0;
        status.DemoResetObserved += (_, _) => raised++;

        await api.GetDonorAsync(7);
        await api.GetDonorAsync(7);
        await api.GetDonorAsync(7);
        Assert.Equal(1, raised);

        reset = "2026-11-23T00:00:04.0000000Z";
        await api.GetDonorAsync(7);
        await api.GetDonorAsync(7);
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task ApiClient_SendsBearerToken_FromTheSession_AndNoneWhenSignedOut()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, DonorJson));
        var (api, _, _, session) = Build(handler);
        session.Establish(FakeApiClient.Tokens());

        await api.GetDonorAsync(1);
        session.Clear();
        await api.GetDonorAsync(1);

        Assert.Equal("Bearer access-token-1", handler.Requests[0].Header("Authorization"));
        Assert.Null(handler.Requests[1].Header("Authorization")); // a later call is anonymous
    }

    [Fact]
    public async Task ApiClient_Unauthorized_RefreshesOnce_AndRetriesWithTheNewToken()
    {
        var handler = new ScriptedHandler((r, _) =>
        {
            if (r.Uri.Contains("auth/refresh", StringComparison.Ordinal))
            {
                return ScriptedHandler.Json(HttpStatusCode.OK, """{"accessToken":"new-access","refreshToken":"new-refresh","accessTokenExpiresUtc":"2030-01-01T00:00:00Z","user":{"staffId":1,"displayName":"T","role":"Staff","isDemo":false,"canManageStaff":false}}""");
            }

            return r.Header("Authorization") == "Bearer new-access"
                ? ScriptedHandler.Json(HttpStatusCode.OK, DonorJson)
                : ScriptedHandler.Problem(HttpStatusCode.Unauthorized, "unauthorized");
        });
        var (api, _, _, session) = Build(handler);
        session.Establish(FakeApiClient.Tokens());

        var result = await api.GetDonorAsync(1);

        Assert.True(result.IsOk);
        Assert.Equal("new-access", session.AccessToken);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ApiClient_RefreshRefused_ExpiresTheSession_WithoutThrowing()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Problem(HttpStatusCode.Unauthorized, "unauthorized"));
        var (api, _, _, session) = Build(handler);
        session.Establish(FakeApiClient.Tokens());
        var expired = 0;
        session.Expired += (_, _) => expired++;

        var result = await api.GetDonorAsync(1);

        Assert.Equal(ClientProblemCodes.SessionExpired, result.Problem!.Code);
        Assert.Equal(1, expired);
    }

    [Fact]
    public async Task ApiClient_BuildsQueryStrings_WithoutEmptyValues()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, """{"items":[],"total":0,"page":1,"pageSize":50}"""));
        var (api, _, _, _) = Build(handler);

        await api.GetDonorsAsync(new DonorQuery(Search: "ayşe öz", BloodType: BloodType.ONeg, Page: 2));

        Assert.Equal("https://api.test/api/v1/donors?search=ay%C5%9Fe%20%C3%B6z&bloodType=ONeg&page=2&pageSize=50", handler.Requests[0].Uri);
    }

    [Fact]
    public void ApiClient_SharesOneHttpClient()
    {
        using var rig = new Rig();
        // Resolve the real registrations: the Rig replaced only the client; the HttpClient registration is the app's own.
        var first = rig.Provider.GetRequiredService<HttpClient>();
        var second = rig.Provider.GetRequiredService<HttpClient>();
        Assert.Same(first, second);

        SocketsHttpHandler? captured = null;
        using var created = ApiHttpClientFactory.Create(new Uri("https://api.test/"), new WakePolicy(), new ServerStatus(), h => captured = h);
        Assert.False(captured!.EnableMultipleHttp2Connections);
        Assert.Equal(HttpVersion.Version20, created.DefaultRequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrHigher, created.DefaultVersionPolicy);
    }

    [Fact]
    public async Task ApiClient_RequestsCarryTheConfiguredHttp2Version_ThroughTheRetryHandler()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, DonorJson));
        var clock = new ManualTimeProvider();
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Clock = clock }, new ServerStatus(), handler))
        {
            BaseAddress = new Uri("https://api.test/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        using var api = new LifestreamApiClient(http, new SessionService());

        await api.GetDonorAsync(1);

        Assert.Equal(HttpVersion.Version20, handler.Requests[0].Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrHigher, handler.Requests[0].VersionPolicy);
    }

    [Fact]
    public async Task ApiClient_TwentyConcurrentCalls_UseOneHttp2Connection()
    {
        // A real loopback Kestrel server speaking HTTP/2 (cleartext, prior knowledge) records the connection id of every request.
        var connectionIds = new System.Collections.Concurrent.ConcurrentBag<string>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http2));
        await using var app = builder.Build();
        app.MapGet("/api/v1/donors/{id:int}", (int id, HttpContext c) =>
        {
            _ = id;
            connectionIds.Add(c.Connection.Id);
            return Results.Text(DonorJson, "application/json");
        });
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;

        using var http = ApiHttpClientFactory.Create(new Uri($"http://127.0.0.1:{port}/"), new WakePolicy(), new ServerStatus());
        http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact; // cleartext HTTP/2 has no ALPN
        using var api = new LifestreamApiClient(http, new SessionService());

        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => api.GetDonorAsync(i)));

        Assert.All(results, r => Assert.True(r.IsOk, r.Problem?.Title + " " + r.Problem?.Detail));
        Assert.Equal(20, connectionIds.Count);
        Assert.Single(connectionIds.Distinct());
        await app.StopAsync();
    }

    [Fact]
    public async Task ApiClient_Unreachable_NeverThrows_ForAClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens now
        using var http = ApiHttpClientFactory.Create(new Uri($"http://127.0.0.1:{port}/"), new WakePolicy { Budget = TimeSpan.FromMilliseconds(300), FirstBackoff = TimeSpan.FromMilliseconds(50), MaxBackoff = TimeSpan.FromMilliseconds(100) }, new ServerStatus());
        using var api = new LifestreamApiClient(http, new SessionService());

        var result = await api.GetDonorAsync(1);

        Assert.True(result.Problem!.IsUnreachable);
    }
}
