using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

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

    /// <summary>Shows a toast. Name the affected record in <paramref name="message"/>; add an action such as View or Undo if one makes sense.</summary>
    ToastItem Show(ToastKind kind, string message, string? actionLabel = null, Func<Task>? action = null);
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

/// <summary>One toast. It dismisses itself after <see cref="ToastService.Lifetime"/>; hovering or focusing it pauses the countdown.</summary>
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
        ActionCommand = new AsyncRelayCommand(async _ =>
        {
            Dismiss();
            if (_action is not null)
            {
                await _action().ConfigureAwait(true);
            }
        });
        Resume();
    }

    public ToastKind Kind { get; }

    public string Message { get; }

    public string? ActionLabel { get; }

    public bool HasAction => ActionLabel is not null;

    public AsyncRelayCommand DismissCommand { get; }

    public AsyncRelayCommand ActionCommand { get; }

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
    public ToastService(IUiDispatcher dispatcher, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        Clock = clock ?? TimeProvider.System;
    }

    /// <summary>How long a toast stays: inside the 6 to 10 second window the design asks for.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(8);

    public ObservableCollection<ToastItem> Toasts { get; } = [];

    internal TimeProvider Clock { get; }

    public ToastItem Show(ToastKind kind, string message, string? actionLabel = null, Func<Task>? action = null)
    {
        var toast = new ToastItem(this, kind, message, actionLabel, action, Lifetime);
        Post(() => Toasts.Add(toast));
        return toast;
    }

    internal void Remove(ToastItem toast) => Post(() => Toasts.Remove(toast));

    /// <summary>Runs the action on the UI thread (inline when already there); posts keep their order, so an add always runs before its remove.</summary>
    internal void Post(Action action) => _dispatcher.Run(action);
}
