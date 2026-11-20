namespace Lifestream.Client.Tests.Fakes;

/// <summary>
/// A server that holds every request until the test answers it, as a waking server holds one while its container starts and its
/// database resumes. It counts the requests that arrived and the ones that were cancelled while it held them.
/// </summary>
public sealed class HoldingServer : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<TaskCompletionSource<HttpResponseMessage>> _held = [];

    /// <summary>How many of the held requests were cancelled by the caller (the HTTP request itself, not only the wait for it).</summary>
    public int Cancelled => Volatile.Read(ref _cancelled);

    private int _cancelled;

    public int Arrived
    {
        get
        {
            lock (_gate)
            {
                return _held.Count;
            }
        }
    }

    public void Answer(int index, HttpResponseMessage response)
    {
        lock (_gate)
        {
            _held[index].SetResult(response);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _held.Add(held);
        }

        try
        {
            return await held.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }
    }
}
