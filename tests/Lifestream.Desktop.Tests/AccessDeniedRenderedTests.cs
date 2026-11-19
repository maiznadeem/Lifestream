using System.Windows.Controls;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Desktop.Shell;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-22 / SHELL-12: the real ShellView, rendered off-screen, with a Staff user opening an admin page.</summary>
[Collection("Wpf")]
public sealed class AccessDeniedRenderedTests
{
    [Fact]
    public void AccessDenied_BackToDashboardButton_IsBoundToACommand_AndNavigatesToTheDashboard()
    {
        var (hasCommand, titleAfter, deniedShown) = StaHost.Run(() =>
        {
            var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            rig.SignIn(Lifestream.Domain.Role.Staff);
            var shell = rig.Get<ShellViewModel>();
            using (rig)
            {
                var view = new ShellView { DataContext = shell };
                using var host = Hosted.Create(view, 1100, 700);
                Hosted.Await(() => shell.StartAsync());
                host.Settle(200);
                // A frame is drawn the moment the page is published, as it is whenever a page takes any time to load.
                shell.Navigation.CurrentChanged += (_, _) => view.UpdateLayout();
                var staff = NavigationCatalog.Default.First(d => d.Title == "Staff");
                Hosted.Await(() => shell.OpenAsync(staff));
                host.Settle(300);
                var shown = shell.PageTitle;
                host.Capture("AccessDenied.png");
                var button = host.All<Button>().First(b => b.Content as string == "Back to Dashboard");
                var has = button.Command is not null;
                if (has)
                {
                    button.Command!.Execute(null);
                    host.Settle(300);
                }

                return (has, shell.PageTitle, shown);
            }
        });

        Assert.Equal("Access denied", deniedShown);
        Assert.True(hasCommand, "the rendered 'Back to Dashboard' button has no Command");
        Assert.Equal("Dashboard", titleAfter);
    }
}
