using System.Collections.Concurrent;
using System.Globalization;
using Lifestream.Api.Auth;
using Lifestream.Contracts;
using Lifestream.Data;
using Lifestream.Data.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Infrastructure;

/// <summary>
/// After authentication: runs the once-per-UTC-day demo reset, marks responses to sessions that began before the last reset with
/// X-Demo-Reset, takes the acting staff id from the token for audit rows, and adds the user id to the log scope.
/// </summary>
public sealed class IdentityMiddleware(RequestDelegate next, ILogger<IdentityMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, DemoDirectory demo, TimeProvider time, IOptions<DemoSettings> demoSettings, CurrentUser user)
    {
        if (!user.IsAuthenticated)
        {
            await next(context);
            return;
        }

        var ct = context.RequestAborted;
        var now = time.GetUtcNow().UtcDateTime;
        if (demoSettings.Value.Enabled)
        {
            await demo.EnsureDailyResetAsync(context.RequestServices, DateOnly.FromDateTime(now), now, ct);
            // "iat" has whole seconds: a session that began in the same second as the reset, after it, must not count as older than the reset.
            if (demo.LastResetUtc is { } reset && long.TryParse(context.User.FindFirst("iat")?.Value, CultureInfo.InvariantCulture, out var issued)
                && issued < new DateTimeOffset(reset, TimeSpan.Zero).ToUnixTimeSeconds())
            {
                // The value names the reset, so a client acts on each reset once, however many answers of the session repeat it.
                var value = reset.ToString("O", CultureInfo.InvariantCulture);
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[ApiHeaders.DemoReset] = value;
                    return Task.CompletedTask;
                });
            }
        }

        var db = context.RequestServices.GetRequiredService<LifestreamDbContext>();
        var isWrite = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method);
        db.CurrentStaffId = user.IsDemo ? (isWrite ? await user.StaffIdAsync(ct) : null) : user.TokenStaffId;

        using (logger.BeginScope(new Dictionary<string, object?> { ["UserId"] = user.TokenStaffId, ["IsDemo"] = user.IsDemo }))
        {
            await next(context);
        }
    }
}

/// <summary>
/// While a signed-in person still has an administrator-set password, every endpoint except sign-in (auth/*), reading their own
/// profile (GET me) and changing the password (PUT me/password) answers 403 password_change_required.
/// </summary>
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private static readonly string AuthPrefix = ApiRoutes.Path("auth") + "/";
    private static readonly string MePath = ApiRoutes.Path(ApiRoutes.Me);
    private static readonly string MePasswordPath = ApiRoutes.Path(ApiRoutes.MePassword);

    public async Task InvokeAsync(HttpContext context, CurrentUser user)
    {
        if (user.IsAuthenticated && user.MustChangePassword && !IsAllowed(context.Request))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, ExtraProblemCodes.PasswordChangeRequired, "You must set a new password before you continue.");
        }

        await next(context);
    }

    private static bool IsAllowed(HttpRequest request)
    {
        var path = request.Path.Value?.TrimEnd('/') ?? "";
        return path.StartsWith(AuthPrefix, StringComparison.OrdinalIgnoreCase)
            || path.Equals(MePasswordPath, StringComparison.OrdinalIgnoreCase)
            || (path.Equals(MePath, StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(request.Method));
    }
}

/// <summary>Demo abuse limits: at most N write requests per client address per UTC day (the design's "creates per IP per day").</summary>
public sealed class DemoWriteCapMiddleware(RequestDelegate next)
{
    private static readonly ConcurrentDictionary<(DateOnly Day, string Ip), int> Counts = new();

