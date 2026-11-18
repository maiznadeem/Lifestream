using System.Net;

namespace Lifestream.Client.Api;

/// <summary>
/// Builds the one <see cref="HttpClient"/> the whole app shares (SHELL-08): HTTP/2 with a single multiplexed connection,
/// wrapped in the wake-aware retry handler. Never create a second client; register this one as a singleton.
/// </summary>
public static class ApiHttpClientFactory
{
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose call on object", Justification = "Ownership of the handlers passes to the HttpClient, which lives for the whole app.")]
    public static HttpClient Create(Uri baseAddress, WakePolicy policy, ServerStatus status, Action<SocketsHttpHandler>? configure = null)
    {
        var sockets = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
        };
        configure?.Invoke(sockets);

        return new HttpClient(new WakeAwareHandler(policy, status, sockets))
        {
            BaseAddress = baseAddress,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            // Per-attempt timeouts and the 120 s budget live in the handler; the HttpClient itself must not cut in first.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
