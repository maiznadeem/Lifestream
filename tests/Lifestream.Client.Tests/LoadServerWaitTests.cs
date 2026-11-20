using System.ComponentModel;
using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Session;
using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

/// <summary>
/// The load's 10 s limit (SHELL-28) times the wait for the server and nothing else. Found in the real 2.1.0 app (real-app walk, defects 9 and 23):
/// the dashboard and the profile said "The server is taking too long to answer. Check your connection" while the server had answered every call
/// in milliseconds; the ten seconds had gone by inside the app, on a busy PC. Every test here runs a screen load through the real API client,
/// the real retry handler and the real operation scope; only the network and the clock are stand-ins.
/// </summary>
public sealed class LoadServerWaitTests
{
    private const string DonorJson = """{"id":7,"name":"Ayşe","dateOfBirth":"1990-01-02","gender":"Female","bloodType":"APos","phone":null,"address":null,"frequency":"Regular","lastDonated":null,"rowVersion":"AAA="}""";

    /// <summary>The network: every request is handed to the test, which answers it when and how it likes.</summary>
    private sealed class Server(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private int _arrived;

        /// <summary>How many requests have reached the server.</summary>
        public int Arrived => Volatile.Read(ref _arrived);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _arrived);
            return answer(request, cancellationToken);
        }

        public static HttpResponseMessage Donor() => ScriptedHandler.Json(HttpStatusCode.OK, DonorJson);
    }

    /// <summary>The app's API client as the app wires it: the scope wrapper over the real client over the real retry handler.</summary>
    private sealed class Client : IDisposable
    {
        private readonly HttpClient _http;
        private readonly LifestreamApiClient _inner;

        public Client(TimeProvider clock, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        {
            Server = new Server(answer);
            var policy = new WakePolicy { Clock = clock };
            _http = new HttpClient(new WakeAwareHandler(policy, Status, Server)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
            var session = new SessionService();
            session.Establish(FakeApiClient.Tokens());
            _inner = new LifestreamApiClient(_http, session);
            Api = ScopedApiClient.Wrap(_inner, policy, strict: true);
        }

        public Server Server { get; }

        public ServerStatus Status { get; } = new();

        public ILifestreamApiClient Api { get; }

        public void Dispose()
        {
            _inner.Dispose();
            _http.Dispose();
        }
    }

    /// <summary>A screen whose load is the given work.</summary>
    private sealed class Screen(Func<CancellationToken, Task> fetch) : ViewModelBase
    {
        public List<LoadState> States { get; } = [];

        protected override Task FetchAsync(CancellationToken ct) => fetch(ct);

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName == nameof(State))
            {
                lock (States)
                {
                    States.Add(State);
                }
            }
        }
    }

    /// <summary>
    /// The clock of a PC too busy to run the app: <see cref="Stall"/> lets time pass in one jump, and only then does each timer that came due
    /// run, once, however many of its periods went by (which is what a process that got no processor time sees when it runs again).
    /// </summary>
    private sealed class StarvedClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<StarvedTimer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_gate)
            {
                return _ticks;
            }
        }

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new StarvedTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Stall(TimeSpan by)
        {
            List<StarvedTimer> due;
            lock (_gate)
            {
                _ticks += by.Ticks;
                due = [.. _timers.Where(t => t.Due <= _ticks)];
                foreach (var timer in due)
                {
                    timer.Reschedule();
                }
            }

            foreach (var timer in due)
            {
                timer.Run();
            }
        }

        private sealed class StarvedTimer(StarvedClock owner, TimerCallback callback, object? state) : ITimer
        {
            private long _period;

            public long Due { get; private set; } = long.MaxValue;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        Due = owner._ticks + dueTime.Ticks;
                        _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                        owner._timers.Add(this);
                    }
                }

                return true;
            }

            /// <summary>Called under the clock's lock: a periodic timer is due one period from now, a one-shot timer is done.</summary>
            public void Reschedule()
            {
                if (_period > 0)
                {
                    Due = owner._ticks + _period;
                }
                else
                {
                    owner._timers.Remove(this);
                }
            }

            public void Run() => callback(state);

            public void Dispose()
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2500 && !condition(); i++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "the condition was not reached in 5 s");
    }

    public enum AppWork
    {
        BeforeTheFirstCall,
        BetweenTwoCalls,
        AfterTheLastCall,
    }

    [Theory]
    [InlineData(AppWork.BeforeTheFirstCall)]
    [InlineData(AppWork.BetweenTwoCalls)]
    [InlineData(AppWork.AfterTheLastCall)]
    public async Task Load_WhoseCallsAreAnsweredAtOnce_NeverBlamesTheServer_HoweverLongTheAppsOwnWorkTakes(AppWork slow)
    {
        var clock = new ManualTimeProvider();
        using var client = new Client(clock, (_, _) => Task.FromResult(Server.Donor()));

        // Eleven seconds go by inside the app (waiting for a free thread, reading an answer, getting ready to show it): never on the server.
        void Work(AppWork at)
        {
            if (at == slow)
            {
                clock.Advance(TimeSpan.FromSeconds(11));
            }
        }

        using var screen = new Screen(async ct =>
        {
            Work(AppWork.BeforeTheFirstCall);
            (await client.Api.GetDonorAsync(7, ct)).Unwrap();
            Work(AppWork.BetweenTwoCalls);
            (await client.Api.GetDonorAsync(8, ct)).Unwrap();
            Work(AppWork.AfterTheLastCall);
        });
        screen.Attach(client.Status, clock);

        await screen.LoadAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, client.Server.Arrived);
        Assert.DoesNotContain(LoadState.Error, screen.States); // released 2.1.0 and 2.1.1: Error, "The server is taking too long to answer"
        Assert.Equal(LoadState.Ready, screen.State);
        Assert.Null(screen.ErrorMessage);
    }

    [Fact]
    public async Task Load_OnAPcTooBusyToRunTheApp_DoesNotCountTheTimeTheAppWasNotRunning()
    {
        var clock = new StarvedClock();
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new Client(new ManualTimeProvider(), (_, _) => answer.Task); // the retry handler's own timers are not part of this test
        using var screen = new Screen(async ct => (await client.Api.GetDonorAsync(7, ct)).Unwrap());
        screen.Attach(null, clock);

        var load = screen.LoadAsync();
        await UntilAsync(() => client.Server.Arrived == 1);

        // The request is out. For half a minute the PC gives the app no processor time; then the app runs again and the answer is read.
        clock.Stall(TimeSpan.FromSeconds(30));
        var stateWhenTheAppRanAgain = screen.State;
        answer.SetResult(Server.Donor());
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoadState.Loading, stateWhenTheAppRanAgain); // released 2.1.0 and 2.1.1: Error, the 10 s were over on the wall clock
        Assert.DoesNotContain(LoadState.Error, screen.States);
        Assert.Equal(LoadState.Ready, screen.State);
        Assert.Null(screen.ErrorMessage);
    }

    [Fact]
    public async Task Load_WhenTheServerKeepsEveryCallWaiting_SaysSoOnceTheWaitingAddsUpToTenSeconds_AndCancelsTheRequest()
    {
        var clock = new ManualTimeProvider();
        var cancelled = 0;

        // Every call takes 2.5 s on the server: under the 3 s after which a single call would show "Waking up the server".
        using var client = new Client(clock, async (_, ct) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), clock, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }

            return Server.Donor();
        });
        using var screen = new Screen(async ct =>
        {
            for (var id = 1; id <= 8; id++)
            {
                (await client.Api.GetDonorAsync(id, ct)).Unwrap();
            }
        });
        screen.Attach(client.Status, clock);

        var load = screen.LoadAsync();
        for (var i = 0; i < 2000 && !load.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(2);
        }

        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoadState.Error, screen.State);
        Assert.Equal(ViewModelBase.TimeoutMessage, screen.ErrorMessage);
        Assert.Equal("The server is taking too long to answer. Check your connection, then retry.", screen.ErrorMessage);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(10), $"timed out after {clock.Elapsed}");
        Assert.InRange(client.Server.Arrived, 4, 7); // the load was stopped part-way: it never made all eight calls
        await UntilAsync(() => Volatile.Read(ref cancelled) == 1); // and the request that was out is cancelled, not left running
    }
}
