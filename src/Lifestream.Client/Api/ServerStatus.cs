namespace Lifestream.Client.Api;

public enum ServerState
{
    Online,
    Waking,
}

/// <summary>
/// What a waking server is doing, as far as its own answers tell. A wake from cold has two parts that follow each other: the
/// container starts (24 to 31 s measured), then the database resumes (42 to 58 s). The client can tell them apart: until the
/// container is up nothing answers at all, and once it is up the server says "database waking" for as long as that lasts.
/// </summary>
public enum WakePhase
{
    /// <summary>The server itself has not answered yet: its container is starting.</summary>
    ServerStarting,

    /// <summary>The server answers, and nothing it said means its database is waking: it is working on the call.</summary>
    ServerWorking,

    /// <summary>The server answers and has said that its database is still waking.</summary>
    DatabaseWaking,
}

/// <summary>Observable state of the connection to the API: online, or waking up after a cold start (SHELL-07).</summary>
public interface IServerStatus
{
    ServerState State { get; }

    /// <summary>Which part of a wake is going on. Only meaningful while <see cref="State"/> is <see cref="ServerState.Waking"/>.</summary>
    WakePhase Phase => WakePhase.ServerStarting;

    /// <summary>Raised when <see cref="State"/> or <see cref="Phase"/> changed.</summary>
    event EventHandler? Changed;

    /// <summary>Raised when a response carries the X-Demo-Reset header: the demo data was reset since the last call.</summary>
    event EventHandler? DemoResetObserved;
}

public sealed class ServerStatus : IServerStatus
{
    /// <summary>
    /// How long after an answer the server is taken to be still running: under the five minutes a container without requests is kept
    /// before it is stopped. A call that turns slow within this time is not waiting for a container to start.
    /// </summary>
    public static readonly TimeSpan ServerStaysUp = TimeSpan.FromMinutes(4);

    private readonly Lock _gate = new();
    private int _wakingCalls;
    private (TimeProvider Clock, long At)? _heard;
    private bool _serverUp;
    private bool _databaseWaking;

    public ServerState State { get; private set; } = ServerState.Online;

    public WakePhase Phase { get; private set; } = WakePhase.ServerStarting;

    public event EventHandler? Changed;

    public event EventHandler? DemoResetObserved;

    /// <summary>A call started waiting on the server (slow, or a waking response). Balanced by <see cref="Leave"/>.</summary>
    internal void Enter() => Enter(TimeProvider.System);

    /// <summary><see cref="Enter()"/> on the caller's clock (the one its answers were noted on).</summary>
    internal void Enter(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        bool changed;
        lock (_gate)
        {
            _wakingCalls++;
            changed = State != ServerState.Waking;
            if (changed)
            {
                // A new wait begins: the server counts as up when it answered a short while ago (its container is still there).
                _serverUp = _heard is { } heard && heard.Clock.GetElapsedTime(heard.At, clock.GetTimestamp()) < ServerStaysUp;
                Phase = CurrentPhase();
            }

            State = ServerState.Waking;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void Leave()
    {
        bool changed;
        lock (_gate)
        {
            _wakingCalls = Math.Max(0, _wakingCalls - 1);
            changed = _wakingCalls == 0 && State != ServerState.Online;
            if (_wakingCalls == 0)
            {
                State = ServerState.Online;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The server itself answered (not the platform in front of it). <paramref name="databaseWaking"/> is true when the answer said the
    /// database is waking, false when the answer came from the database, and null when the answer says nothing about it.
    /// </summary>
    internal void Heard(TimeProvider clock, bool? databaseWaking)
    {
        ArgumentNullException.ThrowIfNull(clock);
        bool changed;
        lock (_gate)
        {
            _heard = (clock, clock.GetTimestamp());
            _databaseWaking = databaseWaking ?? _databaseWaking;
            _serverUp = true;
            var phase = CurrentPhase();
            changed = State == ServerState.Waking && phase != Phase;
            Phase = phase;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// An answer carried X-Demo-Reset, whose value names the reset. The server repeats it on every answer of a session that began before the
    /// reset, so each reset is raised once: the same value again is ignored (before 2.1.9 every answer raised it, and the shell's reload at
    /// each one never let a screen finish loading). No value (a test, or a server older than 2.1.9 that said only "true") always raises it.
    /// </summary>
    internal void NotifyDemoReset(string? reset = null)
    {
        if (reset is not null)
        {
            lock (_gate)
            {
                if (reset == _lastReset)
                {
                    return;
                }

                _lastReset = reset;
            }
        }

        DemoResetObserved?.Invoke(this, EventArgs.Empty);
    }

    private string? _lastReset;

    private WakePhase CurrentPhase() =>
        !_serverUp ? WakePhase.ServerStarting
        : _databaseWaking ? WakePhase.DatabaseWaking
        : WakePhase.ServerWorking;
}
