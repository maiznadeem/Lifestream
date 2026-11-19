using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;

namespace Lifestream.Client.Common;

public enum ToastKind
{
    Success,
    Info,
    Warning,
    Error,
}

public interface IToastService
{
    ObservableCollection<ToastItem> Toasts { get; }

    /// <summary>
    /// The shell-level runner of the toasts' actions (Undo, View, Reload). A toast outlives the screen that showed it, so its action must not depend on that
    /// screen: it runs here, and the shell shows "Waking up the server" with a Cancel for it on whatever screen is open (SHELL-07).
    /// </summary>
    OperationRunner Actions { get; }

    /// <summary>Changes every time <see cref="EndSession"/> runs. An action that started before a sign-out compares it, so it never shows a toast to the next person (SHELL-07).</summary>
    int Epoch { get; }

    /// <summary>Sign-out: cancels every waiting toast action (Undo, View, Reload) and removes every toast, before the session is cleared.</summary>
    void EndSession();

    /// <summary>Shows a toast. Name the affected record in <paramref name="message"/>; add an action such as View or Undo if one makes sense.</summary>
    /// <param name="kind">Success, info, warning or error.</param>
    /// <param name="message">The text; names the affected record.</param>
    /// <param name="actionLabel">The action button's label, such as View or Undo.</param>
    /// <param name="action">Runs when the action button is pressed.</param>
    /// <param name="lifetime">How long the toast stays, kept within 6 to 10 seconds. Null: 10 seconds for a toast with an Undo action, otherwise 8.</param>
    ToastItem Show(ToastKind kind, string message, string? actionLabel = null, Func<Task>? action = null, TimeSpan? lifetime = null);
}

public static class ToastServiceExtensions
{
    /// <summary>Shows "Donor 'Name' saved." style toasts so the record is always named (SHELL-30).</summary>
    public static ToastItem ShowForRecord(this IToastService toasts, ToastKind kind, string recordType, string recordName, string outcome, string? actionLabel = null, Func<Task>? action = null)
    {
        ArgumentNullException.ThrowIfNull(toasts);
        return toasts.Show(kind, $"{recordType} '{recordName}' {outcome}.", actionLabel, action);
    }
}

/// <summary>One toast. It dismisses itself after its lifetime (6 to 10 seconds, see <see cref="ToastService"/>); hovering or focusing it pauses the countdown.</summary>
public sealed partial class ToastItem : ObservableObject, IDisposable
{
    private readonly ToastService _owner;
    private readonly Func<Task>? _action;
    private ITimer? _timer;
    private TimeSpan _remaining;
    private DateTimeOffset _startedAt;

    internal ToastItem(ToastService owner, ToastKind kind, string message, string? actionLabel, Func<Task>? action, TimeSpan lifetime)
    {
        _owner = owner;
        Kind = kind;
        Message = message;
        ActionLabel = action is null ? null : actionLabel;
        _action = action;
        _remaining = lifetime;
        DismissCommand = new AsyncRelayCommand(_ =>
        {
            Dismiss();
            return Task.CompletedTask;
        });
        ActionCommand = new AsyncRelayCommand(
            async _ =>
            {
                Dismiss();
                if (_action is not null)
                {
                    await _action().ConfigureAwait(true);
                }
            },
            runner: owner.Actions);
        ActionCommand.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsBusy));
        Resume();
    }

    /// <summary>The shell-level runner the toast's action runs through (see <see cref="IToastService.Actions"/>).</summary>
    public OperationRunner Operations => _owner.Actions;

    public ToastKind Kind { get; }

    public string Message { get; }

    public string? ActionLabel { get; }

    public bool HasAction => ActionLabel is not null;

    [NoOperationRunner("It only closes the toast; it makes no API call.")]
    public AsyncRelayCommand DismissCommand { get; }

    public AsyncRelayCommand ActionCommand { get; }

    /// <summary>A toast never loads anything; present so every view model offers the same state (SHELL-11).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML like the other view models; the shared state name is the point (SHELL-11).")]
    public bool IsLoading => false;

    /// <summary>True while the toast's action is running.</summary>
    public bool IsBusy => ActionCommand.IsRunning;

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    public bool IsDismissed { get; private set; }

    /// <summary>Stops the countdown (the pointer is over the toast or it has keyboard focus).</summary>
    public void Pause()
    {
        if (IsDismissed || IsPaused)
        {
            return;
        }

        IsPaused = true;
        _remaining -= _owner.Clock.GetUtcNow() - _startedAt;
        if (_remaining < TimeSpan.Zero)
        {
            _remaining = TimeSpan.Zero;
        }

        _timer?.Dispose();
        _timer = null;
    }

    public void Resume()
    {
        if (IsDismissed)
        {
            return;
        }

        IsPaused = false;
        _startedAt = _owner.Clock.GetUtcNow();
        _timer?.Dispose();
        _timer = _owner.Clock.CreateTimer(_ => _owner.Post(Dismiss), null, _remaining, Timeout.InfiniteTimeSpan);
    }

    public void Dismiss()
    {
        if (IsDismissed)
        {
            return;
        }

        IsDismissed = true;
        _timer?.Dispose();
        _timer = null;
        _owner.Remove(this);
    }

    public void Dispose() => Dismiss();
}

