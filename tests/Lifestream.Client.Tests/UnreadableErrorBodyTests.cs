using System.Net;
using System.Text;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

/// <summary>
/// SHELL-02: whatever the error response looks like (unknown charset, odd content type, any body), the REAL
/// LifestreamApiClient and WakeAwareHandler (only the network stubbed) turn it into a problem, and sign-in shows an
/// inline message instead of raising to the global handler.
/// </summary>
[Collection("GlobalErrorHandler")]
public sealed class UnreadableErrorBodyTests
{
    private static readonly string[] Charsets = ["no-such-charset", "utf-8", "UTF-16", "iso-8859-1", "\"utf-8\"", "x-bogus", "utf-7", "", "utf-32be", "windows-1252"];
    private static readonly string[] ContentTypes = ["application/json", "application/problem+json", "text/html", "text/plain", "application/octet-stream", "application/xml"];
    private static readonly byte[][] Bodies =
    [
        [],
        "{\"code\":\"x\"}"u8.ToArray(),
        "[]"u8.ToArray(),
        "<html>Bad gateway</html>"u8.ToArray(),
        [0xFF, 0xFE, 0x00, 0xD8, 0x41],
        [0xC3, 0x28, 0xA0, 0xA1],
    ];

    public static TheoryData<int, string, string, int> Cases()
    {
        var data = new TheoryData<int, string, string, int>();
        foreach (var status in new[] { 400, 401, 403, 404, 429, 500, 502, 503, 504 })
        {
            foreach (var charset in Charsets)
            {
                for (var c = 0; c < ContentTypes.Length; c++)
                {
                    for (var b = 0; b < Bodies.Length; b++)
                    {
                        if ((status + charset.Length + c + b) % 5 == 0 || charset == "no-such-charset")
                        {
                            data.Add(status, charset, ContentTypes[c], b);
                        }
                    }
                }
            }
        }

        return data;
    }

    private static HttpResponseMessage Respond(int status, string charset, string contentType, int body)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(Bodies[body]) };
        var header = charset.Length == 0 ? contentType : $"{contentType}; charset={charset}";
        response.Content.Headers.TryAddWithoutValidation("Content-Type", header);
        return response;
    }

    private static (LifestreamApiClient Api, Rig Rig) NewClient(Func<HttpResponseMessage> respond)
    {
        var rig = new Rig();
        var handler = new ScriptedHandler((_, _) => respond());
        var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Budget = TimeSpan.Zero, Clock = new ManualTimeProvider() }, new ServerStatus(), handler)) { BaseAddress = new Uri("https://api.test/") };
        return (new LifestreamApiClient(http, rig.Session), rig);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RealClient_UnreadableErrorBody_BecomesAProblem_NeverAnException(int status, string charset, string contentType, int body)
    {
        var (api, rig) = NewClient(() => Respond(status, charset, contentType, body));
        using (api)
        using (rig)
        {
            var result = await api.GetReadinessAsync();

            Assert.False(result.IsOk);
            Assert.NotNull(result.Problem);
            Assert.False(string.IsNullOrEmpty(result.Problem!.Code));
        }
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    [InlineData(503)]
    [InlineData(429)]
    public async Task Login_ErrorWithUnknownCharset_ThroughRealClient_ShowsInlineMessage_NotTheGlobalDialog(int status)
    {
        var (api, rig) = NewClient(() => Respond(status, "no-such-charset", "application/json", 1));
        using (api)
        using (rig)
        {
            var vm = new LoginViewModel(api, rig.Session, rig.Get<IAppFlow>()) { Username = "u", Password = TestSecrets.P };
            var captured = new List<Exception>();
            var previous = AsyncRelayCommand.DefaultErrorHandler;
            AsyncRelayCommand.DefaultErrorHandler = captured.Add;
            try
            {
                await vm.SignInCommand.ExecuteAsync();
            }
            finally
            {
                AsyncRelayCommand.DefaultErrorHandler = previous;
            }

            Assert.Equal(string.Empty, string.Join(" | ", captured.Select(e => $"{e.GetType().Name}: {e.Message}")));
            Assert.NotEqual(LoginProblem.None, vm.Problem);
            Assert.False(string.IsNullOrEmpty(vm.Message));
            Assert.True(vm.SignInCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task RealClient_SuccessBodyWithUnknownCharset_IsABadResponse_NotAnException()
    {
        var (api, rig) = NewClient(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=no-such-charset");
            return response;
        });
        using (api)
        using (rig)
        {
            var result = await api.GetReadinessAsync();

            Assert.False(result.IsOk);
            Assert.Equal(ClientProblemCodes.BadResponse, result.Problem!.Code);
        }
    }
}
