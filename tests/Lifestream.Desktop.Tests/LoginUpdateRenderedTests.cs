using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Lifestream.Client.Auth;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Client.Updates;
using Lifestream.Desktop.Auth;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-44: the login window shows the "update ready" prompt (fake release feed, real view model and view).</summary>
[Collection("Wpf")]
public sealed class LoginUpdateRenderedTests
{
    [Fact]
    public void LoginWindow_ShowsTheUpdateReadyPrompt_WithARestartButton_BeforeAnySignIn()
    {
        var (before, promptText, restartVisible, applied, signedIn) = StaHost.Run(() =>
        {
            using var rig = new Rig(latestVersion: new Version(2, 1, 0), currentVersion: new Version(2, 0, 0), dispatcher: WpfTestUi.Dispatcher());
            var vm = rig.Get<LoginViewModel>();
            using var host = Hosted.Create(new LoginView { DataContext = vm }, 520, 688);
            host.Settle(150);
            var noPrompt = host.All<TextBlock>().Any(t => t.IsVisible && t.Text.Contains("is ready", StringComparison.Ordinal));

            Hosted.Await(() => rig.Get<UpdateService>().CheckAsync()); // what AppFlow.Start does at startup
            Hosted.PumpUntil(() => vm.UpdatePrompt is not null, 2000);
            host.Settle(250);
            host.Capture("LoginWindow_UpdateReady.png");
            var text = host.All<TextBlock>().FirstOrDefault(t => t.IsVisible && t.Text.Contains("is ready", StringComparison.Ordinal))?.Text;
            var restart = host.All<Button>().FirstOrDefault(b => b.IsVisible && b.Content as string == "Restart and update");
            if (restart is not null)
            {
                ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(restart)!).Invoke();
                Hosted.PumpUntil(() => rig.Updates.Applied > 0, 2000);
            }

            return (noPrompt, text, restart is not null, rig.Updates.Applied, rig.Session.IsAuthenticated);
        });

        Assert.False(before, "no prompt before the check has found an update");
        Assert.Contains("2.1.0", promptText);
        Assert.True(restartVisible, "the login window must offer 'Restart and update'");
        Assert.Equal(1, applied);
        Assert.False(signedIn);
    }
}
