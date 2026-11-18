using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>API-14: a photo upload returns the saved resource and leaves the staff row (and so its row version) alone (the verifier's probe P09).</summary>
public sealed class PhotoRowVersionTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<string> RowVersionInDbAsync(int id) => fx.Db.ScalarAsync<string>($"SELECT CONVERT(varchar(32), RowVersion, 1) FROM Staff WHERE Id = {id}", Ct);

    [Fact]
    public async Task AdminUploadsAPhoto_ReturnsTheStaffDetail_RowVersionUnchanged_ThenSavingWithTheOldDetailWorks()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var target = await fx.AddStaffAsync(Role.Staff);
        var before = await (await admin.Client.GetApi(ApiRoutes.StaffById, target.Id)).Read<StaffDetail>();
        Assert.False(before.HasPhoto);
        var dbBefore = await RowVersionInDbAsync(target.Id);

        using var upload = await admin.Client.PutBytes(ApiRoutes.StaffPhoto, target.Id, TestImages.Png(300, 300), "image/png");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var returned = await upload.Read<StaffDetail>();
        Assert.True(returned.HasPhoto);
        Assert.Equal(before.RowVersion, returned.RowVersion);
        Assert.Equal(target.Id, returned.Id);
        Assert.Equal(dbBefore, await RowVersionInDbAsync(target.Id));

        // The record held before the upload is still current: this save used to fail with 409 concurrency.
        using var save = await admin.Client.PatchApi(ApiRoutes.StaffById, target.Id, new UpdateStaffRequest(before.RowVersion, DisplayName: "Renamed After Photo"));
        var saved = await save.Read<StaffDetail>();
        Assert.Equal("Renamed After Photo", saved.DisplayName);
        Assert.True(saved.HasPhoto);

        // Replacing a photo that exists changes nothing on the row either, and the list shows the flag.
        using var again = await admin.Client.PutBytes(ApiRoutes.StaffPhoto, target.Id, TestImages.Png(120, 120), "image/png");
        Assert.Equal(saved.RowVersion, (await again.Read<StaffDetail>()).RowVersion);
        var list = await (await admin.Client.GetApi(ApiRoutes.Staff, "?pageSize=200")).Read<PagedResult<StaffListItem>>();
        Assert.True(list.Items.Single(s => s.Id == target.Id).HasPhoto);
    }

    [Fact]
    public async Task UserUploadsTheirOwnPhoto_ReturnsTheProfile_RowVersionUnchanged_ThenSavingWithTheOldProfileWorks()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        var before = await (await user.Client.GetApi(ApiRoutes.Me)).Read<ProfileResponse>();
        Assert.False(before.HasPhoto);

        using var upload = await user.Client.PutBytes(ApiRoutes.MePhoto, null, TestImages.Png(300, 300), "image/png");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var returned = await upload.Read<ProfileResponse>();
        Assert.True(returned.HasPhoto);
        Assert.Equal(before.RowVersion, returned.RowVersion);

        using var save = await user.Client.PatchMe(new UpdateProfileRequest(before.RowVersion, DisplayName: "Own Rename"));
        var saved = await save.Read<ProfileResponse>();
        Assert.Equal("Own Rename", saved.DisplayName);
        Assert.True(saved.HasPhoto);
    }
}
