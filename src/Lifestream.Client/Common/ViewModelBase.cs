using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;

namespace Lifestream.Client.Common;

/// <summary>
/// Base of every view model (SHELL-11, SHELL-12). It owns the load template: <see cref="LoadAsync"/> sets
/// <see cref="State"/> to Loading synchronously (so the skeleton shows within one frame), runs
/// <see cref="FetchAsync"/> on a thread-pool thread (so no API call touches the UI thread, SHELL-09), then calls
/// <see cref="Apply"/> back on the original context and settles on Ready, Empty or Error. The load never throws.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject, IDisposable
{
    private CancellationTokenSource? _loadCts;
    private ITimer? _timeoutTimer;
    private ITimer? _wakingTimer;
    private IServerStatus? _serverStatus;
    private SynchronizationContext? _context;

    protected ViewModelBase(LoadState initialState = LoadState.Loading)
    {
        State = initialState;
        _context = SynchronizationContext.Current;
        Operations = new OperationRunner(() => _serverStatus, () => Clock, () => WakingAfter);
        Operations.Changed += OnOperationsChanged;
    }

    /// <summary>
    /// The one runner of every API-calling operation of this view model (SHELL-07): loads, detail panes, saves, deletes, dialog submits and
    /// sign-in all run through it, so each shows the waking state after 3 s and can be cancelled down to the HTTP request.
    /// </summary>
    public OperationRunner Operations { get; }

    /// <summary>True while a call of this view model is waiting on a waking server for more than 3 s: the view shows "Waking up the server" with Cancel.</summary>
    public bool IsOperationWaking => Operations.IsWaking;

    /// <summary>The text of the waking state (the same on every screen, dialog and editor).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML, which needs instance properties.")]
    public string OperationWakingText => WakingTexts.Operation;

    /// <summary>The "Cancel" of the waking state: cancels every running operation of this view model, down to the HTTP request.</summary>
    [NoOperationRunner("It only cancels the runner; it makes no API call.")]
    public AsyncRelayCommand CancelOperationCommand => _cancelOperationCommand ??= new AsyncRelayCommand(_ =>
    {
        Operations.Cancel();
        return Task.CompletedTask;
    });

    private AsyncRelayCommand? _cancelOperationCommand;

    /// <summary>Creates a command that runs through <see cref="Operations"/>; every command of a view model is made with this (or <see cref="NoOperationRunnerAttribute"/> says why not).</summary>
    /// <param name="execute">The work.</param>
    /// <param name="canExecute">Extra condition for <see cref="AsyncRelayCommand.CanExecute"/>.</param>
    /// <param name="opensModal">True when the command opens a modal dialog (a confirmation, a form, a viewer): it is disabled while any operation of this view model runs, so the screen's "Waking up the server" notice and its Cancel can never be hidden behind a modal (SHELL-07).</param>
    protected AsyncRelayCommand Command(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null, bool opensModal = false) =>
        Track(new(execute, opensModal ? () => !Operations.IsRunning && (canExecute?.Invoke() ?? true) : canExecute, Operations), opensModal);

    /// <summary>Creates a command with a parameter that runs through <see cref="Operations"/>.</summary>
    protected AsyncRelayCommand Command(Func<object?, CancellationToken, Task> execute, Func<object?, bool>? canExecute = null, bool allowConcurrent = false, bool opensModal = false) =>
        Track(new(execute, opensModal ? p => !Operations.IsRunning && (canExecute?.Invoke(p) ?? true) : canExecute, allowConcurrent, Operations), opensModal);

    private readonly List<AsyncRelayCommand> _modalCommands = [];

    /// <summary>The commands of this view model that open a modal dialog; each is disabled while any operation of the view model runs (the <c>opensModal</c> flag of <c>Command</c>).</summary>
    public IReadOnlyList<AsyncRelayCommand> ModalCommands => _modalCommands;
    private bool _operationRunning;

    private AsyncRelayCommand Track(AsyncRelayCommand command, bool opensModal)
    {
        if (opensModal)
        {
            _modalCommands.Add(command);
        }

        return command;
    }

    /// <summary>Runs API work off the UI thread inside an operation of this view model (a detail pane, a lookup, a photo).</summary>
    protected Task<T> RunOperationAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default) => Operations.RunAsync(work, ct);

    /// <summary><see cref="RunOperationAsync{T}"/> for work with no result.</summary>
    protected Task RunOperationAsync(Func<CancellationToken, Task> work, CancellationToken ct = default) => Operations.RunAsync(work, ct);

    /// <summary>Called on the view model's context when <see cref="IsOperationWaking"/> changes.</summary>
    protected virtual void OnOperationWakingChanged(bool isWaking)
    {
    }

    private bool _operationWaking;

    private void OnOperationsChanged(object? sender, EventArgs e) =>
        Post(() =>
        {
            var running = Operations.IsRunning;
            if (running != _operationRunning)
            {
                _operationRunning = running;
                foreach (var command in _modalCommands)
                {
                    command.RaiseCanExecuteChanged();
                }
            }

            var waking = Operations.IsWaking;
            if (waking == _operationWaking)
            {
                return;
            }

            _operationWaking = waking;
            OnPropertyChanged(nameof(IsOperationWaking));
            OnOperationWakingChanged(waking);
        });

    /// <summary>The server status this view model was attached to (null before it is attached).</summary>
    protected IServerStatus? CurrentServerStatus => _serverStatus;

    /// <summary>Gives a child view model (an editor, a dialog) the status and clock of this one, so its operations show the waking state too.</summary>
    protected void AttachChild(ViewModelBase child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.Attach(_serverStatus, Clock);
    }

    /// <summary>Raised by Ctrl+F: the page's search box should take keyboard focus (SHELL-34).</summary>
    public event EventHandler? FocusSearchRequested;

    /// <summary>The page's "new record" action, run by Ctrl+N; null when the page has none.</summary>
    public virtual AsyncRelayCommand? NewCommand => null;

    public void RequestFocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The title shown in the page header, the sidebar and the window title; one string for all three (SHELL-21, SHELL-23).</summary>
    public virtual string PageTitle => string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsWaking), nameof(IsReady), nameof(IsEmpty), nameof(HasError), nameof(ShowSkeleton))]
    public partial LoadState State { get; set; }

    /// <summary>True while a save or another action is running (not the initial load); buttons show their busy state from it.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    partial void OnIsBusyChanged(bool value) => OnBusyChanged(value);

    /// <summary>Lets a view model refresh the members that depend on <see cref="IsBusy"/> (button text, command state).</summary>
    protected virtual void OnBusyChanged(bool isBusy)
    {
    }

    /// <summary>Plain-language cause shown by the error state.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>Technical detail behind the "Details" control; never shown in the main text.</summary>
    [ObservableProperty]
    public partial string? ErrorDetail { get; set; }

    public bool IsLoading => State is LoadState.Loading or LoadState.Waking;

    public bool IsWaking => State == LoadState.Waking;

    public bool IsReady => State == LoadState.Ready;

    public bool IsEmpty => State == LoadState.Empty;

    public bool HasError => State == LoadState.Error;

    /// <summary>True while a skeleton should be on screen: Loading and Waking.</summary>
    public bool ShowSkeleton => IsLoading;

    /// <summary>How long a load may stay in Loading before it turns into an error state with Retry (SHELL-28).</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Runs on a thread-pool thread. Call the API and keep the results in fields; do not touch bound collections here.</summary>
    [RunsInOperation("LoadAsync calls it inside Operations.RunAsync.")]
    protected virtual Task FetchAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Runs on the original (UI) context after a successful fetch: publish the fetched data to bound members.</summary>
    protected virtual void Apply()
    {
    }

    /// <summary>Whether the fetched data is empty, which switches to the Empty state.</summary>
    protected virtual bool HasNoData => false;

    /// <summary>Called by navigation so the model can show the waking state and so timers use the app clock.</summary>
    internal void Attach(IServerStatus? status, TimeProvider clock)
    {
        Clock = clock;
        if (_serverStatus is not null)
        {
            _serverStatus.Changed -= OnServerStatusChanged;
        }

        _serverStatus = status;
        if (status is not null)
        {
            status.Changed += OnServerStatusChanged;
        }

        Operations.StatusChanged();
    }

    /// <summary>Loads (or reloads) the data. Safe to call again: the previous load is cancelled first.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        CancelLoad();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loadCts = cts;
        _context = SynchronizationContext.Current;

        ErrorMessage = null;
        ErrorDetail = null;
        State = LoadState.Loading;
        StartTimeout(cts);
        StartWakingWatch(cts);

        try
        {
            await Operations.RunAsync(ct => FetchAsync(ct), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Apply();
            State = HasNoData ? LoadState.Empty : LoadState.Ready;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_loadCts, cts) && !_timedOut)
            {
                // Cancelled by the user (or by Dispose): offer Retry rather than leaving a skeleton on screen.
                ShowError("The request was cancelled.", null);
            }
        }
        catch (ApiProblemException ex)
        {
            ShowProblem(ex.Problem);
        }
        catch (Exception ex)
        {
            ShowError("Something went wrong while loading this screen.", ex.Message);
            AsyncRelayCommand.DefaultErrorHandler(ex);
        }
        finally
        {
            _timeoutTimer?.Dispose();
            _timeoutTimer = null;
            _wakingTimer?.Dispose();
            _wakingTimer = null;
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
            }

            cts.Dispose();
        }
    }

    private bool _timedOut;

    /// <summary>Stops a load that is in flight (the "Cancel" of the waking state).</summary>
    public void CancelLoad() => _loadCts?.Cancel();

    /// <summary>The "Cancel" button of the waking banner, on every screen that shows one (SHELL-07).</summary>
    [NoOperationRunner("It only cancels the load in flight; it makes no API call.")]
    public AsyncRelayCommand CancelLoadCommand => _cancelLoadCommand ??= new AsyncRelayCommand(_ =>
    {
        CancelLoad();
        return Task.CompletedTask;
    });

    private AsyncRelayCommand? _cancelLoadCommand;

    private void StartTimeout(CancellationTokenSource cts)
    {
        _timedOut = false;
        _timeoutTimer?.Dispose();
        _timeoutTimer = Clock.CreateTimer(
            _ =>
            {
                // Only a plain Loading state times out: the Waking state has its own 120 s budget inside the API client.
                try
                {
                    if (State == LoadState.Loading && ReferenceEquals(_loadCts, cts))
                    {
                        if (_serverStatus?.State == ServerState.Waking)
                        {
                            // Another call is already waking the server: this call gets the long budget too, not the 10 s one.
                            Post(MarkWaking);
                            return;
                        }

                        _timedOut = true;
                        Post(() => ShowError("This is taking longer than expected.", null));
                        cts.Cancel();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The load finished while the timer was firing.
                }
            },
            null,
            LoadTimeout,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// A load that starts while another call is already waking the server never sees the Online-to-Waking edge, so
    /// after the same 3 s the API client uses it moves to the waking state itself (SHELL-07).
    /// </summary>
    private void StartWakingWatch(CancellationTokenSource cts)
    {
        _wakingTimer?.Dispose();
        _wakingTimer = null;
        if (_serverStatus?.State != ServerState.Waking)
        {
            return;
        }

        _wakingTimer = Clock.CreateTimer(
            _ =>
            {
                if (ReferenceEquals(_loadCts, cts))
                {
                    Post(MarkWaking);
                }
            },
            null,
            WakingAfter,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>How long a load stays in Loading, while the server is already waking, before the waking state shows.</summary>
    public TimeSpan WakingAfter { get; init; } = TimeSpan.FromSeconds(3);

    private void MarkWaking()
    {
        if (_serverStatus?.State == ServerState.Waking && State == LoadState.Loading && _loadCts is not null)
        {
            State = LoadState.Waking;
        }
    }

    private void OnServerStatusChanged(object? sender, EventArgs e)
    {
        Operations.StatusChanged(); // a call of this view model may now be waiting on a waking server
        Post(() =>
        {
            if (_serverStatus?.State == ServerState.Waking && State == LoadState.Loading)
            {
                State = LoadState.Waking;
            }
            else if (_serverStatus?.State == ServerState.Online && State == LoadState.Waking)
            {
                State = LoadState.Loading;
            }
        });
    }

    protected void Post(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    private void ShowProblem(ApiProblem problem)
    {
        var message = problem.Code switch
        {
            Contracts.ProblemCodes.DatabaseWaking => "The server is still starting up. Try again in a moment.",
            Contracts.ProblemCodes.DatabaseResting => "The demo is resting until the start of next month.",
            ClientProblemCodes.Unreachable => "Can't reach the server. Check your internet connection.",
            ClientProblemCodes.SessionExpired => "Your session has expired. Sign in again.",
            "forbidden" => "You don't have permission to see this.",
            _ => "Something went wrong while loading this screen.",
        };
        var detail = $"{problem.Status} {problem.Code}: {problem.Title}" + (problem.CorrelationId is null ? string.Empty : $" (correlation {problem.CorrelationId})");
        ShowError(message, detail);
    }

    private void ShowError(string message, string? detail)
    {
        ErrorMessage = message;
        ErrorDetail = detail;
        State = LoadState.Error;
    }

    /// <summary>Runs an action with <see cref="IsBusy"/> set, so its button shows the busy state; failures are reported, never thrown.</summary>
    protected async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>True once the view model was disposed (the user left the screen); work that finishes later must not touch it.</summary>
    protected bool IsDisposed { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _timeoutTimer?.Dispose();
            _wakingTimer?.Dispose();
            if (_serverStatus is not null)
            {
                _serverStatus.Changed -= OnServerStatusChanged;
            }

            Operations.Changed -= OnOperationsChanged;
            Operations.Dispose();
        }
    }
}
