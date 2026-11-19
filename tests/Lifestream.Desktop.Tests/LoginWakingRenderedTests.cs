using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;
using Lifestream.Desktop.Auth;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-07: the login screen during a cold start. Fake API, real LoginViewModel, real ServerStatus, real LoginView rendered off-screen.</summary>
[Collection("Wpf")]
public sealed class LoginWakingRenderedTests
{
    [Fact]
    public void Login_ColdStart_FakeApi_ShowsWakingBannerWithCancel_AndCancelEndsTheAttempt()
    {
        var (bannerBefore, bannerShown, cancelShown, bannerText, afterCancel) = StaHost.Run(() =>
        {
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            rig.Api.On<TokenResponse>("LoginAsync", async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            });
            var vm = rig.Get<LoginViewModel>();
            using var host = Hosted.Create(new LoginView { DataContext = vm }, 520, 688);
            host.Settle(150);
            vm.Username = "u";
            vm.Password = TestSecrets.P;
            host.Settle(100);
            var banner = host.Find<FrameworkElement>("WakingBanner");
            var cancel = host.Find<Button>("CancelButton");
            var before = banner.IsVisible;

            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(host.Find<Button>("SignInButton"))!).Invoke(); // a click, as the user does it
            host.Settle(100);
            rig.Get<ServerStatus>().Enter(); // what the HTTP layer does from the third second of a slow call
            host.Settle(250);
            host.Capture("LoginWindow_Waking.png");
            var shown = banner.IsVisible;
            var cancelVisible = cancel.IsVisible;
            var text = host.All<TextBlock>().First(t => t.IsVisible && t.Text.Contains("Waking up the server", StringComparison.Ordinal)).Text;

            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(cancel)!).Invoke();
            Hosted.PumpUntil(() => !vm.IsBusy, 2000);
            host.Settle(150);
            return (before, shown, cancelVisible, text, (vm.IsBusy, vm.SignInCommand.CanExecute(null), host.Find<Button>("SignInButton").IsEnabled));
        });

        Assert.False(bannerBefore);
        Assert.True(bannerShown, "the waking banner must be visible");
        Assert.True(cancelShown, "Cancel must be visible while the sign-in waits on a waking server");
        Assert.Contains("this can take up to a minute", bannerText, StringComparison.Ordinal);
        Assert.False(afterCancel.IsBusy);
        Assert.True(afterCancel.Item3, $"Sign in is usable again after Cancel: canExecute={afterCancel.Item2}");
    }
}
