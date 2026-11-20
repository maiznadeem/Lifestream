using System.Net;
using System.Text.Json;
using Lifestream.Contracts;

namespace Lifestream.Client.Api;

/// <summary>Timing of the cold-start handling (SHELL-07). The defaults are the product values; tests pass a fake clock.</summary>
public sealed record WakePolicy
{
    /// <summary>The longest a call keeps retrying a waking server before it gives up.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The longest a call keeps retrying while no connection can be made at all (refused, no network, no such host). That is not a server
    /// waking up: a waking server accepts the connection and answers late or with 502/503/504. It is retried quietly, every
    /// <see cref="FirstBackoff"/>, for this long from the start of the first attempt that could not connect, in case the network or the
    /// server is back in a moment, and then reported as unreachable. It is well under the 10 s a screen load waits for an answer
    /// (ViewModelBase.LoadTimeout), and a refused connection can take 2 s to fail on Windows: the screen must end on "Can't reach the
    /// server", never on "The server is taking too long to answer".
    /// </summary>
    public TimeSpan UnreachableBudget { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>How long a call may be slow, or be retried, before the "waking up the server" state is shown.</summary>
    public TimeSpan ShowWakingAfter { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>A single attempt that takes longer than this is abandoned and retried.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(10);

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// Retries a call, with exponential backoff, while the server or database is waking up: on an attempt timeout, a 502/503/504, or a
/// <c>database_waking</c> problem, for up to <see cref="WakePolicy.Budget"/>, showing the waking state after
/// <see cref="WakePolicy.ShowWakingAfter"/>. A connection that cannot be made at all is a different thing: it is retried quietly for
/// <see cref="WakePolicy.UnreachableBudget"/> only, never shows the waking state, and then fails as unreachable. The retry re-sends the same
/// request headers, so the Idempotency-Key is identical on every attempt. The caller's cancellation token stops it at any moment.
/// </summary>
public sealed class WakeAwareHandler(WakePolicy policy, ServerStatus status, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    /// <summary>Set on a request that must be tried exactly once, such as sign-out (SHELL-17): no waking state, no backoff, no retries.</summary>
    public static readonly HttpRequestOptionsKey<bool> NoRetry = new("Lifestream.NoRetry");

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The token sources and requests are disposed by their using declarations; the analyzer cannot follow the retry loop.")]
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(NoRetry, out var once) && once)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var clock = policy.Clock;
        var started = clock.GetTimestamp();
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var call = new CallState(status);
        using var slowTimer = clock.CreateTimer(_ => call.Slow(), null, policy.ShowWakingAfter, Timeout.InfiniteTimeSpan);
        var backoff = policy.FirstBackoff;
        long? unreachableSince = null;
        var unreachableBudget = policy.UnreachableBudget < policy.Budget ? policy.UnreachableBudget : policy.Budget;
        try
        {
            while (true)
            {
                HttpResponseMessage? response = null;
                Exception? failure = null;
                var attemptStarted = clock.GetTimestamp();
                using var attemptTimeout = new CancellationTokenSource(policy.AttemptTimeout, clock);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token);
                try
                {
                    using var copy = Clone(request, body);
                    response = await base.SendAsync(copy, attempt.Token).ConfigureAwait(false);
                    response = await Buffered(response).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    failure = ex;
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    failure = ex; // the attempt timed out, not the caller
                }

                if (response is not null)
                {
                    if (response.Headers.Contains(ApiHeaders.DemoReset))
                    {
                        status.NotifyDemoReset();
                    }

                    if (!await IsWakingAsync(response).ConfigureAwait(false))
                    {
                        return response;
                    }
                }

                // No connection at all (refused, no network) is not "waking": no waking state while it lasts, and a short budget of its own.
                var unreachable = failure is HttpRequestException;
                unreachableSince = unreachable ? unreachableSince ?? attemptStarted : null;
                call.Unreachable(unreachable);

                var elapsed = clock.GetElapsedTime(started);
                var delay = RetryAfter(response) ?? (unreachable ? policy.FirstBackoff : backoff);
                if (elapsed + delay >= policy.Budget || (unreachableSince is { } since && clock.GetElapsedTime(since) + delay >= unreachableBudget))
                {
                    if (response is not null)
                    {
                        return response;
                    }

                    throw failure ?? new HttpRequestException("The server did not respond in time.");
                }

                response?.Dispose();
                await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
                if (!unreachable)
                {
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, policy.MaxBackoff.Ticks));
                }
            }
        }
        finally
        {
            call.Complete();
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage? response)
    {
        var delta = response?.Headers.RetryAfter?.Delta;
        return delta is { } d && d > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Min(d.Ticks, TimeSpan.FromSeconds(15).Ticks)) : null;
    }

    private static async Task<HttpResponseMessage> Buffered(HttpResponseMessage response)
    {
        if (response.Content is not null)
        {
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
        }

        return response;
    }

    private static async Task<bool> IsWakingAsync(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
        {
            return true;
        }

        if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
        {
            return false;
        }

        // A "resting" database (free allowance used up) will not wake by waiting, so it is returned as is.
        try
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var code = JsonText.Property(doc.RootElement, "code");
            return code != ProblemCodes.DatabaseResting;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? body)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version, VersionPolicy = original.VersionPolicy };
        foreach (var header in original.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var header in original.Content!.Headers)
            {
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return copy;
    }

    /// <summary>
    /// Tracks whether this call has the server in the waking state: it does once it has been slow for <see cref="WakePolicy.ShowWakingAfter"/>,
    /// except while its last attempt could not connect at all, and never after it has ended. Every Enter is balanced by one Leave.
    /// </summary>
    private sealed class CallState(ServerStatus status)
    {
        private readonly Lock _gate = new();
        private bool _slow;
        private bool _unreachable;
        private bool _entered;
        private bool _completed;

        /// <summary>The call has been running for <see cref="WakePolicy.ShowWakingAfter"/>.</summary>
        public void Slow() => Update(() => _slow = true);

        /// <summary>Whether the last attempt failed without any connection (true), or the server was there (false).</summary>
        public void Unreachable(bool value) => Update(() => _unreachable = value);

        public void Complete() => Update(() => _completed = true);

        private void Update(Action change)
        {
            bool enter;
            bool leave;
            lock (_gate)
            {
                change();
                var waking = _slow && !_unreachable && !_completed;
                enter = waking && !_entered;
                leave = !waking && _entered;
                _entered = waking;
            }

            if (enter)
            {
                status.Enter();
            }
            else if (leave)
            {
                status.Leave();
            }
        }
    }
}
