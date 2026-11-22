using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Navigation;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;

namespace Lifestream.Client.Tests;

/// <summary>
/// 2.1.9: a sign-in clicked while the server is waking waits for the readiness check (short /readyz calls) and is sent once the server is
/// ready, instead of one request held through the whole wake. Up to 2.1.8 that held request could outlast its two-minute budget and end
/// in "Can't reach the server" for a server that was only slow (the owner's report of 2026-11-22). Real client and handler; only the
/// network and the clock are stand-ins.
/// </summary>
public sealed class LoginWaitsForReadinessTests
{
    private const string DemoTokens = """{"accessToken":"a","refreshToken":"r","accessTokenExpiresUtc":"2030-01-01T00:00:00Z","user":{"staffId":1,"displayName":"Demo","role":"Staff","isDemo":true,"canManageStaff":false}}""";

    private static HttpResponseMessage Waking() =>
        ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, $$"""{"title":"waking","status":503,"code":"{{ProblemCodes.DatabaseWaking}}","database":"waking"}""", ("Retry-After", "2"));

    private static HttpResponseMessage Ready() => ScriptedHandler.Json(HttpStatusCode.OK, """{"database":"ready"}""");

    private static bool IsReadyz(RecordedRequest r) => r.Uri.Contains(ApiRoutes.Readyz, StringComparison.Ordinal);

    private static bool IsDemo(RecordedRequest r) => r.Uri.Contains(ApiRoutes.AuthDemo, StringComparison.Ordinal);

    private sealed record Screen(LoginViewModel Vm, ManualTimeProvider Clock, Rig Rig, TestUiThread Ui) : IDisposable
    {
        private readonly List<Task> _operations = [];

        public Task Start(Func<LoginViewModel, Task> operation)
        {
            var task = Ui.Run(() => operation(Vm));
            _operations.Add(task);
            return task;
        }

        /// <summary>Nothing is in between: every operation is over, or waiting on the clock (a retry delay, the budget).</summary>
        public bool Waiting => _operations.TrueForAll(o => o.IsCompleted) || Clock.DelaysWaiting > 0;

        public void Dispose()
        {
            Ui.Run(() =>
            {
                Vm.Dispose();
                return true;
            });
            Rig.Dispose();
            Ui.Dispose();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The client objects live as long as the view model of the test.")]
    private static Screen Open(HttpMessageHandler server)
    {
        var rig = new Rig();
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var policy = new WakePolicy { Clock = clock };
        var http = new HttpClient(new WakeAwareHandler(policy, status, server)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        var inner = new LifestreamApiClient(http, rig.Session);
        var ui = new TestUiThread();
        var vm = ui.Run(() => new LoginViewModel(ScopedApiClient.Wrap(inner, policy, strict: true), rig.Session, rig.Get<IAppFlow>(), status) { Username = "u", Password = TestSecrets.P, Clock = clock });
        return new Screen(vm, clock, rig, ui);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2500 && !condition(); i++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "the awaited state was not reached");
    }

    private static async Task SettledAsync(Screen screen)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            await WaitForAsync(() => screen.Waiting);
            await screen.Ui.DrainedAsync();
        }

        await WaitForAsync(() => screen.Waiting);
    }

    /// <summary>Steps the clock half a second at a time, letting the screen settle after any step that fired one of its timers.</summary>
    private static async Task PassAsync(Screen screen, double seconds)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            var fired = screen.Clock.TimersFired;
            screen.Clock.Advance(TimeSpan.FromMilliseconds(500));
            if (screen.Clock.TimersFired != fired)
            {
                await SettledAsync(screen);
            }
        }
    }

    private static async Task RunUntilAsync(Screen screen, Task operation, int seconds)
    {
        await SettledAsync(screen);
        for (var i = 0; i < seconds && !operation.IsCompleted; i++)
        {
            await PassAsync(screen, 1);
        }

        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await screen.Ui.DrainedAsync();
    }

    [Fact]
    public async Task Demo_WhileTheReadinessCheckSaysWaking_SendsNoSignIn_UntilReady_ThenSignsInOnce()
    {
        var readyz = 0;
        var handler = new ScriptedHandler((r, _) => IsReadyz(r) ? (++readyz <= 3 ? Waking() : Ready()) : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = Open(handler);

        _ = screen.Start(v => v.CheckServerAsync());
        await SettledAsync(screen);
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await RunUntilAsync(screen, demo, 60);

        var requests = handler.Requests.ToList();
        var firstPost = requests.FindIndex(IsDemo);
        Assert.Equal(1, requests.Count(IsDemo));
        Assert.Equal(4, requests.Take(firstPost).Count(IsReadyz)); // three "waking", then "ready", and only then the sign-in
        Assert.Equal(LoginProblem.None, screen.Vm.Problem);
        Assert.NotNull(screen.Rig.Session.AccessToken);
    }

    [Fact]
    public async Task Demo_WhenTheReadinessCheckEndsUnreachable_FailsAsUnreachable_AndSendsNoSignIn()
    {
        var handler = new ScriptedHandler((r, _) => IsReadyz(r) ? throw new HttpRequestException("refused") : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = Open(handler);

        _ = screen.Start(v => v.CheckServerAsync());
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await RunUntilAsync(screen, demo, 30);

        Assert.Equal(LoginProblem.ServerUnreachable, screen.Vm.Problem);
        Assert.DoesNotContain(handler.Requests, IsDemo);
    }

    [Fact]
    public async Task Demo_WhenTheDatabaseIsResting_SaysResting_AndSendsNoSignIn()
    {
        var handler = new ScriptedHandler((r, _) => IsReadyz(r)
            ? ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, $$"""{"title":"resting","status":503,"code":"{{ProblemCodes.DatabaseResting}}","database":"resting"}""")
            : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = Open(handler);

        _ = screen.Start(v => v.CheckServerAsync());
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await RunUntilAsync(screen, demo, 30);

        Assert.Equal(LoginProblem.DemoResting, screen.Vm.Problem);
        Assert.DoesNotContain(handler.Requests, IsDemo);
    }

    [Fact]
    public async Task Cancel_WhileWaitingForReadiness_SendsNoSignIn_AndTheButtonsWorkAgain()
    {
        var handler = new ScriptedHandler((r, _) => IsReadyz(r) ? Waking() : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = Open(handler);

        _ = screen.Start(v => v.CheckServerAsync());
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await SettledAsync(screen);
        await PassAsync(screen, 10);
        Assert.True(screen.Vm.IsWaitingForServer);
        Assert.True(screen.Vm.CanCancel);

        await screen.Ui.Run(() => screen.Vm.CancelCommand.ExecuteAsync());
        await demo.WaitAsync(TimeSpan.FromSeconds(5));
        await screen.Ui.DrainedAsync();
        await PassAsync(screen, 10);

        Assert.DoesNotContain(handler.Requests, IsDemo);
        Assert.False(screen.Vm.IsBusy);
        Assert.False(screen.Vm.IsWaitingForServer);
        Assert.True(screen.Ui.Run(() => screen.Vm.DemoCommand.CanExecute(null)));
    }

    [Fact]
    public async Task SignIn_WithNoReadinessCheckRun_GoesStraightToTheServer()
    {
        var handler = new ScriptedHandler((r, _) => IsReadyz(r) ? Ready() : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = Open(handler);

        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await RunUntilAsync(screen, demo, 10);

        Assert.True(IsDemo(Assert.Single(handler.Requests)));
        Assert.Equal(LoginProblem.None, screen.Vm.Problem);
    }

    [Fact]
    public async Task SignIn_RightAfterServerReady_GoesStraightToTheServer_ButAfterMoreThanFourMinutes_ChecksReadinessFirst()
    {
        var handler = new ScriptedHandler((r, _) => IsReadyz(r) ? Ready() : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = Open(handler);

        await RunUntilAsync(screen, screen.Start(v => v.CheckServerAsync()), 10);
        Assert.Equal(ServerReadiness.Ready, screen.Vm.Server);
        await RunUntilAsync(screen, screen.Start(v => v.DemoCommand.ExecuteAsync()), 10);
        Assert.Equal(["readyz", "demo"], handler.Requests.Select(r => IsReadyz(r) ? "readyz" : "demo"));

        // Ready, then nobody signs in for five minutes: the container has gone back to sleep, so the window asks again before signing in.
        screen.Clock.Advance(ServerStatus.ServerStaysUp + TimeSpan.FromMinutes(1));
        await RunUntilAsync(screen, screen.Start(v => v.DemoCommand.ExecuteAsync()), 10);
        Assert.Equal(["readyz", "demo", "readyz", "demo"], handler.Requests.Select(r => IsReadyz(r) ? "readyz" : "demo"));
    }

    [Fact]
    public async Task BusyTexts_SayWhenTheServerIsReady_WhileWaiting()
    {
        var readyz = 0;
        var handler = new ScriptedHandler((r, _) => IsReadyz(r) ? (++readyz <= 2 ? Waking() : Ready()) : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = Open(handler);
        Assert.Equal("Signing in", screen.Vm.SignInBusyText);
        Assert.Equal("Opening the demo", screen.Vm.DemoBusyText);

        _ = screen.Start(v => v.CheckServerAsync());
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await SettledAsync(screen);
        Assert.True(screen.Vm.IsWaitingForServer);
        Assert.Equal("Opening the demo when the server is ready", screen.Vm.DemoBusyText);
        Assert.Equal("Signing in when the server is ready", screen.Vm.SignInBusyText);

        await RunUntilAsync(screen, demo, 30);
        Assert.False(screen.Vm.IsWaitingForServer);
        Assert.Equal("Opening the demo", screen.Vm.DemoBusyText);
    }
}
