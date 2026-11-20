using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Lifestream.Client.Tests.Fakes;

/// <summary>
/// The one UI thread the app has, for a test: a synchronization context with a single thread that runs what is posted to it, one thing after
/// the other, in the order it was posted. A view model made on it posts its changes to it, and a command started on it comes back to it after
/// every await, as in the app. Because it is one thread and in order, <see cref="DrainedAsync"/> is exact: when it completes, everything that
/// was posted before it has run to its end. (xUnit's own context runs what is posted to it on several threads, so nothing can be said there
/// about work that was posted a moment ago, however long one waits.)
/// </summary>
public sealed class TestUiThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;
    private ExceptionDispatchInfo? _failure;

    public TestUiThread()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    _failure ??= ExceptionDispatchInfo.Capture(ex); // shown by the next DrainedAsync or Run: a test must not lose it
                }
            }
        })
        { IsBackground = true, Name = "TestUiThread" };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // The test is over and its UI thread has stopped. What still comes back to it (the end of a call the view model cancelled when it
            // was disposed) is dropped, as the dispatcher of a closed window drops it; thrown from here it would end the test process.
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Thread.CurrentThread == _thread)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        ExceptionDispatchInfo? failure = null;
        Post(
            _ =>
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    done.Set();
                }
            },
            null);
        done.Wait();
        failure?.Throw();
    }

    /// <summary>Runs the work on the UI thread and gives back its result: a view model is made there, and a command is started there, as a click starts it.</summary>
    public T Run<T>(Func<T> work)
    {
        T result = default!;
        Send(_ => result = work(), null);
        return result;
    }

    /// <summary>Completes when everything that was posted to the UI thread before this call has run to its end.</summary>
    public async Task DrainedAsync()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ => drained.SetResult(), null);
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _failure?.Throw();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        _queue.Dispose();
    }
}
