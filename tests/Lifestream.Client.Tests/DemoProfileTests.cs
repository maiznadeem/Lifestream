using Lifestream.Client.Api;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Client.Tests.Features;
using Lifestream.Contracts.Profile;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

/// <summary>
/// The demo account could type a new name and phone and choose a photo on Profile, and Save was then refused by the server (the account is
/// shared). Its Profile is read-only and says why in one line; every other account edits as before.
/// </summary>
public sealed class DemoProfileTests
{
    private static async Task<(Rig Rig, ShellViewModel Shell, ProfileViewModel Page)> ProfileAsync(bool demo)
    {
        var rig = new Rig(passwordPrompt: new FakePasswordPrompt());
        rig.SignIn(Role.Staff, demo);
        rig.Api.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile()));
        var shell = rig.Get<ShellViewModel>();
        await shell.ProfileCommand.ExecuteAsync();
        return (rig, shell, Assert.IsType<ProfileViewModel>(shell.Navigation.Current));
    }

    [Fact]
    public async Task DemoAccount_ProfileIsReadOnly_SaysWhyInOneLine_AndNothingTypedCanBeSaved()
    {
        var (rig, shell, page) = await ProfileAsync(demo: true);
        using var a = rig;
        using var b = shell;

        Assert.False(page.CanEditProfile);
        Assert.True(page.IsProfileReadOnly);
        Assert.Equal("The demo account is shared by everyone who tries Lifestream, so its name, phone and photo can't be changed.", page.DetailsSectionText);
        Assert.False(page.PickPhotoCommand.CanExecute(null)); // released 2.1.2: offered, and the save of the photo was refused with 403

        // Even if something reaches the view model (a view that forgot to lock a box), it is not a change to save.
        page.DisplayName = "Someone Else";
        page.Phone = "+1 555 0100";
        Assert.False(page.HasUnsavedChanges); // released 2.1.2: true, with Save offered
        Assert.False(page.SaveCommand.CanExecute(null));
        await page.SaveCommand.ExecuteAsync();
        Assert.Equal(0, rig.Api.CountOf("UpdateProfileAsync"));
    }

    [Fact]
    public async Task OtherAccounts_EditTheirProfileAsBefore()
    {
        var (rig, shell, page) = await ProfileAsync(demo: false);
        using var a = rig;
        using var b = shell;

        Assert.True(page.CanEditProfile);
        Assert.False(page.IsProfileReadOnly);
        Assert.Equal("Your name and phone show to the rest of the team.", page.DetailsSectionText);
        Assert.True(page.PickPhotoCommand.CanExecute(null));
        page.DisplayName = "A New Name";
        Assert.True(page.HasUnsavedChanges);
        Assert.True(page.SaveCommand.CanExecute(null));
    }
}
