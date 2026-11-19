using System.Windows.Input;

namespace Lifestream.Client.Common;

/// <summary>
/// The one asynchronous command (SHELL-10). While it runs <see cref="IsRunning"/> is true and <see cref="CanExecute"/> is
/// false, so a second click is ignored. An exception from the work is captured: it is stored in
/// <see cref="LastException"/>, passed to <see cref="DefaultErrorHandler"/> (which logs it and shows a friendly dialog),
/// and never escapes, so a command cannot terminate the process. Cancellation is not an error.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The token source is disposed at the end of every run; nothing outlives it.")]
public sealed class AsyncRelayCommand : ICommand, System.ComponentModel.INotifyPropertyChanged
{
    private readonly Func<object?, CancellationToken, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly bool _allowConcurrent;
    private readonly Lock _gate = new();
    private readonly List<CancellationTokenSource> _active = [];
    private Exception? _lastException;

    public AsyncRelayCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null, OperationRunner? runner = null)
        : this((_, ct) => execute(ct), canExecute is null ? null : _ => canExecute(), runner: runner)
    {
    }

    /// <param name="execute">The work.</param>
    /// <param name="canExecute">Extra condition for <see cref="CanExecute"/>.</param>
    /// <param name="allowConcurrent">True for a command that may be started again while it runs (sidebar navigation: the newer run supersedes the older, which the work cancels itself).</param>
    /// <param name="runner">The runner of the owning view model: the whole run is inside one of its operations, so every API call it makes can be cancelled and shows the waking state (SHELL-07). Null only for a command that makes no API call.</param>
    public AsyncRelayCommand(Func<object?, CancellationToken, Task> execute, Func<object?, bool>? canExecute = null, bool allowConcurrent = false, OperationRunner? runner = null)
    {
        Runner = runner;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _allowConcurrent = allowConcurrent;
    }

    /// <summary>The operation runner this command runs through, or null for a command that makes no API call (see <see cref="NoOperationRunnerAttribute"/>).</summary>
    public OperationRunner? Runner { get; }

    /// <summary>Receives every captured exception. The composition root points it at the error reporter.</summary>
    public static Action<Exception> DefaultErrorHandler { get; set; } = static _ => { };

    private static readonly AsyncLocal<Action<Exception>?> ScopedErrorHandler = new();

    /// <summary>
    /// Sends <paramref name="exception"/> to the handler of the current async flow if <see cref="CaptureErrors"/> set one, otherwise to
    /// <see cref="DefaultErrorHandler"/>.
    /// </summary>
    internal static void ReportError(Exception exception) => (ScopedErrorHandler.Value ?? DefaultErrorHandler)(exception);

    /// <summary>
    /// Makes <paramref name="handler"/> receive the captured exceptions of the code that runs under the caller (and what it awaits), instead of
    /// <see cref="DefaultErrorHandler"/>, until the result is disposed. Other threads and other tests keep the default, so a test can look at the
    /// errors of its own commands without swapping the process-wide handler under tests that run in parallel.
    /// </summary>
    public static IDisposable CaptureErrors(Action<Exception> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var previous = ScopedErrorHandler.Value;
        ScopedErrorHandler.Value = handler;
        return new ErrorCapture(previous);
    }

    private sealed class ErrorCapture(Action<Exception>? previous) : IDisposable
    {
        public void Dispose() => ScopedErrorHandler.Value = previous;
    }

    public event EventHandler? CanExecuteChanged;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

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

    public Exception? LastException
    {
        get => _lastException;
        private set
        {
            _lastException = value;
            PropertyChanged?.Invoke(this, new(nameof(LastException)));
        }
    }

    public bool CanExecute(object? parameter) => (_allowConcurrent || !IsRunning) && (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

    /// <summary>Runs the command and completes when it has finished; never throws for a failure of the work itself.</summary>
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var token = cts.Token;
        LastException = null;
        bool started;
        lock (_gate)
        {
            _active.Add(cts);
            started = _active.Count == 1;
        }

        if (started)
        {
            OnRunningChanged();
        }

        try
        {
            if (Runner is { } runner)
            {
                await runner.ScopeAsync(scoped => _execute(parameter, scoped), token).ConfigureAwait(true);
            }
            else
            {
                await _execute(parameter, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user: not an error.
        }
        catch (Exception ex)
        {
            LastException = ex;
            ReportError(ex);
        }
        finally
        {
            bool stopped;
            lock (_gate)
            {
                _active.Remove(cts);
                stopped = _active.Count == 0;
            }

            cts.Dispose();
            if (stopped)
            {
                OnRunningChanged();
            }
        }
    }

    /// <summary>Asks the running work to stop.</summary>
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
                // The run finished while it was being cancelled.
            }
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private void OnRunningChanged()
    {
        PropertyChanged?.Invoke(this, new(nameof(IsRunning)));
        RaiseCanExecuteChanged();
    }
}
