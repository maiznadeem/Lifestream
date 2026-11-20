using System.Collections.Concurrent;

namespace Lifestream.Api.Auth;

/// <summary>
/// Runs password hashing and checking (PBKDF2, about 0.3 s of CPU per call) on one dedicated thread per processor, never on a thread-pool worker.
/// <para>
/// A check on a worker holds it for the whole hash; in a burst of logins every worker holds one, and the pool adds a worker only about every half
/// second. A login that SQL Server has just granted its account's lock to then waits for a worker while holding the lock, so a burst at one account
/// moved one guess per new worker. Here the checks queue for these threads, the workers stay free for the short database steps of counting, and
/// the threads run below normal priority so those steps also get the processor first. Real accounts and unknown names take the same path.
/// </para>
/// </summary>
public static class CredentialWork
{
    private static readonly BlockingCollection<Action> Queue = Start(Environment.ProcessorCount);

    /// <summary>Runs <paramref name="work"/> on a hashing thread; the caller's continuation runs on the thread pool.</summary>
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        return done.Task;
    }

    private static BlockingCollection<Action> Start(int threads)
    {
        var queue = new BlockingCollection<Action>();
        for (var i = 0; i < threads; i++)
        {
            new Thread(() =>
            {
                foreach (var work in queue.GetConsumingEnumerable())
                {
                    work();
                }
            })
            { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"Password hashing {i + 1}" }.Start();
        }

        return queue;
    }
}
