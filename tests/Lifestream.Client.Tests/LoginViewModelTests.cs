using System.Diagnostics;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;

namespace Lifestream.Client.Tests;

[Collection("GlobalErrorHandler")]
public sealed class LoginViewModelTests
{
    private static LoginViewModel Create(Rig rig) => rig.Get<LoginViewModel>();

    [Fact]
    public void LoginViewModel_StartsEmpty_SignInDisabled()
    {
        using var rig = new Rig();
        var vm = Create(rig);

        Assert.Equal(string.Empty, vm.Username);
        Assert.Equal(string.Empty, vm.Password);
        Assert.False(vm.CanSignIn);
        Assert.False(vm.SignInCommand.CanExecute(null));
        Assert.Null(vm.Message);
    }

    [Theory]
    [InlineData("user", "", false)]
    [InlineData("", "secret", false)]
    [InlineData("   ", "secret", false)]
    [InlineData("user", "secret", true)]
    public void LoginViewModel_SignInEnabledOnlyWhenBothFieldsHaveValues(string user, string password, bool expected)
    {
        using var rig = new Rig();
        var vm = Create(rig);

        vm.Username = user;
        vm.Password = password;

        Assert.Equal(expected, vm.CanSignIn);
        Assert.Equal(expected, vm.SignInCommand.CanExecute(null));
    }

    [Fact]
    public void LoginViewModel_ContainsNoPrefilledOrPrintedCredentials()
    {
        // The strings a view model could print: every constant string in the Auth view model's compiled members.
        var literals = typeof(LoginViewModel).Assembly.GetTypes()
            .Where(t => t.Namespace == "Lifestream.Client.Auth")
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            .Where(f => f.FieldType == typeof(string) && f.IsLiteral)
            .Select(f => (string?)f.GetRawConstantValue())
            .ToList();
        Assert.DoesNotContain(literals, l => l is not null && (l.Contains("password", StringComparison.OrdinalIgnoreCase) || l.Contains("admin", StringComparison.OrdinalIgnoreCase)));

        using var rig = new Rig();
        var vm = Create(rig);
        Assert.Empty(vm.Username);
        Assert.Empty(vm.Password);
    }

