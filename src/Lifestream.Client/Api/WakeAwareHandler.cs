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
    /// waking up: a waking server accepts the connection and answers late or with 502/503/504 (or Cloudflare's 520-524). It is retried quietly, every
    /// <see cref="FirstBackoff"/>, for this long from the start of the first attempt that could not connect, in case the network or the
    /// server is back in a moment, and then reported as unreachable. It is well under the 10 s a screen load waits for an answer
    /// (ViewModelBase.LoadTimeout), and a refused connection can take 2 s to fail on Windows: the screen must end on "Can't reach the
    /// server", never on "The server is taking too long to answer".
    /// </summary>
    public TimeSpan UnreachableBudget { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>How long a call may be slow, or be retried, before the "waking up the server" state is shown.</summary>
    public TimeSpan ShowWakingAfter { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The longest any one call may last: the budget plus a margin for the answer that arrives as the budget ends. The handler itself
    /// stops at the budget; this is the limit the operation scope puts on top (<see cref="ScopedApiClient"/>).
    /// </summary>
    public TimeSpan Ceiling => Budget + TimeSpan.FromSeconds(20);

    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(10);

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// Waits for a server or database that is waking up, for up to <see cref="WakePolicy.Budget"/>, showing the waking state after
/// <see cref="WakePolicy.ShowWakingAfter"/>. A request that is out and unanswered is left alone for as long as the budget lasts:
/// the server is working on it (starting the container, waiting for the database to resume), and giving up on it would cancel
/// that work on the server and start it again. A request is sent again, with exponential backoff, only when the last one is over
/// without an answer worth keeping: a 502/503/504, a Cloudflare 520-524 (the proxy in front of the API: 524 is its 100-second limit,
/// which a cold start can reach), a <c>database_waking</c> problem, or a connection that was lost under it.
/// <para>
/// A connection that cannot be made at all is a different thing: it is retried quietly for <see cref="WakePolicy.UnreachableBudget"/>
/// only, never shows the waking state, and then fails as unreachable.
/// </para>
/// <para>
/// The retry re-sends the same request headers, so the Idempotency-Key is identical on every attempt. The caller's cancellation
/// token stops it at any moment, down to the request in flight. A connection that has died is not waited on:
/// <see cref="ApiHttpClientFactory"/> has the connection checked while a request waits, and its failure is retried here.
/// </para>
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

        var call = new CallState(status, clock);
        using var slowTimer = clock.CreateTimer(_ => call.Slow(), null, policy.ShowWakingAfter, Timeout.InfiniteTimeSpan);
        var backoff = policy.FirstBackoff;
        var serverHadIt = false;
        var connectTimedOut = false;
        long? unreachableSince = null;
        var unreachableBudget = policy.UnreachableBudget < policy.Budget ? policy.UnreachableBudget : policy.Budget;

        // The one time limit of a call: an attempt still unanswered when the budget ends is given up. Nothing shorter cuts an attempt off.
        // (A budget of zero means one attempt and no retry; that attempt is not cut off at all.)
        using var budget = policy.Budget > TimeSpan.Zero ? new CancellationTokenSource(policy.Budget, clock) : new CancellationTokenSource();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            while (true)
            {
                HttpResponseMessage? response = null;
                Exception? failure = null;
                var attemptStarted = clock.GetTimestamp();
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
                    failure = ex; // the budget ended with the attempt unanswered; the caller did not cancel
                }

                if (response is not null)
                {
                    serverHadIt = true;
                    if (response.Headers.TryGetValues(ApiHeaders.DemoReset, out var reset))
                    {
                        status.NotifyDemoReset(reset.FirstOrDefault() ?? string.Empty);
                    }

                    var answer = await ReadAnswerAsync(request, response).ConfigureAwait(false);
                    if (answer.FromServer)
                    {
                        // The server itself answered, so its container is up; what it said about its database decides the phase shown.
                        status.Heard(clock, answer.DatabaseWaking);
                    }

                    if (!answer.IsWaking)
                    {
                        return response;
                    }
                }

                // No connection at all (refused, no network) is not "waking": no waking state while it lasts, and a short budget of its own.
                // A connection that cannot be made fails within seconds. An attempt that fails only after it has been out for longer than
                // that budget had its connection and lost it while the server was working on the request: that one is sent again as any
                // other request of a waking server, not given up as unreachable.
                var unreachable = failure is HttpRequestException && clock.GetElapsedTime(attemptStarted) < unreachableBudget;

                // What the attempt says about the server, for the end of the budget: a connection lost after the server had the request for a
                // while means it was reached; a connect that timed out (a cancellation with a TimeoutException inside) means it was not.
                serverHadIt |= failure is HttpRequestException && !unreachable;
                connectTimedOut |= IsConnectTimeout(failure);
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

                    // The budget ended on an attempt still out. When the server had it (an earlier attempt was answered, or this one was out longer
                    // than a connection takes to fail), the server is slow, not out of reach: the caller says "still waking", never "can't reach".
                    // A network whose connects time out is not reached however long its attempts were out.
                    if (failure is OperationCanceledException && !IsConnectTimeout(failure)
                        && (serverHadIt || (!connectTimedOut && clock.GetElapsedTime(attemptStarted) >= policy.UnreachableBudget)))
                    {
                        throw new NoAnswerException(failure);
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

    private static bool IsConnectTimeout(Exception? failure) => failure is OperationCanceledException { InnerException: TimeoutException };

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

    /// <summary>
    /// What an answer means for the wait. <c>IsWaking</c>: try again. <c>FromServer</c>: the API itself wrote it (a 502, a 504 or a 503
    /// without a problem code comes from the platform in front of a container that is not up yet). <c>DatabaseWaking</c>: true when the
    /// API said so, false when the answer came out of the database, null when the answer says nothing about the database.
    /// </summary>
    private readonly record struct Answer(bool IsWaking, bool FromServer, bool? DatabaseWaking);

    private static async Task<Answer> ReadAnswerAsync(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout || IsCloudflareOriginError(response.StatusCode))
        {
            return new Answer(IsWaking: true, FromServer: false, DatabaseWaking: null);
        }

        if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
        {
            // /healthz never touches the database, and an error answer may not have either; any other success came out of the database.
            var fromDatabase = response.IsSuccessStatusCode && request.RequestUri?.AbsolutePath.EndsWith(ApiRoutes.Healthz, StringComparison.OrdinalIgnoreCase) != true;
            return new Answer(IsWaking: false, FromServer: true, DatabaseWaking: fromDatabase ? false : null);
        }

        try
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var code = JsonText.Property(doc.RootElement, "code");

            // A "resting" database (free allowance used up) will not wake by waiting, so it is returned as is.
            return code == ProblemCodes.DatabaseResting ? new Answer(IsWaking: false, FromServer: true, DatabaseWaking: null)
                : code == ProblemCodes.DatabaseWaking ? new Answer(IsWaking: true, FromServer: true, DatabaseWaking: true)
                : new Answer(IsWaking: true, FromServer: code is not null, DatabaseWaking: null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new Answer(IsWaking: true, FromServer: false, DatabaseWaking: null);
        }
    }

    /// <summary>
    /// Cloudflare's own answers about the origin behind it: 520 (unknown error), 521 (refused), 522 (connect timed out), 523 (unreachable)
    /// and 524 (no answer within 100 seconds). During a cold start they mean what a 502 or 504 means: the server is not answering yet.
    /// </summary>
    public static bool IsCloudflareOriginError(HttpStatusCode status) => (int)status is >= 520 and <= 524;

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
    /// except while its last attempt could not connect at all, and never after it has ended. Every Enter is balanced by one Leave. The
    /// state is entered on the handler's clock, the one the server's answers are noted on (<see cref="ServerStatus.Heard"/>).
    /// </summary>
    private sealed class CallState(ServerStatus status, TimeProvider clock)
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
                status.Enter(clock);
            }
            else if (leave)
            {
                status.Leave();
            }
        }
    }
}

/// <summary>
/// The call's budget ended while the server held its request (see <see cref="WakeAwareHandler"/>): a wake that ran long, not a network
/// problem. The API client reports it as <see cref="ClientProblemCodes.NoAnswer"/>.
/// </summary>
public sealed class NoAnswerException(Exception inner) : OperationCanceledException("The server did not answer within the wait.", inner);

