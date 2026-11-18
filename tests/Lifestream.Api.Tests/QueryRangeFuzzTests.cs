using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>API-09: every query parameter has a range, and a value outside it is a 400 problem, never a 500 (the verifier's probes P02 and P03).</summary>
public sealed class QueryRangeFuzzTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static readonly string[] ListRoutes = [ApiRoutes.Donors, ApiRoutes.Patients, ApiRoutes.Appointments, ApiRoutes.Requests, ApiRoutes.Receipts, ApiRoutes.Staff];
    private static readonly string[] DateRoutes = [ApiRoutes.Appointments, ApiRoutes.Requests, ApiRoutes.Receipts];

    public static TheoryData<string, string> BadPages()
    {
        var data = new TheoryData<string, string>();
        foreach (var route in ListRoutes)
        {
            foreach (var page in new[] { "2147483647", "-2147483648", "-1", "0", "50000000", "100001" })
            {
                data.Add(route, page);
            }
        }

        return data;
    }

    public static TheoryData<string, string, string> BadDates()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var route in DateRoutes)
        {
            foreach (var field in new[] { "fromUtc", "toUtc" })
            {
                foreach (var value in new[] { "0001-01-01", "1899-12-31", "2201-01-01", "9999-12-31", "9999-12-31T23:59:59.9999999Z", "9999-12-31T23:59:59.9999999%2B14:00", "0001-01-01T00:00:00-14:00" })
                {
                    data.Add(route, field, value);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BadPages))]
    public async Task List_PageOutsideItsRange_Is400WithTheFieldNamed(string route, string page)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var response = await admin.Client.GetApi(route, "?page=" + page);
        using var problem = await response.Problem(400, ProblemCodes.Validation);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("page", out _), problem.RootElement.GetRawText());
    }

    [Theory]
    [MemberData(nameof(BadDates))]
    public async Task List_DateFilterOutsideTheWindow_Is400WithTheFieldNamed(string route, string field, string value)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var response = await admin.Client.GetApi(route, $"?{field}={value}");
        using var problem = await response.Problem(400, ProblemCodes.Validation);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), problem.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("?from=0001-01-01&to=0001-01-01&utcOffsetMinutes=60", "from")]
    [InlineData("?from=0001-01-01&to=0001-01-31", "from")]
    [InlineData("?from=9999-12-01&to=9999-12-31", "from")]
    [InlineData("?to=9999-12-31", "to")]
    [InlineData("?from=9999-12-31&to=9999-12-31&utcOffsetMinutes=-60", "from")]
    [InlineData("?to=1800-01-01", "to")]
    [InlineData("?utcOffsetMinutes=2147483647", "utcOffsetMinutes")]
    [InlineData("?utcOffsetMinutes=-2147483648", "utcOffsetMinutes")]
    public async Task Dashboard_DatesOrOffsetOutsideTheirRange_Is400(string query, string field)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var response = await admin.Client.GetApi(ApiRoutes.Dashboard, query);
        using var problem = await response.Problem(400, ProblemCodes.Validation);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), problem.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("9999-12-31")]
    [InlineData("0001-01-01")]
    [InlineData("2201-01-01")]
    public async Task Slots_DateOutsideTheWindow_Is400(string date)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var response = await admin.Client.GetApi(ApiRoutes.AppointmentSlots, "?date=" + date);
        using var problem = await response.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task EveryEdgeValueInsideTheRange_StillWorks_PageSizeIsClamped()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        foreach (var route in ListRoutes)
        {
            foreach (var query in new[] { "?page=1", "?page=100000", "?page=1&pageSize=2147483647", "?page=1&pageSize=-5", "?page=1&pageSize=0" })
            {
                using var response = await admin.Client.GetApi(route, query);
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{route}{query}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
            }
        }

        foreach (var route in DateRoutes)
        {
            using var response = await admin.Client.GetApi(route, "?fromUtc=1900-01-01&toUtc=2200-12-31");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var dashboard = await admin.Client.GetApi(ApiRoutes.Dashboard, "?from=2200-12-01&to=2200-12-31");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        using var slots = await admin.Client.GetApi(ApiRoutes.AppointmentSlots, "?date=2200-12-31");
        Assert.Equal(HttpStatusCode.OK, slots.StatusCode);
    }
}
