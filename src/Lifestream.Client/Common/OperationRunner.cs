using Lifestream.Client.Api;

namespace Lifestream.Client.Common;

/// <summary>
/// The one mechanism behind every API-calling operation of a view model (SHELL-07): screen loads, detail panes, saves, deletes, dialog
/// submits, record pickers and sign-in. While an operation runs, an ambient <see cref="OperationScope"/> is set, so every API call made
/// inside it (however deep, whatever token the caller passed) is linked to the scope's cancellation token by <see cref="ScopedApiClient"/>:
/// <see cref="Cancel"/> cancels the HTTP request itself. The runner also tracks the calls in flight and says when one is waking the server
/// (<see cref="IsWaking"/>: the server is waking and a call has been waiting for <c>WakingAfter</c>, 3 s by default), so the view model
/// can show "Waking up the server" with a Cancel.
/// </summary>
public sealed class OperationRunner : IDisposable
{
    private readonly Func<IServerStatus?> _status;
    private readonly Func<TimeProvider> _clock;
    private readonly Func<TimeSpan> _wakingAfter;
    private readonly Lock _gate = new();
    private readonly List<CancellationTokenSource> _active = [];
    private readonly List<long> _calls = [];
    private ITimer? _timer;
    private bool _waking;

    public OperationRunner(Func<IServerStatus?> status, Func<TimeProvider> clock, Func<TimeSpan>? wakingAfter = null)
    {
        _status = status;
        _clock = clock;
        _wakingAfter = wakingAfter ?? (() => DefaultWakingAfter);
    }

    /// <summary>How long a call waits, while the server is waking, before the waking state shows (SHELL-07).</summary>
    public static TimeSpan DefaultWakingAfter { get; } = TimeSpan.FromSeconds(3);

    /// <summary>Raised (on any thread) when <see cref="IsWaking"/> or <see cref="IsRunning"/> may have changed (an operation started or ended, or the waking state flipped).</summary>
    public event EventHandler? Changed;

    /// <summary>True while an operation of this runner is running.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _active.Count > 0;
            }
        }
    }

    /// <summary>True while a call of this runner is waiting on a waking server (shown after 3 s).</summary>
    public bool IsWaking => _waking;

    /// <summary>True when an API call of this runner is in flight right now.</summary>
    public bool HasCallInFlight
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count > 0;
            }
        }
    }

    /// <summary>
    /// Runs API work off the UI thread inside a scope and returns its result. The work receives the scope's token; the token of every API call
    /// inside is linked to it anyway. A cancelled scope throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Start(scope => Task.Run(() => work(scope.Token), scope.Token), ct);
    }

    /// <summary><see cref="RunAsync{T}"/> for work with no result.</summary>
    public Task RunAsync(Func<CancellationToken, Task> work, CancellationToken ct = default) => RunAsync(work, ct, out _);

    /// <summary>
    /// <see cref="RunAsync(Func{CancellationToken, Task}, CancellationToken)"/> that also hands back the operation's scope, so the caller can ask
    /// whether a call of this very operation is waiting on the server right now (<see cref="OperationScope.HasCallInFlight"/>).
    /// </summary>
    internal Task RunAsync(Func<CancellationToken, Task> work, CancellationToken ct, out OperationScope scope)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Start(
            async s =>
            {
                await Task.Run(() => work(s.Token), s.Token).ConfigureAwait(false);
                return true;
            },
            ct,
            out scope);
    }

    /// <summary>
    /// Runs a whole command body inside a scope, on the caller's context (the body may touch the UI): every API call it makes, directly or
    /// through <see cref="RunAsync{T}"/>, is cancellable by <see cref="Cancel"/> and counted for the waking state.
    /// </summary>
    public Task ScopeAsync(Func<CancellationToken, Task> body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Start(async scope =>
        {
            await body(scope.Token).ConfigureAwait(true);
            return true;
        }, ct);
    }

    /// <summary><see cref="ScopeAsync(Func{CancellationToken, Task}, CancellationToken)"/> for a body that returns a result.</summary>
    public Task<T> ScopeAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Start(scope => body(scope.Token), ct);
    }

    /// <summary>Cancels every operation of this runner: the HTTP requests inside them are cancelled.</summary>
    public void Cancel()
    {
        CancellationTokenSource[] running;
        lock (_gate)
        {
            running = [.. _active];
        }

        foreach (var cts in running)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation finished while it was being cancelled.
            }
        }
    }

    /// <summary>Re-reads the server status (the view model attached to a different one) and re-evaluates the waking state.</summary>
    public void StatusChanged() => Recompute();

    /// <summary>Notes that an API call started now (on this runner's clock) and returns the handle to pass to <see cref="CallEnded"/>.</summary>
    internal long CallStarted()
    {
        var started = _clock().GetTimestamp();
        lock (_gate)
        {
            _calls.Add(started);
        }

        Recompute();
        return started;
    }

    internal void CallEnded(long startTimestamp)
    {
        lock (_gate)
        {
            _calls.Remove(startTimestamp);
        }

        Recompute();
    }

    private Task<T> Start<T>(Func<OperationScope, Task<T>> run, CancellationToken ct) => Start(run, ct, out _);

    private Task<T> Start<T>(Func<OperationScope, Task<T>> run, CancellationToken ct, out OperationScope started)
    {
        var parent = OperationScope.Current is { } current && ReferenceEquals(current.Runner, this) ? current : null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, parent?.Token ?? CancellationToken.None);
        var scope = new OperationScope(this, cts.Token, parent);
        started = scope;
        lock (_gate)
        {
            _active.Add(cts);
        }

        Changed?.Invoke(this, EventArgs.Empty);

        // The ambient scope is set only while the work starts: it flows into the work (and its continuations) and is put back before
        // control returns to the caller, so a caller that does not await us is never mistaken for being inside the scope.
        var previous = OperationScope.Swap(scope);
        Task<T> task;
        try
        {
            task = Finish(run(scope), cts);
        }
        finally
        {
            OperationScope.Swap(previous);
        }

        return task;
    }

    private async Task<T> Finish<T>(Task<T> work, CancellationTokenSource cts)
    {
        try
        {
            return await work.ConfigureAwait(true);
        }
        finally
        {
            lock (_gate)
            {
                _active.Remove(cts);
            }

            cts.Dispose();
            Recompute();
            Changed?.Invoke(this, EventArgs.Empty); // IsRunning changed too
        }
    }

    private void Recompute()
    {
        var clock = _clock();
        var waking = false;
        TimeSpan? nextCheck = null;
        if (_status()?.State == ServerState.Waking)
        {
            var after = _wakingAfter();
            lock (_gate)
            {
                foreach (var started in _calls)
                {
                    var waited = clock.GetElapsedTime(started);
                    if (waited >= after)
                    {
                        waking = true;
                    }
                    else
                    {
                        var left = after - waited;
                        nextCheck = nextCheck is { } n && n < left ? n : left;
                    }
                }
            }
        }

        lock (_gate)
        {
            _timer?.Dispose();
            _timer = nextCheck is { } due ? clock.CreateTimer(_ => Recompute(), null, due, Timeout.InfiniteTimeSpan) : null;
        }

        var changed = waking != _waking;
        _waking = waking;
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        Cancel();
    }
}

