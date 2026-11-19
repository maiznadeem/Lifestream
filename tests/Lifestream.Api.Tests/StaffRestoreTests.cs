using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>
/// FEAT-06 for staff (real API, real SQL Server): a deleted staff member comes back within the window with the same id, the same hash, the same
/// lock state and must-change flag, and the photo. Their sessions stay ended. Admin only; the last-admin rule still applies to the delete.
/// </summary>
public sealed class StaffRestoreTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<List<object?[]>> CredentialRowAsync(int id) =>
        fx.Db.RowsAsync($"SELECT PasswordHash, FailedCount, CONVERT(varchar(30), LockoutEndUtc, 126), MustChangePassword, CAST(SecurityStamp AS varchar(36)) FROM StaffCredential WHERE StaffId = {id}", Ct);

    [Fact]
    public async Task Restore_PutsBackTheSameIdProfileAndCredential_HashLockStateMustChangeAndSecurityStamp()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        await fx.Db.ExecuteAsync($"UPDATE StaffCredential SET FailedCount = 3, LockoutEndUtc = DATEADD(hour, 1, SYSUTCDATETIME()), MustChangePassword = 1 WHERE StaffId = {worker.Id}", Ct);
        var before = await (await admin.Client.GetApi(ApiRoutes.StaffById, worker.Id)).Read<StaffDetail>();
        var credentialBefore = await CredentialRowAsync(worker.Id);

        using var deleted = await admin.Client.DeleteApi(ApiRoutes.StaffById, worker.Id);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM StaffCredential WHERE StaffId = {worker.Id}", Ct));

        var restored = await (await admin.Client.PostApi(ApiRoutes.StaffRestore, null, worker.Id)).Read<StaffDetail>();

        Assert.Equal(worker.Id, restored.Id);
        Assert.Equal(before with { RowVersion = restored.RowVersion }, restored);
        Assert.Equal(before, await (await admin.Client.GetApi(ApiRoutes.StaffById, worker.Id)).Read<StaffDetail>() with { RowVersion = before.RowVersion });
        var credentialAfter = await CredentialRowAsync(worker.Id);
        Assert.Equal(credentialBefore.Single(), credentialAfter.Single());
        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM DeletedRecord WHERE Entity = 'Staff' AND EntityId = {worker.Id}", Ct));
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM AuditLog WHERE Entity = 'Staff' AND EntityId = {worker.Id} AND Action = 'Delete'", Ct));
        Assert.Equal(2, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM AuditLog WHERE Entity = 'Staff' AND EntityId = {worker.Id} AND Action = 'Insert'", Ct)); // the first row and the restore
    }

    [Fact]
    public async Task Restore_TheRestoredPersonCanSignInWithTheSamePassword_AndMustChangeStillApplies()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        using var reset = await admin.Client.PostApi(ApiRoutes.StaffResetPassword, new ResetPasswordRequest("temporary-pass-77"), worker.Id);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        (await admin.Client.DeleteApi(ApiRoutes.StaffById, worker.Id)).Dispose();
        (await admin.Client.PostApi(ApiRoutes.StaffRestore, null, worker.Id)).Dispose();

        using var login = await fx.NewClient().PostApi(ApiRoutes.AuthLogin, new LoginRequest(worker.Username, "temporary-pass-77"));
        var tokens = await login.Read<TokenResponse>();
        Assert.Equal(worker.Id, tokens.User.StaffId);
        Assert.True(tokens.User.MustChangePassword);
    }

    [Fact]
    public async Task Restore_BringsBackThePhoto_ByteForByte()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        using var put = await admin.Client.PutBytes(ApiRoutes.StaffPhoto, worker.Id, TestImages.Jpeg(200, 200, 60), "image/jpeg");
        Assert.True(put.IsSuccessStatusCode);
        var photoBefore = await (await admin.Client.GetApi(ApiRoutes.StaffPhoto, worker.Id)).Content.ReadAsByteArrayAsync(Ct);

        (await admin.Client.DeleteApi(ApiRoutes.StaffById, worker.Id)).Dispose();
        var restored = await (await admin.Client.PostApi(ApiRoutes.StaffRestore, null, worker.Id)).Read<StaffDetail>();

        Assert.True(restored.HasPhoto);
        Assert.Equal(photoBefore, await (await admin.Client.GetApi(ApiRoutes.StaffPhoto, worker.Id)).Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Restore_TheDeletedPersonsSessionsStayEnded_BothTokens()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        using var login = await fx.NewClient().PostApi(ApiRoutes.AuthLogin, new LoginRequest(worker.Username, ApiFixture.Password));
        var session = await login.Read<TokenResponse>();

        (await admin.Client.DeleteApi(ApiRoutes.StaffById, worker.Id)).Dispose();
        (await admin.Client.PostApi(ApiRoutes.StaffRestore, null, worker.Id)).Dispose();

        using var me = await fx.NewClient(session.AccessToken).GetApi(ApiRoutes.Me);
        using var meProblem = await me.Problem(401);
        using var refresh = await fx.NewClient().PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(session.RefreshToken));
        using var refreshProblem = await refresh.Problem(401);
        using var original = await worker.Client.GetApi(ApiRoutes.Me);
        using var originalProblem = await original.Problem(401);
        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM RefreshToken WHERE StaffId = {worker.Id} AND RevokedAtUtc IS NULL", Ct));
    }

    [Fact]
    public async Task Restore_AfterTheWindow_404_AndTheCopyIsPurgedWhenAnotherDeleteHappens_NoTimer()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var late = await fx.AddStaffAsync(Role.Staff);
        var other = await fx.AddStaffAsync(Role.Staff);
        (await admin.Client.DeleteApi(ApiRoutes.StaffById, late.Id)).Dispose();
        await fx.Db.ExecuteAsync($"UPDATE DeletedRecord SET DeletedAtUtc = DATEADD(MINUTE, -10, SYSUTCDATETIME()) WHERE Entity = 'Staff' AND EntityId = {late.Id}", Ct);

        using var tooLate = await admin.Client.PostApi(ApiRoutes.StaffRestore, null, late.Id);
        using var problem = await tooLate.Problem(404);
        (await admin.Client.DeleteApi(ApiRoutes.StaffById, other.Id)).Dispose();

        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM DeletedRecord WHERE Entity = 'Staff' AND EntityId = {late.Id}", Ct));
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM DeletedRecord WHERE Entity = 'Staff' AND EntityId = {other.Id}", Ct));
    }

    [Fact]
    public async Task Restore_NeverDeleted_404_Twice_404_AndAUsernameTakenAgain_409()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var never = await admin.Client.PostApi(ApiRoutes.StaffRestore, null, 2_000_000_000);
        using var neverProblem = await never.Problem(404);

        var worker = await fx.AddStaffAsync(Role.Staff);
        (await admin.Client.DeleteApi(ApiRoutes.StaffById, worker.Id)).Dispose();
        await fx.AddStaffAsync(Role.Staff, username: worker.Username); // somebody takes the username in the meantime
        using var taken = await admin.Client.PostApi(ApiRoutes.StaffRestore, null, worker.Id);
        using var takenProblem = await taken.Problem(409);

        var second = await fx.AddStaffAsync(Role.Staff);
        (await admin.Client.DeleteApi(ApiRoutes.StaffById, second.Id)).Dispose();
        (await admin.Client.PostApi(ApiRoutes.StaffRestore, null, second.Id)).Dispose();
        using var again = await admin.Client.PostApi(ApiRoutes.StaffRestore, null, second.Id);
        using var againProblem = await again.Problem(404);
    }

    [Fact]
    public async Task Restore_IsAdminOnly_AnAdminCanBeRestored_AndTheDeleteRulesAreUnchanged()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var demo = await fx.AddDemoAsync();
        using var asStaff = await staff.Client.PostApi(ApiRoutes.StaffRestore, null, staff.Id);
        Assert.Equal(HttpStatusCode.Forbidden, asStaff.StatusCode);
        using var asDemo = await demo.Client.PostApi(ApiRoutes.StaffRestore, null, staff.Id);
        Assert.Equal(HttpStatusCode.Forbidden, asDemo.StatusCode);

        var firstAdmin = await fx.AddStaffAsync(Role.Admin);
        var helper = await fx.AddStaffAsync(Role.Admin);
        (await helper.Client.DeleteApi(ApiRoutes.StaffById, firstAdmin.Id)).Dispose();
        using var self = await helper.Client.DeleteApi(ApiRoutes.StaffById, helper.Id);
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode); // the delete rules are unchanged: nobody deletes their own account
        using var restoredAdmin = await helper.Client.PostApi(ApiRoutes.StaffRestore, null, firstAdmin.Id);
        Assert.Equal(HttpStatusCode.OK, restoredAdmin.StatusCode);
    }

    [Fact]
    public async Task Restore_TheRestoredRowIsEditable_WithItsNewRowVersion()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        (await admin.Client.DeleteApi(ApiRoutes.StaffById, worker.Id)).Dispose();
        var restored = await (await admin.Client.PostApi(ApiRoutes.StaffRestore, null, worker.Id)).Read<StaffDetail>();

        using var edit = await admin.Client.PatchApi(ApiRoutes.StaffById, worker.Id, new UpdateStaffRequest(restored.RowVersion, DisplayName: "Back And Edited"));
        Assert.Equal("Back And Edited", (await edit.Read<StaffDetail>()).DisplayName);
    }
}
