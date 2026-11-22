using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Navigation;

namespace Lifestream.Client.Tests.Fakes;

/// <summary>
/// One sign-in window under test: the real view model on a UI thread of its own, over the real API client and wake handler, with a manual
/// clock that everything waits on. Only the network (<paramref name="Server"/>) and the clock are stand-ins.
/// </summary>
public sealed record LoginScreen(LoginViewModel Vm, ManualTimeProvider Clock, ServerStatus Status, Rig Rig, TestUiThread Ui, HttpMessageHandler Server) : IDisposable
{
    private readonly List<Task> _operations = [];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The client objects live as long as the view model of the test.")]
    public static LoginScreen Open(HttpMessageHandler server)
    {
        var rig = new Rig();
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var policy = new WakePolicy { Clock = clock };
        var http = new HttpClient(new WakeAwareHandler(policy, status, server)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        var inner = new LifestreamApiClient(http, rig.Session);
        var ui = new TestUiThread();
        var vm = ui.Run(() => new LoginViewModel(ScopedApiClient.Wrap(inner, policy, strict: true), rig.Session, rig.Get<IAppFlow>(), status) { Username = "u", Password = TestSecrets.P, Clock = clock });
        return new LoginScreen(vm, clock, status, rig, ui, server);
    }

    /// <summary>Starts an operation of the view model on its UI thread, as a click or the window's appearing starts it.</summary>
    public Task Start(Func<LoginViewModel, Task> operation)
    {
        var task = Ui.Run(() => operation(Vm));
        _operations.Add(task);
        return task;
    }

    /// <summary>Reads the view model on its UI thread.</summary>
    public T Read<T>(Func<LoginViewModel, T> read) => Ui.Run(() => read(Vm));

    /// <summary>
    /// Nothing is in between: every operation is over, or waiting on the clock (a retry delay, the budget), or on a request the test's
    /// <see cref="HoldingServer"/> holds.
    /// </summary>
    public bool Waiting => _operations.TrueForAll(o => o.IsCompleted) || Clock.DelaysWaiting > 0 || Server is HoldingServer { Arrived: > 0 };

    public static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2500 && !condition(); i++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "the awaited state was not reached");
    }

    /// <summary>Waits until the screen is waiting again and its UI thread has run everything posted to it (twice: what ran may start more).</summary>
    public async Task SettledAsync()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            await WaitForAsync(() => Waiting);
            await Ui.DrainedAsync();
        }

        await WaitForAsync(() => Waiting);
    }

    /// <summary>Steps the clock half a second at a time, letting the screen settle after any step that fired one of its timers.</summary>
    public async Task PassAsync(double seconds)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            var fired = Clock.TimersFired;
            Clock.Advance(TimeSpan.FromMilliseconds(500));
            if (Clock.TimersFired != fired)
            {
                await SettledAsync();
            }
        }
    }

    /// <summary>Moves the clock to <paramref name="second"/> seconds after the screen opened.</summary>
    public Task PassToAsync(double second) => PassAsync(second - Clock.Elapsed.TotalSeconds);

    /// <summary>Lets time pass, a second at a time, until <paramref name="operation"/> ends (at most <paramref name="seconds"/>), then waits for its end.</summary>
    public async Task RunUntilAsync(Task operation, int seconds)
    {
        await SettledAsync();
        for (var i = 0; i < seconds && !operation.IsCompleted; i++)
        {
            await PassAsync(1);
        }

        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await Ui.DrainedAsync();
    }

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
