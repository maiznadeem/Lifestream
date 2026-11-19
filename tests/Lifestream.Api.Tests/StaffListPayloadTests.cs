using System.Text;
using Lifestream.Api.Tests.Data;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests;

/// <summary>
/// API-31: 110 staff with 48-character names (the verifier's measurement, 22,869 bytes before the list dropped salary and phone) stay under
/// 20 KB, names whole. The longest enum values are used for designation and shift. User names are short ("bulkstaff109"): the budget is
/// about 186 bytes per row, so user names of 20 characters or more with 48-character display names no longer fit, whatever the list holds.
/// </summary>
public sealed class StaffListPayloadTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private const int NameLength = 48;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string LongName(int i) => $"Dr Maximilian Bartholomew-Featherstonehaugh {i:D3}".PadRight(NameLength, 'x')[..NameLength];

    [Fact]
    public async Task StaffList_110StaffWith48CharacterNames_IsUnder20KB_NamesWhole_NoImageBytes_NoSalaryOrPhone()
    {
        var admin = await fx.AddStaffAsync(Role.Admin, change: s => s.DisplayName = LongName(0));
        await using (var db = fx.Db.NewContext())
        {
            var have = await db.Staff.CountAsync(Ct);
            for (var i = have; i < 110; i++)
            {
                var staff = TestRows.Staff($"bulkstaff{i}");
                staff.DisplayName = LongName(i);
                staff.Designation = Designation.Assistant;
                staff.Shift = Shift.FullTime;
                staff.Role = Role.Staff;
                staff.Salary = 99_999;
                staff.Phone = "+1 (555) 012-3456";
                db.Staff.Add(staff);
            }

            await db.SaveChangesAsync(Ct);
        }

        // Some photos exist, so a leak of image bytes into the list would show.
        await fx.Db.ExecuteAsync("INSERT INTO StaffPhoto (StaffId, Content, ContentType, Width, Height, UpdatedAtUtc) SELECT TOP 10 Id, 0x01, 'image/jpeg', 1, 1, SYSUTCDATETIME() FROM Staff WHERE IsDemo = 0 ORDER BY Id", Ct);
        Assert.Equal(110, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Staff", Ct));

        using var response = await admin.Client.GetApi(ApiRoutes.Staff, "?pageSize=200");
        var body = await response.Content.ReadAsByteArrayAsync(Ct);
        var page = System.Text.Json.JsonSerializer.Deserialize<PagedResult<StaffListItem>>(body, ApiFixture.Json)!;

        Assert.Equal(110, page.Items.Count);
        Assert.All(page.Items, s => Assert.Equal(NameLength, s.DisplayName.Length));
        Assert.True(body.Length < 20 * 1024, $"the staff list is {body.Length} bytes for 110 staff with {NameLength}-character names");
        Assert.Equal(10, page.Items.Count(s => s.HasPhoto));
        var text = Encoding.UTF8.GetString(body);
        Assert.DoesNotContain("salary", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"phone\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("base64", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content\"", text, StringComparison.OrdinalIgnoreCase);
    }
}
