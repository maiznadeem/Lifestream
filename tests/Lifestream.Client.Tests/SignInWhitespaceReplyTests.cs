using System.Net;
using System.Text;
using Lifestream.Client.Api;
using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

/// <summary>
/// SHELL-02: a 200 sign-in reply whose access token, refresh token or display name is only whitespace is no sign-in. Probe V12Probes.Probe_SignIn200_Edges
/// showed such a reply opening the main window with a blank name; it must show the inline error instead. Real API client and wake-aware handler on a stub network.
/// </summary>
[Collection("GlobalErrorHandler")]
public sealed class SignInWhitespaceReplyTests
{
    private const string GoodUser = "{\"staffId\":1,\"displayName\":\"X\",\"role\":\"Staff\",\"isDemo\":false,\"canManageStaff\":false}";

    private static string Reply(string access, string refresh, string user) =>
        "{\"accessToken\":\"" + access + "\",\"refreshToken\":\"" + refresh + "\",\"accessTokenExpiresUtc\":\"2030-01-01T00:00:00Z\",\"user\":" + user + "}";

    [Theory]
    [InlineData("   ", "r", GoodUser)]
    [InlineData("\\t\\n", "r", GoodUser)]
    [InlineData("a", "   ", GoodUser)]
    [InlineData("a", "r", "{\"staffId\":1,\"displayName\":\"   \",\"role\":\"Staff\",\"isDemo\":false,\"canManageStaff\":false}")]
    [InlineData("a", "r", "{\"staffId\":1,\"displayName\":\"\\u00a0\\t\",\"role\":\"Staff\",\"isDemo\":false,\"canManageStaff\":false}")]
    public async Task RealClient_SignInAnsweredWith200ButWhitespaceOnlyValues_ShowsTheInlineError_AndNeverOpensTheMainWindow(string access, string refresh, string user)
    {
        using var rig = new Rig();
        var body = Reply(access, refresh, user);
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Budget = TimeSpan.Zero, Clock = new ManualTimeProvider() }, new ServerStatus(), handler)) { BaseAddress = new Uri("https://api.test/") };
        using var inner = new LifestreamApiClient(http, rig.Session);
        var login = new Auth.LoginViewModel(ScopedApiClient.Wrap(inner, strict: true), rig.Session, rig.Get<Navigation.IAppFlow>()) { Username = "u", Password = TestSecrets.P };

        await login.SignInCommand.ExecuteAsync();

        Assert.NotEqual(Auth.LoginProblem.None, login.Problem);
        Assert.False(string.IsNullOrEmpty(login.Message)); // the inline message
        Assert.True(login.SignInCommand.CanExecute(null)); // the button is enabled again
        Assert.False(rig.Session.IsAuthenticated);
        Assert.DoesNotContain("ShowMain", rig.Windows.Events); // the main window never opened
    }

    [Fact]
    public async Task RealClient_SignInAnsweredWithAGoodReply_StillSignsIn_SoTheCheckIsNotJustRejectingEverything()
    {
        using var rig = new Rig();
        var body = Reply("a", "r", GoodUser);
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Budget = TimeSpan.Zero, Clock = new ManualTimeProvider() }, new ServerStatus(), handler)) { BaseAddress = new Uri("https://api.test/") };
        using var inner = new LifestreamApiClient(http, rig.Session);
        var login = new Auth.LoginViewModel(ScopedApiClient.Wrap(inner, strict: true), rig.Session, rig.Get<Navigation.IAppFlow>()) { Username = "u", Password = TestSecrets.P };

        await login.SignInCommand.ExecuteAsync();

        Assert.True(rig.Session.IsAuthenticated);
        Assert.Contains("ShowMain", rig.Windows.Events);
    }
}
