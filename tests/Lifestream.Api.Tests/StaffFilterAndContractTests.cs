using System.Reflection;
using Lifestream.Api.Common;
using Lifestream.Api.Features;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>
/// The small API pieces the feature screens need (real API, real SQL Server): the staff list's server-side sort and salary range (admins only),
/// the profile's date of birth and member-since date (FEAT-24), and the sort keys every list accepts being defined once in the contracts.
/// </summary>
public sealed class StaffFilterAndContractTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private async Task<(TestUser Admin, string Tag)> AdminWithStaffAsync()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var tag = fx.NextName("sal");
        foreach (var (suffix, salary) in new[] { ("a", 1000), ("b", 2000), ("c", 3000) })
        {
            await fx.AddStaffAsync(Role.Staff, username: tag.Replace(" ", "", StringComparison.Ordinal) + suffix, change: s =>
            {
                s.Salary = salary;
                s.DisplayName = tag + " " + suffix;
            });
        }

        return (admin, Uri.EscapeDataString(tag));
    }

    [Fact]
    public async Task StaffList_SalaryRange_FiltersOnTheServer_ForAnAdmin_AndStillHidesTheSalary()
    {
        var (admin, tag) = await AdminWithStaffAsync();

        var both = await (await admin.Client.GetApi(ApiRoutes.Staff, $"?search={tag}&minSalary=1500&maxSalary=2500")).Read<PagedResult<StaffListItem>>();
        Assert.Single(both.Items);
        Assert.EndsWith(" b", both.Items[0].DisplayName, StringComparison.Ordinal);
        Assert.Null(both.Items[0].Salary);

        var min = await (await admin.Client.GetApi(ApiRoutes.Staff, $"?search={tag}&minSalary=2000")).Read<PagedResult<StaffListItem>>();
        Assert.Equal(2, min.Total);
        var max = await (await admin.Client.GetApi(ApiRoutes.Staff, $"?search={tag}&maxSalary=2000")).Read<PagedResult<StaffListItem>>();
        Assert.Equal(2, max.Total);

        using var backwards = await admin.Client.GetApi(ApiRoutes.Staff, "?minSalary=5&maxSalary=1");
        using var problem = await backwards.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task StaffList_SalaryRange_IsRefusedForANonAdmin()
    {
        var plain = await fx.AddStaffAsync(Role.Staff);

        using var response = await plain.Client.GetApi(ApiRoutes.Staff, "?minSalary=1");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StaffList_SortsByNameAndByUsername_BothDirections()
    {
        var (admin, tag) = await AdminWithStaffAsync();

        var byUserDesc = await (await admin.Client.GetApi(ApiRoutes.Staff, $"?search={tag}&sort=-username")).Read<PagedResult<StaffListItem>>();
        Assert.Equal(["c", "b", "a"], byUserDesc.Items.Select(s => s.Username[^1].ToString()));
        var byNameAsc = await (await admin.Client.GetApi(ApiRoutes.Staff, $"?search={tag}&sort=name")).Read<PagedResult<StaffListItem>>();
        Assert.Equal(["a", "b", "c"], byNameAsc.Items.Select(s => s.Username[^1].ToString()));
    }

    [Fact]
    public async Task Profile_CarriesTheDateOfBirthAndTheMemberSinceDate()
    {
        var dob = new DateOnly(1990, 4, 12);
        var user = await fx.AddStaffAsync(Role.Staff, change: s => s.DateOfBirth = dob);

        var me = await (await user.Client.GetApi(ApiRoutes.Me)).Read<ProfileResponse>();

        Assert.Equal(dob, me.DateOfBirth);
        Assert.InRange(me.MemberSinceUtc, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(DateTimeKind.Utc, me.MemberSinceUtc.Kind);
    }

    [Theory]
    [InlineData(typeof(DonorEndpoints), "Sorts", new[] { "name", "lastDonated" })]
    [InlineData(typeof(PatientEndpoints), "Sorts", new[] { "name" })]
    [InlineData(typeof(AppointmentEndpoints), "Sorts", new[] { "slot", "placed" })]
    [InlineData(typeof(RequestEndpoints), "Sorts", new[] { "placed", "requiredBy" })]
    [InlineData(typeof(ReceiptEndpoints), "Sorts", new[] { "date" })]
    [InlineData(typeof(ReceiptEndpoints), "HitSorts", new[] { "date" })]
    [InlineData(typeof(StaffEndpoints), "Sorts", new[] { "name", "username" })]
    public void EverySortMap_AcceptsExactlyTheKeysTheContractsDefine(Type endpoints, string field, string[] expected)
    {
        var map = endpoints.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var keys = (IReadOnlyCollection<string>)map.GetType().GetProperty(nameof(SortMap<object>.Keys))!.GetValue(map)!;
        var contract = endpoints.Name switch
        {
            nameof(DonorEndpoints) => SortKeys.Donors,
            nameof(PatientEndpoints) => SortKeys.Patients,
            nameof(AppointmentEndpoints) => SortKeys.Appointments,
            nameof(RequestEndpoints) => SortKeys.Requests,
            nameof(ReceiptEndpoints) => SortKeys.Receipts,
            _ => SortKeys.Staff,
        };

        Assert.Equal(expected.Order(), keys.Order());
        Assert.Equal(contract.Order(), keys.Order());
    }
}
