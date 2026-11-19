using System.Net;
using Lifestream.Api.Auth;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Api.Tests;

/// <summary>API-02: a demo session can be ended on the server, and a refresh token works once (the verifier's probe P06).</summary>
public sealed class DemoSessionRevocationTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private async Task<TokenResponse> DemoLoginAsync()
    {
        await fx.AddDemoAsync();
        using var anonymous = fx.NewClient();
        return await (await anonymous.PostApi(ApiRoutes.AuthDemo)).Read<TokenResponse>();
    }

    [Fact]
    public async Task DemoLogout_RevokesThatSession_RefreshTokenAndAccessTokenStop_OtherVisitorsAreUnaffected()
    {
        var one = await DemoLoginAsync();
        var other = await DemoLoginAsync();
        using var anonymous = fx.NewClient();
        using var oneClient = fx.NewClient(one.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await oneClient.GetApi(ApiRoutes.Me)).StatusCode);

        using var logout = await anonymous.PostApi(ApiRoutes.AuthLogout, new RefreshRequest(one.RefreshToken));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var refresh = await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(one.RefreshToken));
        using var refreshProblem = await refresh.Problem(401, "unauthorized");
        using var me = await oneClient.GetApi(ApiRoutes.Me);
        using var meProblem = await me.Problem(401, "unauthorized");

        using var otherClient = fx.NewClient(other.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetApi(ApiRoutes.Me)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(other.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task DemoRefreshToken_WorksOnce_ReusingARotatedOneFailsAndEndsTheWholeSession()
    {
        var first = await DemoLoginAsync();
        using var anonymous = fx.NewClient();
        var second = await (await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(first.RefreshToken))).Read<TokenResponse>();
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        using var secondClient = fx.NewClient(second.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await secondClient.GetApi(ApiRoutes.Me)).StatusCode);

        // The first token was already used: presenting it again means a copy exists, so the session ends.
        using var reuse = await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(first.RefreshToken));
        using var reuseProblem = await reuse.Problem(401, "unauthorized");
        using var latest = await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(second.RefreshToken));
        using var latestProblem = await latest.Problem(401, "unauthorized");
        using var denied = await secondClient.GetApi(ApiRoutes.Me);
        using var deniedProblem = await denied.Problem(401, "unauthorized");
    }

    [Fact]
    public async Task DemoLogout_WithTheLatestRefreshToken_EndsTheSessionIncludingEarlierAccessTokens()
    {
        var first = await DemoLoginAsync();
        using var anonymous = fx.NewClient();
        var second = await (await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(first.RefreshToken))).Read<TokenResponse>();
        using var firstClient = fx.NewClient(first.AccessToken);
        using var secondClient = fx.NewClient(second.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostApi(ApiRoutes.AuthLogout, new RefreshRequest(second.RefreshToken))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await firstClient.GetApi(ApiRoutes.Me)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await secondClient.GetApi(ApiRoutes.Me)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(second.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task DemoLogout_WithATokenThatIsNotOurs_StillNoContent_NothingRevoked()
    {
        var session = await DemoLoginAsync();
        using var anonymous = fx.NewClient();
        using var forged = await anonymous.PostApi(ApiRoutes.AuthLogout, new RefreshRequest("a.b.c"));
        Assert.Equal(HttpStatusCode.NoContent, forged.StatusCode);
        using var client = fx.NewClient(session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetApi(ApiRoutes.Me)).StatusCode);
    }

    [Fact]
    public void Denylist_EntriesExpire_AndAUsedTokenIdCanOnlyBeUsedOnceWhileItLives()
    {
        var denylist = fx.Factory.Services.GetRequiredService<DemoSessionDenylist>();
        var clock = fx.Clock;
        var family = Guid.NewGuid();
        var token = Guid.NewGuid();
        var until = clock.GetUtcNow().UtcDateTime.AddMinutes(10);

        Assert.False(denylist.IsRevoked(family));
        denylist.RevokeFamily(family, until);
        Assert.True(denylist.IsRevoked(family));
        Assert.True(denylist.TryUse(token, until));
        Assert.False(denylist.TryUse(token, until));

        clock.Advance(TimeSpan.FromMinutes(11));
        try
        {
            Assert.False(denylist.IsRevoked(family));
            Assert.True(denylist.TryUse(token, clock.GetUtcNow().UtcDateTime.AddMinutes(10)));
        }
        finally
        {
            clock.Advance(TimeSpan.FromMinutes(-11));
        }
    }
}