public sealed class ToastService : IToastService
{
    private readonly IUiDispatcher _dispatcher;

    /// <param name="dispatcher">The UI thread. Every change to <see cref="Toasts"/> runs on it, whichever thread asked (SHELL-01).</param>
    /// <param name="clock">The clock for the auto-dismiss timers; tests pass a manual one.</param>
    /// <param name="serverStatus">The server's state, which decides when an action that waits shows the waking notice (SHELL-07).</param>
    public ToastService(IUiDispatcher dispatcher, TimeProvider? clock = null, IServerStatus? serverStatus = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        Clock = clock ?? TimeProvider.System;
        Actions = new OperationRunner(() => serverStatus, () => Clock);
        if (serverStatus is not null)
        {
            serverStatus.Changed += (_, _) => Actions.StatusChanged(); // the service lives as long as the app, like the status
        }
    }

    public OperationRunner Actions { get; }

    /// <summary>How long an ordinary toast stays: inside the 6 to 10 second window the design asks for.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(8);

    /// <summary>A toast with an Undo action stays longer, so there is time to read it and decide (FEAT-06).</summary>
    public static TimeSpan UndoLifetime { get; } = TimeSpan.FromSeconds(10);

    public static TimeSpan MinLifetime { get; } = TimeSpan.FromSeconds(6);

    public static TimeSpan MaxLifetime { get; } = TimeSpan.FromSeconds(10);

    public ObservableCollection<ToastItem> Toasts { get; } = [];

    public int Epoch => Volatile.Read(ref _epoch);

    private int _epoch;

    public void EndSession()
    {
        Interlocked.Increment(ref _epoch);
        Actions.Cancel();
        Post(() =>
        {
            foreach (var toast in Toasts.ToList())
            {
                toast.Dismiss();
            }
        });
    }

    internal TimeProvider Clock { get; }

    public ToastItem Show(ToastKind kind, string message, string? actionLabel = null, Func<Task>? action = null, TimeSpan? lifetime = null)
    {
        var wanted = lifetime ?? (action is not null && string.Equals(actionLabel, "Undo", StringComparison.OrdinalIgnoreCase) ? UndoLifetime : Lifetime);
        var toast = new ToastItem(this, kind, message, actionLabel, action, wanted < MinLifetime ? MinLifetime : wanted > MaxLifetime ? MaxLifetime : wanted);
        Post(() => Toasts.Add(toast));
        return toast;
    }

    internal void Remove(ToastItem toast) => Post(() => Toasts.Remove(toast));

    /// <summary>Runs the action on the UI thread (inline when already there); posts keep their order, so an add always runs before its remove.</summary>
    internal void Post(Action action) => _dispatcher.Run(action);
}
