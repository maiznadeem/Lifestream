using System.Windows.Controls;
using Lifestream.Client.Features.Profile;
using Lifestream.Desktop.Features.Profile;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>The Profile screen of the demo account on screen: its name and phone cannot be typed in, there is no "Change photo", and one line says why.</summary>
[Collection("Wpf")] // the list screens set the record picker's process-wide test hooks: one such class at a time
public sealed class DemoProfileViewTests
{
    private static (bool NameLocked, bool PhoneLocked, bool PhotoButton, List<string> Texts, bool SaveBar) Show(bool demo, string capture) => StaHost.Run(() =>
    {
        using var rig = Part2Harness.NewRig(Role.Staff, demo);
        var vm = Part2Harness.Create<ProfileViewModel>(rig);
        using var host = Hosted.Create(Part2Harness.OnCanvas(new ProfileView(), vm), 1366, 900);
        Hosted.Await(() => vm.LoadAsync());
        host.Settle(500);
        Part2Harness.Save(host, capture);
        return (
            host.Find<TextBox>("NameBox").IsReadOnly,
            host.Find<TextBox>("PhoneBox").IsReadOnly,
            host.Find<Button>("ChangePhotoButton").IsVisible,
            host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList(),
            host.Find<Button>("SaveButton").IsVisible);
    });

    [Fact]
    public void ProfileView_ForTheDemoAccount_IsReadOnly_WithOneLineSayingWhy()
    {
        var f = Show(demo: true, "fix212b-profile-demo.png");

        Assert.True(f.NameLocked, "the demo account can type in Full name"); // released 2.1.2: editable, and the save was refused
        Assert.True(f.PhoneLocked, "the demo account can type in Phone");
        Assert.False(f.PhotoButton, "the demo account is offered Change photo");
        Assert.False(f.SaveBar);
        Assert.Single(f.Texts, t => t == "The demo account is shared by everyone who tries Lifestream, so its name, phone and photo can't be changed.");
    }

    [Fact]
    public void ProfileView_ForAStaffAccount_IsEditable()
    {
        var f = Show(demo: false, "fix212b-profile-staff.png");

        Assert.False(f.NameLocked);
        Assert.False(f.PhoneLocked);
        Assert.True(f.PhotoButton);
        Assert.Contains("Your name and phone show to the rest of the team.", f.Texts);
    }
}
