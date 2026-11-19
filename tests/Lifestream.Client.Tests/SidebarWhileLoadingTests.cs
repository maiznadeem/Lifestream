using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Shell;
using Lifestream.Client.Shell.Placeholders;
using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

/// <summary>SHELL-07: the sidebar stays usable while a screen loads, and leaving a loading screen cancels its load.</summary>
public sealed class SidebarWhileLoadingTests
{
    public sealed class SlowViewModel : ViewModelBase
    {
        public static TaskCompletionSource<CancellationToken> Started { get; set; } = new();

        public override string PageTitle => "Slow";

        protected override async Task FetchAsync(CancellationToken ct)
        {
            Started.TrySetResult(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private static ShellViewModel Shell(Rig rig) =>
        new(
            rig.Get<INavigationService>(),
            rig.Session,
            rig.Get<IAppFlow>(),
            rig.Get<IToastService>(),
            rig.Get<Lifestream.Client.Api.IServerStatus>(),
            rig.Get<Lifestream.Client.Updates.UpdateService>(),
            [
                new Destination("Slow", "G", "x", typeof(SlowViewModel)),
                new Destination("Donors", "G", "x", typeof(DonorsPlaceholderViewModel)),
            ]);

    [Fact]
    public async Task Sidebar_StaysUsable_WhileAScreenLoads_AndLeavingCancelsThatLoad()
    {
        SlowViewModel.Started = new TaskCompletionSource<CancellationToken>();
        using var rig = new Rig();
        rig.SignIn(Lifestream.Domain.Role.Admin);
        using var shell = Shell(rig);
        var slow = shell.Items.First(i => i.Title == "Slow");
        var donors = shell.Items.First(i => i.Title == "Donors");

        var opening = shell.NavigateCommand.ExecuteAsync(slow);
        var loadToken = await SlowViewModel.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<SlowViewModel>(shell.CurrentPage);
        Assert.True(shell.CurrentPage!.IsLoading);

        Assert.True(shell.NavigateCommand.CanExecute(donors), "every sidebar item is disabled until the current screen finishes loading");
        await shell.NavigateCommand.ExecuteAsync(donors);

        Assert.True(loadToken.IsCancellationRequested, "navigating away must cancel the load of the screen being left");
        Assert.IsType<DonorsPlaceholderViewModel>(shell.CurrentPage);
        await opening.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<DonorsPlaceholderViewModel>(shell.CurrentPage);
        Assert.False(slow.IsSelected);
        Assert.True(donors.IsSelected, "the sidebar highlight must follow the latest navigation, not the one that finished last");
    }
}
