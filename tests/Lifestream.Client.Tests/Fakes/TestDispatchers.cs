using System.Collections.Concurrent;
using Lifestream.Client.Common;

namespace Lifestream.Client.Tests.Fakes;

/// <summary>A dispatcher for tests that do not care about threads: every thread counts as the UI thread, and posts run at once.</summary>
public sealed class InlineUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();
}

/// <summary>
/// A real "UI thread" for tests: one dedicated thread that runs posted work in order. <see cref="CheckAccess"/> is true only on that
/// thread, so a test can prove that bound state is changed there and nowhere else.
/// </summary>
public sealed class ThreadAffineUiDispatcher : IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public ThreadAffineUiDispatcher()
    {
        _thread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        })
        { IsBackground = true, Name = "TestUiThread" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public void Post(Action action) => _queue.Add(action);

    /// <summary>Runs the action on the UI thread and waits for it.</summary>
    public T Invoke<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>();
        Post(() =>
        {
            try
            {
                done.SetResult(action());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
    }
}
