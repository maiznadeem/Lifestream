using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
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

    [Fact]
    public async Task Demo_WhileTheReadinessCheckSaysWaking_SendsNoSignIn_UntilReady_ThenSignsInOnce()
    {
        var readyz = 0;
        var handler = new ScriptedHandler((r, _) => IsReadyz(r) ? (++readyz <= 3 ? Waking() : Ready()) : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = LoginScreen.Open(handler);

        _ = screen.Start(v => v.CheckServerAsync());
        await screen.SettledAsync();
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await screen.RunUntilAsync(demo, 60);

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
        using var screen = LoginScreen.Open(handler);

        _ = screen.Start(v => v.CheckServerAsync());
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await screen.RunUntilAsync(demo, 30);

        Assert.Equal(LoginProblem.ServerUnreachable, screen.Vm.Problem);
        Assert.DoesNotContain(handler.Requests, IsDemo);
    }

    [Fact]
    public async Task Demo_WhenTheDatabaseIsResting_SaysResting_AndSendsNoSignIn()
    {
        var handler = new ScriptedHandler((r, _) => IsReadyz(r)
            ? ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, $$"""{"title":"resting","status":503,"code":"{{ProblemCodes.DatabaseResting}}","database":"resting"}""")
            : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = LoginScreen.Open(handler);

        // The window's check has already said "resting" when the person clicks (on a busy CI runner it always had): the click asks again.
        await screen.RunUntilAsync(screen.Start(v => v.CheckServerAsync()), 10);
        Assert.Equal(ServerReadiness.Resting, screen.Vm.Server);
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await screen.RunUntilAsync(demo, 30);

        Assert.Equal(LoginProblem.DemoResting, screen.Vm.Problem);
        Assert.DoesNotContain(handler.Requests, IsDemo);
    }

    [Fact]
    public async Task Cancel_WhileWaitingForReadiness_SendsNoSignIn_AndTheButtonsWorkAgain()
    {
        var handler = new ScriptedHandler((r, _) => IsReadyz(r) ? Waking() : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = LoginScreen.Open(handler);

        _ = screen.Start(v => v.CheckServerAsync());
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await screen.SettledAsync();
        await screen.PassAsync(10);
        Assert.True(screen.Vm.IsWaitingForServer);
        Assert.True(screen.Vm.CanCancel);

        await screen.Ui.Run(() => screen.Vm.CancelCommand.ExecuteAsync());
        await demo.WaitAsync(TimeSpan.FromSeconds(5));
        await screen.Ui.DrainedAsync();
        await screen.PassAsync(10);

        Assert.DoesNotContain(handler.Requests, IsDemo);
        Assert.False(screen.Vm.IsBusy);
        Assert.False(screen.Vm.IsWaitingForServer);
        Assert.True(screen.Ui.Run(() => screen.Vm.DemoCommand.CanExecute(null)));
    }

    [Fact]
    public async Task SignIn_WithNoReadinessCheckRun_GoesStraightToTheServer()
    {
        var handler = new ScriptedHandler((r, _) => IsReadyz(r) ? Ready() : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = LoginScreen.Open(handler);

        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await screen.RunUntilAsync(demo, 10);

        Assert.True(IsDemo(Assert.Single(handler.Requests)));
        Assert.Equal(LoginProblem.None, screen.Vm.Problem);
    }

    [Fact]
    public async Task SignIn_RightAfterServerReady_GoesStraightToTheServer_ButAfterMoreThanFourMinutes_ChecksReadinessFirst()
    {
        var handler = new ScriptedHandler((r, _) => IsReadyz(r) ? Ready() : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = LoginScreen.Open(handler);

        await screen.RunUntilAsync(screen.Start(v => v.CheckServerAsync()), 10);
        Assert.Equal(ServerReadiness.Ready, screen.Vm.Server);
        await screen.RunUntilAsync(screen.Start(v => v.DemoCommand.ExecuteAsync()), 10);
        Assert.Equal(["readyz", "demo"], handler.Requests.Select(r => IsReadyz(r) ? "readyz" : "demo"));

        // Ready, then nobody signs in for five minutes: the container has gone back to sleep, so the window asks again before signing in.
        screen.Clock.Advance(ServerStatus.ServerStaysUp + TimeSpan.FromMinutes(1));
        await screen.RunUntilAsync(screen.Start(v => v.DemoCommand.ExecuteAsync()), 10);
        Assert.Equal(["readyz", "demo", "readyz", "demo"], handler.Requests.Select(r => IsReadyz(r) ? "readyz" : "demo"));
    }

    [Fact]
    public async Task BusyTexts_SayWhenTheServerIsReady_WhileWaiting()
    {
        var readyz = 0;
        var handler = new ScriptedHandler((r, _) => IsReadyz(r) ? (++readyz <= 2 ? Waking() : Ready()) : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = LoginScreen.Open(handler);
        Assert.Equal("Signing in", screen.Vm.SignInBusyText);
        Assert.Equal("Opening the demo", screen.Vm.DemoBusyText);

        _ = screen.Start(v => v.CheckServerAsync());
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await screen.SettledAsync();
        Assert.True(screen.Vm.IsWaitingForServer);
        Assert.Equal("Opening the demo when ready", screen.Vm.DemoBusyText);
        Assert.Equal("Signing in when ready", screen.Vm.SignInBusyText);

        await screen.RunUntilAsync(demo, 30);
        Assert.False(screen.Vm.IsWaitingForServer);
        Assert.Equal("Opening the demo", screen.Vm.DemoBusyText);
    }

    /// <summary>
    /// A readiness check the sign-in itself started (after "can't reach", or a "ready" gone stale) goes on when the sign-in is cancelled, and
    /// a cancel never turns the pill to "Can't reach the server" (review of 2.1.9: the check ran in the sign-in's scope and died with it).
    /// </summary>
    [Fact]
    public async Task Cancel_OfASignInThatStartedTheReadinessCheck_LeavesTheCheckRunning_AndNeverSaysCantReach()
    {
        ManualTimeProvider? clock = null;
        var handler = new ScriptedHandler((r, _) => IsReadyz(r)
            ? (clock!.Elapsed < TimeSpan.FromSeconds(8) ? throw new HttpRequestException("refused") : Waking())
            : ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        using var screen = LoginScreen.Open(handler);
        clock = screen.Clock;

        await screen.RunUntilAsync(screen.Start(v => v.CheckServerAsync()), 20);
        Assert.Equal(ServerReadiness.Unreachable, screen.Vm.Server);
        await screen.PassToAsync(10);

        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await screen.SettledAsync();
        await screen.PassAsync(10);
        Assert.True(screen.Vm.IsWaitingForServer);

        await screen.Ui.Run(() => screen.Vm.CancelCommand.ExecuteAsync());
        await demo.WaitAsync(TimeSpan.FromSeconds(5));
        await screen.Ui.DrainedAsync();
        var asked = handler.Requests.Count(IsReadyz);
        await screen.PassAsync(10);

        Assert.NotEqual(ServerReadiness.Unreachable, screen.Vm.Server);
        Assert.True(handler.Requests.Count(IsReadyz) > asked, "the readiness check stopped with the cancelled sign-in");
        Assert.DoesNotContain(handler.Requests, IsDemo);
    }

    /// <summary>
    /// A readiness call the server held past its whole budget (Cloudflare's 524 at 100 s, then a retry cut at 120 s) is a wake that runs
    /// long: the check asks again, and the sign-in that waits never says "Can't reach the server" (review of 2.1.9).
    /// </summary>
    [Fact]
    public async Task ReadinessHeldPastItsBudget_IsStillWaking_AndTheWaitingSignInGoesOnceReady()
    {
        var server = new HoldingServer();
        using var screen = LoginScreen.Open(server);

        _ = screen.Start(v => v.CheckServerAsync());
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await LoginScreen.WaitForAsync(() => server.Arrived == 1);
        await screen.SettledAsync();
        await screen.PassToAsync(121);
        await LoginScreen.WaitForAsync(() => server.Arrived == 2); // the second round of the readiness check

        Assert.NotEqual(ServerReadiness.Unreachable, screen.Read(v => v.Server));
        Assert.Equal(LoginProblem.None, screen.Read(v => v.Problem));
        server.Answer(1, Ready());
        await LoginScreen.WaitForAsync(() => server.Arrived == 3); // the sign-in, sent once the server is ready
        server.Answer(2, ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        await demo.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoginProblem.None, screen.Read(v => v.Problem));
        Assert.NotNull(screen.Rig.Session.AccessToken);
    }
}
