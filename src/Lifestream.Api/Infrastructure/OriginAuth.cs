using System.Net;
using System.Security.Cryptography;
using System.Text;
using Lifestream.Contracts;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Infrastructure;

/// <summary>
/// The public API hostname (api.lifestream.himaiz.com) is a Cloudflare Worker: the zone's WAF runs first, then the Worker forwards the
/// request to this container with <c>X-Origin-Auth</c> set to a shared secret and <c>X-Client-IP</c> set to the visitor's address.
/// </summary>
public sealed class OriginAuthSettings
{
    public const string Section = "OriginAuth";

    /// <summary>The shared secret (a Container Apps secret in Azure). Absent: the check is off (local development and tests).</summary>
    public string? Secret { get; set; }

    /// <summary><c>enforce</c> refuses requests without the secret (403). Any other value only logs them, for rollout.</summary>
    public string? Mode { get; set; }

    public bool Enforce => string.Equals(Mode, "enforce", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Requests that skip Cloudflare (the *.azurecontainerapps.io address) carry no secret. Requests that carry it came through the Worker,
/// so their <c>X-Client-IP</c> is the visitor's address and replaces the peer address (otherwise a Cloudflare edge address, which would put
/// every user in one rate-limit bucket). <c>X-Client-IP</c> is never trusted without the secret.
/// <para>
/// /healthz is exempt: the platform's startup probe calls it on the container directly, without the header. It never touches the database.
/// </para>
/// </summary>
public sealed partial class OriginAuthMiddleware(RequestDelegate next, IOptions<OriginAuthSettings> options, ILogger<OriginAuthMiddleware> logger)
{
    public const string AuthHeader = "X-Origin-Auth";
    public const string ClientIpHeader = "X-Client-IP";

    private readonly byte[]? _secret = Validate(options.Value);
    private readonly bool _enforce = options.Value.Enforce;

    public async Task InvokeAsync(HttpContext context)
    {
        if (_secret is null)
        {
            await next(context);
            return;
        }

        var fromWorker = Matches(context.Request.Headers[AuthHeader].ToString());
        var clientIp = context.Request.Headers[ClientIpHeader].ToString();
        context.Request.Headers.Remove(AuthHeader); // nothing later in the pipeline (or a log of headers) ever sees the secret
        context.Request.Headers.Remove(ClientIpHeader);

        if (fromWorker)
        {
            if (IPAddress.TryParse(clientIp, out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            await next(context);
            return;
        }

        if (string.Equals(context.Request.Path.Value, ApiRoutes.Healthz, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!_enforce)
        {
            LogMissing(logger, context.Request.Method);
            await next(context);
            return;
        }

        LogRefused(logger, context.Request.Method);
        await ProblemWriter.WriteAsync(context, StatusCodes.Status403Forbidden, ExtraProblemCodes.Forbidden, "Forbidden.");
    }

    private bool Matches(string sent)
    {
        if (sent.Length == 0)
        {
            return false;
        }

        var given = Encoding.UTF8.GetBytes(sent);
        return given.Length == _secret!.Length && CryptographicOperations.FixedTimeEquals(given, _secret);
    }

    private static byte[]? Validate(OriginAuthSettings settings)
    {
        if (string.IsNullOrEmpty(settings.Secret))
        {
            return settings.Enforce
                ? throw new InvalidOperationException("OriginAuth:Mode is enforce but OriginAuth:Secret is not set; refusing to start without the secret.")
                : null;
        }

        return settings.Secret.Length < 32
            ? throw new InvalidOperationException("OriginAuth:Secret must be at least 32 characters.")
            : Encoding.UTF8.GetBytes(settings.Secret);
    }

    // The method only: never the path (it can hold names), the header or the address.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Origin auth missing (log only): {Method} request without the Cloudflare secret")]
    private static partial void LogMissing(ILogger logger, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Origin auth refused: {Method} request without the Cloudflare secret")]
    private static partial void LogRefused(ILogger logger, string method);
}
