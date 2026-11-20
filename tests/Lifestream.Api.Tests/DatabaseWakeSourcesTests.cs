using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Lifestream.Api.Tests;

/// <summary>
/// Every wake of the free database uses about 1,000 of its 100,000 free vCore-seconds a month, so only a person may cause one: by opening
/// the sign-in window (the client then calls /readyz) or by signing in. This pins what does NOT reach the database: a new process, the
/// platform's probe and the deploy's health check (/healthz), a scanner's stray request, and a request without a valid session.
/// </summary>
public sealed class DatabaseWakeSourcesTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpStatusCode> SendAsync(ColdHost host, HttpMethod method, string path, string? bearer = null, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await host.Client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task ANewProcess_Probes_StrayRequests_AndRequestsWithoutASession_OpenNoConnection()
    {
        await using var host = new ColdHost(fx);

        // The startup probe and the deploy's health check, as often as they like.
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await SendAsync(host, HttpMethod.Get, ApiRoutes.Healthz));
        }

        // What a scanner sends: the root, guessed paths, another method.
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(host, HttpMethod.Get, "/"));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(host, HttpMethod.Get, "/.env"));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(host, HttpMethod.Get, "/livez"));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(host, HttpMethod.Get, "/api/v1/nothing-here"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await SendAsync(host, HttpMethod.Post, ApiRoutes.Healthz));

        // The API without a session: no token, a token that is no token, and a well-formed token signed with another key.
        var foreign = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "lifestream-api",
            Audience = "lifestream-client",
            Claims = new Dictionary<string, object> { ["sub"] = "1", ["name"] = "x", ["role"] = "Admin", ["sid"] = Guid.NewGuid().ToString("N") },
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(48)), SecurityAlgorithms.HmacSha256),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(host, HttpMethod.Get, ApiRoutes.Path(ApiRoutes.Donors)));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(host, HttpMethod.Get, ApiRoutes.Path(ApiRoutes.Donors), bearer: "not-a-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(host, HttpMethod.Get, ApiRoutes.Path(ApiRoutes.Me), bearer: foreign));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(host, HttpMethod.Post, ApiRoutes.Path(ApiRoutes.Donors), bearer: foreign, json: "{}"));

        // A sign-in that is refused before it is looked up (no name, no password).
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(host, HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AuthLogin), json: """{"username":"","password":""}"""));

        await host.WarmUp.OfflineCompletion.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await Task.Delay(300, Ct); // anything started in the background would have opened its connection by now
        Assert.Equal(0, host.Connections.Opened);
        Assert.Empty(host.Commands.Commands);
        Assert.Equal(0, host.WarmUp.Runs);
    }

    [Fact]
    public async Task OnlyReadyz_AndASignIn_ReachTheDatabase()
    {
        await fx.AddDemoAsync();

        // /readyz: its own probe (a stand-in here) and the warm-up it starts.
        await using (var host = new ColdHost(fx))
        {
            Assert.Equal(HttpStatusCode.OK, await host.ReadyAndWarmAsync());
            Assert.Equal(1, host.WarmUp.Runs);
            Assert.True(host.Connections.Opened > 0);
        }

        // A sign-in, in a process nobody asked /readyz of.
        await using (var host = new ColdHost(fx))
        {
            using var signIn = await host.Client.PostAsync(ApiRoutes.Path(ApiRoutes.AuthDemo), null, Ct);
            var tokens = await signIn.Read<TokenResponse>();
            Assert.True(host.Connections.Opened > 0);
            Assert.Equal(0, host.WarmUp.Runs);

            // An idle signed-in session does nothing by itself: no request, no connection.
            var opened = host.Connections.Opened;
            await Task.Delay(500, Ct);
            Assert.Equal(opened, host.Connections.Opened);
            Assert.False(string.IsNullOrEmpty(tokens.AccessToken));
        }
    }
}
