using Lifestream.Client.Navigation;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

/// <summary>The sidebar highlight follows the page on screen, whoever navigated there (a dashboard card, the account menu, a sidebar click).</summary>
public sealed class SidebarHighlightTests
{
    private static ShellViewModel Shell(Rig rig) => rig.Get<ShellViewModel>();

    [Fact]
    public async Task NavigatingFromADashboardCard_MovesTheSidebarHighlight()
    {
        using var rig = new Rig();
        rig.SignIn(Lifestream.Domain.Role.Admin);
        using var shell = Shell(rig);
        await shell.StartAsync();
        var dashboard = shell.Items.First(i => i.Title == "Dashboard");
        var donors = shell.Items.First(i => i.Title == "Donors");
        Assert.True(dashboard.IsSelected);

        // What a dashboard card does: it goes through the navigation service, not through the shell.
        await rig.Get<INavigationService>().NavigateTo(NavigationCatalog.Default.First(d => d.Title == "Donors").ViewModelType, "prefilter");

        Assert.True(donors.IsSelected, "the Donors item must be highlighted after a card opened the Donors page");
        Assert.False(dashboard.IsSelected);
        Assert.Equal(1, shell.Items.Count(i => i.IsSelected));
    }

    [Fact]
    public async Task OpeningTheProfile_ClearsTheHighlight_AndComingBackRestoresIt()
    {
        using var rig = new Rig();
        rig.SignIn(Lifestream.Domain.Role.Admin);
        using var shell = Shell(rig);
        await shell.StartAsync();

        await shell.ProfileCommand.ExecuteAsync();
        Assert.IsType<Lifestream.Client.Features.Profile.ProfileViewModel>(shell.CurrentPage); // the real profile screen replaced the placeholder
        Assert.DoesNotContain(shell.Items, i => i.IsSelected);

        await shell.BackToDashboardCommand.ExecuteAsync();
        Assert.True(shell.Items.First(i => i.Title == "Dashboard").IsSelected);
    }
}
