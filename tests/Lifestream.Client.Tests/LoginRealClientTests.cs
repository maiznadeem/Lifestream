using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

/// <summary>SHELL-02: sign-in through the REAL LifestreamApiClient and WakeAwareHandler, with only the network stubbed.</summary>
public sealed class LoginRealClientTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"Internal error\"")]
    [InlineData("{\"code\":500}")]
    [InlineData("{\"code\":{\"x\":1},\"title\":7}")]
    [InlineData("<html>oops</html>")]
    [InlineData("")]
    public async Task Login_ServerError500_ThroughRealClient_ShowsInlineMessage_NotTheGlobalDialog(string body)
    {
        using var rig = new Rig();
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.InternalServerError, body));
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy(), new ServerStatus(), handler)) { BaseAddress = new Uri("https://api.test/") };
        using var api = new LifestreamApiClient(http, rig.Session);
        var vm = new LoginViewModel(api, rig.Session, rig.Get<IAppFlow>()) { Username = "u", Password = TestSecrets.P };
        var captured = new List<Exception>();
        using (AsyncRelayCommand.CaptureErrors(captured.Add))
        {
            await vm.SignInCommand.ExecuteAsync();
        }

        Assert.Empty(captured);
        Assert.True(vm.SignInCommand.CanExecute(null));
        Assert.Equal(LoginProblem.Other, vm.Problem);
        Assert.False(string.IsNullOrEmpty(vm.Message));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"code\":503}")]
    public async Task Login_Server503WithOddBody_ThroughRealClient_IsTreatedAsWaking_NotACrash(string body)
    {
        using var rig = new Rig();
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, body));
        var clock = new ManualTimeProvider();
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Budget = TimeSpan.Zero, Clock = clock }, new ServerStatus(), handler)) { BaseAddress = new Uri("https://api.test/") };
        using var api = new LifestreamApiClient(http, rig.Session);
        var vm = new LoginViewModel(api, rig.Session, rig.Get<IAppFlow>()) { Username = "u", Password = TestSecrets.P };
        var captured = new List<Exception>();
        using (AsyncRelayCommand.CaptureErrors(captured.Add))
        {
            await vm.SignInCommand.ExecuteAsync();
        }

        Assert.Empty(captured);
        Assert.Equal(LoginProblem.ServerWaking, vm.Problem);
        Assert.True(vm.CanRetry);
    }
}

/// <summary>SHELL-07: the cold start, on the login screen, through the real client; only the network and the clock are stand-ins.</summary>
public sealed class LoginColdStartTests
{
    /// <summary>A server that does not answer until the caller gives up.</summary>
    private sealed class SilentServer : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task Login_ColdStart_ShowsWakingStateAfterThreeSeconds_AndCancelStopsTheAttempt()
    {
        using var rig = new Rig();
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var server = new SilentServer();
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Clock = clock }, status, server)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var api = new LifestreamApiClient(http, rig.Session);
        var vm = new LoginViewModel(api, rig.Session, rig.Get<IAppFlow>(), status) { Username = "u", Password = TestSecrets.P };

        var signIn = vm.SignInCommand.ExecuteAsync();
        await Task.Delay(50);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(vm.IsBusy);
        Assert.False(vm.IsServerWaking);
        Assert.False(vm.CanCancel);

        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.True(vm.IsServerWaking, "after 3 seconds the login screen must say the server is waking up");
        Assert.Contains("waking up the server", vm.WakingText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a minute", vm.WakingText, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.CanCancel);
        Assert.True(vm.CancelCommand.CanExecute(null));

        await vm.CancelCommand.ExecuteAsync();
        await signIn.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsServerWaking);
        Assert.True(vm.SignInCommand.CanExecute(null));
        Assert.Equal("Sign-in cancelled.", vm.Message);
    }
}
