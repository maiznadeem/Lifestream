using Lifestream.Client.Updates;
using Lifestream.Client.Navigation;
using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

/// <summary>SHELL-44: the release feed is checked when the app starts, before anyone signs in, and the login screen shows the prompt.</summary>
public sealed class StartupUpdateTests
{
    [Fact]
    public async Task AppStart_ChecksTheFeedBeforeSignIn_AndTheLoginViewModelOffersTheUpdate()
    {
        using var rig = new Rig(latestVersion: new Version(2, 1, 0), currentVersion: new Version(2, 0, 0));

        rig.Get<IAppFlow>().Start();

        var login = rig.Windows.Login!;
        var service = rig.Get<UpdateService>();
        for (var i = 0; i < 100 && !service.IsUpdateReady; i++)
        {
            await Task.Delay(20);
        }

        Assert.False(rig.Session.IsAuthenticated, "nobody has signed in");
        Assert.Equal(1, rig.Updates.Checks);
        Assert.Contains("2.1.0", login.UpdatePrompt);

        await login.ApplyUpdateCommand.ExecuteAsync();
        Assert.Equal(1, rig.Updates.Applied);
    }

    [Fact]
    public async Task AppStart_WithNoNewerVersion_ShowsNoPrompt_AndAFeedFailureIsSilent()
    {
        using var rig = new Rig(latestVersion: new Version(2, 0, 0), currentVersion: new Version(2, 0, 0));
        rig.Updates.Throw = true;

        rig.Get<IAppFlow>().Start();
        await Task.Delay(100);

        Assert.Null(rig.Windows.Login!.UpdatePrompt);
        Assert.Equal(1, rig.Updates.Checks);
    }

    [Fact]
    public async Task SigningIn_AfterTheStartupCheck_DoesNotAskTheFeedAgain()
    {
        using var rig = new Rig(latestVersion: new Version(2, 1, 0), currentVersion: new Version(2, 0, 0));
        rig.Get<IAppFlow>().Start();
        var service = rig.Get<UpdateService>();
        for (var i = 0; i < 100 && !service.IsUpdateReady; i++)
        {
            await Task.Delay(20);
        }

        rig.SignIn();
        await rig.Get<IAppFlow>().SignedInAsync();
        await Task.Delay(50);

        Assert.Equal(1, rig.Updates.Checks);
        Assert.Contains("2.1.0", rig.Windows.Main!.UpdatePrompt);
    }
}
