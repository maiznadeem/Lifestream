using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Lifestream.Desktop.Shell;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// The account menu closes when one of its items runs, however it was run. Night-iso's isolated real-app walk, confirmed on the isolated desktop
/// (run/agents/night-minors.md): "Sign out" invoked through UI Automation (a screen reader's way) left the menu's popup on screen over the sign-in
/// window and over the next session's main window. The window is never shown.
/// </summary>
[Collection("Wpf")]
public sealed class ShellAccountMenuTests
{
    [Fact]
    public void AccountMenu_SignOutInvokedThroughUiAutomation_SignsOut_AndClosesTheMenu()
    {
        var f = StaHost.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            using var rig = Part2Harness.NewRig();
            // Signed in through the app's flow (only a shell the flow opened can be signed out of): the demo, as on the sign-in screen.
            rig.Api.On("DemoLoginAsync", Lifestream.Client.Api.ApiResult<Lifestream.Contracts.Auth.TokenResponse>.Ok(Lifestream.Client.Tests.Fakes.FakeApiClient.Tokens(Lifestream.Client.Tests.Fakes.FakeApiClient.User(demo: true))));
            rig.Get<Lifestream.Client.Navigation.IAppFlow>().Start();
            Hosted.Await(() => rig.Windows.Login!.DemoCommand.ExecuteAsync());
            var shell = rig.Windows.Main!;
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(view, 1440, 900);
            host.Settle(300);
            var menu = (ContextMenu)view.Resources["AccountMenu"];
            ((Button)view.FindName("AvatarButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            host.Settle(300);
            var opened = menu.IsOpen;
            var signOut = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Sign out"));
            var peer = UIElementAutomationPeer.CreatePeerForElement(signOut);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            var signedOut = Hosted.PumpUntil(() => rig.Windows.Events.Contains("CloseMain"), 5000);
            host.Settle(100);
            return (Opened: opened, StillOpen: menu.IsOpen, SignedOut: signedOut);
        });

        Assert.True(f.Opened, "set-up: the account menu did not open");
        Assert.True(f.SignedOut, "set-up: Sign out did not run");
        Assert.False(f.StillOpen, "the account menu stayed open after Sign out"); // night-iso: its popup stayed over the next windows
    }
}
