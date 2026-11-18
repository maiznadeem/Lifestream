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
        var vm = new LoginViewModel(api, rig.Session, rig.Get<IAppFlow>()) { Username = "u", Password = "p" };
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
        var vm = new LoginViewModel(api, rig.Session, rig.Get<IAppFlow>()) { Username = "u", Password = "p" };
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

        Assert.Empty(captured);
        Assert.Equal(LoginProblem.ServerWaking, vm.Problem);
        Assert.True(vm.CanRetry);
    }
}
