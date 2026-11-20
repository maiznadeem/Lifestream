using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Navigation;
using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

/// <summary>
/// Real-app walk, defect 2: one sign-in that took longer than 3 s turned the sign-in screen's footer pill to "Waking the server", and it stayed
/// there for good although the server had answered. Through the real client and retry handler; only the network and the clock are stand-ins.
/// </summary>
public sealed class LoginServerPillTests
{
    /// <summary>Answers the readiness check at once; holds a sign-in until the test releases it with an answer.</summary>
    private sealed class SlowSignInServer : HttpMessageHandler
    {
        public TaskCompletionSource<HttpResponseMessage> SignIn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SignInsArrived;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("readyz", StringComparison.Ordinal))
            {
                return ScriptedHandler.Json(HttpStatusCode.OK, """{"database":"ready"}""");
            }

            Interlocked.Increment(ref SignInsArrived);
            return await SignIn.Task.WaitAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task FooterPill_AfterASlowSignInThatTheServerAnswered_SaysServerReadyAgain_NotWakingForGood()
    {
        using var rig = new Rig();
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var server = new SlowSignInServer();
        var policy = new WakePolicy { Clock = clock };
        using var http = new HttpClient(new WakeAwareHandler(policy, status, server)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var inner = new LifestreamApiClient(http, rig.Session);
        var vm = new LoginViewModel(ScopedApiClient.Wrap(inner, policy, strict: true), rig.Session, rig.Get<IAppFlow>(), status) { Username = "u", Password = TestSecrets.P };
        await vm.CheckServerAsync();
        Assert.Equal("Server ready", vm.ServerText);

        var signIn = vm.SignInCommand.ExecuteAsync();
        for (var i = 0; i < 2500 && Volatile.Read(ref server.SignInsArrived) == 0; i++)
        {
            await Task.Delay(2);
        }

        clock.Advance(TimeSpan.FromSeconds(3.5));
        Assert.Equal("Waking the server", vm.ServerText); // a call slower than 3 s: the pill says so while it lasts

        server.SignIn.SetResult(ScriptedHandler.Problem(HttpStatusCode.Unauthorized, "invalid_credentials"));
        await signIn.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoginProblem.WrongCredentials, vm.Problem);
        Assert.False(vm.IsServerWaking);
        Assert.Equal(ServerReadiness.Ready, vm.Server); // released 2.1.0 and 2.1.1: Waking, until the app was restarted
        Assert.Equal("Server ready", vm.ServerText);
    }
}
