using System.Net;

namespace Lifestream.Client.Api;

/// <summary>
/// Builds the one <see cref="HttpClient"/> the whole app shares (SHELL-08): HTTP/2 with a single multiplexed connection,
/// wrapped in the wake-aware retry handler. Never create a second client; register this one as a singleton.
/// </summary>
public static class ApiHttpClientFactory
{
    /// <summary>How long opening a connection may take before it counts as failed (and is retried).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long a connection with a request waiting may stay silent before it is pinged.</summary>
    public static readonly TimeSpan SilenceBeforePing = TimeSpan.FromSeconds(15);

    /// <summary>How long the answer to that ping may take before the connection counts as dead.</summary>
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(15);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose call on object", Justification = "Ownership of the handlers passes to the HttpClient, which lives for the whole app.")]
    public static HttpClient Create(Uri baseAddress, WakePolicy policy, ServerStatus status, Action<SocketsHttpHandler>? configure = null)
    {
        var sockets = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,

            // A request to a waking server is left unanswered for a minute or more and is never given up on a guess (WakeAwareHandler).
            // What must not be waited on is a connection that has died, so that is checked for: a connect that does not complete fails
            // after 15 s, and while a request waits the connection is pinged after 15 s of silence and dropped when no answer comes
            // within 15 s more. Either failure is retried by the handler on a new connection.
            ConnectTimeout = ConnectTimeout,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = SilenceBeforePing,
            KeepAlivePingTimeout = PingTimeout,
        };
        configure?.Invoke(sockets);

        return new HttpClient(new WakeAwareHandler(policy, status, sockets))
        {
            BaseAddress = baseAddress,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            // The 120 s budget lives in the handler; the HttpClient itself must not cut in first.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
