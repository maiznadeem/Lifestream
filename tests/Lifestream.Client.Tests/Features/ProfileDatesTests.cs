using Lifestream.Client.Api;
using Lifestream.Contracts.Profile;

namespace Lifestream.Client.Tests.Features;

/// <summary>FEAT-24, against the fake API client: the profile shows the date of birth and the member-since date as read-only text in the shared date format.</summary>
public sealed class ProfileDatesTests
{
    [Fact]
    public async Task Profile_ShowsTheDateOfBirthAndMemberSince_InTheSharedDateFormat_FakeApi()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile() with { DateOfBirth = new DateOnly(1990, 5, 17), MemberSince = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc) }));
        var vm = rig.Profile();

        await vm.LoadAsync();

        Assert.Equal("17 May 1990", vm.DateOfBirthText);
        Assert.Equal("02 Mar 2026", vm.MemberSinceText);
        Assert.False(vm.SaveCommand.CanExecute(null)); // and showing them is not a change: Save stays disabled until something is edited
    }

    [Fact]
    public async Task Profile_WhenTheServerSentNoDates_SaysNotRecorded_NeverAMinValueDate_FakeApi()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile()));
        var vm = rig.Profile();

        await vm.LoadAsync();

        Assert.Equal("Not recorded", vm.DateOfBirthText);
        Assert.Equal("Not recorded", vm.MemberSinceText);
    }
}
