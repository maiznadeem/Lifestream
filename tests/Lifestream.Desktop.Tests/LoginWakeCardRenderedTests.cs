using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Health;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// 2.1.9: the sign-in window's wait card, rendered off-screen through a whole cold wake. Fake API, real LoginViewModel, real ServerStatus,
/// real LoginView: each step shows its own illustration (and only that one), its title and "Step n of 3"; the illustrations loop only
/// while Windows animation effects are on.
/// </summary>
[Collection("Wpf")]
public sealed class LoginWakeCardRenderedTests
{
    private sealed record StepSeen(bool CardVisible, string Title, string StepOf, string[] ArtsShown, bool Animating, bool DemoInView, string Capture);

    private static StepSeen[] Walk(bool animations) => StaHost.Run(() =>
    {
        using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
        var ready = new TaskCompletionSource<ApiResult<ReadinessResponse>>();
        rig.Api.On<ReadinessResponse>("GetReadinessAsync", _ => ready.Task);
        rig.Api.On<TokenResponse>("DemoLoginAsync", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        });
        var vm = rig.Get<LoginViewModel>();
        var status = rig.Get<ServerStatus>();
        // The form side of a 1240 x 800 window (the size the owner uses): 560 wide, 756 tall under the 44 DIP title bar.
        using var host = Hosted.Create(new LoginView { DataContext = vm }, 560, 756, animations: animations);
        host.Settle(150);
        var art = host.Find<WakeStepArt>("WakeArt");
        var suffix = animations ? "Animated" : "Still";

        StepSeen Look(string name)
        {
            host.Settle(250);
            host.Capture($"LoginWindow_WakeCard_{name}_{suffix}.png");
            var card = host.Find<FrameworkElement>("WakingBanner");
            string Text(string element) => host.Find<TextBlock>(element).Text;
            var shown = new[] { "ServerArt", "DatabaseArt", "SignInArt" }.Where(n => host.Find<FrameworkElement>(n).IsVisible).ToArray();
            var demo = host.Find<Button>("DemoButton");
            var demoBottom = demo.TranslatePoint(new Point(0, demo.ActualHeight), host.Element).Y;
            return new StepSeen(card.IsVisible, Text("WakeTitle"), Text("WakeStepOf"), shown, art.IsAnimating, demoBottom <= host.Element.ActualHeight, name);
        }

        _ = vm.CheckServerAsync();
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(host.Find<Button>("DemoButton"))!).Invoke();
        status.Enter(); // what the HTTP layer does from the third second of a slow call
        var step1 = Look("Step1");

        status.Heard(TimeProvider.System, databaseWaking: true); // the server answered: its database is waking
        var step2 = Look("Step2");

        status.Leave();
        ready.SetResult(ApiResult<ReadinessResponse>.Ok(new ReadinessResponse(DatabaseStates.Ready)));
        Hosted.PumpUntil(() => vm.Step == WakeStep.SigningIn, 2000);
        var step3 = Look("Step3");

        vm.CancelCommand.Execute(null);
        Hosted.PumpUntil(() => !vm.IsBusy, 2000);
        return new[] { step1, step2, step3 };
    });

    [Fact]
    public void Card_EachStep_ShowsItsOwnArt_TitleAndStepNumber_AndAnimatesWhenAnimationsAreOn()
    {
        var steps = Walk(animations: true);

        Assert.All(steps, s => Assert.True(s.CardVisible, s.Capture));
        Assert.Equal(["ServerArt"], steps[0].ArtsShown);
        Assert.Equal(["DatabaseArt"], steps[1].ArtsShown);
        Assert.Equal(["SignInArt"], steps[2].ArtsShown);
        Assert.Equal(["Step 1 of 3", "Step 2 of 3", "Step 3 of 3"], steps.Select(s => s.StepOf));
        Assert.Equal(["Getting the server ready", "Getting the server ready", "Signing you in"], steps.Select(s => s.Title));
        Assert.All(steps, s => Assert.True(s.Animating, s.Capture));
        Assert.All(steps, s => Assert.True(s.DemoInView, $"{s.Capture}: Try the demo is pushed out of the window by the card"));
    }

    [Fact]
    public void Card_WithAnimationEffectsOff_StandsStill()
    {
        var steps = Walk(animations: false);

        Assert.Equal(["ServerArt"], steps[0].ArtsShown);
        Assert.All(steps, s => Assert.False(s.Animating, s.Capture));
    }
}
