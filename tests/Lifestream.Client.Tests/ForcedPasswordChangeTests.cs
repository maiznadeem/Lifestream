using Lifestream.Client.Features.Profile;
using Lifestream.Client.Navigation;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;

namespace Lifestream.Client.Tests;

/// <summary>After an admin reset the API marks the session (SessionUser.MustChangePassword). The shell opens the required change-password dialog before any screen; the only ways out are changing the password or signing out. Real AppFlow and shell, fake API, fake prompt.</summary>
public sealed class ForcedPasswordChangeTests
{
    private sealed class RecordingPrompt(Func<bool> answer) : IChangePasswordPrompt
    {
        public INavigationService? Navigation { get; set; }

        public List<(bool Required, bool ScreenOpen)> Calls { get; } = [];

        public Task<bool> PromptAsync(bool required = false)
        {
            Calls.Add((required, Navigation?.Current is not null));
            return Task.FromResult(answer());
        }
    }

    private static (Rig Rig, RecordingPrompt Prompt) Start(bool mustChange, Func<bool> answer)
    {
        var prompt = new RecordingPrompt(answer);
        var rig = new Rig(passwordPrompt: prompt);
        prompt.Navigation = rig.Get<INavigationService>();
        var user = FakeApiClient.User(Lifestream.Domain.Role.Staff) with { MustChangePassword = mustChange };
        rig.Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), user));
        return (rig, prompt);
    }

    [Fact]
    public async Task AfterAnAdminReset_RequiredDialogOpensBeforeAnyScreen_ThenTheFirstScreenOpens_FakeApi()
    {
        var (rig, prompt) = Start(mustChange: true, () => true);
        using (rig)
        {
            await rig.Get<IAppFlow>().SignedInAsync();

            Assert.Equal([(true, false)], prompt.Calls);
            Assert.NotNull(rig.Get<INavigationService>().Current);
            Assert.True(rig.Session.IsAuthenticated);
        }
    }

    [Fact]
    public async Task DismissingTheRequiredDialog_SignsOut_AndOpensNoScreen_FakeApi()
    {
        var (rig, prompt) = Start(mustChange: true, () => false);
        using (rig)
        {
            await rig.Get<IAppFlow>().SignedInAsync();

            Assert.Equal([(true, false)], prompt.Calls);
            Assert.Null(rig.Get<INavigationService>().Current);
            Assert.False(rig.Session.IsAuthenticated);
            Assert.Equal(["ShowMain", "CloseLogin", "CloseMain", "ShowLogin"], rig.Windows.Events);
            Assert.False(string.IsNullOrEmpty(rig.Windows.Login!.Notice));
        }
    }

    [Fact]
    public async Task WithoutAReset_NoPasswordDialogOpens_FakeApi()
    {
        var (rig, prompt) = Start(mustChange: false, () => true);
        using (rig)
        {
            await rig.Get<IAppFlow>().SignedInAsync();

            Assert.Empty(prompt.Calls);
            Assert.NotNull(rig.Get<INavigationService>().Current);
        }
    }
}
