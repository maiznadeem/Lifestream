using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;

namespace Lifestream.Client.Tests;

/// <summary>SHELL-16 and SHELL-17: sign-out must clear the session and tokens, fail closed, and not be undone by a refresh in flight.</summary>
public sealed class SignOutSecurityTests
{
    private const string DonorJson = """{"id":7,"name":"Ayşe","dateOfBirth":"1990-01-02","gender":"Female","bloodType":"APos","phone":null,"address":null,"frequency":"Regular","lastDonated":null,"rowVersion":"AAA="}""";

    private const string TokensJson = """{"accessToken":"refreshed-access","refreshToken":"refreshed-refresh","accessTokenExpiresUtc":"2030-01-01T00:00:00Z","user":{"staffId":1,"displayName":"T","role":"Staff","isDemo":false,"canManageStaff":false}}""";

    private static TokenResponse Tokens(string access, string refresh) =>
        new(access, refresh, DateTime.UtcNow.AddMinutes(15), FakeApiClient.User());

    private static (LifestreamApiClient Api, SessionService Session, ScriptedHandler Handler) Build(Func<RecordedRequest, int, HttpResponseMessage> respond)
    {
        var handler = new ScriptedHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        var session = new SessionService();
        return (new LifestreamApiClient(http, session), session, handler);
    }

    // 1. Logout: the given refresh token only, no access token, never a refresh

    [Fact]
    public async Task ApiClient_Logout_SendsTheGivenRefreshToken_WithoutAnAccessToken_AndNeverRefreshes()
    {
        var (api, session, handler) = Build((_, _) => ScriptedHandler.Problem(HttpStatusCode.Unauthorized, "unauthorized"));
        session.Establish(Tokens("access-A", "refresh-A"));

        var first = await api.LogoutAsync("refresh-A");
        session.Clear(); // the session is gone, and logout still works with the token the caller took beforehand
        await api.LogoutAsync("refresh-B");

        Assert.False(first.IsOk); // the 401 is reported, not retried
        Assert.Equal(2, handler.Requests.Count); // two logouts, and no refresh call between or after them
        Assert.All(handler.Requests, r =>
        {
            Assert.EndsWith(ApiRoutes.AuthLogout, r.Uri, StringComparison.Ordinal);
            Assert.Null(r.Header("Authorization")); // no access token is sent
        });
        Assert.Contains("refresh-A", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("refresh-B", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Contains("auth/refresh", StringComparison.Ordinal));
    }

    // 2. A refresh that finishes after sign-out must not bring the session back

    private sealed class GatedRefreshHandler : HttpMessageHandler
    {
        public TaskCompletionSource RefreshStarted { get; } = new();

        public TaskCompletionSource ReleaseRefresh { get; } = new();

        public string RefreshBody { get; set; } = TokensJson;

        public HttpStatusCode RefreshStatus { get; set; } = HttpStatusCode.OK;

        public List<(string Uri, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("auth/refresh", StringComparison.Ordinal))
            {
                RefreshStarted.TrySetResult();
                await ReleaseRefresh.Task.WaitAsync(cancellationToken);
                return ScriptedHandler.Json(RefreshStatus, RefreshBody);
            }

            return request.Headers.Authorization?.Parameter == "refreshed-access"
                ? ScriptedHandler.Json(HttpStatusCode.OK, DonorJson)
                : ScriptedHandler.Problem(HttpStatusCode.Unauthorized, "unauthorized");
        }
    }

