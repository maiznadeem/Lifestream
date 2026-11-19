using System.Text.Json;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Data.Queries;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>
/// DB-05: one rule for every list search (the verifier's probe P5). A phone is matched only when the term has at least seven digits, so "+", "+1" and
/// "+1-555" (the start of every seeded phone) search names only and never read the whole table; a record id is matched only when the WHOLE term is a number.
/// </summary>
public sealed class SearchTermRuleTests
{
    [Theory]
    [InlineData("+", false)]
    [InlineData("+1", false)]
    [InlineData("+1-555", false)]
    [InlineData("+1-555-01", false)]
    [InlineData("555-0101", true)]
    [InlineData("+1-555-0101", true)]
    [InlineData("0123456", true)]
    [InlineData("012345", false)]
    [InlineData("Ayse 1234567", true)]
    [InlineData("a", false)]
    public void ThePhoneIsMatchedOnlyFromSevenDigits(string term, bool matchesPhone) =>
        Assert.Equal(matchesPhone, SearchQueries.ParseTerm(term).PhonePattern is not null);

    [Theory]
    [InlineData("1", 1)]
    [InlineData("42", 42)]
    [InlineData("0042", 42)]
    [InlineData("+1", null)]
    [InlineData("-1", null)]
    [InlineData("1-5", null)]
    [InlineData("1 5", null)]
    [InlineData("0", null)]
    [InlineData("99999999999", null)]
    [InlineData("1a", null)]
    [InlineData("a", null)]
    public void AnIdIsMatchedOnlyWhenTheWholeTermIsANumber(string term, int? id) =>
        Assert.Equal(id, SearchQueries.ParseTerm(term).Id);
}

/// <summary>The rule through the real API, on every list, against a real database (donors carry +1-555-0101 and patients +1-555-0102, like the seeded ones start with +1-555-01).</summary>
public sealed class SearchTermApiTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(int Total, List<string> Names)> SearchAsync(HttpClient client, string route, string term)
    {
        using var response = await client.GetAsync($"{ApiRoutes.Path(route)}?search={Uri.EscapeDataString(term)}&pageSize=200", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, text);
        var json = JsonElement.Parse(text);
        return (json.GetProperty("total").GetInt32(), [.. json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32().ToString())]);
    }

    [Fact]
    public async Task EveryList_APhonePrefixTermFindsNobody_ButTheWholeNumberStillDoes_AndAnExactIdStillFindsTheRecord()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, DateTime.UtcNow.AddHours(-2));
        var issue = await Seed.ReceiptAsync(fx, ReceiptKind.Issue, DateTime.UtcNow.AddHours(-1));
        var donor = await Seed.DonorAsync(fx, "Zephyr Quill");
        var patient = await Seed.PatientAsync(fx, "Zorro Quill");
        var appointment = await Seed.AppointmentAsync(fx, donor, Seed.Slot(3, 10));
        var request = await Seed.RequestAsync(fx, patient, 1);

        foreach (var route in new[] { ApiRoutes.Donors, ApiRoutes.Patients, ApiRoutes.Appointments, ApiRoutes.Requests, ApiRoutes.Receipts })
        {
            // Every one of these rows has the phone +1-555-0101. Short phone prefixes must not match it (and "+1" must not be read as the id 1).
            foreach (var term in new[] { "+", "+1", "+1-555", "+1-555-01" })
            {
                var (total, ids) = await SearchAsync(admin.Client, route, term);
                Assert.True(total == 0, $"{route}?search={term} found {total} ({string.Join(",", ids)}): a short phone prefix must search names only");
            }

            // The whole phone matches (a donor's or patient's phone, through the record that holds it): donors +1-555-0101, patients +1-555-0102.
            var phone = route is "patients" or "requests" ? "+1-555-0102" : "+1-555-0101";
            var (whole, _) = await SearchAsync(admin.Client, route, phone);
            Assert.True(whole > 0, $"{route}?search={phone} found nothing");
        }

        // A name prefix still finds the record, and a term that is a whole number is also an id.
        Assert.Contains(donor.Id.ToString(), (await SearchAsync(admin.Client, ApiRoutes.Donors, "Zeph")).Names);
        Assert.Contains(patient.Id.ToString(), (await SearchAsync(admin.Client, ApiRoutes.Patients, "Zorr")).Names);
        Assert.Contains(donor.Id.ToString(), (await SearchAsync(admin.Client, ApiRoutes.Donors, donor.Id.ToString())).Names);
        Assert.Contains(appointment.Id.ToString(), (await SearchAsync(admin.Client, ApiRoutes.Appointments, appointment.Id.ToString())).Names);
        Assert.Contains(request.Id.ToString(), (await SearchAsync(admin.Client, ApiRoutes.Requests, request.Id.ToString())).Names);
        Assert.Contains(issue.Id.ToString(), (await SearchAsync(admin.Client, ApiRoutes.Receipts, issue.Id.ToString())).Names);
    }

    [Fact]
    public async Task Receipts_APageFarPastTheEnd_IsEmptyWithTheTotal_FromOneStatement_AndTheSearchHasNoFallbackCount()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, DateTime.UtcNow.AddHours(-3));
        await Seed.ReceiptAsync(fx, ReceiptKind.Issue, DateTime.UtcNow.AddHours(-2));

        foreach (var query in new[] { "?search=Receipt&page=100000", "?page=100000", "?search=%2B1-555-0101&page=100000&pageSize=200", "?search=nobodyhasthisname&page=100000" })
        {
            fx.Statements.Drain();
            fx.Statements.Recording = true;
            HttpResponseMessage response;
            try
            {
                response = await admin.Client.GetAsync(ApiRoutes.Path(ApiRoutes.Receipts) + query, Ct);
            }
            finally
            {
                fx.Statements.Recording = false;
            }

            var json = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
            var statements = fx.Statements.Drain().Where(s => s.Sql.Contains("[Receipt]", StringComparison.Ordinal)).ToList();
            Assert.Empty(json.GetProperty("items").EnumerateArray());
            Assert.Single(statements); // the total and the (empty) page come from the same statement: nothing counts the result again
            if (!query.Contains("nobodyhasthisname", StringComparison.Ordinal))
            {
                Assert.True(json.GetProperty("total").GetInt32() >= 2, query);
            }
            else
            {
                Assert.Equal(0, json.GetProperty("total").GetInt32());
            }

            Assert.Equal(100000, json.GetProperty("page").GetInt32());
        }
    }
}
