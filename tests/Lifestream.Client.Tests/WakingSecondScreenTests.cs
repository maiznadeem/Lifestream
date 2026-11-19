using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

/// <summary>SHELL-07: a screen that starts loading while ANOTHER call is already waking shows the waking state and gets the long budget, not the 10 s "taking longer" error. Real client and handler, manual clock, stubbed network.</summary>
public sealed class WakingSecondScreenTests
{
    private sealed class SilentServer : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class ReadyzViewModel(ILifestreamApiClient api) : ViewModelBase
    {
        protected override async Task FetchAsync(CancellationToken ct) => (await api.GetReadinessAsync(ct).ConfigureAwait(false)).Unwrap();
    }

    private static async Task Pump(ManualTimeProvider clock, TimeSpan by)
    {
        await Task.Delay(100);
        clock.Advance(by);
        await Task.Delay(100);
    }

    [Fact]
    public async Task SecondScreen_WhileAnotherCallIsAlreadyWaking_ShowsWaking_NotATimeoutError_RealClient()
    {
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        using var rig = new Rig();
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Clock = clock }, status, new SilentServer())) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var api = new LifestreamApiClient(http, rig.Session);

        using var first = new ReadyzViewModel(api);
        first.Attach(status, clock);
        _ = first.LoadAsync();
        await Pump(clock, TimeSpan.FromSeconds(3.5));
        Assert.Equal(LoadState.Waking, first.State); // control: the first screen shows waking

        using var second = new ReadyzViewModel(api);
        second.Attach(status, clock);
        _ = second.LoadAsync();
        Assert.Equal(LoadState.Loading, second.State); // no banner flash before the 3 s mark
        await Pump(clock, TimeSpan.FromSeconds(3.5));
        var after3s = second.State;
        await Pump(clock, TimeSpan.FromSeconds(7));
        var after10s = second.State;
        var message = second.ErrorMessage;
        first.CancelLoad();
        second.CancelLoad();

        Assert.True(after3s == LoadState.Waking && after10s == LoadState.Waking, $"second screen while the server is waking: after 3.5 s {after3s}, after 10.5 s {after10s} ('{message}')");
    }

    [Fact]
    public async Task CancelLoadCommand_StopsAWakingLoad_AndOffersRetry()
    {
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        using var rig = new Rig();
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Clock = clock }, status, new SilentServer())) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var api = new LifestreamApiClient(http, rig.Session);
        using var vm = new ReadyzViewModel(api);
        vm.Attach(status, clock);
        var load = vm.LoadAsync();
        await Pump(clock, TimeSpan.FromSeconds(3.5));
        Assert.Equal(LoadState.Waking, vm.State);

        vm.CancelLoadCommand.Execute(null);
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoadState.Error, vm.State);
    }
}
