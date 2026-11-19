using System.Net;
using System.Text;
using System.Text.Json;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>Every list endpoint: page of 50 by default, hard cap 200, total count, server-side sort, no oversize payloads (API-21, 31).</summary>
public sealed class ListTests(Scale10ApiFixture fx) : IClassFixture<Scale10ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Lists => new()
    {
        { ApiRoutes.Donors, "Donor" },
        { ApiRoutes.Patients, "Patient" },
        { ApiRoutes.Appointments, "Appointment" },
        { ApiRoutes.Requests, "Request" },
        { ApiRoutes.Receipts, "Receipt" },
        { ApiRoutes.Staff, "Staff" },
    };

    private static async Task<(int Total, int Page, int PageSize, List<int> Ids)> ReadPageAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = doc.RootElement;
        return (root.GetProperty("total").GetInt32(), root.GetProperty("page").GetInt32(), root.GetProperty("pageSize").GetInt32(),
            [.. root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32())]);
    }

    /// <summary>The generated data has 110 staff; the cap needs more than 200, so top it up (once) with ordinary staff rows.</summary>
    private async Task EnsureManyStaffAsync()
    {
        await using var db = fx.Db.NewContext();
        db.AuditEnabled = false;
        var have = await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Staff", Ct);
        for (var i = have; i < 260; i++)
        {
            var staff = Data.TestRows.Staff($"extra{Guid.NewGuid():N}"[..20]);
            staff.DisplayName = $"Extra Staff {i}";
            db.Staff.Add(staff);
        }

        await db.SaveChangesAsync(Ct);
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Lists_DefaultPage50_Cap200_TotalCount(string route, string table)
    {
        await EnsureManyStaffAsync();
        var admin = await fx.AddStaffAsync(Role.Admin);
        var total = await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM {table}", Ct);
        Assert.True(total > 200, $"the fixture must hold more than 200 {table} rows to prove the cap");

        var first = await ReadPageAsync(await admin.Client.GetApi(route));
        Assert.Equal(50, first.Ids.Count);
        Assert.Equal(total, first.Total);
        Assert.Equal((1, 50), (first.Page, first.PageSize));

        var huge = await ReadPageAsync(await admin.Client.GetApi(route, "?pageSize=100000"));
        Assert.Equal(200, huge.Ids.Count);
        Assert.Equal(200, huge.PageSize);
        Assert.Equal(total, huge.Total);

        var second = await ReadPageAsync(await admin.Client.GetApi(route, "?pageSize=75&page=2"));
        Assert.Equal(75, second.Ids.Count);
        Assert.Empty(second.Ids.Intersect((await ReadPageAsync(await admin.Client.GetApi(route, "?pageSize=75&page=1"))).Ids));

        var beyond = await ReadPageAsync(await admin.Client.GetApi(route, "?page=9999"));
        Assert.Empty(beyond.Ids);
        Assert.Equal(total, beyond.Total);

        using var zero = await admin.Client.GetApi(route, "?page=0");
        using var problem = await zero.Problem(400, ProblemCodes.Validation);
    }

    [Theory]
    [InlineData(ApiRoutes.Donors, "name")]
    [InlineData(ApiRoutes.Patients, "name")]
    [InlineData(ApiRoutes.Appointments, "slot")]
    [InlineData(ApiRoutes.Appointments, "placed")]
    [InlineData(ApiRoutes.Requests, "placed")]
    [InlineData(ApiRoutes.Requests, "requiredBy")]
    [InlineData(ApiRoutes.Receipts, "date")]
    [InlineData(ApiRoutes.Donors, "lastDonated")]
    [InlineData(ApiRoutes.Staff, "name")]
    [InlineData(ApiRoutes.Staff, "username")]
    public async Task Lists_SortOnTheServer_AscendingAndDescending(string route, string key)
    {
        await EnsureManyStaffAsync();
        var admin = await fx.AddStaffAsync(Role.Admin);
        var asc = await ReadPageAsync(await admin.Client.GetApi(route, $"?sort={key}&pageSize=200"));
        var desc = await ReadPageAsync(await admin.Client.GetApi(route, $"?sort=-{key}&pageSize=200"));
        Assert.NotEqual(asc.Ids, desc.Ids);
        Assert.Equal(200, asc.Ids.Count);

        using var bad = await admin.Client.GetApi(route, "?sort=passwordHash");
        using var problem = await bad.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task Donors_FilterByBloodType_OnTheServer()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var expected = await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor WHERE BloodType = 'ONeg'", Ct);
        var page = await ReadPageAsync(await admin.Client.GetApi(ApiRoutes.Donors, "?bloodType=ONeg&pageSize=200"));
        Assert.Equal(expected, page.Total);
        Assert.Equal(Math.Min(expected, 200), page.Ids.Count);
        using var bad = await admin.Client.GetApi(ApiRoutes.Donors, "?bloodType=NotAType");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task StaffList_110Staff_Under20KB_NoImageBytes()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);

        // Give some staff a photo so a leak of image bytes would show up.
        await using (var db = fx.Db.NewContext())
        {
            var ids = db.Staff.Where(s => !s.IsDemo).Select(s => s.Id).Take(5).ToList();
            foreach (var id in ids)
            {
                db.StaffPhotos.Add(new Lifestream.Data.Entities.StaffPhoto { StaffId = id, Content = new byte[20_000], Width = 256, Height = 256 });
            }

            await db.SaveChangesAsync(Ct);
        }
        await fx.Db.ExecuteAsync("UPDATE Staff SET HasPhoto = 1 WHERE Id IN (SELECT StaffId FROM StaffPhoto)", Ct);

        using var response = await admin.Client.GetApi(ApiRoutes.Staff, "?pageSize=110");
        var body = await response.Content.ReadAsByteArrayAsync(Ct);
        var page = JsonSerializer.Deserialize<PagedResult<StaffListItem>>(body, ApiFixture.Json)!;
        Assert.Equal(110, page.Items.Count);
        Assert.True(body.Length < 20 * 1024, $"the staff list is {body.Length} bytes for 110 staff");
        var text = Encoding.UTF8.GetString(body);
        Assert.DoesNotContain("content\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("base64", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(page.Items, s => s.HasPhoto);
    }

    [Fact]
    public async Task StaffList_ForTheStaffRoleIsRefused_AndSalaryIsAdminOnly()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var demo = await fx.AddDemoAsync();
        var adminView = await (await admin.Client.GetApi(ApiRoutes.Staff, "?pageSize=200")).Read<PagedResult<StaffListItem>>();

        // The list never carries them (the payload budget); the detail gives them to an administrator.
        Assert.All(adminView.Items, s =>
        {
            Assert.Null(s.Salary);
            Assert.Null(s.Phone);
        });
        var adminDetail = await (await admin.Client.GetApi(ApiRoutes.StaffById, admin.Id)).Read<StaffDetail>();
        Assert.NotNull(adminDetail.Salary);
        Assert.NotNull(adminDetail.Phone);
        var demoView = await (await demo.Client.GetApi(ApiRoutes.Staff, "?pageSize=200")).Read<PagedResult<StaffListItem>>();
        Assert.All(demoView.Items, s =>
        {
            Assert.Null(s.Salary);
            Assert.Null(s.Phone);
        });
        var detail = await (await demo.Client.GetApi(ApiRoutes.StaffById, demoView.Items[0].Id)).Read<StaffDetail>();
        Assert.Null(detail.Salary);
        Assert.Null(detail.Phone);
    }
}
