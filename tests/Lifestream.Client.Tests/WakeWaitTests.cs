using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;

namespace Lifestream.Client.Tests;

/// <summary>
/// Z3 of the cold-start work, client side. Up to 2.1.2 an attempt was abandoned after 20 s and sent again, which cancelled the work the
/// server was doing for it and started that work from the beginning (five requests in the session measured on 2026-11-20). A request to a
/// waking server is now left alone until it is answered, the user cancels, or the 120 s budget ends. Through the real handler and client;
/// only the network and the clock are stand-ins.
/// </summary>
public sealed class WakeWaitTests
{
    private const string DonorJson = """{"id":7,"name":"A","dateOfBirth":"1990-01-02","gender":"Female","bloodType":"APos","phone":null,"address":null,"frequency":"Regular","lastDonated":null,"rowVersion":"AAA="}""";

    private static (LifestreamApiClient Api, ServerStatus Status, ManualTimeProvider Clock) Build(HttpMessageHandler server)
    {
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Clock = clock }, status, server)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        return (new LifestreamApiClient(http, new SessionService()), status, clock);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2500 && !condition(); i++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "the awaited state was not reached");
    }

    /// <summary>Moves the clock a second at a time, letting continuations run, as a waiting user's minute passes.</summary>
    private static async Task PassAsync(ManualTimeProvider clock, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task ARequestTheServerIsStillWorkingOn_IsNotGivenUp_AndNotSentAgain()
    {
        var server = new HoldingServer();
        var (api, status, clock) = Build(server);

        var call = api.GetDonorAsync(7);
        await WaitForAsync(() => server.Arrived == 1);

        // 95 s: the longest wake measured (container 27 s, database 58 s, first use 10 s). 2.1.2 gave up at 20, 40, 60 and 80 s.
        await PassAsync(clock, 95);
        Assert.Equal(1, server.Arrived);
        Assert.Equal(0, server.Cancelled);
        Assert.Equal(ServerState.Waking, status.State);
        Assert.False(call.IsCompleted);

        server.Answer(0, ScriptedHandler.Json(HttpStatusCode.OK, DonorJson));
        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsOk);
        Assert.Equal(7, result.Value.Id);
        Assert.Equal(1, server.Arrived);
        Assert.Equal(0, server.Cancelled);
        Assert.Equal(ServerState.Online, status.State);
    }

    [Fact]
    public async Task UserCancel_AMinuteIntoOneAttempt_CancelsTheRequestItself_AtOnce()
    {
        var server = new HoldingServer();
        var (api, status, clock) = Build(server);
        using var user = new CancellationTokenSource();

        var call = api.GetDonorAsync(7, user.Token);
        await WaitForAsync(() => server.Arrived == 1);
        await PassAsync(clock, 60);
        Assert.Equal(0, server.Cancelled);

        await user.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, server.Cancelled); // the HTTP request, not only the wait for it
        Assert.Equal(ServerState.Online, status.State);
        await PassAsync(clock, 30);
        Assert.Equal(1, server.Arrived); // and nothing is sent after a cancel
    }

    [Fact]
    public async Task TheBudget_IsTheOnlyTimeLimit_AnAttemptStillUnansweredAfter120s_IsReportedAsNoAnswer()
    {
        var server = new HoldingServer();
        var (api, status, clock) = Build(server);

        var call = api.GetDonorAsync(7);
        await WaitForAsync(() => server.Arrived == 1);
        await PassAsync(clock, 119);
        Assert.False(call.IsCompleted);

        await PassAsync(clock, 2);
        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsOk);
        Assert.True(result.Problem!.IsUnreachable, result.Problem.Code);
        Assert.Equal(1, server.Arrived);
        Assert.Equal(1, server.Cancelled);
        Assert.Equal(ServerState.Online, status.State);
    }

    [Fact]
    public async Task AWakingAnswer_IsStillRetried_AndTheServersTwoSecondAdvice_IsWhatTheNextAttemptWaits()
    {
        // /readyz of a waking database answers 503 with Retry-After: 2, so the readiness poll sees a resumed database within about 2 s.
        var times = new List<TimeSpan>();
        ManualTimeProvider? clockRef = null;
        var handler = new ScriptedHandler((_, n) =>
        {
            times.Add(clockRef!.Elapsed);
            return n <= 3
                ? ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, $$"""{"title":"waking","status":503,"code":"{{ProblemCodes.DatabaseWaking}}","database":"waking"}""", ("Retry-After", "2"))
                : ScriptedHandler.Json(HttpStatusCode.OK, """{"database":"ready"}""");
        });
        var (api, _, clock) = Build(handler);
        clockRef = clock;

        var call = api.GetReadinessAsync();
        for (var i = 0; i < 200 && !call.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1);
        }

        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsOk);
        Assert.Equal(4, times.Count);
        Assert.All(times.Zip(times.Skip(1), (a, b) => b - a), gap => Assert.InRange(gap.TotalSeconds, 2, 2.2));
    }

    [Fact]
    public void TheSharedHttpClient_ChecksTheConnection_WhileARequestWaits()
    {
        // What replaces the 20 s attempt timeout: a connection that died is found by a ping, not guessed from the server being slow.
        SocketsHttpHandler? sockets = null;
        using var http = ApiHttpClientFactory.Create(new Uri("https://api.test/"), new WakePolicy(), new ServerStatus(), h => sockets = h);

        Assert.Equal(TimeSpan.FromSeconds(15), sockets!.ConnectTimeout);
        Assert.Equal(HttpKeepAlivePingPolicy.WithActiveRequests, sockets.KeepAlivePingPolicy);
        Assert.Equal(TimeSpan.FromSeconds(15), sockets.KeepAlivePingDelay);
        Assert.Equal(TimeSpan.FromSeconds(15), sockets.KeepAlivePingTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);

        // A dead connection is noticed, and the request sent again, well inside the budget.
        var policy = new WakePolicy();
        Assert.True(sockets.KeepAlivePingDelay + sockets.KeepAlivePingTimeout < policy.Budget / 2);
        Assert.Equal(policy.Budget + TimeSpan.FromSeconds(20), policy.Ceiling);
    }

    [Fact]
    public async Task SignIn_ThatTheServerAnswersAfter70Seconds_SignsIn_WithOneRequest_AndCancelWasOfferedAllAlong()
    {
        using var rig = new Rig();
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var server = new HoldingServer();
        var policy = new WakePolicy { Clock = clock };
        using var http = new HttpClient(new WakeAwareHandler(policy, status, server)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var inner = new LifestreamApiClient(http, rig.Session);
        var vm = new LoginViewModel(ScopedApiClient.Wrap(inner, policy, strict: true), rig.Session, rig.Get<IAppFlow>(), status) { Username = "u", Password = TestSecrets.P };

        var signIn = vm.SignInCommand.ExecuteAsync();
        await WaitForAsync(() => server.Arrived == 1);
        await PassAsync(clock, 70);

        Assert.True(vm.IsBusy);
        Assert.True(vm.IsServerWaking);
        Assert.True(vm.CanCancel);
        Assert.Equal(LoginProblem.None, vm.Problem); // a wake inside the expected time is not a failure
        Assert.Null(vm.Message);
        Assert.Equal(1, server.Arrived);

        server.Answer(0, ScriptedHandler.Json(HttpStatusCode.OK, """{"accessToken":"a","refreshToken":"r","accessTokenExpiresUtc":"2030-01-01T00:00:00Z","user":{"staffId":1,"displayName":"T","role":"Staff","isDemo":false,"canManageStaff":false}}"""));
        await signIn.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoginProblem.None, vm.Problem);
        Assert.NotNull(rig.Session.AccessToken);
        Assert.Equal(1, server.Arrived);
        Assert.Equal(0, server.Cancelled);
    }

    [Fact]
    public async Task SignIn_Cancel_FiftySecondsIn_StopsTheRequest_AndTheScreenIsUsableAgain()
    {
        using var rig = new Rig();
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var server = new HoldingServer();
        var policy = new WakePolicy { Clock = clock };
        using var http = new HttpClient(new WakeAwareHandler(policy, status, server)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var inner = new LifestreamApiClient(http, rig.Session);
        var vm = new LoginViewModel(ScopedApiClient.Wrap(inner, policy, strict: true), rig.Session, rig.Get<IAppFlow>(), status) { Username = "u", Password = TestSecrets.P };

        var signIn = vm.DemoCommand.ExecuteAsync();
        await WaitForAsync(() => server.Arrived == 1);
        await PassAsync(clock, 50);
        Assert.True(vm.CanCancel);

        await vm.CancelCommand.ExecuteAsync();
        await signIn.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, server.Cancelled);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsServerWaking);
        Assert.Equal("Sign-in cancelled.", vm.Message);
        Assert.True(vm.DemoCommand.CanExecute(null));
        Assert.Null(rig.Session.AccessToken);
    }
}
