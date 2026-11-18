namespace Lifestream.Client.Api;

public enum ServerState
{
    Online,
    Waking,
}

/// <summary>Observable state of the connection to the API: online, or waking up after a cold start (SHELL-07).</summary>
public interface IServerStatus
{
    ServerState State { get; }

    event EventHandler? Changed;

    /// <summary>Raised when a response carries the X-Demo-Reset header: the demo data was reset since the last call.</summary>
    event EventHandler? DemoResetObserved;
}

public sealed class ServerStatus : IServerStatus
{
    private readonly Lock _gate = new();
    private int _wakingCalls;

    public ServerState State { get; private set; } = ServerState.Online;

    public event EventHandler? Changed;

    public event EventHandler? DemoResetObserved;

    /// <summary>A call started waiting on the server (slow, or a waking response). Balanced by <see cref="Leave"/>.</summary>
    internal void Enter()
    {
        bool changed;
        lock (_gate)
        {
            _wakingCalls++;
            changed = State != ServerState.Waking;
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

    internal void NotifyDemoReset() => DemoResetObserved?.Invoke(this, EventArgs.Empty);
}
