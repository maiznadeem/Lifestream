using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;

namespace Lifestream.Client.Tests;

/// <summary>
/// What the sign-in window tells someone who is waiting for a waking server, against the wake as measured on 2026-11-20: 77 to 95 s from
/// cold. Up to 2.1.2 the notice said "up to a minute", and a readiness check that ended on "still waking" turned the pill to "Can't reach
/// the server". Through the real client and handler; only the network and the clock are stand-ins.
/// </summary>
public sealed class LoginWakeMessagesTests
{
    private sealed record Screen(LoginViewModel Vm, ManualTimeProvider Clock, ServerStatus Status, Rig Rig) : IDisposable
    {
        public void Dispose()
        {
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The client objects live as long as the view model of the test.")]
    private static Screen Open(HttpMessageHandler server, WakePolicy? basePolicy = null)
    {
        var rig = new Rig();
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var policy = (basePolicy ?? new WakePolicy()) with { Clock = clock };
        var http = new HttpClient(new WakeAwareHandler(policy, status, server)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        var inner = new LifestreamApiClient(http, rig.Session);
        var vm = new LoginViewModel(ScopedApiClient.Wrap(inner, policy, strict: true), rig.Session, rig.Get<IAppFlow>(), status) { Username = "u", Password = TestSecrets.P, Clock = clock };
        return new Screen(vm, clock, status, rig);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2500 && !condition(); i++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "the awaited state was not reached");
    }

    /// <summary>
    /// Waits until an operation that was just started has reached the clock (the handler puts its waking timer and its budget there before its
    /// first attempt) or is over already (an answer that needs no waiting), and gives its first attempt the same real moment a step used to
    /// give. Before this, the first step of the clock went ahead of an operation that had not started counting yet.
    /// </summary>
    private static async Task StartedAsync(ManualTimeProvider clock, Task operation)
    {
        await WaitForAsync(() => clock.TimersWaiting > 0 || operation.IsCompleted);
        await Task.Delay(1);
    }

    /// <summary>
    /// Steps the manual clock half a second at a time. Everything under test waits on that clock (the handler's waking timer, its retry delays
    /// and its budget, the view model's "longer than usual" timer), so it can only move when a step fires one of the clock's timers. After such a
    /// step the code that was woken gets the same real moment to run as before, ahead of the next step; a step that fired nothing woke nothing,
    /// and is not waited after. (The pause used to follow every step: hundreds of them, about 15 ms each on Windows, for a clock that had mostly
    /// nothing due: 31 s for this class.)
    /// </summary>
    private static async Task PassAsync(ManualTimeProvider clock, double seconds)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            var fired = clock.TimersFired;
            clock.Advance(TimeSpan.FromMilliseconds(500));
            if (clock.TimersFired != fired)
            {
                await Task.Delay(1);
            }
        }
    }

    [Fact]
    public void TheNotices_SayAboutAMinuteAndAHalf_OnEveryScreen()
    {
        Assert.Equal("Waking up the server, this can take about a minute and a half", WakingTexts.Operation);
        Assert.StartsWith(WakingTexts.Operation + ".", WakingTexts.SignIn, StringComparison.Ordinal);
        Assert.Contains("Step 1 of 2", WakingTexts.SignIn, StringComparison.Ordinal);
        Assert.Contains("Step 2 of 2", WakingTexts.SignInDatabase, StringComparison.Ordinal);
        Assert.Contains("about a minute", WakingTexts.SignInDatabase, StringComparison.Ordinal);
        Assert.DoesNotContain("up to a minute", WakingTexts.Operation, StringComparison.OrdinalIgnoreCase);

        // "Longer than usual" starts after the longest wake measured (95 s) and before the attempt's own two minutes end.
        Assert.InRange(WakingTexts.LongerThanUsualAfter.TotalSeconds, 96, 110);
        Assert.True(WakingTexts.LongerThanUsualAfter < new WakePolicy().Budget);
    }

