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

/// <summary>
/// Runs a test body on a UI thread as the app has one: a thread with a synchronization context that runs posted work one item at a time. A view model made in
/// the body applies its loads, its detail and its command changes there, in turn, as on the app's dispatcher. Without it the fake API's answers come back on
/// pool threads, and two of them (the list's load and a detail) can change the same view model at once, which the app never does.
/// </summary>
public static class UiThread
{
    public static Task RunAsync(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new PumpContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                Task work;
                try
                {
                    work = body();
                }
                catch (Exception ex)
                {
                    work = Task.FromException(ex);
                }

                context.RunUntil(work);
                if (work.IsFaulted)
                {
                    result.SetException(work.Exception!.InnerExceptions);
                }
                else if (work.IsCanceled)
                {
                    result.SetCanceled();
                }
                else
                {
                    result.SetResult();
                }
            }
            catch (Exception ex)
            {
                result.TrySetException(ex); // a posted item threw
            }
        })
        { IsBackground = true, Name = "TestUiThread" };
        thread.Start();
        return result.Task;
    }

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private bool _closed;

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
            {
                if (!_closed)
                {
                    _queue.Enqueue((d, state));
                    Monitor.Pulse(_queue);
                    return;
                }
            }

            ThreadPool.QueueUserWorkItem(_ => d(state)); // the body has finished: work still arriving (a closed step's clean-up) runs on the pool, as it would with no context
        }

        public override SynchronizationContext CreateCopy() => this;

        /// <summary>Runs posted work on this thread until <paramref name="work"/> has completed and nothing is queued.</summary>
        public void RunUntil(Task work)
        {
            _ = work.ContinueWith(
                _ =>
                {
                    lock (_queue)
                    {
                        Monitor.Pulse(_queue);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_queue)
                {
                    while (_queue.Count == 0 && !work.IsCompleted)
                    {
                        Monitor.Wait(_queue);
                    }

                    if (_queue.Count == 0)
                    {
                        _closed = true;
                        return;
                    }

                    next = _queue.Dequeue();
                }

                next.Callback(next.State);
            }
        }
    }
}
