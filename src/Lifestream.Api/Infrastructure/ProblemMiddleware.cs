using System.Globalization;
using System.Text.Json;
using Lifestream.Contracts;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Infrastructure;

/// <summary>Writes every error as RFC 7807 problem details with a stable "code" and the correlation id. Never exception, SQL or schema text.</summary>
public static class ProblemWriter
{
    public static async Task WriteAsync(HttpContext context, int status, string code, string title, IReadOnlyDictionary<string, string[]>? errors = null, TimeSpan? retryAfter = null, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        if (retryAfter is { } wait)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var body = new Dictionary<string, object?>
        {
            ["type"] = $"https://lifestream.example/problems/{code}",
            ["title"] = title,
            ["status"] = status,
            ["code"] = code,
            ["correlationId"] = CorrelationMiddleware.IdOf(context),
        };
        if (errors is not null)
        {
            body["errors"] = errors;
        }

        // RFC 7807 extension members (for example the readiness state), added after the standard ones so they can never replace them.
        foreach (var (name, value) in extensions ?? new Dictionary<string, object?>())
        {
            body.TryAdd(name, value);
        }

        context.Response.ContentType = "application/problem+json";
        var options = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        await JsonSerializer.SerializeAsync(context.Response.Body, body, options, context.RequestAborted);
    }
}

/// <summary>A problem response returned from an endpoint instead of thrown (used where the status is the answer, such as the readiness probe).</summary>
public sealed class ProblemResult(int status, string code, string title, TimeSpan? retryAfter = null, IReadOnlyDictionary<string, object?>? extensions = null) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext) =>
        ProblemWriter.WriteAsync(httpContext, status, code, title, retryAfter: retryAfter, extensions: extensions);
}

public static class DatabaseProblems
{
    /// <summary>How long until the monthly free allowance renews (the first of next month, UTC): the Retry-After of a resting database.</summary>
    public static TimeSpan RestingRetryAfter() =>
        new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1) - DateTime.UtcNow;

    public const string RestingTitle = "The demo database has used its free monthly allowance and is back on the 1st.";
    public const string WakingTitle = "The database is waking up. Try again in a moment.";
    public static readonly TimeSpan WakingRetryAfter = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The Retry-After of a readiness check that found the database still waking: the same interval a waiting request tries at, so the
    /// client's readiness poll sees a resumed database as soon as a request would.
    /// </summary>
    public static TimeSpan ReadinessRetryAfter(DatabaseSettings settings) => TimeSpan.FromSeconds(Math.Clamp(settings.MaxRetryDelaySeconds, 1, 15));
}

public sealed class ProblemMiddleware(RequestDelegate next, ILogger<ProblemMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            await HandleAsync(context, exception);
            return;
        }

        if (!context.Response.HasStarted && context.Response.StatusCode >= 400 && context.Response.ContentLength is null && context.Response.ContentType is null)
        {
            var (code, title) = context.Response.StatusCode switch
            {
                StatusCodes.Status401Unauthorized => (ExtraProblemCodes.Unauthorized, "Sign in to continue."),
                StatusCodes.Status403Forbidden => (ExtraProblemCodes.Forbidden, "You do not have permission to do this."),
                StatusCodes.Status404NotFound => (ProblemCodes.NotFound, "Nothing exists at this address."),
                StatusCodes.Status405MethodNotAllowed => (ExtraProblemCodes.MethodNotAllowed, "This method is not allowed here."),
                StatusCodes.Status413PayloadTooLarge => (ExtraProblemCodes.PayloadTooLarge, "The request is too large."),
                StatusCodes.Status429TooManyRequests => (ProblemCodes.RateLimited, "Too many requests. Try again shortly."),
                < 500 => (ProblemCodes.Validation, "The request could not be processed."),
                _ => (ExtraProblemCodes.ServerError, "Something went wrong on the server."),
            };
            await ProblemWriter.WriteAsync(context, context.Response.StatusCode, code, title);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        switch (exception)
        {
            case ApiException p:
                await ProblemWriter.WriteAsync(context, p.Status, p.Code, p.Title, p.Errors, p.RetryAfter);
                return;
            case BadHttpRequestException bad:
                var tooLarge = bad.StatusCode == StatusCodes.Status413PayloadTooLarge;
                await ProblemWriter.WriteAsync(
                    context,
                    bad.StatusCode,
                    tooLarge ? ExtraProblemCodes.PayloadTooLarge : ProblemCodes.Validation,
                    tooLarge ? "The request is too large." : "The request could not be read.");
                return;
            case DbUpdateConcurrencyException:
                await ProblemWriter.WriteAsync(context, 409, ProblemCodes.Concurrency, "Someone else changed this record. Reload it and try again.");
                return;
        }

        if (DatabaseErrors.IsResting(exception))
        {
            logger.LogWarning("The database is paused for the rest of the month: its free allowance is used up (sql {SqlNumber}). It is back on the 1st, 00:00 UTC.", DatabaseErrors.FreeAllowanceExhausted);
            await ProblemWriter.WriteAsync(context, 503, ProblemCodes.DatabaseResting, DatabaseProblems.RestingTitle, retryAfter: DatabaseProblems.RestingRetryAfter());
            return;
        }

        if (DatabaseErrors.IsWaking(exception))
        {
            await ProblemWriter.WriteAsync(context, 503, ProblemCodes.DatabaseWaking, DatabaseProblems.WakingTitle, retryAfter: DatabaseProblems.WakingRetryAfter);
            return;
        }

        if (exception is DbUpdateException { InnerException: SqlException sql })
        {
            if (sql.Number is 2601 or 2627)
            {
                await ProblemWriter.WriteAsync(context, 409, ProblemCodes.Conflict, "A record with the same value already exists.");
                return;
            }

            if (sql.Number == 547)
            {
                await ProblemWriter.WriteAsync(context, 409, ProblemCodes.Conflict, "This record is used by other records and cannot be changed this way.");
                return;
            }
        }

        // Log the type, the SQL error number and the stack only: exception messages can carry values from the database.
        logger.LogError("Unhandled {ExceptionType} (sql {SqlNumber}) {StackTrace}", exception.GetType().FullName, DatabaseErrors.NumberOf(exception), exception.StackTrace);
        await ProblemWriter.WriteAsync(context, 500, ExtraProblemCodes.ServerError, "Something went wrong on the server.");
    }
}

public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    private const string ItemKey = "lifestream.correlation";

    public static string IdOf(HttpContext context) => context.Items.TryGetValue(ItemKey, out var v) && v is string s ? s : context.TraceIdentifier;

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[ApiHeaders.CorrelationId].ToString();
        var id = supplied.Length is > 0 and <= 64 && supplied.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? supplied
            : Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[ApiHeaders.CorrelationId] = id;
            return Task.CompletedTask;
        });

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using (logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = id }))
        {
            await next(context);

            // One structured access line per request: the route TEMPLATE (never the path or query, which can hold names), the
            // status, the time, and the numeric staff id from the token. No body, header, token or personal data.
            logger.LogInformation(
                "HTTP {Method} {Route} {Status} {ElapsedMs}ms user {UserId}",
                context.Request.Method,
                (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched",
                context.Response.StatusCode,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                context.User.FindFirst("sub")?.Value ?? "anonymous");
        }
    }
}
