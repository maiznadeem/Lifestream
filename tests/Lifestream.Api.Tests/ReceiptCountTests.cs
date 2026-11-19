using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Receipts;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>GET receipts/counts: the Receipts tabs' counts agree with the list's totals for the same filters, come from one query, and need a signed-in user.</summary>
public sealed class ReceiptCountTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static async Task<int> ListTotalAsync(TestUser user, string query)
    {
        var page = await (await user.Client.GetApi(ApiRoutes.Receipts, query)).Read<PagedResult<ReceiptListItem>>();
        return page.Total;
    }

    [Theory]
    [InlineData("")]
    [InlineData("bloodType=ABNeg")]
    [InlineData("search=CountParty")]
    [InlineData("search=CountParty&bloodType=ABNeg")]
    public async Task Counts_EqualTheListTotals_ForTheSameFilters(string filter)
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, DateTime.UtcNow.AddDays(-2), BloodType.ABNeg);
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, DateTime.UtcNow.AddDays(-1), BloodType.OPos);
        await Seed.ReceiptAsync(fx, ReceiptKind.Issue, DateTime.UtcNow.AddDays(-1), BloodType.ABNeg);

        var query = filter.Length == 0 ? "" : "?" + filter;
        var counts = await (await staff.Client.GetApi(ApiRoutes.ReceiptCounts, query)).Read<ReceiptCounts>();
        var separator = filter.Length == 0 ? "?" : "&";
        Assert.Equal(await ListTotalAsync(staff, query), counts.All);
        Assert.Equal(await ListTotalAsync(staff, query + separator + "kind=Donation"), counts.Donations);
        Assert.Equal(await ListTotalAsync(staff, query + separator + "kind=Issue"), counts.Issues);
        Assert.Equal(counts.All, counts.Donations + counts.Issues);
        if (filter.Length == 0)
        {
            Assert.True(counts.Donations >= 2 && counts.Issues >= 1, $"{counts}");
        }
    }

    [Fact]
    public async Task Counts_FollowTheDateRange()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var day = DateTime.UtcNow.AddDays(-400);
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, day, BloodType.BNeg);
        var from = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var counts = await (await staff.Client.GetApi(ApiRoutes.ReceiptCounts, $"?fromUtc={from}&toUtc={from}")).Read<ReceiptCounts>();
        Assert.Equal(await ListTotalAsync(staff, $"?fromUtc={from}&toUtc={from}"), counts.All);
        Assert.True(counts.Donations >= 1);
    }

    [Fact]
    public async Task Counts_AreOneQuery()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        (await staff.Client.GetApi(ApiRoutes.Me)).Dispose();
        fx.Commands.Clear();
        fx.Commands.Recording = true;
        try
        {
            (await staff.Client.GetApi(ApiRoutes.ReceiptCounts, "?bloodType=OPos")).Dispose();
        }
        finally
        {
            fx.Commands.Recording = false;
        }

        Assert.Equal(1, fx.Commands.Commands.Count(c => c.Contains("[Receipt]", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("?bloodType=Q")]
    [InlineData("?bloodType=1")]
    [InlineData("?kind=Donation&bloodType=APos,ANeg")]
    public async Task Counts_RefuseAnUndefinedGroup(string query)
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        using var response = await staff.Client.GetApi(ApiRoutes.ReceiptCounts, query);
        using var problem = await response.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task Counts_NeedASignedInUser_AndAreOpenToEveryRole()
    {
        using var anonymous = fx.NewClient();
        using var refused = await anonymous.GetApi(ApiRoutes.ReceiptCounts);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        foreach (var user in new[] { await fx.AddStaffAsync(Role.Staff), await fx.AddStaffAsync(Role.Admin), await fx.AddDemoAsync() })
        {
            using var ok = await user.Client.GetApi(ApiRoutes.ReceiptCounts);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
    }
}
