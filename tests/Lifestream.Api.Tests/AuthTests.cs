using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Data.Security;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests;

/// <summary>Auth against a real SQL Server (the fixture asserts the server version): login, lockout, tokens, refresh, logout, demo.</summary>
public sealed class AuthTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpResponseMessage> LoginAsync(string username, string password, string? ip = null)
    {
        using var client = fx.NewClient(ip: ip);
        return await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest(username, password));
    }

    [Fact]
    public async Task Fixture_RunsAgainstRealSqlServer() => Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);

    [Fact]
    public async Task Login_VerifiesPasswordAgainstStoredHash_RightPasswordIssuesTokens()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        await using var db = fx.Db.NewContext();
        var hash = await db.Staff.Where(s => s.Id == user.Id).Select(s => s.PasswordHash).SingleAsync(Ct);
        Assert.True(PasswordHashing.IsValidHash(hash));

        using var ok = await LoginAsync(user.Username, ApiFixture.Password);
        var tokens = await ok.Read<TokenResponse>();
        Assert.Equal(user.Id, tokens.User.StaffId);
        Assert.False(string.IsNullOrEmpty(tokens.AccessToken));
        Assert.DoesNotContain(hash, await ok.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var wrong = await LoginAsync(user.Username, ApiFixture.Password + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownUserAndWrongPassword_SameResponse()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        using var unknown = await LoginAsync("nobody-" + Guid.NewGuid().ToString("N")[..8], "whatever-password");
        using var wrong = await LoginAsync(user.Username, "wrong-password-123");

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(unknown.StatusCode, wrong.StatusCode);
        using var a = await unknown.Problem(401, "invalid_credentials");
        using var b = await wrong.Problem(401, "invalid_credentials");
        Assert.Equal(a.RootElement.GetProperty("title").GetString(), b.RootElement.GetProperty("title").GetString());
        Assert.Equal(a.RootElement.GetProperty("type").GetString(), b.RootElement.GetProperty("type").GetString());
        Assert.Equal(a.RootElement.EnumerateObject().Select(p => p.Name).Order(), b.RootElement.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Login_FiveFailures_LocksOut_429WithRetryAfter()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        for (var i = 0; i < 5; i++)
        {
            using var failed = await LoginAsync(user.Username, "wrong-password-" + i);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // Even the right password is refused while the lock lasts.
        using var locked = await LoginAsync(user.Username, ApiFixture.Password);
        using var problem = await locked.Problem(429, ProblemCodes.LockedOut);
        Assert.True(int.Parse(locked.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) > 0);

        // The lock expires with the clock.
        fx.Clock.Advance(TimeSpan.FromMinutes(16));
        using var after = await LoginAsync(user.Username, ApiFixture.Password);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Token_Expires15Min_AccessLifetimeAtMostFifteenMinutes_ExpiredIs401()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        using var login = await LoginAsync(user.Username, ApiFixture.Password);
        var tokens = await login.Read<TokenResponse>();
        Assert.True(tokens.AccessTokenExpiresUtc - fx.Clock.GetUtcNow().UtcDateTime <= TimeSpan.FromMinutes(15));

        using var client = fx.NewClient(tokens.AccessToken);
        using var fresh = await client.GetApi(ApiRoutes.Me);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        fx.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        try
        {
            using var expired = await client.GetApi(ApiRoutes.Me);
            using var problem = await expired.Problem(401, "unauthorized");
        }
        finally
        {
            fx.Clock.Advance(-(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public async Task Refresh_RotatesToken_OldOneIsRefused_AndReuseRevokesTheWholeFamily()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        using var login = await LoginAsync(user.Username, ApiFixture.Password);
        var first = await login.Read<TokenResponse>();

        using var anonymous = fx.NewClient();
        using var rotated = await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(first.RefreshToken));
        var second = await rotated.Read<TokenResponse>();
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        using (var newAccess = fx.NewClient(second.AccessToken))
        {
            using var me = await newAccess.GetApi(ApiRoutes.Me);
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }

        // The first token was already used. Presenting it again means a copy exists: the session ends.
        using var reuse = await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        using var latest = await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(second.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, latest.StatusCode);
        using var staleAccess = fx.NewClient(second.AccessToken);
        using var denied = await staleAccess.GetApi(ApiRoutes.Me);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheWholeFamily_OnTheServer_WithoutAnAccessToken()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        using var login = await LoginAsync(user.Username, ApiFixture.Password);
        var first = await login.Read<TokenResponse>();
        using var anonymous = fx.NewClient();
        using var rotated = await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(first.RefreshToken));
        var current = await rotated.Read<TokenResponse>();

        using var accessClient = fx.NewClient(current.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await accessClient.GetApi(ApiRoutes.Me)).StatusCode);

        // No Authorization header at all: the refresh token alone names the session.
        using var logout = await anonymous.PostApi(ApiRoutes.AuthLogout, new RefreshRequest(current.RefreshToken));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var refreshAfter = await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(current.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfter.StatusCode);
        using var accessAfter = await accessClient.GetApi(ApiRoutes.Me);
        Assert.Equal(HttpStatusCode.Unauthorized, accessAfter.StatusCode);

        await using var db = fx.Db.NewContext();
        var hash = Lifestream.Api.Auth.TokenService.HashRefresh(current.RefreshToken);
        var family = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => t.FamilyId).SingleAsync(Ct);
        var inFamily = await db.RefreshTokens.Where(t => t.FamilyId == family).ToListAsync(Ct);
        Assert.Equal(2, inFamily.Count);
        Assert.All(inFamily, t => Assert.NotNull(t.RevokedAtUtc));
    }

    [Fact]
    public async Task Logout_UnknownToken_StillNoContent_NothingRevoked()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        using var anonymous = fx.NewClient();
        using var logout = await anonymous.PostApi(ApiRoutes.AuthLogout, new RefreshRequest("not-a-token"));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetApi(ApiRoutes.Me)).StatusCode);
    }

    [Fact]
    public async Task DemoLogin_SessionsAreIndependent_AndTokensAreLowPrivilege()
    {
        await fx.AddDemoAsync();
        using var one = fx.NewClient();
        using var two = fx.NewClient();
        var a = await (await one.PostApi(ApiRoutes.AuthDemo)).Read<TokenResponse>();
        var b = await (await two.PostApi(ApiRoutes.AuthDemo)).Read<TokenResponse>();
        Assert.True(a.User.IsDemo);
        Assert.Equal(Role.Staff, a.User.Role);
        Assert.False(a.User.CanManageStaff);
        Assert.NotEqual(a.RefreshToken, b.RefreshToken);

        // Refreshing one visitor's session must not end the other's.
        using var anonymous = fx.NewClient();
        var refreshedA = await (await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(a.RefreshToken))).Read<TokenResponse>();
        Assert.True(refreshedA.User.IsDemo);
        var refreshedB = await (await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(b.RefreshToken))).Read<TokenResponse>();
        Assert.True(refreshedB.User.IsDemo);
        using var stillWorks = fx.NewClient(b.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await stillWorks.GetApi(ApiRoutes.Me)).StatusCode);
    }

    [Fact]
    public async Task DemoLogin_ByPassword_IsRefused()
    {
        await fx.AddDemoAsync();
        using var response = await LoginAsync("demo", ApiFixture.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_OversizeBody_413_AndDemoLoginHasItsOwnSmallerLimit()
    {
        using var client = fx.NewClient();
        var big = new string('a', 3000);
        using var login = await client.PostAsync(ApiRoutes.Path(ApiRoutes.AuthLogin), new StringContent($"{{\"username\":\"{big}\",\"password\":\"x\"}}", Encoding.UTF8, "application/json"), Ct);
        using var p1 = await login.Problem(413, "payload_too_large");

        // 1.5 KB is fine for login (2 KB limit) but too big for the demo endpoint (1 KB limit).
        var mid = new string('b', 1500);
        using var demo = await client.PostAsync(ApiRoutes.Path(ApiRoutes.AuthDemo), new StringContent(mid, Encoding.UTF8, "application/json"), Ct);
        using var p2 = await demo.Problem(413, "payload_too_large");
        using var loginMid = await client.PostAsync(ApiRoutes.Path(ApiRoutes.AuthLogin), new StringContent($"{{\"username\":\"{mid[..30]}\",\"password\":\"x\"}}", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, loginMid.StatusCode);
    }

    [Fact]
    public async Task Request_OversizeJsonBody_413()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var content = new StringContent("{\"name\":\"" + new string('x', 70_000) + "\"}", Encoding.UTF8, "application/json");
        using var response = await admin.Client.PostAsync(ApiRoutes.Path(ApiRoutes.Donors), content, Ct);
        using var problem = await response.Problem(413, "payload_too_large");
    }

    [Fact]
    public async Task NoToken_IsProblem401_WithCorrelationId()
    {
        using var client = fx.NewClient();
        using var response = await client.GetApi(ApiRoutes.Donors);
        using var problem = await response.Problem(401, "unauthorized");
        Assert.True(response.Headers.Contains(ApiHeaders.CorrelationId));
    }

    [Fact]
    public async Task Token_WithWrongSignature_IsRejected()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        using var client = fx.NewClient(user.Client.DefaultRequestHeaders.Authorization!.Parameter![..^3] + "abc");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetApi(ApiRoutes.Me)).StatusCode);
    }

    [Fact]
    public async Task AccessToken_ClaimsCarryNoPasswordMaterial()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        var token = user.Client.DefaultRequestHeaders.Authorization!.Parameter!;
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(token.Split('.')[1].Replace('-', '+').Replace('_', '/').PadRight((token.Split('.')[1].Length + 3) / 4 * 4, '=')));
        using var json = JsonDocument.Parse(payload);
        Assert.False(json.RootElement.TryGetProperty("passwordHash", out _));
        Assert.DoesNotContain("AQAAAAI", payload, StringComparison.Ordinal);
        Assert.Equal(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), json.RootElement.GetProperty("sub").GetString());
        _ = new AuthenticationHeaderValue("Bearer", token);
    }
}

