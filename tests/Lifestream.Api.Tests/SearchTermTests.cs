using System.Text.Json;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Data.Queries;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>
/// One rule for every list search (<see cref="SearchQueries"/>): the text is split into words and every word must match. A word of digits is looked for inside
/// phone numbers from three digits on (it was seven, and a prefix, while a search could only seek: "0156" did not find the phone it is part of), so "+" and
/// "+1" still match no phone; a record number is matched only when the WHOLE word is digits.
/// </summary>
public sealed class SearchTermRuleTests
{
    [Theory]
    [InlineData("+", false)]
    [InlineData("+1", false)]
    [InlineData("+1-555", true)] // was false: a part of a phone number is now found (four digits)
    [InlineData("+1-555-01", true)] // was false, as above
    [InlineData("555-0101", true)]
    [InlineData("+1-555-0101", true)]
    [InlineData("0123456", true)]
    [InlineData("012345", true)] // was false, as above
    [InlineData("Ayse 1234567", true)]
    [InlineData("a", false)]
    [InlineData("12", false)]
    [InlineData("0156", true)]
    [InlineData("(555)", true)]
    [InlineData("555x", false)]
    public void ThePhoneIsMatchedOnlyFromThreeDigits(string term, bool matchesPhone) =>
        Assert.Equal(matchesPhone, SearchQueries.Parse(term).Words.Any(w => w.PhonePattern is not null));

    [Theory]
    [InlineData("0156", "%0156%")]
    [InlineData("555-0156", "%5550156%")]
    [InlineData("+1-555-0156", "%15550156%")]
    [InlineData("(555)0156", "%5550156%")]
    public void APhoneWordIsComparedByItsDigitsOnly(string word, string pattern) =>
        Assert.Equal(pattern, Assert.Single(SearchQueries.Parse(word).Words).PhonePattern);

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
    public void AnIdIsMatchedOnlyWhenTheWholeTermIsANumber(string term, int? id)
    {
        // "1 5" is two words now, each a number, and no record has both numbers: no single record is asked for, as before.
        var words = SearchQueries.Parse(term).Words;
        Assert.Equal(id, words.Count == 1 ? words[0].Number : null);
    }

    [Theory]
    [InlineData("  Selim   Eastwood ", new[] { "Selim", "Eastwood" })]
    [InlineData("eastwood\tselim", new[] { "eastwood", "selim" })]
    [InlineData("a", new[] { "a" })]
    [InlineData("eastwood 0156", new[] { "eastwood", "0156" })]
    [InlineData("+1 (555) 019-2834", new[] { "+1 (555) 019-2834" })] // digits that follow each other are one number typed with spaces
    [InlineData("555  0156 eastwood 12", new[] { "555 0156", "eastwood", "12" })]
    [InlineData("0+ 12", new[] { "0+", "12" })] // a blood group typed with a zero is not a part of a number
    public void TheTextIsSplitIntoWordsAtWhiteSpace(string text, string[] words) =>
        Assert.Equal(words, SearchQueries.Parse(text).Words.Select(w => w.Text));

    [Theory]
    [InlineData("+1 (555) 019-2834", "%15550192834%")]
    [InlineData("555 0156", "%5550156%")]
    [InlineData("+1 5", null)] // two digits in all: too few
    public void ANumberTypedWithSpacesIsOnePhoneNumber_AndNeverARecordNumber(string text, string? pattern)
    {
        var word = Assert.Single(SearchQueries.Parse(text).Words);
        Assert.Equal(pattern, word.PhonePattern);
        Assert.Null(word.Number);
        Assert.True(word.IsNumberLike);
    }