    [Fact]
    public async Task ApiClient_ClearDuringAnInFlightRefresh_LeavesTheSessionCleared()
    {
        using var handler = new GatedRefreshHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        var session = new SessionService();
        using var api = new LifestreamApiClient(http, session);
        session.Establish(Tokens("stale-access", "refresh-1"));
        var expired = 0;
        session.Expired += (_, _) => expired++;

        var call = api.GetDonorAsync(7); // 401 with the stale token, so a refresh starts
        await handler.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Clear(); // the user signs out while the refresh is in flight
        handler.ReleaseRefresh.SetResult(); // the server answers the refresh with fresh tokens
        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.AccessToken);
        Assert.Null(session.RefreshToken);
        Assert.Null(session.User);
        Assert.Equal(ClientProblemCodes.SessionExpired, result.Problem!.Code);
        Assert.Equal(0, expired); // a deliberate sign-out is not an "expired session" notice
        Assert.Equal(1, handler.Requests.Count(r => r.Uri.Contains("donors", StringComparison.Ordinal))); // no anonymous retry after the clear
    }

    [Fact]
    public async Task ApiClient_SignInAgainDuringAnInFlightRefresh_DoesNotGetTheOldRefreshedTokens()
    {
        using var handler = new GatedRefreshHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        var session = new SessionService();
        using var api = new LifestreamApiClient(http, session);
        session.Establish(Tokens("stale-access", "refresh-1"));

        var call = api.GetDonorAsync(7);
        await handler.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Clear();
        session.Establish(Tokens("second-user-access", "second-user-refresh")); // another user signs in meanwhile
        handler.ReleaseRefresh.SetResult();
        await call.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("second-user-access", session.AccessToken); // the first user's refreshed tokens were discarded
        Assert.Equal("second-user-refresh", session.RefreshToken);
    }

    [Fact]
    public async Task ApiClient_RefreshAnswerThatIsNotJson_IsAFailedRefresh_NotACrash()
    {
        using var handler = new GatedRefreshHandler { RefreshBody = "<html>proxy error</html>" };
        handler.ReleaseRefresh.SetResult();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        var session = new SessionService();
        using var api = new LifestreamApiClient(http, session);
        session.Establish(Tokens("stale-access", "refresh-1"));
        var expired = 0;
        session.Expired += (_, _) => expired++;

        var result = await api.GetDonorAsync(7);

        Assert.Equal(ClientProblemCodes.SessionExpired, result.Problem!.Code);
        Assert.Equal(1, expired); // a refresh the server will not give is a real expiry
    }

    [Fact]
    public async Task ApiClient_RefreshSucceedsWhileSignedIn_StoresTheNewTokens_AndRetries()
    {
        using var handler = new GatedRefreshHandler();
        handler.ReleaseRefresh.SetResult();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        var session = new SessionService();
        using var api = new LifestreamApiClient(http, session);
        session.Establish(Tokens("stale-access", "refresh-1"));

        var result = await api.GetDonorAsync(7);

        Assert.True(result.IsOk);
        Assert.Equal("refreshed-access", session.AccessToken);
        Assert.Equal("refreshed-refresh", session.RefreshToken);
    }

    [Fact]
    public void Session_TryUpdateTokens_IsACompareAndSwap()
    {
        var session = new SessionService();
        session.Establish(Tokens("a1", "r1"));
        var generation = session.Generation;

        Assert.True(session.TryUpdateTokens(Tokens("a2", "r2"), generation));
        Assert.Equal("a2", session.AccessToken);

        session.Clear();
        Assert.False(session.TryUpdateTokens(Tokens("a3", "r3"), generation));
        Assert.Null(session.AccessToken);

        session.Establish(Tokens("b1", "s1"));
        Assert.False(session.TryUpdateTokens(Tokens("a4", "r4"), generation)); // an older generation never wins
        Assert.Equal("b1", session.AccessToken);
    }

    // 3. Sign-out fails closed

    [Fact]
    public async Task Logout_WhenTheServerCallThrows_StillClearsTheSession_ClosesTheMainWindow_AndShowsLogin()
    {
        using var rig = new Rig();
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        rig.Api.Handlers["LogoutAsync"] = _ => throw new InvalidOperationException("the network stack blew up");
        var flow = rig.Get<IAppFlow>();
        flow.Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        rig.Get<Lifestream.Client.Common.IListViewStateStore>().GetState("donors").Search = "kept?";
        rig.Windows.Events.Clear();

        await flow.SignOutAsync(); // must not throw

        Assert.False(rig.Session.IsAuthenticated);
        Assert.Null(rig.Session.AccessToken);
        Assert.Null(rig.Session.RefreshToken);
        Assert.Equal(["CloseMain", "ShowLogin"], rig.Windows.Events);
        Assert.Null(rig.Get<Lifestream.Client.Common.IListViewStateStore>().GetState("donors").Search);
        Assert.Equal(1, rig.Api.CountOf("LogoutAsync"));
    }

    [Fact]
    public async Task Logout_WhenTheServerCallIsCancelledOrTimesOut_StillSignsOutLocally()
    {
        using var rig = new Rig();
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        rig.Api.Handlers["LogoutAsync"] = _ => throw new TaskCanceledException();
        var flow = rig.Get<IAppFlow>();
        flow.Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();

        await flow.SignOutAsync();

        Assert.False(rig.Session.IsAuthenticated);
        Assert.Contains("CloseMain", rig.Windows.Events);
    }

    [Fact]
    public async Task Logout_WhenAWindowStepThrows_TheSessionIsStillCleared_AndTheLoginWindowStillOpens()
    {
        using var rig = new Rig();
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        var flow = rig.Get<IAppFlow>();
        flow.Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        rig.Windows.ThrowOnCloseMain = true;
        rig.Windows.Events.Clear();

        await flow.SignOutAsync();

        Assert.False(rig.Session.IsAuthenticated);
        Assert.Contains("ShowLogin", rig.Windows.Events);
    }

    [Fact]
    public async Task Logout_AfterAServerFailure_CanRunAgainAtTheNextSignIn()
    {
        using var rig = new Rig();
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        rig.Api.Handlers["LogoutAsync"] = _ => throw new HttpRequestException("down");
        var flow = rig.Get<IAppFlow>();
        flow.Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        await flow.SignOutAsync();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        await flow.SignOutAsync();

        Assert.Equal(2, rig.Api.CountOf("LogoutAsync"));
        Assert.False(rig.Session.IsAuthenticated);
    }
}