    [Fact]
    public async Task SignIn_DuringAWakeOf95Seconds_ShowsTheExpectedTime_AndNeverAnythingThatLooksLikeAFailure()
    {
        var server = new HoldingServer();
        using var screen = Open(server);
        var vm = screen.Vm;

        var signIn = vm.DemoCommand.ExecuteAsync();
        await WaitForAsync(() => server.Arrived == 1);
        await PassAsync(screen.Clock, 3.5);
        Assert.True(vm.IsServerWaking);
        Assert.Equal(WakingTexts.SignIn, vm.WakingText);
        Assert.Equal("Starting the server", vm.ServerText); // nothing has answered: the container is starting

        foreach (var second in new[] { 20, 40, 60, 80, 95 })
        {
            await PassAsync(screen.Clock, second - screen.Clock.Elapsed.TotalSeconds);
            Assert.True(vm.IsBusy, $"at {second} s");
            Assert.True(vm.CanCancel, $"at {second} s");
            Assert.Equal(LoginProblem.None, vm.Problem);
            Assert.Null(vm.Message);
            Assert.Equal(WakingTexts.SignIn, vm.WakingText);
            Assert.Equal("Opening the demo...", vm.DemoText);
            Assert.NotEqual(ServerReadiness.Unreachable, vm.Server);
        }

        server.Answer(0, ScriptedHandler.Json(HttpStatusCode.OK, """{"accessToken":"a","refreshToken":"r","accessTokenExpiresUtc":"2030-01-01T00:00:00Z","user":{"staffId":1,"displayName":"Demo","role":"Staff","isDemo":true,"canManageStaff":false}}"""));
        await signIn.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoginProblem.None, vm.Problem);
        Assert.NotNull(screen.Rig.Session.AccessToken);
        Assert.Equal(1, server.Arrived);
    }

    [Fact]
    public async Task SignIn_PastTheExpectedTime_SaysLongerThanUsual_StillAsANotice_AndGoesBackWhenTheWakeEnds()
    {
        var server = new HoldingServer();
        using var screen = Open(server);
        var vm = screen.Vm;
        var changes = 0;
        vm.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(LoginViewModel.WakingText) ? 1 : 0;

        var signIn = vm.SignInCommand.ExecuteAsync();
        await WaitForAsync(() => server.Arrived == 1);
        await PassAsync(screen.Clock, 102);
        Assert.Equal(WakingTexts.SignIn, vm.WakingText); // waking has shown for 99 s: not yet

        await PassAsync(screen.Clock, 3);
        Assert.Equal(WakingTexts.SignInLate, vm.WakingText);
        Assert.Equal(1, changes);
        Assert.True(vm.IsBusy);
        Assert.True(vm.CanCancel);
        Assert.Equal(LoginProblem.None, vm.Problem); // still a notice of an attempt that is running, with its Cancel
        Assert.Null(vm.Message);

        server.Answer(0, ScriptedHandler.Problem(HttpStatusCode.Unauthorized, "invalid_credentials"));
        await signIn.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsServerWaking);
        Assert.NotEqual(WakingTexts.SignInLate, vm.WakingText); // "longer than usual" ends with the wake it was about
        Assert.Equal(LoginProblem.WrongCredentials, vm.Problem);
    }

    [Fact]
    public async Task SignIn_ThatGaveUpAfterItsTwoMinutes_SaysLongerThanUsual_WithRetry_NotUpToAMinute()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, $$"""{"title":"waking","status":503,"code":"{{ProblemCodes.DatabaseWaking}}"}""", ("Retry-After", "15")));
        using var screen = Open(handler);
        var vm = screen.Vm;

        var signIn = vm.SignInCommand.ExecuteAsync();
        await StartedAsync(screen.Clock, signIn);
        for (var i = 0; i < 300 && !signIn.IsCompleted; i++)
        {
            await PassAsync(screen.Clock, 1);
        }

        await signIn.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(screen.Clock.Elapsed.TotalSeconds, 100, 125);
        Assert.Equal(LoginProblem.ServerWaking, vm.Problem);
        Assert.Contains("longer than usual", vm.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("up to a minute", vm.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.CanRetry);
        Assert.Equal("Waking the database", vm.ServerText); // the server did answer, every time: it is its database that is not back
    }

    [Theory]
    [InlineData("database_waking", ServerReadiness.Waking, "Waking the database")]
    [InlineData("database_resting", ServerReadiness.Resting, "Demo database paused until the 1st")]
    public async Task Pill_AfterAReadinessCheckTheServerAnswered_SaysWhatTheServerSaid_NotCantReach(string code, ServerReadiness expected, string text)
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, $$"""{"title":"t","status":503,"code":"{{code}}","database":"x"}""", ("Retry-After", "2")));
        using var screen = Open(handler);

        var check = screen.Vm.CheckServerAsync();
        await StartedAsync(screen.Clock, check);
        for (var i = 0; i < 300 && !check.IsCompleted; i++)
        {
            await PassAsync(screen.Clock, 1);
        }

        await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, screen.Vm.Server);
        Assert.Equal(text, screen.Vm.ServerText);
    }

    [Fact]
    public async Task AWakeThatOutlastsTheTwoMinuteWait_IsAskedAboutOnceMore_AndEndsInServerReadyByItself()
    {
        // The database is back after 150 s: longer than any wake measured, and longer than one wait of the readiness check.
        ManualTimeProvider? clock = null;
        var handler = new ScriptedHandler((_, _) => clock!.Elapsed < TimeSpan.FromSeconds(150)
            ? ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, $$"""{"title":"waking","status":503,"code":"{{ProblemCodes.DatabaseWaking}}","database":"waking"}""", ("Retry-After", "2"))
            : ScriptedHandler.Json(HttpStatusCode.OK, """{"database":"ready"}"""));
        using var screen = Open(handler);
        clock = screen.Clock;

        var check = screen.Vm.CheckServerAsync();
        await StartedAsync(screen.Clock, check);
        await PassAsync(screen.Clock, 95);
        Assert.Equal(WakingTexts.SignInDatabase, screen.Vm.WakingText);

        await PassAsync(screen.Clock, 40); // 135 s: the first wait is over, the second has shown "waking" again
        Assert.False(check.IsCompleted);
        Assert.True(screen.Vm.IsServerWaking);
        Assert.Equal(WakingTexts.SignInLate, screen.Vm.WakingText); // one wake, counted from its start: longer than usual by now
        Assert.Equal("Waking the database", screen.Vm.ServerText);
        Assert.NotEqual(ServerReadiness.Unreachable, screen.Vm.Server);

        await PassAsync(screen.Clock, 20);
        await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Server ready", screen.Vm.ServerText);
        Assert.False(screen.Vm.IsServerWaking);
    }

    [Fact]
    public async Task ADatabaseThatNeverComesBack_IsAskedAboutForTwoWaits_ThenLeftAlone()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, $$"""{"title":"waking","status":503,"code":"{{ProblemCodes.DatabaseWaking}}","database":"waking"}""", ("Retry-After", "2")));
        using var screen = Open(handler);

        var check = screen.Vm.CheckServerAsync();
        await StartedAsync(screen.Clock, check);
        for (var i = 0; i < 400 && !check.IsCompleted; i++)
        {
            await PassAsync(screen.Clock, 1);
        }

        await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(screen.Clock.Elapsed.TotalSeconds, 230, 245);
        Assert.Equal("Waking the database", screen.Vm.ServerText);
        var asked = handler.Requests.Count;

        await PassAsync(screen.Clock, 120);
        Assert.Equal(asked, handler.Requests.Count); // nothing keeps knocking on a database that is not coming back
    }

    [Fact]
    public async Task Pill_WhenNoAnswerComesAtAll_StillSaysCantReach()
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("no route"));
        using var screen = Open(handler);

        var check = screen.Vm.CheckServerAsync();
        await StartedAsync(screen.Clock, check);
        for (var i = 0; i < 300 && !check.IsCompleted; i++)
        {
            await PassAsync(screen.Clock, 1);
        }

        await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ServerReadiness.Unreachable, screen.Vm.Server);
        Assert.Equal("Can't reach the server", screen.Vm.ServerText);
    }

    [Fact]
    public async Task Pill_TurnsToReady_WithinTwoSecondsOfTheDatabaseAnswering()
    {
        // The server asks the readiness poll back every 2 s while its database wakes (Z3); the database is "back" from the 60th second here.
        ManualTimeProvider? clock = null;
        var handler = new ScriptedHandler((_, _) => clock!.Elapsed < TimeSpan.FromSeconds(60)
            ? ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, $$"""{"title":"waking","status":503,"code":"{{ProblemCodes.DatabaseWaking}}","database":"waking"}""", ("Retry-After", "2"))
            : ScriptedHandler.Json(HttpStatusCode.OK, """{"database":"ready"}"""));
        using var screen = Open(handler);
        clock = screen.Clock;

        var check = screen.Vm.CheckServerAsync();
        await StartedAsync(screen.Clock, check);
        await PassAsync(screen.Clock, 59.5);
        Assert.False(check.IsCompleted);
        Assert.Equal("Waking the database", screen.Vm.ServerText);
        Assert.Equal(WakingTexts.SignInDatabase, screen.Vm.WakingText);

        await PassAsync(screen.Clock, 2.5);
        await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Server ready", screen.Vm.ServerText);
        Assert.InRange(screen.Clock.Elapsed.TotalSeconds, 60, 62.5);
    }
}
