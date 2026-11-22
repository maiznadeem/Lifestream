using Lifestream.Api.Auth;
using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Features;

public static class AuthEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("").AllowAnonymous();

        auth.MapPost(ApiRoutes.AuthLogin, async (HttpRequest http, AuthService service, CancellationToken ct) =>
            {
                var request = await http.ReadBodyAsync<LoginRequest>(ct);
                new Check().Text("username", request.Username, 50).Text("password", request.Password, 128).ThrowIfInvalid();
                return Results.Ok(await service.LoginAsync(request, ct));
            })
            .AddEndpointFilter(DailyResetFirst)
            .RequireRateLimiting(RateLimitPolicies.Login)
            .WithMetadata(new BodyLimitMetadata(BodyLimitKind.Login));

        // The demo login has its own, tighter per-IP limit and a tiny body limit (it takes no body at all).
        auth.MapPost(ApiRoutes.AuthDemo, async (AuthService service, CancellationToken ct) => Results.Ok(await service.DemoLoginAsync(ct)))
            .AddEndpointFilter(DailyResetFirst)
            .RequireRateLimiting(RateLimitPolicies.Demo)
            .WithMetadata(new BodyLimitMetadata(BodyLimitKind.Demo));

        auth.MapPost(ApiRoutes.AuthRefresh, async (HttpRequest http, AuthService service, CancellationToken ct) =>
            {
                var request = await http.ReadBodyAsync<RefreshRequest>(ct);
                new Check().Text("refreshToken", request.RefreshToken, 2000).ThrowIfInvalid();
                return Results.Ok(await service.RefreshAsync(request, ct));
            })
            .RequireRateLimiting(RateLimitPolicies.Login)
            .WithMetadata(new BodyLimitMetadata(BodyLimitKind.Login));

        // Needs no access token: the refresh token in the body identifies the session, and the whole family is revoked on the server.
        auth.MapPost(ApiRoutes.AuthLogout, async (HttpRequest http, AuthService service, CancellationToken ct) =>
            {
                var request = await http.ReadBodyAsync<RefreshRequest>(ct);
                new Check().Text("refreshToken", request.RefreshToken, 2000).ThrowIfInvalid();
                await service.LogoutAsync(request.RefreshToken, ct);
                return Results.NoContent();
            })
            .RequireRateLimiting(RateLimitPolicies.Login)
            .WithMetadata(new BodyLimitMetadata(BodyLimitKind.Login));
    }

    /// <summary>
    /// The first sign-in of a UTC day (after a cold start, the usual case) runs that day's demo reset before it hands out the session, so
    /// the session begins on the new data and is never told "the demo data was reset" about a reset it caused itself. Without this the
    /// reset ran on the session's first request, after the session had begun, and every answer of that session carried X-Demo-Reset.
    /// </summary>
    private static async ValueTask<object?> DailyResetFirst(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        if (services.GetRequiredService<IOptions<DemoSettings>>().Value.Enabled)
        {
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
            await services.GetRequiredService<DemoDirectory>().EnsureDailyResetAsync(services, DateOnly.FromDateTime(now), now, context.HttpContext.RequestAborted);
        }

        return await next(context);
    }
}