/// <summary>The ambient context of a running operation (see <see cref="OperationRunner"/>).</summary>
public sealed class OperationScope
{
    private static readonly AsyncLocal<OperationScope?> Ambient = new();

    private readonly OperationScope? _parent;
    private int _callsInFlight;

    internal OperationScope(OperationRunner runner, CancellationToken token, OperationScope? parent = null)
    {
        Runner = runner;
        Token = token;
        _parent = parent;
    }

    /// <summary>The scope the current code runs in, or null when it runs outside any operation.</summary>
    public static OperationScope? Current => Ambient.Value;

    public OperationRunner Runner { get; }

    /// <summary>Cancelled by <see cref="OperationRunner.Cancel"/>, by the caller's token and by the enclosing scope.</summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// True while an API call made inside this operation (or an operation nested in it) is waiting on the server: the request is out and the
    /// answer has not come back. False while the operation does its own work (before its first call, between calls, after the last one).
    /// </summary>
    public bool HasCallInFlight => Volatile.Read(ref _callsInFlight) > 0;

    /// <summary>Notes that an API call of this operation started; balanced by <see cref="CallEnded"/>.</summary>
    internal void CallStarted()
    {
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            Interlocked.Increment(ref scope._callsInFlight);
        }
    }

    internal void CallEnded()
    {
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            Interlocked.Decrement(ref scope._callsInFlight);
        }
    }

    internal static OperationScope? Swap(OperationScope? next)
    {
        var previous = Ambient.Value;
        Ambient.Value = next;
        return previous;
    }
}

/// <summary>
/// Marks a command property of a view model that deliberately does not run through an <see cref="OperationRunner"/> (it makes no API call and
/// never waits on one). The reason is read by the inventory test, so every exemption is reviewed.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method)]
public sealed class NoOperationRunnerAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Marks a view model method that calls the API directly but is only ever invoked by the runner-backed template (the load, detail, delete
/// and submit hooks), so the call is already inside an operation. The reason names the template that calls it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RunsInOperationAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>Marks an API method that is deliberately called outside an operation (warm-up and sign-out are made by services, not by a screen).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OutsideOperationAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>The wording of the waking state, shared by every view.</summary>
public static class WakingTexts
{
    /// <summary>Shown on a screen, an editor, a dialog or a pane while a call waits on a waking server.</summary>
    public const string Operation = "Waking up the server, this can take up to a minute";
}
