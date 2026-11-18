using System.Diagnostics;
using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

/// <summary>
/// Regression tests for three shell defects: toasts changing a bound collection off the UI thread (SHELL-01, SHELL-30), a call
/// replayed under another user's session (SHELL-16), and sign-out waiting on the cold-start retry budget (SHELL-17).
/// Tests that use <see cref="ScriptedHandler"/>-style HTTP fakes or <see cref="FakeApiClient"/> say so in their names.
/// </summary>
public sealed class ShellDefectTests
{
    private const string DonorJson = """{"id":7,"name":"Ayşe","dateOfBirth":"1990-01-02","gender":"Female","bloodType":"APos","phone":null,"address":null,"frequency":"Regular","lastDonated":null,"rowVersion":"AAA="}""";

    private const string RefreshedJson = """{"accessToken":"refreshed-access","refreshToken":"refreshed-refresh","accessTokenExpiresUtc":"2030-01-01T00:00:00Z","user":{"staffId":1,"displayName":"A","role":"Staff","isDemo":false,"canManageStaff":false}}""";

    private static TokenResponse Tokens(string access, string refresh, string name = "T") =>
        new(access, refresh, DateTime.UtcNow.AddMinutes(15), FakeApiClient.User(name: name));

    // SHELL-30 / SHELL-01: the toast timer must not touch the bound collection from its own thread

    [Fact]
    public async Task Toast_AutoDismissFiredFromATimerThread_ChangesTheCollectionOnTheDispatcherThread()
    {
        using var ui = new ThreadAffineUiDispatcher();
        var clock = new ManualTimeProvider();
        var toasts = new ToastService(ui, clock);
        var changeThreads = new List<(string Action, int Thread)>();
        toasts.Toasts.CollectionChanged += (_, e) =>
        {
            lock (changeThreads)
            {
                changeThreads.Add((e.Action.ToString(), Environment.CurrentManagedThreadId));
            }
        };

        ui.Invoke(() => toasts.Show(ToastKind.Success, "Donor 'Ayşe' saved."));
        Assert.Equal(1, ui.Invoke(() => toasts.Toasts.Count));
        var timerThread = await Task.Run(() =>
        {
            clock.Advance(ToastService.Lifetime); // the toast's timer fires here, on a thread-pool thread, not the UI thread
            return Environment.CurrentManagedThreadId;
        });

        Assert.Equal(0, ui.Invoke(() => toasts.Toasts.Count)); // the removal ran (the invoke queues behind it)
        Assert.NotEqual(ui.ThreadId, timerThread);
        lock (changeThreads)
        {
            Assert.Equal(["Add", "Remove"], changeThreads.Select(c => c.Action));
            Assert.All(changeThreads, c => Assert.Equal(ui.ThreadId, c.Thread));
        }
    }

    [Fact]
    public async Task Toast_ShownFromABackgroundThread_IsAddedOnTheDispatcherThread()
    {
        using var ui = new ThreadAffineUiDispatcher();
        var toasts = new ToastService(ui, new ManualTimeProvider());
        var addedOn = new List<int>();
        toasts.Toasts.CollectionChanged += (_, _) =>
        {
            lock (addedOn)
            {
                addedOn.Add(Environment.CurrentManagedThreadId);
            }
        };

        await Task.Run(() => toasts.Show(ToastKind.Info, "Hello"));

        Assert.Equal(1, ui.Invoke(() => toasts.Toasts.Count));
        lock (addedOn)
        {
            Assert.Equal([ui.ThreadId], addedOn);
        }
    }

    // SHELL-16: a call never travels under another session

    /// <summary>The real session, but with no cancellation scope, so the generation guard is tested on its own (without sign-out cancelling calls first).</summary>
    private sealed class NoScopeSession(SessionService inner) : ISessionService
    {
        public Action? AfterTokensUpdated { get; set; }

        public SessionUser? User => inner.User;

        public bool IsAuthenticated => inner.IsAuthenticated;

        public string? AccessToken => inner.AccessToken;

        public string? RefreshToken => inner.RefreshToken;

        public long Generation => inner.Generation;

        public event EventHandler? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }

        public event EventHandler? Expired { add => inner.Expired += value; remove => inner.Expired -= value; }

        public void Establish(TokenResponse tokens) => inner.Establish(tokens);

        public SessionSnapshot Snapshot() => inner.Snapshot() with { Scope = default };

        public bool TryUpdateTokens(TokenResponse tokens, long expectedGeneration)
        {
            var stored = inner.TryUpdateTokens(tokens, expectedGeneration);
            AfterTokensUpdated?.Invoke();
            return stored;
        }