    [Theory]
    [InlineData("ali", "%ali%")]
    [InlineData("50%", @"%50\%%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData("[x]", @"%\[x]%")]
    [InlineData(@"a\b", @"%a\\b%")]
    public void ATextWordIsLookedForAnywhereInAName_WithItsWildcardsEscaped(string word, string pattern) =>
        Assert.Equal(pattern, Assert.Single(SearchQueries.Parse(word).Words).ContainsPattern);

    [Theory]
    [InlineData("O+", BloodType.OPos)]
    [InlineData("o-", BloodType.ONeg)]
    [InlineData("O\u2212", BloodType.ONeg)] // the minus sign the screens show
    [InlineData("0+", BloodType.OPos)] // a zero typed for the letter
    [InlineData("ab+", BloodType.ABPos)]
    [InlineData("AB-", BloodType.ABNeg)]
    [InlineData("a+", BloodType.APos)]
    [InlineData("B-", BloodType.BNeg)]
    [InlineData("O", null)]
    [InlineData("AB", null)]
    [InlineData("C+", null)]
    [InlineData("A++", null)]
    [InlineData("Anna-", null)]
    public void ABloodGroupIsALetterGroupAndASign(string word, BloodType? group) =>
        Assert.Equal(group, Assert.Single(SearchQueries.Parse(word).Words).BloodGroup);

    [Theory]
    [InlineData("D-0041", "D", 41)]
    [InlineData("d-41", "D", 41)]
    [InlineData("rc-0007", "RC", 7)]
    [InlineData("D0041", null, null)] // a code has its hyphen, as the screens show it
    [InlineData("D-", null, null)]
    [InlineData("D-0", null, null)]
    [InlineData("ABC-1", null, null)]
    [InlineData("41", null, null)]
    public void ARecordCodeIsLettersAHyphenAndANumber(string word, string? letters, int? number)
    {
        var parsed = Assert.Single(SearchQueries.Parse(word).Words);
        Assert.Equal(letters, parsed.CodeLetters);
        Assert.Equal(number, parsed.CodeNumber);
    }

    [Fact]
    public void EachListReadsAWordItsOwnWay()
    {
        static SearchQueries.Reading Read(string word, string code, bool bloodGroup = true) =>
            SearchQueries.Read(Assert.Single(SearchQueries.Parse(word).Words), code, bloodGroup);

        // A blood group is only a blood group, on a list that shows one; staff have none, so there it is a text.
        Assert.Equal(new SearchQueries.Reading(BloodType.ANeg, null, null, null), Read("A-", SearchQueries.Codes.Donor));
        Assert.Equal(new SearchQueries.Reading(null, null, null, "%A-%", AtWordStart: true), Read("A-", SearchQueries.Codes.Staff, bloodGroup: false));

        // The list's own code is its record; another list's code is a text no name contains.
        Assert.Equal(new SearchQueries.Reading(null, 41, null, null), Read("D-0041", SearchQueries.Codes.Donor));
        Assert.Equal(new SearchQueries.Reading(null, null, null, "%D-0041%"), Read("D-0041", SearchQueries.Codes.Patient));
        Assert.Equal(new SearchQueries.Reading(null, 7, null, null), Read("RC-0007", SearchQueries.Codes.Receipt));

        // Digits are a record number and, from three digits, a part of a phone number; and a part of a name like any word ("Unknown Male 3").
        Assert.Equal(new SearchQueries.Reading(null, 156, "%0156%", "%0156%"), Read("0156", SearchQueries.Codes.Donor));
        Assert.Equal(new SearchQueries.Reading(null, 41, null, "%41%", AtWordStart: true), Read("41", SearchQueries.Codes.Donor));
        Assert.Equal(new SearchQueries.Reading(null, null, "%15550156%", "%+1-555-0156%"), Read("+1-555-0156", SearchQueries.Codes.Donor));
        Assert.Equal(new SearchQueries.Reading(null, null, null, "%+1%", AtWordStart: true), Read("+1", SearchQueries.Codes.Donor)); // no number, no phone: a text no name has
        Assert.Equal(new SearchQueries.Reading(null, null, null, "%1-5%"), Read("1-5", SearchQueries.Codes.Donor));

        // Anything else is looked for in names: three letters or more anywhere, one or two only where a word of the name starts.
        Assert.Equal(new SearchQueries.Reading(null, null, null, "%eastw%"), Read("eastw", SearchQueries.Codes.Donor));
        Assert.Equal(new SearchQueries.Reading(null, null, null, "%ali%"), Read("ali", SearchQueries.Codes.Donor));
        Assert.Equal(new SearchQueries.Reading(null, null, null, "%al%", AtWordStart: true), Read("al", SearchQueries.Codes.Donor));
        Assert.Equal(new SearchQueries.Reading(null, null, null, "%a%", AtWordStart: true), Read("a", SearchQueries.Codes.Donor));
        Assert.Equal(new SearchQueries.Reading(null, null, null, "%+%", AtWordStart: true), Read("+", SearchQueries.Codes.Donor));
    }
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
    public async Task EveryList_PlusAndPlusOneFindNobody_APartOfThePhoneAndTheWholeNumberDo_AndAnExactIdStillFindsTheRecord()
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
            // Every one of these rows has the phone +1-555-0101. One digit is not a part of a phone number (and "+1" must not be read as the id 1).
            foreach (var term in new[] { "+", "+1" })
            {
                var (total, ids) = await SearchAsync(admin.Client, route, term);
                Assert.True(total == 0, $"{route}?search={term} found {total} ({string.Join(",", ids)}): fewer than three digits match no phone");
            }

            // Three digits or more are (these two found nobody while a search could only seek the start of a phone and needed seven digits for that).
            foreach (var term in new[] { "+1-555", "+1-555-01" })
            {
                var (total, _) = await SearchAsync(admin.Client, route, term);
                Assert.True(total > 0, $"{route}?search={term} found nothing: a part of a phone number finds it");
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
        // Two donations and one issue, so each of the searches below (the donor phone is on the donation receipts only) matches at least two
        // receipts from this test's own rows, whichever other test of the class ran before it.
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, DateTime.UtcNow.AddHours(-4));
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, DateTime.UtcNow.AddHours(-3));
        await Seed.ReceiptAsync(fx, ReceiptKind.Issue, DateTime.UtcNow.AddHours(-2));

        foreach (var query in new[] { "?search=Receipt&page=100000", "?page=100000", "?search=%2B1-555-0101&page=100000&pageSize=200", "?search=nobodyhasthisname&page=100000" })
        {
            var (response, sent) = await fx.Statements.OfAsync(admin.Client, ApiRoutes.Path(ApiRoutes.Receipts) + query, Ct);
            var json = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
            var statements = sent.Where(s => s.Sql.Contains("[Receipt]", StringComparison.Ordinal)).ToList(); // of this request only: the ones that read receipts
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
