using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;

namespace Lifestream.Client.Tests;

/// <summary>
/// The two parts of a wake from cold, as the app can tell them apart: while the container starts nothing answers; once it is up the server
/// itself answers, and says "database waking" until its database is back. And the state that is not a wake at all: the database has used
/// up the month's free allowance (Azure SQL error 42119) and stays paused until the 1st. Through the real handler and client.
/// </summary>
public sealed class WakePhaseTests
{
    private const string Waking = """{"title":"The database is waking up. Try again in a moment.","status":503,"code":"database_waking","database":"waking"}""";
    private const string Resting = """{"title":"The demo database has used its free monthly allowance and is back on the 1st.","status":503,"code":"database_resting","database":"resting"}""";

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2500 && !condition(); i++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "the awaited state was not reached");
    }

    private static async Task PassAsync(ManualTimeProvider clock, double seconds)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(1);
        }
    }

    [Fact]
    public void Status_NamesThePhase_FromWhatTheServerSaid()
    {
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var changes = 0;
        status.Changed += (_, _) => changes++;

        status.Enter(clock);
        Assert.Equal(ServerState.Waking, status.State);
        Assert.Equal(WakePhase.ServerStarting, status.Phase); // nothing has answered

        status.Heard(clock, databaseWaking: null); // /healthz answered: the container is up
        Assert.Equal(WakePhase.ServerWorking, status.Phase);
        status.Heard(clock, databaseWaking: true); // /readyz answered "waking"
        Assert.Equal(WakePhase.DatabaseWaking, status.Phase);
        status.Heard(clock, databaseWaking: null); // an answer that says nothing about the database changes nothing
        Assert.Equal(WakePhase.DatabaseWaking, status.Phase);
        Assert.Equal(3, changes);

        status.Heard(clock, databaseWaking: false); // an answer out of the database
        Assert.Equal(WakePhase.ServerWorking, status.Phase);
        status.Leave();
        Assert.Equal(ServerState.Online, status.State);

        // A call that turns slow a minute later: the container is still there, so this is not a start.
        clock.Advance(TimeSpan.FromMinutes(1));
        status.Enter(clock);
        Assert.Equal(WakePhase.ServerWorking, status.Phase);
        status.Leave();

        // After the time a container without requests is kept, a slow call may well be a start again.
        clock.Advance(ServerStatus.ServerStaysUp + TimeSpan.FromSeconds(1));
        status.Enter(clock);
        Assert.Equal(WakePhase.ServerStarting, status.Phase);
    }

    [Fact]
    public async Task AnswersOfThePlatformInFrontOfTheServer_AreNotTheServerAnswering()
    {
        // While the container starts, the platform may answer 502, 503 or 504 itself. Only the API's own answer ends "starting the server".
        ManualTimeProvider? clock = null;
        var handler = new ScriptedHandler((_, _) => clock!.Elapsed < TimeSpan.FromSeconds(10)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("upstream connect error") }
            : clock.Elapsed < TimeSpan.FromSeconds(20)
                ? ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, Waking, ("Retry-After", "2"))
                : ScriptedHandler.Json(HttpStatusCode.OK, """{"database":"ready"}"""));
        clock = new ManualTimeProvider();
        var status = new ServerStatus();
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Clock = clock }, status, handler)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var api = new LifestreamApiClient(http, new SessionService());

        var call = api.GetReadinessAsync();
        await PassAsync(clock, 8);
        Assert.Equal(ServerState.Waking, status.State);
        Assert.Equal(WakePhase.ServerStarting, status.Phase);

        await PassAsync(clock, 10);
        Assert.Equal(WakePhase.DatabaseWaking, status.Phase);

        await PassAsync(clock, 6);
        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsOk);
        Assert.Equal(ServerState.Online, status.State);
    }

    [Fact]
    public async Task SignInWindow_ColdStart_SaysStartingTheServer_ThenWakingTheDatabase_ThenReady()
    {
        using var rig = new Rig();
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var server = new HoldingServer();
        var policy = new WakePolicy { Clock = clock };
        using var http = new HttpClient(new WakeAwareHandler(policy, status, server)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var inner = new LifestreamApiClient(http, rig.Session);
        using var vm = new LoginViewModel(ScopedApiClient.Wrap(inner, policy, strict: true), rig.Session, rig.Get<IAppFlow>(), status) { Clock = clock };

        // The window opens and asks /readyz. The container is starting: the platform holds the request and nothing answers.
        var check = vm.CheckServerAsync();
        await WaitForAsync(() => server.Arrived == 1);
        Assert.Equal("Checking the server", vm.ServerText);
        await PassAsync(clock, 3.5);
        Assert.True(vm.IsServerWaking);
        Assert.Equal("Starting the server", vm.ServerText);
        Assert.Equal(WakingTexts.SignIn, vm.WakingText);
        Assert.Contains("Step 1 of 2: starting the server", vm.WakingText, StringComparison.Ordinal);

        // 27 s: the container is up and answers; its database is still resuming.
        await PassAsync(clock, 23.5);
        Assert.Equal("Starting the server", vm.ServerText);
        server.Answer(0, ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, Waking, ("Retry-After", "2")));
        await WaitForAsync(() => vm.ServerText == "Waking the database");
        Assert.Equal(WakingTexts.SignInDatabase, vm.WakingText);
        Assert.Contains("Step 2 of 2", vm.WakingText, StringComparison.Ordinal);
        Assert.True(vm.IsServerWaking);
        Assert.Equal(LoginProblem.None, vm.Problem);

        // The readiness poll comes back every 2 s, as the server asked; 85 s in, the database answers.
        await PassAsync(clock, 2.5);
        Assert.Equal(2, server.Arrived);
        await PassAsync(clock, 55);
        Assert.Equal("Waking the database", vm.ServerText);
        Assert.Equal(WakingTexts.SignInDatabase, vm.WakingText); // 85 s: inside the expected time, nothing says "longer than usual"
        server.Answer(1, ScriptedHandler.Json(HttpStatusCode.OK, """{"database":"ready"}"""));
        await check.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Server ready", vm.ServerText);
        Assert.False(vm.IsServerWaking);
        Assert.Equal(LoginProblem.None, vm.Problem);
        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task SignIn_WhenTheAllowanceIsUsedUp_SaysSoAtOnce_WithOneRequest_NoWakingNotice_AndNoRetry()
    {
        using var rig = new Rig();
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, Resting, ("Retry-After", "864000")));
        var policy = new WakePolicy { Clock = clock };
        using var http = new HttpClient(new WakeAwareHandler(policy, status, handler)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var inner = new LifestreamApiClient(http, rig.Session);
        using var vm = new LoginViewModel(ScopedApiClient.Wrap(inner, policy, strict: true), rig.Session, rig.Get<IAppFlow>(), status) { Clock = clock };
        var wakingShown = false;
        vm.PropertyChanged += (_, e) => wakingShown |= e.PropertyName == nameof(LoginViewModel.IsServerWaking) && vm.IsServerWaking;

        await vm.CheckServerAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ServerReadiness.Resting, vm.Server);
        Assert.Equal("Demo database paused until the 1st", vm.ServerText);

        await vm.DemoCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoginProblem.DemoResting, vm.Problem);
        Assert.Equal("The demo database has used its free monthly allowance and is back on the 1st.", vm.Message);
        Assert.False(vm.CanRetry); // retrying does not bring it back
        Assert.False(vm.IsBusy);
        Assert.False(wakingShown);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed); // answered at once: nothing waited, nothing was retried
        Assert.Equal(2, handler.Requests.Count); // one readiness check, one sign-in
        Assert.Equal("Demo database paused until the 1st", vm.ServerText);
        Assert.Null(rig.Session.AccessToken);
    }

    [Fact]
    public void EveryScreen_SaysTheSameAboutAUsedUpAllowance()
    {
        Assert.Equal("The demo database has used its free monthly allowance and is back on the 1st.", WakingTexts.Resting);
        Assert.Equal(WakingTexts.Resting, Lifestream.Client.Features.ListScreens.ProblemText.For(new ApiProblem(503, ProblemCodes.DatabaseResting, "t"), "donor"));
    }
}
