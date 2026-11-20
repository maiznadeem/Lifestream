using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>Stock by age (the age bands on GET stock) and the bag list of one group (GET stock/bags), for the Stock screen's matrix and panel.</summary>
public sealed class StockAgeTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static DateTime Ago(int days) => DateTime.UtcNow.AddDays(-days);

    [Fact]
    public void Bands_AreSixWithTheLastOneTheExpiringSoonWindow()
    {
        Assert.Equal(new[] { "0–6", "7–13", "14–20", "21–27", "28–34", "35–42" }, StockAges.Bands.Select(b => b.Label));
        Assert.Equal(BloodRules.ShelfLifeDays - BloodRules.ExpiringSoonDays, StockAges.Bands[^1].FromDay);
        Assert.Equal(BloodRules.ShelfLifeDays, StockAges.Bands[^1].ToDay);
        Assert.Equal(0, StockAges.BandOfAge(-1));
        Assert.Equal(1, StockAges.BandOfAge(7));
        Assert.Equal(4, StockAges.BandOfAge(34));
        Assert.Equal(5, StockAges.BandOfDaysLeft(BloodRules.ExpiringSoonDays));
        Assert.Equal(4, StockAges.BandOfDaysLeft(BloodRules.ExpiringSoonDays + 1));
    }

    [Fact]
    public async Task Stock_CarriesAgeBands_ThatSumToAvailable_AndEndWithTheExpiringCount()
    {
        await Seed.ClearBagsAsync(fx, BloodType.BPos);
        var staff = await fx.AddStaffAsync(Role.Staff);

        // Ages 0, 3, 6 | 7 | 20 | 21, 27 | 34 | 35, 42; plus expired (43, 60) and one issued bag, which never count.
        await Seed.BagsAsync(fx, BloodType.BPos, Ago(0), Ago(3), Ago(6), Ago(7), Ago(20), Ago(21), Ago(27), Ago(34), Ago(35), Ago(42), Ago(43), Ago(60));
        var issued = (await Seed.BagsAsync(fx, BloodType.BPos, Ago(10)))[0];
        await using (var db = fx.Db.NewContext())
        {
            db.AuditEnabled = false;
            var bag = await db.BloodBags.FindAsync([issued.Id], TestContext.Current.CancellationToken);
            var issue = await Seed.ReceiptAsync(fx, ReceiptKind.Issue, DateTime.UtcNow, BloodType.BPos);
            bag!.Status = BagStatus.Issued;
            bag.IssueReceiptId = issue.Id;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var stock = await (await staff.Client.GetApi(ApiRoutes.Stock)).Read<List<StockItem>>();
        Assert.Equal(8, stock.Count);
        Assert.All(stock, s => Assert.Equal(StockAges.BandCount, s.AgeBands!.Count));
        Assert.All(stock, s => Assert.Equal(s.Available, s.AgeBands!.Sum()));
        Assert.All(stock, s => Assert.Equal(s.ExpiringSoon, s.AgeBands![^1]));

        var item = stock.Single(s => s.BloodType == BloodType.BPos);
        Assert.Equal(new[] { 3, 1, 1, 2, 1, 2 }, item.AgeBands);
        Assert.Equal(10, item.Available);
        Assert.Equal(2, item.ExpiringSoon);
    }

    [Fact]
    public async Task Stock_AgeBands_AreOneQuery_AndTheDashboardStillHasNone()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        (await staff.Client.GetApi(ApiRoutes.Me)).Dispose();
        fx.Commands.Clear();
        fx.Commands.Recording = true;
        try
        {
            (await staff.Client.GetApi(ApiRoutes.Stock)).Dispose();
        }
        finally
        {
            fx.Commands.Recording = false;
        }

        Assert.Equal(1, fx.Commands.Commands.Count(c => c.Contains("FROM [BloodBag]", StringComparison.Ordinal)));

        using var dashboard = await staff.Client.GetApi(ApiRoutes.Dashboard);
        var text = await dashboard.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("\"ageBands\":[", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bags_ListsTheGroupsAvailableBags_OldestFirst_WithoutExpiredOrOtherGroups()
    {
        await Seed.ClearBagsAsync(fx, BloodType.ABNeg);
        await Seed.ClearBagsAsync(fx, BloodType.ABPos);
        var staff = await fx.AddStaffAsync(Role.Staff);
        var bags = await Seed.BagsAsync(fx, BloodType.ABNeg, Ago(5), Ago(30), Ago(50), Ago(12));
        await Seed.BagsAsync(fx, BloodType.ABPos, Ago(40));

        using var response = await staff.Client.GetApi(ApiRoutes.StockBags, "?bloodType=ABNeg");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Read<StockBagList>();
        Assert.Equal(BloodType.ABNeg, list.BloodType);
        Assert.False(list.HasMore);
        Assert.Equal(new[] { bags[1].Id, bags[3].Id, bags[0].Id }, list.Bags.Select(b => b.Id));
        Assert.All(list.Bags, b => Assert.Equal(DateTimeKind.Utc, b.CollectedAtUtc.Kind));
        Assert.Equal(DateOnly.FromDateTime(bags[1].CollectedAtUtc).AddDays(BloodRules.ShelfLifeDays), list.Bags[0].ExpiresOn);
    }

    [Fact]
    public async Task Bags_AreCapped_AndSayThereAreMore()
    {
        await Seed.ClearBagsAsync(fx, BloodType.OPos);
        var staff = await fx.AddStaffAsync(Role.Staff);
        await Seed.BagsAsync(fx, BloodType.OPos, [.. Enumerable.Range(0, StockBagList.Limit + 3).Select(i => Ago(i % 40).AddMinutes(-i))]);
        var list = await (await staff.Client.GetApi(ApiRoutes.StockBags, "?bloodType=OPos")).Read<StockBagList>();
        Assert.Equal(StockBagList.Limit, list.Bags.Count);
        Assert.True(list.HasMore);
        Assert.Equal(list.Bags.OrderBy(b => b.CollectedAtUtc).ThenBy(b => b.Id).Select(b => b.Id), list.Bags.Select(b => b.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?bloodType=")]
    [InlineData("?bloodType=Z")]
    [InlineData("?bloodType=3")]
    [InlineData("?bloodType=APos,ANeg")]
    public async Task Bags_WithoutAValidGroup_Is400(string query)
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        using var response = await staff.Client.GetApi(ApiRoutes.StockBags, query);
        using var problem = await response.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task Bags_NeedASignedInUser_AndAreOpenToEveryRole()
    {
        using var anonymous = fx.NewClient();
        using var refused = await anonymous.GetApi(ApiRoutes.StockBags, "?bloodType=APos");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        foreach (var user in new[] { await fx.AddStaffAsync(Role.Staff), await fx.AddStaffAsync(Role.Admin), await fx.AddDemoAsync() })
        {
            using var ok = await user.Client.GetApi(ApiRoutes.StockBags, "?bloodType=APos");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
    }
}