        public void Clear() => inner.Clear();

        public void Expire(long generation) => inner.Expire(generation);

        public bool IsInRole(Role role) => inner.IsInRole(role);
    }

    /// <summary>A server that accepts any token except "stale-access" and gates the refresh call; it records the token of every donors request.</summary>
    private sealed class ReplayServer : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private int _stale401s;

        public TaskCompletionSource RefreshStarted { get; } = new();

        public TaskCompletionSource ReleaseRefresh { get; } = new();

        public TaskCompletionSource BothCallsRejected { get; } = new();

        public bool HoldRefresh { get; set; } = true;

        public List<string?> DonorTokens { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("auth/refresh", StringComparison.Ordinal))
            {
                RefreshStarted.TrySetResult();
                if (HoldRefresh)
                {
                    await ReleaseRefresh.Task.WaitAsync(cancellationToken);
                }

                return ScriptedHandler.Json(HttpStatusCode.OK, RefreshedJson);
            }

            var token = request.Headers.Authorization?.Parameter;
            lock (_gate)
            {
                DonorTokens.Add(token);
            }

            if (token == "stale-access")
            {
                lock (_gate)
                {
                    if (++_stale401s == 2)
                    {
                        BothCallsRejected.TrySetResult();
                    }
                }

                return ScriptedHandler.Problem(HttpStatusCode.Unauthorized, "unauthorized");
            }

            return ScriptedHandler.Json(HttpStatusCode.OK, DonorJson);
        }
    }

    [Fact]
    public async Task ApiClient_FakeServer_CallWaitingForARefreshWhileAnotherUserSignsIn_IsNotReplayedWithTheNewUsersToken()
    {
        using var server = new ReplayServer();
        using var http = new HttpClient(server) { BaseAddress = new Uri("https://api.test/") };
        var session = new NoScopeSession(new SessionService());
        using var api = new LifestreamApiClient(http, session);
        session.Establish(Tokens("stale-access", "refresh-A", "User A"));

        var first = api.GetDonorAsync(7); // both calls are User A's and both get a 401, so one refreshes while the other waits for it
        var second = api.GetDonorAsync(8);
        await server.BothCallsRejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Clear();
        session.Establish(Tokens("user-b-access", "refresh-B", "User B")); // a different user signs in while User A's commands are still in flight
        server.ReleaseRefresh.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, r => Assert.Equal(ClientProblemCodes.SessionExpired, r.Problem!.Code));
        Assert.DoesNotContain("user-b-access", server.DonorTokens); // User A's command never carried User B's token
        Assert.Equal("user-b-access", session.AccessToken); // and User B's session is untouched
    }

    [Fact]
    public async Task ApiClient_FakeServer_SessionChangingJustAfterARefresh_StopsTheResendBeforeItIsSent()
    {
        using var server = new ReplayServer { HoldRefresh = false };
        using var http = new HttpClient(server) { BaseAddress = new Uri("https://api.test/") };
        var session = new NoScopeSession(new SessionService());
        using var api = new LifestreamApiClient(http, session);
        session.Establish(Tokens("stale-access", "refresh-A", "User A"));
        session.AfterTokensUpdated = () =>
        {
            session.AfterTokensUpdated = null;
            session.Clear();
            session.Establish(Tokens("user-b-access", "refresh-B", "User B")); // sign-out and a new sign-in land between the refresh and the resend
        };

        var result = await api.GetDonorAsync(7);

        Assert.Equal(ClientProblemCodes.SessionExpired, result.Problem!.Code);
        Assert.Equal(["stale-access"], server.DonorTokens); // exactly one send: the resend never happened
    }

    [Fact]
    public async Task ApiClient_FakeServer_SignOutCancelsACallThatIsInFlight()
    {
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        using var blocking = new BlockingHandler(started, cancelled);
        using var http = new HttpClient(blocking) { BaseAddress = new Uri("https://api.test/") };
        var session = new SessionService();
        using var api = new LifestreamApiClient(http, session);
        session.Establish(Tokens("access-A", "refresh-A"));

        var call = api.GetDonorAsync(7);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Clear(); // sign-out
        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the request itself was cancelled, not merely abandoned
        Assert.Equal(ClientProblemCodes.SessionExpired, result.Problem!.Code);
    }

    [Fact]
    public void Session_Scope_IsCancelledBySignOut_AndByAnotherUserSigningIn_ButNotByARefresh()
    {
        var session = new SessionService();
        session.Establish(Tokens("a1", "r1"));
        var first = session.Snapshot().Scope;

        Assert.True(session.TryUpdateTokens(Tokens("a2", "r2"), session.Generation));
        Assert.False(first.IsCancellationRequested); // refreshing tokens does not end the session

        session.Clear();
        Assert.True(first.IsCancellationRequested);

        var second = session.Snapshot().Scope;
        Assert.False(second.IsCancellationRequested);
        session.Establish(Tokens("b1", "s1")); // another user signs in
        Assert.True(second.IsCancellationRequested);
        Assert.False(session.Snapshot().Scope.IsCancellationRequested);
    }

    private sealed class BlockingHandler(TaskCompletionSource started, TaskCompletionSource cancelled) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // SHELL-17: sign-out never waits on the network

    [Fact]
    public async Task AppFlow_FakeApi_SignOutWhileTheServerIsWaking_CompletesLocallyInUnder200ms()
    {
        using var rig = new Rig();
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        var waking = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Api.Handlers["LogoutAsync"] = async ct => await waking.Task.WaitAsync(ct); // a waking server: the answer takes "120 s"
        var flow = rig.Get<IAppFlow>();
        flow.Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        var refreshToken = rig.Session.RefreshToken;
        Assert.NotNull(refreshToken);
        rig.Windows.Events.Clear();

        var clock = Stopwatch.StartNew();
        await flow.SignOutAsync().WaitAsync(TimeSpan.FromSeconds(5));
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 200, $"sign-out took {clock.ElapsedMilliseconds} ms");
        Assert.False(rig.Session.IsAuthenticated);
        Assert.Null(rig.Session.RefreshToken);
        Assert.Equal(["CloseMain", "ShowLogin"], rig.Windows.Events);
        lock (rig.Api.LogoutTokens)
        {
            Assert.Equal([refreshToken], rig.Api.LogoutTokens); // the logout was sent in the background with the token taken before the session was cleared
        }

        waking.SetException(new HttpRequestException("still down")); // the late failure is logged, never surfaced
        await Task.Delay(50);
    }

    [Fact]
    public async Task AppFlow_FakeApi_LogoutThrowingSynchronously_StillLeavesTheSessionClearedAndTheLoginWindowShown()
    {
        using var rig = new Rig();
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        rig.Api.LogoutThrowsSynchronously = true;
        var flow = rig.Get<IAppFlow>();
        flow.Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        rig.Windows.Events.Clear();

        await flow.SignOutAsync().WaitAsync(TimeSpan.FromSeconds(5)); // must not throw

        Assert.False(rig.Session.IsAuthenticated);
        Assert.Null(rig.Session.RefreshToken);
        Assert.Equal(["CloseMain", "ShowLogin"], rig.Windows.Events);
    }

    [Fact]
    public async Task AppFlow_FakeApi_LogoutThatNeverAnswers_StillLeavesTheSessionClearedAndTheLoginWindowShown()
    {
        using var rig = new Rig();
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        rig.Api.Handlers["LogoutAsync"] = async _ =>
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
            return ApiResult.Success;
        };
        var flow = rig.Get<IAppFlow>();
        flow.Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        rig.Windows.Events.Clear();

        await flow.SignOutAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(rig.Session.IsAuthenticated);
        Assert.Equal(["CloseMain", "ShowLogin"], rig.Windows.Events);
    }

    [Fact]
    public async Task ApiClient_FakeServer_LogoutToAWakingServer_IsOneAttemptWithNoRetries()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Problem(HttpStatusCode.ServiceUnavailable, ProblemCodes.DatabaseWaking));
        var clock = new ManualTimeProvider(); // never advanced: any retry delay would hang the call
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Clock = clock }, new ServerStatus(), handler)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var api = new LifestreamApiClient(http, new SessionService());

        var result = await api.LogoutAsync("refresh-A").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsOk);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ApiClient_FakeServer_LogoutThatNeverAnswers_GivesUpAfterTheLogoutTimeout()
    {
        using var blocking = new BlockingHandler(new TaskCompletionSource(), new TaskCompletionSource());
        using var http = new HttpClient(blocking) { BaseAddress = new Uri("https://api.test/") };
        using var api = new LifestreamApiClient(http, new SessionService()) { LogoutTimeout = TimeSpan.FromMilliseconds(150) };

        var result = await api.LogoutAsync("refresh-A").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsOk);
        Assert.Equal(ClientProblemCodes.Unreachable, result.Problem!.Code);
        Assert.Equal(TimeSpan.FromSeconds(5), new LifestreamApiClient(http, new SessionService()).LogoutTimeout); // the product value
    }
}
