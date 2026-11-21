using System.Net;
using Lifestream.Api.Infrastructure;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Tests;

/// <summary>The Cloudflare Worker's secret header: what it unlocks, and what a request without it gets (log mode, then enforce).</summary>
public abstract class OriginAuthFixtureBase : ApiFixture
{
    public const string Secret = "OriginAuthTestSecret0123456789abcdefOriginAuth01"; // 48 characters, like the real one; not a real secret

    /// <summary>A Cloudflare edge address: not on a trusted network, so X-Forwarded-For from it is ignored.</summary>
    public const string Edge = "104.16.0.1";

    protected abstract string Mode { get; }

    protected override void Configure(Dictionary<string, string?> settings)
    {
        settings["Limits:LoginPerMinute"] = "3";
        settings["Limits:GlobalPerMinute"] = "40";
        settings["OriginAuth:Secret"] = Secret;
        settings["OriginAuth:Mode"] = Mode;
    }
}

public sealed class OriginAuthEnforceFixture : OriginAuthFixtureBase
{
    protected override string Mode => "enforce";
}

public sealed class OriginAuthLogFixture : OriginAuthFixtureBase
{
    protected override string Mode => "log";
}

internal static class OriginAuthRequests
{
    public static HttpRequestMessage Get(string path, string? secret, string? clientIp = null, string remote = OriginAuthFixtureBase.Edge)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        Stamp(request, secret, clientIp, remote);
        return request;
    }

    public static HttpRequestMessage Login(string username, string? secret, string? clientIp, string remote = OriginAuthFixtureBase.Edge)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AuthLogin))
        {
            Content = JsonContent.Create(new Lifestream.Contracts.Auth.LoginRequest(username, "wrong-password-1"), options: ApiFixture.Json),
        };
        Stamp(request, secret, clientIp, remote);
        return request;
    }

    private static void Stamp(HttpRequestMessage request, string? secret, string? clientIp, string remote)
    {
        request.Headers.Add("X-Test-Remote", remote);
        if (secret is not null)
        {
            request.Headers.Add(OriginAuthMiddleware.AuthHeader, secret);
        }

        if (clientIp is not null)
        {
            request.Headers.Add(OriginAuthMiddleware.ClientIpHeader, clientIp);
        }
    }
}

public sealed class OriginAuthEnforceTests(OriginAuthEnforceFixture fx) : IClassFixture<OriginAuthEnforceFixture>
{
    [Fact]
    public async Task WithoutTheSecret_IsRefused()
    {
        using var client = fx.NewClient();
        using var request = OriginAuthRequests.Get(ApiRoutes.Readyz, secret: null);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var problem = await response.Problem(403, "forbidden");
    }

    [Fact]
    public async Task WithAWrongSecret_IsRefused_EvenWithAClientIp()
    {
        using var client = fx.NewClient();
        using var request = OriginAuthRequests.Get(ApiRoutes.Readyz, secret: OriginAuthFixtureBase.Secret[..^1] + "x", clientIp: "198.51.100.20");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var problem = await response.Problem(403, "forbidden");
    }

    [Fact]
    public async Task WithTheSecret_IsServed()
    {
        using var client = fx.NewClient();
        using var request = OriginAuthRequests.Get(ApiRoutes.Readyz, OriginAuthFixtureBase.Secret, clientIp: "198.51.100.21");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_WithoutTheSecret_IsServed_ForThePlatformProbe()
    {
        using var client = fx.NewClient();
        using var request = OriginAuthRequests.Get(ApiRoutes.Healthz, secret: null);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ThroughTheWorker_EachVisitorHasItsOwnLoginLimit()
    {
        // Every request arrives from the same Cloudflare edge address; X-Client-IP (trusted because the secret is right) names the visitor.
        using var client = fx.NewClient();
        var first = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var request = OriginAuthRequests.Login($"nobody-a{i}", OriginAuthFixtureBase.Secret, clientIp: "198.51.100.30");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            first.Add(response.StatusCode);
        }

        using var other = OriginAuthRequests.Login("nobody-b", OriginAuthFixtureBase.Secret, clientIp: "198.51.100.31");
        using var otherResponse = await client.SendAsync(other, TestContext.Current.CancellationToken);

        Assert.Equal(3, first.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(2, first.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(HttpStatusCode.Unauthorized, otherResponse.StatusCode); // a second visitor behind the same edge is not limited by the first
    }

    [Fact]
    public void EnforceWithoutASecret_RefusesToStart()
    {
        var settings = Options.Create(new OriginAuthSettings { Mode = "enforce" });
        Assert.Throws<InvalidOperationException>(() => new OriginAuthMiddleware(_ => Task.CompletedTask, settings, NullLogger<OriginAuthMiddleware>.Instance));
    }

    [Fact]
    public void AShortSecret_RefusesToStart()
    {
        var settings = Options.Create(new OriginAuthSettings { Secret = "too-short", Mode = "log" });
        Assert.Throws<InvalidOperationException>(() => new OriginAuthMiddleware(_ => Task.CompletedTask, settings, NullLogger<OriginAuthMiddleware>.Instance));
    }

    [Fact]
    public async Task NoSecretConfigured_TheCheckIsOff()
    {
        var reached = false;
        var middleware = new OriginAuthMiddleware(_ => { reached = true; return Task.CompletedTask; }, Options.Create(new OriginAuthSettings()), NullLogger<OriginAuthMiddleware>.Instance);
        await middleware.InvokeAsync(new DefaultHttpContext());
        Assert.True(reached);
    }
}

public sealed class OriginAuthLogModeTests(OriginAuthLogFixture fx) : IClassFixture<OriginAuthLogFixture>
{
    [Fact]
    public async Task WithoutTheSecret_IsServed_AndLogged()
    {
        using var client = fx.NewClient();
        using var request = OriginAuthRequests.Get(ApiRoutes.Readyz, secret: null);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(fx.Logs.Lines, l => l.Contains("Origin auth missing (log only)", StringComparison.Ordinal));
        Assert.DoesNotContain(fx.Logs.Lines, l => l.Contains(OriginAuthFixtureBase.Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheSecret_ASpoofedClientIp_IsIgnored()
    {
        // Rotating X-Client-IP without the secret must not buy a fresh limit: the request is judged by its real address.
        using var client = fx.NewClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var request = OriginAuthRequests.Login($"nobody-s{i}", secret: null, clientIp: $"203.0.113.{i + 1}", remote: "198.51.100.40");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }
}
