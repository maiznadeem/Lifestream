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
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private Exception? _lastException;

    public AsyncRelayCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null)
        : this((_, ct) => execute(ct), canExecute is null ? null : _ => canExecute())
    {
    }

    public AsyncRelayCommand(Func<object?, CancellationToken, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>Receives every captured exception. The composition root points it at the error reporter.</summary>
    public static Action<Exception> DefaultErrorHandler { get; set; } = static _ => { };

    public event EventHandler? CanExecuteChanged;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning != value)
            {
                _isRunning = value;
                PropertyChanged?.Invoke(this, new(nameof(IsRunning)));
                RaiseCanExecuteChanged();
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

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

    /// <summary>Runs the command and completes when it has finished; never throws for a failure of the work itself.</summary>
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        LastException = null;
        IsRunning = true;
        try
        {
            await _execute(parameter, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user: not an error.
        }
        catch (Exception ex)
        {
            LastException = ex;
            DefaultErrorHandler(ex);
        }
        finally
        {
            var cts = _cts;
            _cts = null;
            cts?.Dispose();
            IsRunning = false;
        }
    }

    /// <summary>Asks the running work to stop.</summary>
    public void Cancel() => _cts?.Cancel();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