    [Fact]
    public async Task LoginViewModel_NetworkError_ShowsInline_ReEnables()
    {
        using var rig = new Rig();
        rig.Api.On("LoginAsync", FakeApiClient.Unreachable<TokenResponse>());
        var vm = Create(rig);
        vm.Username = "nadeem";
        vm.Password = TestSecrets.Pw;

        await vm.SignInCommand.ExecuteAsync();

        Assert.Equal(LoginProblem.ServerUnreachable, vm.Problem);
        Assert.Contains("Can't reach the server", vm.Message);
        Assert.True(vm.CanRetry);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanSignIn);
        Assert.True(vm.SignInCommand.CanExecute(null));
        Assert.Null(vm.SignInCommand.LastException);
        Assert.DoesNotContain("ShowMain", rig.Windows.Events); // never signed in
    }

    [Fact]
    public async Task LoginViewModel_ApiThrowsNetworkException_ShowsInline_NoCrash()
    {
        using var rig = new Rig();
        rig.Api.Handlers["LoginAsync"] = _ => throw new HttpRequestException("boom");
        var vm = Create(rig);
        vm.Username = "u";
        vm.Password = TestSecrets.P;

        await vm.SignInCommand.ExecuteAsync();

        Assert.Equal(LoginProblem.ServerUnreachable, vm.Problem);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanSignIn);
    }

    [Fact]
    public async Task LoginViewModel_UnexpectedException_IsCaptured_ButNotThrown_AndButtonReEnables()
    {
        using var rig = new Rig();
        rig.Api.Handlers["LoginAsync"] = _ => throw new InvalidOperationException("bug");
        var captured = new List<Exception>();
        var vm = Create(rig);
        vm.Username = "u";
        vm.Password = TestSecrets.P;
        var previous = Lifestream.Client.Common.AsyncRelayCommand.DefaultErrorHandler;
        Lifestream.Client.Common.AsyncRelayCommand.DefaultErrorHandler = captured.Add;
        try
        {
            await vm.SignInCommand.ExecuteAsync();
        }
        finally
        {
            Lifestream.Client.Common.AsyncRelayCommand.DefaultErrorHandler = previous;
        }

        Assert.Single(captured);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanSignIn);
    }

    [Fact]
    public async Task LoginViewModel_ThreeFailureKinds_HaveDistinctMessages_AndOnlyTwoOfferRetry()
    {
        var results = new Dictionary<string, (string? Message, bool Retry)>();
        foreach (var (name, answer) in new (string, ApiResult<TokenResponse>)[]
        {
            ("wrong", FakeApiClient.Fail<TokenResponse>(401, "unauthorized")),
            ("unreachable", FakeApiClient.Unreachable<TokenResponse>()),
            ("waking", FakeApiClient.Fail<TokenResponse>(503, ProblemCodes.DatabaseWaking)),
        })
        {
            using var rig = new Rig();
            rig.Api.On("LoginAsync", answer);
            var vm = Create(rig);
            vm.Username = "u";
            vm.Password = TestSecrets.P;
            await vm.SignInCommand.ExecuteAsync();
            results[name] = (vm.Message, vm.CanRetry);
        }

        Assert.Equal(3, results.Values.Select(v => v.Message).Distinct().Count());
        Assert.False(results["wrong"].Retry);
        Assert.True(results["unreachable"].Retry);
        Assert.True(results["waking"].Retry);
        Assert.Contains("waking up", results["waking"].Message);
    }

    [Fact]
    public async Task LoginViewModel_Retry_RunsTheSameAttemptAgain()
    {
        using var rig = new Rig();
        var attempt = 0;
        rig.Api.On<TokenResponse>("LoginAsync", _ => Task.FromResult(++attempt == 1
            ? FakeApiClient.Fail<TokenResponse>(503, ProblemCodes.DatabaseWaking)
            : ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens())));
        var vm = Create(rig);
        vm.Username = "u";
        vm.Password = TestSecrets.P;

        await vm.SignInCommand.ExecuteAsync();
        Assert.True(vm.RetryCommand.CanExecute(null));
        await vm.RetryCommand.ExecuteAsync();

        Assert.Equal(2, rig.Api.CountOf("LoginAsync"));
        Assert.True(rig.Session.IsAuthenticated);
        Assert.Contains("ShowMain", rig.Windows.Events);
    }

    [Fact]
    public async Task LoginViewModel_Success_EstablishesSession_ClearsPassword_OpensMainWindow()
    {
        using var rig = new Rig();
        rig.Api.On("LoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(Lifestream.Domain.Role.Admin, name: "Ada Admin"))));
        rig.Windows.Events.Clear();
        var flow = rig.Get<Lifestream.Client.Navigation.IAppFlow>();
        flow.Start();
        var vm = rig.Windows.Login!;
        vm.Username = "ada";
        vm.Password = TestSecrets.Pw;

        await vm.SignInCommand.ExecuteAsync();

        Assert.Equal("Ada Admin", rig.Session.User!.DisplayName);
        Assert.Equal(["ShowLogin", "ShowMain", "CloseLogin"], rig.Windows.Events);
        Assert.Empty(vm.Password);
    }

    [Fact]
    public async Task LoginViewModel_TryTheDemo_SignsIntoTheDemoAccount()
    {
        using var rig = new Rig();
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true, name: "Demo Visitor"))));
        var vm = Create(rig);

        Assert.True(vm.DemoCommand.CanExecute(null));
        Assert.Equal("Try the demo", vm.DemoText);
        await vm.DemoCommand.ExecuteAsync();

        Assert.Equal(1, rig.Api.CountOf("DemoLoginAsync"));
        Assert.Equal(0, rig.Api.CountOf("LoginAsync"));
        Assert.True(rig.Session.User!.IsDemo);
    }

    [Fact]
    public async Task LoginViewModel_BusyStateShowsWithin100ms_WithSigningInText_AndReadOnlyFields()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource<ApiResult<TokenResponse>>();
        rig.Api.On<TokenResponse>("LoginAsync", _ => gate.Task);
        var vm = Create(rig);
        vm.Username = "u";
        vm.Password = TestSecrets.P;

        var clock = Stopwatch.StartNew();
        var running = vm.SignInCommand.ExecuteAsync(); // returns at the first await, i.e. when the request is in flight
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 100, $"busy state took {clock.ElapsedMilliseconds} ms");
        Assert.True(vm.IsBusy);
        Assert.Equal("Signing in...", vm.SignInText);
        Assert.True(vm.AreFieldsReadOnly);
        Assert.False(vm.SignInCommand.CanExecute(null));
        Assert.False(vm.DemoCommand.CanExecute(null));

        gate.SetResult(FakeApiClient.Fail<TokenResponse>(401, "unauthorized"));
        await running;
        Assert.False(vm.IsBusy);
        Assert.Equal("Sign in", vm.SignInText);
        Assert.False(vm.AreFieldsReadOnly);
    }

    [Fact]
    public async Task LoginViewModel_SecondClickWhileBusy_IsIgnored()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource<ApiResult<TokenResponse>>();
        rig.Api.On<TokenResponse>("LoginAsync", _ => gate.Task);
        var vm = Create(rig);
        vm.Username = "u";
        vm.Password = TestSecrets.P;

        var first = vm.SignInCommand.ExecuteAsync();
        await vm.SignInCommand.ExecuteAsync();
        gate.SetResult(FakeApiClient.Fail<TokenResponse>(401, "unauthorized"));
        await first;

        Assert.Equal(1, rig.Api.CountOf("LoginAsync"));
    }

    [Fact]
    public void LoginViewModel_HasNoForgotPassword_AndNoNotImplementedPaths()
    {
        var members = typeof(LoginViewModel).GetMembers().Select(m => m.Name);
        Assert.DoesNotContain(members, n => n.Contains("Forgot", StringComparison.OrdinalIgnoreCase));

        var source = File.ReadAllText(RepoPaths.Find("src/Lifestream.Client/Auth/LoginViewModel.cs"));
        Assert.DoesNotContain("NotImplementedException", source, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Find("src/Lifestream.Client"), "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("NotImplementedException", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LoginViewModel_DemoResting_ShowsRestingMessage_WithoutRetry()
    {
        using var rig = new Rig();
        rig.Api.On("DemoLoginAsync", FakeApiClient.Fail<TokenResponse>(503, ProblemCodes.DatabaseResting));
        var vm = Create(rig);

        await vm.DemoCommand.ExecuteAsync();

        Assert.Equal(LoginProblem.DemoResting, vm.Problem);
        Assert.False(vm.CanRetry);
    }
}