public sealed class LowLimitApiFixture : ApiFixture
{
    protected override void Configure(Dictionary<string, string?> settings)
    {
        settings["Limits:LoginPerMinute"] = "3";
        settings["Limits:DemoPerMinute"] = "2";
        settings["Limits:GlobalPerMinute"] = "40";
    }
}

/// <summary>Per-IP rate limits: login and demo login are separate buckets, and every endpoint has a global one.</summary>
public sealed class RateLimitTests(LowLimitApiFixture fx) : IClassFixture<LowLimitApiFixture>
{
    [Fact]
    public async Task Login_PerIpLimit_429WithRetryAfter_OtherIpUnaffected()
    {
        using var a = fx.NewClient(ip: "203.0.113.10");
        for (var i = 0; i < 3; i++)
        {
            using var attempt = await a.PostApi(ApiRoutes.AuthLogin, new LoginRequest("nobody", "wrong-password-1"));
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        using var limited = await a.PostApi(ApiRoutes.AuthLogin, new LoginRequest("nobody", "wrong-password-1"));
        using var problem = await limited.Problem(429, ProblemCodes.RateLimited);
        Assert.True(limited.Headers.Contains("Retry-After"));

        using var other = fx.NewClient(ip: "203.0.113.11");
        using var fine = await other.PostApi(ApiRoutes.AuthLogin, new LoginRequest("nobody", "wrong-password-1"));
        Assert.Equal(HttpStatusCode.Unauthorized, fine.StatusCode);
    }

    [Fact]
    public async Task DemoLogin_HasItsOwnPerIpLimit_SeparateFromLogin()
    {
        await fx.AddDemoAsync(ip: "203.0.113.20");
        using var client = fx.NewClient(ip: "203.0.113.21");

        // Use up the normal login budget for this address...
        for (var i = 0; i < 3; i++)
        {
            (await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest("nobody", "wrong-password-1"))).Dispose();
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostApi(ApiRoutes.AuthLogin, new LoginRequest("nobody", "wrong-password-1"))).StatusCode);

        // ...the demo bucket is untouched, and it has its own (smaller) limit.
        Assert.Equal(HttpStatusCode.OK, (await client.PostApi(ApiRoutes.AuthDemo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostApi(ApiRoutes.AuthDemo)).StatusCode);
        using var limited = await client.PostApi(ApiRoutes.AuthDemo);
        using var problem = await limited.Problem(429, ProblemCodes.RateLimited);
    }

    [Fact]
    public async Task EveryEndpoint_HasAGlobalPerIpLimit()
    {
        using var client = fx.NewClient(ip: "203.0.113.30");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 60; i++)
        {
            using var response = await client.GetAsync(ApiRoutes.Healthz, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.OK, statuses[0]);
    }
}
