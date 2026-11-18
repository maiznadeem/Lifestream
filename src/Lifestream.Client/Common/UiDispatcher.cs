namespace Lifestream.Client.Common;

/// <summary>
/// The UI thread, as seen by code that must change UI-bound state (SHELL-01). It is injected, never captured from an ambient
/// <see cref="SynchronizationContext"/>: a context captured at an arbitrary moment can be null or belong to another thread,
/// which let toast timers change a bound collection from a thread-pool thread.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the calling thread is the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Runs <paramref name="action"/> on the UI thread, later, without blocking the caller.</summary>
    void Post(Action action);
}

public static class UiDispatcherExtensions
{
    /// <summary>Runs the action now when already on the UI thread, otherwise posts it to the UI thread.</summary>
    public static void Run(this IUiDispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Post(action);
        }
    }
}
