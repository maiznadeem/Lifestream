using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>API-35: the last-administrator rule holds under concurrency (the verifier's probe P10), and the self-delete rule is tested on its own.</summary>
public sealed class AdminRuleConcurrencyTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<int> AdminCountAsync() => fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Staff WHERE Role = 'Admin'", Ct);

    private async Task<(TestUser A, TestUser B)> TwoAdminsOnlyAsync()
    {
        await fx.Db.ExecuteAsync("UPDATE Staff SET Role = 'Staff' WHERE Role = 'Admin'", Ct);
        var a = await fx.AddStaffAsync(Role.Admin);
        var b = await fx.AddStaffAsync(Role.Admin);
        Assert.Equal(2, await AdminCountAsync());
        return (a, b);
    }

    [Fact]
    public async Task Staff_TwoAdminsDeletingEachOtherConcurrently_ExactlyOneSucceeds_AnAdminRemains_10Rounds()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion);
        for (var round = 0; round < 10; round++)
        {
            var (a, b) = await TwoAdminsOnlyAsync();
            var statuses = await Task.WhenAll(
                Task.Run(async () => { using var r = await a.Client.DeleteApi(ApiRoutes.StaffById, b.Id); return r.StatusCode; }, Ct),
                Task.Run(async () => { using var r = await b.Client.DeleteApi(ApiRoutes.StaffById, a.Id); return r.StatusCode; }, Ct));

            var admins = await AdminCountAsync();
            Assert.True(admins >= 1, $"round {round}: statuses {statuses[0]},{statuses[1]} left {admins} administrators");
            Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.NoContent));
            Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Conflict));
        }
    }

    [Fact]
    public async Task Staff_TwoAdminsDemotingEachOtherConcurrently_AnAdminRemains_10Rounds()
    {
        for (var round = 0; round < 10; round++)
        {
            var (a, b) = await TwoAdminsOnlyAsync();
            var detailA = await (await a.Client.GetApi(ApiRoutes.StaffById, a.Id)).Read<StaffDetail>();
            var detailB = await (await a.Client.GetApi(ApiRoutes.StaffById, b.Id)).Read<StaffDetail>();
            var statuses = await Task.WhenAll(
                Task.Run(async () => { using var r = await a.Client.PatchRaw(ApiRoutes.StaffById, b.Id, $"{{\"rowVersion\":\"{detailB.RowVersion}\",\"role\":\"Staff\"}}"); return r.StatusCode; }, Ct),
                Task.Run(async () => { using var r = await b.Client.PatchRaw(ApiRoutes.StaffById, a.Id, $"{{\"rowVersion\":\"{detailA.RowVersion}\",\"role\":\"Staff\"}}"); return r.StatusCode; }, Ct));

            var admins = await AdminCountAsync();
            Assert.True(admins >= 1, $"round {round}: statuses {statuses[0]},{statuses[1]} left {admins} administrators");
            Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task Staff_DeletingYourOwnAccount_Is409_EvenWhenAnotherAdminExists_AndTheRowStays()
    {
        var (a, _) = await TwoAdminsOnlyAsync();

        // Two administrators: the last-admin rule cannot be what refuses this, only the self rule can.
        using var response = await a.Client.DeleteApi(ApiRoutes.StaffById, a.Id);
        using var problem = await response.Problem(409, ProblemCodes.Conflict);
        Assert.Contains("own account", problem.RootElement.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Staff WHERE Id = {a.Id}", Ct));
        Assert.Equal(2, await AdminCountAsync());
    }
}
