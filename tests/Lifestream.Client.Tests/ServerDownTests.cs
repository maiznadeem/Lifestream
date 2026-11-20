using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Client.Tests.Features;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;

namespace Lifestream.Client.Tests;

/// <summary>
/// Real-app walk, defect 27: with the server down the app showed two "Waking up the server" notices at once, for two minutes, although the
/// connection was refused at once. A server that cannot be reached is not waking: it is retried quietly for a few seconds and then named
/// ("Can't reach the server"), and a screen shows one waking notice, never two.
/// </summary>
public sealed class ServerDownTests
{
    private static HttpRequestException Refused() =>
        new(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it.");

    private static async Task<T> Drive<T>(Task<T> task, ManualTimeProvider clock, int maxSeconds = 400)
    {
        for (var i = 0; i < maxSeconds * 4 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(2);
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2500 && !condition(); i++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "the condition was not reached in 5 s");
    }

    [Fact]
    public async Task RefusedConnection_IsNeverCalledWaking_AndIsReportedAsUnreachableWithinTenSeconds()
    {
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var handler = new ScriptedHandler((_, _) => throw Refused());
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Clock = clock }, status, handler)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var api = new LifestreamApiClient(http, new SessionService());
        var states = new List<ServerState>();
        status.Changed += (_, _) => states.Add(status.State);

        var result = await Drive(api.GetDonorAsync(7), clock);

        Assert.True(result.Problem!.IsUnreachable, result.Problem.Code);
        Assert.Empty(states); // released 2.1.2: Waking from the third second, for two minutes
        Assert.InRange(clock.Elapsed.TotalSeconds, 3, 10); // released 2.1.2: about 120 s
        Assert.True(handler.Requests.Count > 3, "a connection failure is still tried again a few times: the network may be back in a moment");
    }

    [Fact]
    public async Task ServerThatWasWaking_AndThenRefusesConnections_LeavesTheWakingState_AndIsReportedAsUnreachable()
    {
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var down = false;
        var handler = new ScriptedHandler((_, _) => down ? throw Refused() : ScriptedHandler.Problem(HttpStatusCode.ServiceUnavailable, ProblemCodes.DatabaseWaking));
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Clock = clock }, status, handler)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var api = new LifestreamApiClient(http, new SessionService());

        var call = api.GetDonorAsync(7);
        for (var i = 0; i < 32 && status.State != ServerState.Waking; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(2);
        }

        Assert.Equal(ServerState.Waking, status.State); // 503 database_waking for more than 3 s: that is a waking server
        var wentDownAt = clock.Elapsed;
        down = true;
        var result = await Drive(call, clock);

        Assert.True(result.Problem!.IsUnreachable, result.Problem.Code);
        Assert.Equal(ServerState.Online, status.State);
        Assert.InRange((clock.Elapsed - wentDownAt).TotalSeconds, 0, 25); // not the rest of the 120 s
    }

    [Fact]
    public async Task SignIn_WithTheServerDown_NeverSaysWaking_AndSaysItCannotReachTheServer()
    {
        using var rig = new Rig();
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var handler = new ScriptedHandler((_, _) => throw Refused());
        var policy = new WakePolicy { Clock = clock };
        using var http = new HttpClient(new WakeAwareHandler(policy, status, handler)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var inner = new LifestreamApiClient(http, rig.Session);
        var vm = new LoginViewModel(ScopedApiClient.Wrap(inner, policy, strict: true), rig.Session, rig.Get<IAppFlow>(), status) { Username = "u", Password = TestSecrets.P };
        var pill = new List<string>();
        var waking = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoginViewModel.ServerText))
            {
                pill.Add(vm.ServerText);
            }

            waking |= vm.IsServerWaking;
        };

        var signIn = vm.SignInCommand.ExecuteAsync();
        for (var i = 0; i < 1600 && !signIn.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(2);
        }

        await signIn.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoginProblem.ServerUnreachable, vm.Problem);
        Assert.Equal("Can't reach the server. Check your internet connection, then retry.", vm.Message);
        Assert.False(waking, "the sign-in screen said the server was waking while the connection was refused");
        Assert.DoesNotContain("Waking the server", pill);
        Assert.Equal("Can't reach the server", vm.ServerText);
        Assert.InRange(clock.Elapsed.TotalSeconds, 3, 10);
    }

    [Fact]
    public async Task ListScreen_WhoseLoadWaitsOnAWakingServer_ShowsTheLoadSurfacesNotice_AndNotTheOperationNoticeToo()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetDonorsAsync", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(1));
        });
        var status = new ServerStatus();
        var vm = rig.Donors();
        vm.Attach(status, rig.Clock);

        var load = vm.LoadAsync();
        await UntilAsync(() => rig.Fake.CountOf("GetDonorsAsync") == 1);
        status.Enter(); // what the retry handler does once a call has been slow for 3 s
        rig.Clock.Advance(TimeSpan.FromSeconds(3.5));

        Assert.Equal(LoadState.Waking, vm.State); // the load surface shows "Waking up the server" with Retry and Cancel
        Assert.True(vm.Operations.IsWaking);
        Assert.False(vm.IsOperationWaking); // released 2.1.2: true, so the screen's own notice showed as well
        Assert.False(vm.ShowWakingNotice);

        vm.CancelLoad();
        status.Leave();
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.ShowWakingNotice);
    }
}
