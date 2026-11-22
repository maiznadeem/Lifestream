using System.Net;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;

namespace Lifestream.Client.Tests;

/// <summary>
/// 2.1.9: the sign-in window shows a cold wake as one card with three steps (starting the server, waking the database, signing you in),
/// each with how long it took, a clock for the whole wait and the active step's progress. Timed against the wake measured through
/// Cloudflare on 2026-11-22: the container answers after 30 to 46 s, the database after about a minute more.
/// </summary>
public sealed class LoginWakeCardTests
{
    private const string DemoTokens = """{"accessToken":"a","refreshToken":"r","accessTokenExpiresUtc":"2030-01-01T00:00:00Z","user":{"staffId":1,"displayName":"Demo","role":"Staff","isDemo":true,"canManageStaff":false}}""";

    private static HttpResponseMessage Waking() =>
        ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, $$"""{"title":"waking","status":503,"code":"{{ProblemCodes.DatabaseWaking}}","database":"waking"}""", ("Retry-After", "2"));

    private static HttpResponseMessage Ready() => ScriptedHandler.Json(HttpStatusCode.OK, """{"database":"ready"}""");

    private static WakeStepState[] States(LoginScreen screen) => screen.Read(v => v.Steps.Select(s => s.State).ToArray());

    [Fact]
    public async Task Card_FollowsTheWake_StartingServer_ThenDatabase_ThenSigningIn_WithStepTimes()
    {
        var server = new HoldingServer();
        using var screen = LoginScreen.Open(server);

        _ = screen.Start(v => v.CheckServerAsync());
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await LoginScreen.WaitForAsync(() => server.Arrived == 1);
        await screen.SettledAsync();
        await screen.PassToAsync(10);

        Assert.True(screen.Read(v => v.IsWakeCardVisible));
        Assert.Equal(WakeStep.StartingServer, screen.Read(v => v.Step));
        Assert.Equal("Getting the server ready", screen.Read(v => v.WakeTitle));
        Assert.Equal("Step 1 of 3", screen.Read(v => v.StepOfText));
        Assert.Equal("0:10", screen.Read(v => v.WakeClock));
        Assert.Equal([WakeStepState.Active, WakeStepState.Pending, WakeStepState.Pending], States(screen));
        Assert.InRange(screen.Read(v => v.StepProgress), 0.15, 0.30); // 10 of the usual 45 s

        // 34 s: the container is up and says its database is waking.
        await screen.PassToAsync(34);
        server.Answer(0, Waking());
        await LoginScreen.WaitForAsync(() => screen.Clock.DelaysWaiting > 0); // the handler is waiting out the answer's Retry-After
        await screen.SettledAsync();
        await screen.PassToAsync(40); // past the answer's Retry-After of 2 s: the readiness check asks again
        await LoginScreen.WaitForAsync(() => server.Arrived == 2);
        Assert.Equal(WakeStep.WakingDatabase, screen.Read(v => v.Step));
        Assert.Equal("Step 2 of 3", screen.Read(v => v.StepOfText));
        Assert.Equal([WakeStepState.Done, WakeStepState.Active, WakeStepState.Pending], States(screen));
        Assert.Equal("0:34", screen.Read(v => v.Steps[0].Took));
        Assert.Equal("Started the server", screen.Read(v => v.Steps[0].Label));

        // 89 s: the database is back; the sign-in is sent and is step 3.
        await screen.PassToAsync(89);
        server.Answer(1, Ready());
        await LoginScreen.WaitForAsync(() => server.Arrived == 3);
        await screen.SettledAsync();
        Assert.Equal(WakeStep.SigningIn, screen.Read(v => v.Step));
        Assert.Equal("Signing you in", screen.Read(v => v.WakeTitle));
        Assert.Equal("Step 3 of 3", screen.Read(v => v.StepOfText));
        Assert.Equal([WakeStepState.Done, WakeStepState.Done, WakeStepState.Active], States(screen));
        Assert.Equal("0:55", screen.Read(v => v.Steps[1].Took));
        Assert.Equal("Woke the database", screen.Read(v => v.Steps[1].Label));
        Assert.Contains("demo", screen.Read(v => v.WakeExplanation), StringComparison.Ordinal);

        server.Answer(2, ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        await demo.WaitAsync(TimeSpan.FromSeconds(5));
        await screen.Ui.DrainedAsync();
        Assert.False(screen.Read(v => v.IsWakeCardVisible));
        Assert.Equal(LoginProblem.None, screen.Read(v => v.Problem));
    }

    [Fact]
    public async Task Card_PastTheUsualTime_SaysStill_AndNeverShowsAProblem()
    {
        var handler = new ScriptedHandler((_, _) => Waking());
        using var screen = LoginScreen.Open(handler);

        _ = screen.Start(v => v.CheckServerAsync());
        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await screen.SettledAsync();
        await screen.PassToAsync(95);
        Assert.Equal("Getting the server ready", screen.Read(v => v.WakeTitle));

        await screen.PassToAsync(105);
        Assert.Equal("Still waking the database", screen.Read(v => v.WakeTitle));
        Assert.Contains("longer than usual", screen.Read(v => v.WakeExplanation), StringComparison.Ordinal);
        Assert.Equal(0.95, screen.Read(v => v.StepProgress), 3); // never shown as full while still waiting
        Assert.Equal(LoginProblem.None, screen.Read(v => v.Problem));
        Assert.False(demo.IsCompleted);
    }

    [Fact]
    public async Task Card_HiddenWhenTheServerIsReady_AndNoSignInWaits()
    {
        var handler = new ScriptedHandler((_, _) => Ready());
        using var screen = LoginScreen.Open(handler);

        await screen.RunUntilAsync(screen.Start(v => v.CheckServerAsync()), 10);

        Assert.False(screen.Read(v => v.IsWakeCardVisible));
        Assert.Equal(WakeStep.None, screen.Read(v => v.Step));
    }

    [Fact]
    public async Task Card_Clock_IsOneWake_AcrossTheReadinessCheckAndTheSignIn()
    {
        var server = new HoldingServer();
        using var screen = LoginScreen.Open(server);

        _ = screen.Start(v => v.CheckServerAsync()); // the window opens on a cold server; the person clicks a little later
        await LoginScreen.WaitForAsync(() => server.Arrived == 1);
        await screen.SettledAsync();
        await screen.PassToAsync(20);
        Assert.Equal("0:20", screen.Read(v => v.WakeClock));

        var demo = screen.Start(v => v.DemoCommand.ExecuteAsync());
        await screen.SettledAsync();
        await screen.PassToAsync(30);
        Assert.Equal("0:30", screen.Read(v => v.WakeClock)); // the same wake, counted from its start: not reset by the click

        server.Answer(0, Ready());
        await LoginScreen.WaitForAsync(() => server.Arrived == 2);
        await screen.SettledAsync();
        Assert.Equal(WakeStep.SigningIn, screen.Read(v => v.Step));
        Assert.Equal("0:30", screen.Read(v => v.WakeClock)); // and not reset by step 3 either
        server.Answer(1, ScriptedHandler.Json(HttpStatusCode.OK, DemoTokens));
        await demo.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void StepRows_NameTheirStateForScreenReaders()
    {
        Assert.Equal("Starting the server: in progress", new WakeStepRow("Starting the server", "about 30–45 s", WakeStepState.Active, null).AutomationName);
        Assert.Equal("Started the server: done in 0:34", new WakeStepRow("Started the server", "about 30–45 s", WakeStepState.Done, "0:34").AutomationName);
        Assert.Equal("Signing you in: to come", new WakeStepRow("Signing you in", string.Empty, WakeStepState.Pending, null).AutomationName);
        Assert.Equal("Getting the server ready", WakingTexts.CardTitle);
    }

    /// <summary>
    /// The clock ticks every second, the rows of the steps do not: rebuilding them each second restarted the spinner of the active step and
    /// rebuilt the rows for screen readers (review of 2.1.9). They change when a step does.
    /// </summary>
    [Fact]
    public async Task Card_TheClockTicks_ButTheStepRowsChangeOnlyWithAStep()
    {
        var server = new HoldingServer();
        using var screen = LoginScreen.Open(server);

        _ = screen.Start(v => v.CheckServerAsync());
        await LoginScreen.WaitForAsync(() => server.Arrived == 1);
        await screen.SettledAsync();
        await screen.PassToAsync(10);
        Assert.Equal(WakeStep.StartingServer, screen.Read(v => v.Step));

        var rows = 0;
        var clocks = 0;
        screen.Ui.Run(() =>
        {
            screen.Vm.PropertyChanged += (_, e) =>
            {
                rows += e.PropertyName == nameof(LoginViewModel.Steps) ? 1 : 0;
                clocks += e.PropertyName == nameof(LoginViewModel.WakeClock) ? 1 : 0;
            };
            return true;
        });
        await screen.PassToAsync(20);
        await screen.Ui.DrainedAsync();

        Assert.Equal(0, rows);
        Assert.InRange(clocks, 9, 11);
        Assert.Equal("0:20", screen.Read(v => v.WakeClock));
    }
}
