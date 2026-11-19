using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Data.Seed;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests;

/// <summary>FEAT-06 (real API, real SQL Server): a restore is a create, so the demo's table cap applies to it exactly as to a create.</summary>
public sealed class RestoreDemoTests(DemoCapApiFixture fx) : IClassFixture<DemoCapApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DemoRestore_BeyondTheTableCap_IsRefusedLikeACreate_AndWithinItIsAllowed()
    {
        var demo = await fx.AddDemoAsync(ip: "203.0.113.70");
        var admin = await fx.AddStaffAsync(Role.Admin, ip: "203.0.113.71"); // real staff are not capped, so the demo's few writes are left for the restores
        int target;
        await using (var db = fx.Db.NewContext())
        {
            db.AuditEnabled = false;
            var have = await db.Donors.CountAsync(Ct);
            db.Donors.AddRange(Enumerable.Range(0, SeedSizes.Donors + 2 - have).Select(i => Data.TestRows.Donor($"Cap fill {i}", Gender.Male, BloodType.OPos)));
            await db.SaveChangesAsync(Ct);
            target = await db.Donors.Select(d => d.Id).MaxAsync(Ct);
        }

        // Deleted: one slot is free, so the restore is allowed (and the table is full again afterwards).
        (await demo.Client.DeleteApi(ApiRoutes.DonorById, target)).Dispose();
        using var allowed = await demo.Client.PostApi(ApiRoutes.DonorRestore, null, target);
        Assert.Equal(System.Net.HttpStatusCode.OK, allowed.StatusCode);

        // Deleted again, but somebody else takes the free slot first: now the restore is refused, like a create.
        (await admin.Client.DeleteApi(ApiRoutes.DonorById, target)).Dispose();
        await Seed.DonorAsync(fx);
        using var refused = await demo.Client.PostApi(ApiRoutes.DonorRestore, null, target);
        using var problem = await refused.Problem(409);
        Assert.Contains("demo is full", problem.RootElement.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
        using var stillGone = await demo.Client.GetApi(ApiRoutes.DonorById, target);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, stillGone.StatusCode);
    }
}
