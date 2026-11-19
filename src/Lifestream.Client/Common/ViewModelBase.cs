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

    protected ViewModelBase(LoadState initialState = LoadState.Loading) => State = initialState;

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
            await Task.Run(() => FetchAsync(cts.Token), cts.Token).ConfigureAwait(true);
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

    private void OnServerStatusChanged(object? sender, EventArgs e) =>
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

    private void Post(Action action)
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

    public void Dispose()
    {
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
        }
    }
}