    public async Task InvokeAsync(HttpContext context, CurrentUser user, TimeProvider time, IOptions<DemoSettings> options)
    {
        if (user.IsDemo && HttpMethods.IsPost(context.Request.Method) && !context.Request.Path.Value!.EndsWith(ApiRoutes.DemoReset, StringComparison.Ordinal))
        {
            var now = time.GetUtcNow().UtcDateTime;
            var today = DateOnly.FromDateTime(now);
            foreach (var old in Counts.Keys.Where(k => k.Day < today))
            {
                Counts.TryRemove(old, out _);
            }

            var count = Counts.AddOrUpdate((today, RateLimitPolicies.ClientIp(context)), 1, (_, n) => n + 1);
            if (count > options.Value.WriteCapPerIpPerDay)
            {
                var untilMidnight = today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - now;
                throw new ApiException(StatusCodes.Status429TooManyRequests, ProblemCodes.RateLimited, "The demo write limit for today has been reached.", retryAfter: untilMidnight);
            }
        }

        await next(context);
    }
}

public enum DemoTable
{
    Donors,
    Patients,
    Appointments,
    Requests,
}

public static class DemoLimits
{
    /// <summary>Refuses a create from the demo account once the table has grown beyond its seed size plus the cap.</summary>
    public static async Task EnsureRoomAsync(LifestreamDbContext db, CurrentUser user, IOptions<DemoSettings> options, DemoTable table, CancellationToken ct)
    {
        if (!user.IsDemo)
        {
            return;
        }

        var (rows, seed) = table switch
        {
            DemoTable.Donors => (await db.Donors.CountAsync(ct), SeedSizes.Donors),
            DemoTable.Patients => (await db.Patients.CountAsync(ct), SeedSizes.Patients),
            DemoTable.Appointments => (await db.Appointments.CountAsync(ct), SeedSizes.Appointments),
            _ => (await db.Requests.CountAsync(ct), SeedSizes.Requests),
        };
        if (rows >= seed + options.Value.TableGrowthCap)
        {
            throw ApiException.Conflict("The demo is full. Use Reset demo data to start again.");
        }
    }
}

/// <summary>
/// Replays the first result of a POST that arrives again with the same Idempotency-Key (a retry after a lost response or a database
/// that was still waking). Held in memory for 24 hours; concurrent duplicates wait for the first to finish.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public const string ReplayHeader = "Idempotent-Replayed";
    private static readonly ConcurrentDictionary<string, Entry> Store = new();

    public async Task InvokeAsync(HttpContext context, CurrentUser user, TimeProvider time)
    {
        var key = context.Request.Headers[ApiHeaders.IdempotencyKey].ToString();
        if (!HttpMethods.IsPost(context.Request.Method) || key.Length == 0 || !user.IsAuthenticated)
        {
            await next(context);
            return;
        }

        if (key.Length > 100)
        {
            throw ApiException.Validation(ApiHeaders.IdempotencyKey, "must be at most 100 characters");
        }

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var stale in Store.Where(e => e.Value.Created < now.AddHours(-24)).Select(e => e.Key).ToList())
        {
            Store.TryRemove(stale, out _);
        }

        var scope = $"{(user.IsDemo ? "demo:" + user.Family : user.TokenStaffId.ToString(CultureInfo.InvariantCulture))}|{context.Request.Path}|{key}";
        var entry = Store.GetOrAdd(scope, _ => new Entry(now));
        await entry.Gate.WaitAsync(context.RequestAborted);
        try
        {
            if (entry.Result is { } done)
            {
                context.Response.StatusCode = done.Status;
                context.Response.ContentType = done.ContentType;
                if (done.Location is not null)
                {
                    context.Response.Headers.Location = done.Location;
                }

                context.Response.Headers[ReplayHeader] = "true";
                await context.Response.Body.WriteAsync(done.Body, context.RequestAborted);
                return;
            }

            var original = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await next(context);
            }
            finally
            {
                context.Response.Body = original;
            }

            var status = context.Response.StatusCode;
            var bytes = buffer.ToArray();
            if (status < 500 && status is not (401 or 429))
            {
                entry.Result = new Stored(status, context.Response.ContentType, bytes, context.Response.Headers.Location.ToString() is { Length: > 0 } l ? l : null);
            }

            await original.WriteAsync(bytes, context.RequestAborted);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private sealed record Stored(int Status, string? ContentType, byte[] Body, string? Location);

    private sealed class Entry(DateTime created)
    {
        public DateTime Created { get; } = created;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Stored? Result { get; set; }
    }
}
