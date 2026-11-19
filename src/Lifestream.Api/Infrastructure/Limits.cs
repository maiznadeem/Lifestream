using System.Threading.RateLimiting;
using Lifestream.Contracts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Infrastructure;

public enum BodyLimitKind
{
    Default,
    Login,
    Demo,
    Photo,
}

/// <summary>Endpoint metadata choosing which request-body size limit applies.</summary>
public sealed record BodyLimitMetadata(BodyLimitKind Kind);

public static class RateLimitPolicies
{
    public const string Login = "login";
    public const string Demo = "demo";

    public static string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Global per-IP limit on every endpoint plus separate, tighter per-IP limits for login and demo login.</summary>
    public static void Configure(RateLimiterOptions options, LimitSettings limits)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ => Window(limits.GlobalPerMinute)));
        options.AddPolicy(Login, context => RateLimitPartition.GetFixedWindowLimiter("login:" + ClientIp(context), _ => Window(limits.LoginPerMinute)));
        options.AddPolicy(Demo, context => RateLimitPartition.GetFixedWindowLimiter("demo:" + ClientIp(context), _ => Window(limits.DemoPerMinute)));
        options.OnRejected = async (rejected, _) =>
        {
            var wait = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry : TimeSpan.FromSeconds(60);
            await ProblemWriter.WriteAsync(rejected.HttpContext, StatusCodes.Status429TooManyRequests, ProblemCodes.RateLimited, "Too many requests. Try again shortly.", retryAfter: wait);
        };
    }

    private static FixedWindowRateLimiterOptions Window(int perMinute) =>
        new() { PermitLimit = Math.Max(1, perMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true };
}

/// <summary>Refuses an over-size request body with 413 before any handler reads it.</summary>
public sealed class BodyLimitMiddleware(RequestDelegate next, IOptions<LimitSettings> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var limits = options.Value;
        var kind = context.GetEndpoint()?.Metadata.GetMetadata<BodyLimitMetadata>()?.Kind ?? BodyLimitKind.Default;
        long max = kind switch
        {
            BodyLimitKind.Login => limits.LoginBodyBytes,
            BodyLimitKind.Demo => limits.DemoBodyBytes,
            BodyLimitKind.Photo => limits.PhotoBodyBytes,
            _ => limits.MaxBodyBytes,
        };

        if (context.Request.ContentLength is { } length && length > max)
        {
            await ProblemWriter.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, ExtraProblemCodes.PayloadTooLarge, "The request is too large.");
            return;
        }

        var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = max;
        }
        else if (context.Request.ContentLength is null && context.Request.Body is not null)
        {
            // No Content-Length (chunked) and no server-enforced limit: count the bytes as they are read.
            context.Request.Body = new LimitedStream(context.Request.Body, max);
        }

        await next(context);
    }

    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            _read += count;
            if (_read > max)
            {
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            }

            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
