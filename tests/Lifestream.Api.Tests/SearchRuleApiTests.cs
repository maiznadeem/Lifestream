using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lifestream.Api.Tests.Data;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Data.Queries;
using Lifestream.Domain;
using Microsoft.Data.SqlClient;

namespace Lifestream.Api.Tests;

/// <summary>
/// What a search finds (<see cref="SearchQueries"/>), through the real API on a real database, the same on every list and in the lookups the pickers
/// and the command palette ask: a last name, a word inside a full name, a part of a name, a part of a phone number however it is typed, the record's
/// code, two words in either order, a blood group, accents and case, and nothing for a text nobody has. The cases come from using the app:
/// "Eastwood" did not find "Selim Eastwood", "ali" did not find "Aaliyah Mukherjee", "0156" did not find the phone it is part of.
/// </summary>
public sealed class SearchRuleApiTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The ids of the rows this class added, by a short name; the database may hold other rows (the staff who sign in, for one).</summary>
    private sealed record Rows(HttpClient Client, Dictionary<string, int> Ids)
    {
        public int this[string name] => Ids[name];
    }

    private static readonly ConditionalWeakTable<ApiFixture, Task<Rows>> Seeded = [];
    private static readonly Lock SeedGate = new();

    private Task<Rows> RowsAsync()
    {
        lock (SeedGate)
        {
            if (!Seeded.TryGetValue(fx, out var rows))
            {
                rows = SeedAsync();
                Seeded.Add(fx, rows);
            }

            return rows;
        }
    }

    private async Task<Rows> SeedAsync()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        await using var db = fx.Db.NewContext(); // audited saves: they copy the donor's blood group onto the appointment, as the API's do

        var brightwater = TestRows.Staff("o.brightwater");
        (brightwater.DisplayName, brightwater.Phone) = ("Ottoline Brightwater", "+1-555-0177");
        var crowhurst = TestRows.Staff("lior77");
        (crowhurst.DisplayName, crowhurst.Phone) = ("Lior Crowhurst", "+44 20 7946 0958");

        var eastwood = TestRows.Donor("Selim Eastwood", Gender.Male, BloodType.ONeg);
        eastwood.Phone = "+1-555-0156";
        var mukherjee = TestRows.Donor("Aaliyah Mukherjee", Gender.Female, BloodType.APos);
        mukherjee.Phone = "+1 (555) 019-2834";
        var ozturk = TestRows.Donor("Ayşe Öztürk", Gender.Female, BloodType.BPos);
        ozturk.Phone = null;
        var blackwood = TestRows.Donor("Renée Eastman-Blackwood", Gender.Female, BloodType.OPos);
        blackwood.Phone = "+1-555-0101";

        var patientEastwood = TestRows.Patient("Selim Eastwood", Gender.Male, BloodType.ABNeg);
        patientEastwood.Phone = "+1-555-0164";
        var echevarria = TestRows.Patient("Joaquín Echevarría", Gender.Male, BloodType.ONeg);
        echevarria.Phone = "+1 555 010 7788";
        var novak = TestRows.Patient("Dalia Novak", Gender.Female, BloodType.APos);
        novak.Phone = null;

        var forEastwood = TestRows.Appointment(eastwood, Seed.Slot(3, 10));
        var forMukherjee = TestRows.Appointment(mukherjee, Seed.Slot(-3, 10));
        forMukherjee.Status = AppointmentStatus.Completed;
        var forOzturk = TestRows.Appointment(ozturk, Seed.Slot(-2, 11));
        forOzturk.Status = AppointmentStatus.Completed;
        var forBlackwood = TestRows.Appointment(blackwood, Seed.Slot(4, 9));

        var requestEastwood = TestRows.Request(patientEastwood);
        var requestEchevarria = TestRows.Request(echevarria);
        requestEchevarria.Status = RequestStatus.Fulfilled;
        var requestNovak = TestRows.Request(novak);

        var donationMukherjee = TestRows.Donation(brightwater, forMukherjee);
        var donationOzturk = TestRows.Donation(crowhurst, forOzturk);
        var issueEchevarria = TestRows.Issue(brightwater, requestEchevarria);
        var issueEastwood = TestRows.Issue(crowhurst, requestEastwood);

        db.AddRange(brightwater, crowhurst, eastwood, mukherjee, ozturk, blackwood, patientEastwood, echevarria, novak);
        db.AddRange(forEastwood, forMukherjee, forOzturk, forBlackwood, requestEastwood, requestEchevarria, requestNovak);
        db.AddRange(donationMukherjee, donationOzturk, issueEchevarria, issueEastwood);
        await db.SaveChangesAsync(Ct);

        return new Rows(admin.Client, new()
        {
            ["staff:brightwater"] = brightwater.Id, ["staff:crowhurst"] = crowhurst.Id,
            ["donor:eastwood"] = eastwood.Id, ["donor:mukherjee"] = mukherjee.Id, ["donor:ozturk"] = ozturk.Id, ["donor:blackwood"] = blackwood.Id,
            ["patient:eastwood"] = patientEastwood.Id, ["patient:echevarria"] = echevarria.Id, ["patient:novak"] = novak.Id,
            ["appointment:eastwood"] = forEastwood.Id, ["appointment:mukherjee"] = forMukherjee.Id, ["appointment:ozturk"] = forOzturk.Id, ["appointment:blackwood"] = forBlackwood.Id,
            ["request:eastwood"] = requestEastwood.Id, ["request:echevarria"] = requestEchevarria.Id, ["request:novak"] = requestNovak.Id,
            ["receipt:mukherjee"] = donationMukherjee.Id, ["receipt:ozturk"] = donationOzturk.Id, ["receipt:echevarria"] = issueEchevarria.Id, ["receipt:eastwood"] = issueEastwood.Id,
        });
    }

    /// <summary>The rows of this class of one kind ("donor", "receipt"...) that a search finds, by their short names, in name order.</summary>
    private static async Task<(string[] Found, int Total)> SearchAsync(Rows rows, string route, string kind, string search, string extra = "")
    {
        // "{id}" in a search is the id of the row named after it: "D-{donor:eastwood}".
        foreach (var (name, id) in rows.Ids)
        {
            search = search.Replace("{" + name + "}", id.ToString("0000", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{#" + name + "}", id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        using var response = await rows.Client.GetAsync($"{ApiRoutes.Path(route)}?search={Uri.EscapeDataString(search)}&pageSize=200{extra}", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{route}?search={search}: {(int)response.StatusCode} {text}");
        var json = JsonElement.Parse(text);
        var (items, total) = json.ValueKind == JsonValueKind.Array ? (json, json.GetArrayLength()) : (json.GetProperty("items"), json.GetProperty("total").GetInt32());
        var ids = items.EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
        var found = rows.Ids.Where(r => r.Key.StartsWith(kind + ":", StringComparison.Ordinal) && ids.Contains(r.Value)).Select(r => r.Key[(kind.Length + 1)..]).Order(StringComparer.Ordinal).ToArray();
        return (found, total);
    }

    private static string[] Names(string expected) => [.. expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];

    [Theory]
    // A last name, a part of it, a part inside a word (the three that were not found).
    [InlineData("Eastwood", "eastwood")]
    [InlineData("eastw", "eastwood")]
    [InlineData("ali", "mukherjee")]
    [InlineData("wood", "blackwood,eastwood")]
    [InlineData("kherj", "mukherjee")]
    // A part of a phone number, typed with or without its punctuation, spaces and the leading plus.
    [InlineData("0156", "eastwood")]
    [InlineData("555-0156", "eastwood")]
    [InlineData("5550156", "eastwood")]
    [InlineData("+1-555-0156", "eastwood")]
    [InlineData("+15550156", "eastwood")]
    [InlineData("555 0156", "eastwood")]
    [InlineData("0192834", "mukherjee")]
    [InlineData("(555) 019", "mukherjee")]
    [InlineData("555", "blackwood,eastwood,mukherjee")]
    [InlineData("99", "")] // fewer than three digits are not a part of a phone number, and no donor of this class has the number 99 or a 99 in the name
    // The record code and the bare number.
    [InlineData("D-{donor:eastwood}", "eastwood")]
    [InlineData("d-{#donor:eastwood}", "eastwood")]
    [InlineData("{#donor:mukherjee}", "mukherjee")]
    [InlineData("P-{donor:eastwood}", "")] // a patient's code is no donor's
    // Two words, in either order, each anywhere.
    [InlineData("selim eastwood", "eastwood")]
    [InlineData("eastwood selim", "eastwood")]
    [InlineData("eas sel", "eastwood")]
    [InlineData("eastwood 0156", "eastwood")]
    [InlineData("selim mukherjee", "")]
    [InlineData("eastwood 0192834", "")]
    // Case and accents do not matter, in either direction.
    [InlineData("ayse", "ozturk")]
    [InlineData("AYŞE", "ozturk")]
    [InlineData("ozturk", "ozturk")]
    [InlineData("ÖZTÜRK ayşe", "ozturk")]
    [InlineData("renee", "blackwood")]
    [InlineData("EASTWOOD", "eastwood")]
    // A blood group is only a blood group (no donor's name is searched for "o-", although "Eastman-Blackwood" has an "n-").
    [InlineData("O-", "eastwood")]
    [InlineData("o−", "eastwood")]
    [InlineData("0-", "eastwood")]
    [InlineData("A+", "mukherjee")]
    [InlineData("b+ ayse", "ozturk")]
    [InlineData("o- aaliyah", "")]
    [InlineData("an-", "blackwood")] // not a blood group: a part of a name
    // One or two letters match where a word of the name starts (after a space or a hyphen too), not inside a word.
    [InlineData("se", "eastwood")]
    [InlineData("ea", "blackwood,eastwood")]
    [InlineData("bl", "blackwood")]
    [InlineData("aa", "mukherjee")]
    [InlineData("a", "mukherjee,ozturk")]
    [InlineData("öz", "ozturk")]
    [InlineData("li", "")] // inside "Selim" and "Aaliyah": found from three letters ("ali", "lim")
    [InlineData("lim", "eastwood")]
    [InlineData("oo", "")]
    // Nothing.
    [InlineData("zzzz", "")]
    [InlineData("+", "")]
    [InlineData("+1", "")]
    [InlineData("100%", "")]
    [InlineData("_", "")]
    public async Task Donors_AreFoundByWhatStaffType(string search, string expected)
    {
        var rows = await RowsAsync();
        Assert.Equal(Names(expected), (await SearchAsync(rows, ApiRoutes.Donors, "donor", search)).Found);

        // The donor picker and the command palette ask the lookup, which finds the same donors.
        Assert.Equal(Names(expected), (await SearchAsync(rows, ApiRoutes.LookupDonors, "donor", search)).Found);
    }

    [Theory]
    [InlineData("Eastwood", "eastwood")]
    [InlineData("chev", "echevarria")]
    [InlineData("ova", "novak")]
    [InlineData("no", "novak")]
    [InlineData("ov", "")]
    [InlineData("0164", "eastwood")]
    [InlineData("5550107788", "echevarria")]
    [InlineData("555 010 7788", "echevarria")]
    [InlineData("P-{patient:eastwood}", "eastwood")]
    [InlineData("{#patient:novak}", "novak")]
    [InlineData("D-{patient:eastwood}", "")]
    [InlineData("echevarria joaquin", "echevarria")]
    [InlineData("JOAQUÍN", "echevarria")]
    [InlineData("AB-", "eastwood")]
    [InlineData("a+ dalia", "novak")]
    [InlineData("zzzz", "")]
    public async Task Patients_AreFoundByWhatStaffType(string search, string expected)
    {
        var rows = await RowsAsync();
        Assert.Equal(Names(expected), (await SearchAsync(rows, ApiRoutes.Patients, "patient", search)).Found);
        Assert.Equal(Names(expected), (await SearchAsync(rows, ApiRoutes.LookupPatients, "patient", search)).Found);
    }

    [Theory]
    // By the donor: last name, inside a name, phone, two words, accents.
    [InlineData("Eastwood", "eastwood")]
    [InlineData("ali", "mukherjee")]
    [InlineData("wood", "blackwood,eastwood")]
    [InlineData("se", "eastwood")]
    [InlineData("oo", "")]
    [InlineData("0156", "eastwood")]
    [InlineData("+1 (555) 019-2834", "mukherjee")]
    [InlineData("eastwood selim", "eastwood")]
    [InlineData("ozturk ayse", "ozturk")]
    [InlineData("eastwood aaliyah", "")]
    // By the appointment's own code and number; a donor's code is not an appointment's.
    [InlineData("A-{appointment:mukherjee}", "mukherjee")]
    [InlineData("{#appointment:ozturk}", "ozturk")]
    [InlineData("D-{donor:eastwood}", "")]
    // By the donor's blood group, alone and with a name.
    [InlineData("O-", "eastwood")]
    [InlineData("o+ renee", "blackwood")]
    [InlineData("zzzz", "")]
    public async Task Appointments_AreFoundThroughTheirDonor_AndByTheirCode(string search, string expected)
    {
        var rows = await RowsAsync();
        Assert.Equal(Names(expected), (await SearchAsync(rows, ApiRoutes.Appointments, "appointment", search)).Found);
    }

    [Theory]
    [InlineData("Eastwood", "eastwood")]
    [InlineData("chev", "echevarria")]
    [InlineData("0164", "eastwood")]
    [InlineData("joaquin echevarria", "echevarria")]
    [InlineData("echevarria dalia", "")]
    [InlineData("R-{request:echevarria}", "echevarria")]
    [InlineData("{#request:novak}", "novak")]
    [InlineData("P-{patient:eastwood}", "")]
    [InlineData("AB-", "eastwood")]
    [InlineData("o- joaquín", "echevarria")]
    [InlineData("zzzz", "")]
    public async Task Requests_AreFoundThroughTheirPatient_AndByTheirCode(string search, string expected)
    {
        var rows = await RowsAsync();
        Assert.Equal(Names(expected), (await SearchAsync(rows, ApiRoutes.Requests, "request", search)).Found);
    }

    [Theory]
    // By the donor or the patient.
    [InlineData("Mukherjee", "", "mukherjee")]
    [InlineData("ali", "", "mukherjee")]
    [InlineData("echev", "", "echevarria")]
    [InlineData("ozturk", "", "ozturk")]
    [InlineData("0164", "", "eastwood")]
    [InlineData("019-2834", "", "mukherjee")]
    // By the staff member who issued it.
    [InlineData("Brightwater", "", "echevarria,mukherjee")]
    [InlineData("crow", "", "eastwood,ozturk")]
    [InlineData("br", "", "echevarria,mukherjee")] // one or two letters: where a word of a name starts
    [InlineData("mu", "", "mukherjee")]
    [InlineData("ri", "", "")] // inside "Brightwater" and "Echevarría"
    // Two words may match different people of the receipt, in either order.
    [InlineData("brightwater mukherjee", "", "mukherjee")]
    [InlineData("mukherjee brightwater", "", "mukherjee")]
    [InlineData("ottoline joaquin", "", "echevarria")]
    [InlineData("aaliyah mukherjee", "", "mukherjee")]
    [InlineData("brightwater eastwood", "", "")]
    // By its code, its number and its blood group; with the list's own filters.
    [InlineData("RC-{receipt:ozturk}", "", "ozturk")]
    [InlineData("{#receipt:echevarria}", "", "echevarria")]
    [InlineData("B+", "", "ozturk")]
    [InlineData("ab- crowhurst", "", "eastwood")]
    [InlineData("Brightwater", "&kind=Issue", "echevarria")]
    [InlineData("Brightwater", "&kind=Donation", "mukherjee")]
    [InlineData("Brightwater", "&bloodType=APos", "mukherjee")]
    [InlineData("zzzz", "", "")]
    public async Task Receipts_AreFoundThroughTheirDonorPatientAndStaff_AndByTheirCode(string search, string filter, string expected)
    {
        var rows = await RowsAsync();
        var (found, total) = await SearchAsync(rows, ApiRoutes.Receipts, "receipt", search, filter);
        Assert.Equal(Names(expected), found);

        // The tabs' counts count the same receipts (they have no kind filter; a search written with a row's id is left out here).
        if (filter.Length == 0 && !search.Contains('{', StringComparison.Ordinal))
        {
            using var response = await rows.Client.GetAsync($"{ApiRoutes.Path(ApiRoutes.ReceiptCounts)}?search={Uri.EscapeDataString(search)}", Ct);
            var counts = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(total, counts.GetProperty("all").GetInt32());
            Assert.Equal(total, counts.GetProperty("donations").GetInt32() + counts.GetProperty("issues").GetInt32());
        }
    }

    [Theory]
    [InlineData("Brightwater", "brightwater")]
    [InlineData("right", "brightwater")]
    [InlineData("br", "brightwater")] // one or two letters: the start of a word of the name, of the user name or of a part of it after a dot
    [InlineData("li", "crowhurst")]
    [InlineData("ri", "")]
    [InlineData("brightwater ottoline", "brightwater")]
    [InlineData("lior77", "crowhurst")] // the user name
    [InlineData("o.bright", "brightwater")]
    [InlineData("0177", "")] // a staff member's phone is private (the list does not carry it, only an administrator sees it): it is not searched
    [InlineData("7946 0958", "")]
    [InlineData("+1-555-0177", "")]
    [InlineData("S-{staff:crowhurst}", "crowhurst")]
    [InlineData("{#staff:brightwater}", "brightwater")]
    [InlineData("ottoline crowhurst", "")]
    [InlineData("zzzz", "")]
    public async Task Staff_AreFoundByNameUserNameAndCode_AndNeverByTheirPrivatePhone(string search, string expected)
    {
        var rows = await RowsAsync();
        Assert.Equal(Names(expected), (await SearchAsync(rows, ApiRoutes.Staff, "staff", search)).Found);
        Assert.Equal(Names(expected), (await SearchAsync(rows, ApiRoutes.LookupStaff, "staff", search)).Found);
    }

    [Fact]
    public async Task ThePickersOfAppointmentsAndRequests_FindTheOpenOnesByTheSameRule()
    {
        var rows = await RowsAsync();

        // The lookups offer what can still be acted on: pending appointments and pending requests.
        Assert.Equal(["eastwood"], (await SearchAsync(rows, ApiRoutes.LookupAppointments, "appointment", "Eastwood")).Found);
        Assert.Equal(["blackwood", "eastwood"], (await SearchAsync(rows, ApiRoutes.LookupAppointments, "appointment", "wood")).Found);
        Assert.Equal(["eastwood"], (await SearchAsync(rows, ApiRoutes.LookupAppointments, "appointment", "0156")).Found);
        Assert.Empty((await SearchAsync(rows, ApiRoutes.LookupAppointments, "appointment", "ali")).Found); // Aaliyah's appointment is completed
        Assert.Equal(["eastwood"], (await SearchAsync(rows, ApiRoutes.LookupRequests, "request", "eastwood selim")).Found);
        Assert.Equal(["eastwood"], (await SearchAsync(rows, ApiRoutes.LookupRequests, "request", "0164")).Found);
        Assert.Equal(["novak"], (await SearchAsync(rows, ApiRoutes.LookupRequests, "request", "ova")).Found);
        Assert.Empty((await SearchAsync(rows, ApiRoutes.LookupRequests, "request", "chev")).Found); // Joaquín's request is fulfilled
    }

    [Fact]
    public async Task ASearchWithNoMatch_AnswersAnEmptyPageWithTotalZero_OnEveryList()
    {
        var rows = await RowsAsync();
        foreach (var route in new[] { ApiRoutes.Donors, ApiRoutes.Patients, ApiRoutes.Appointments, ApiRoutes.Requests, ApiRoutes.Receipts, ApiRoutes.Staff })
        {
            using var response = await rows.Client.GetAsync($"{ApiRoutes.Path(route)}?search=zzzz%20qqqq", Ct);
            var json = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, json.GetProperty("total").GetInt32());
            Assert.Empty(json.GetProperty("items").EnumerateArray());
        }
    }

    [Fact]
    public async Task ASearchMayHaveFiveWords_AndMoreIsRefusedByName()
    {
        var rows = await RowsAsync();
        foreach (var route in new[] { ApiRoutes.Donors, ApiRoutes.Patients, ApiRoutes.Appointments, ApiRoutes.Requests, ApiRoutes.Receipts, ApiRoutes.Staff, ApiRoutes.LookupDonors })
        {
            using var five = await rows.Client.GetAsync($"{ApiRoutes.Path(route)}?search={Uri.EscapeDataString("a b c d e")}", Ct);
            Assert.Equal(HttpStatusCode.OK, five.StatusCode);

            // A phone number typed with spaces is one word, however many groups it has.
            using var phone = await rows.Client.GetAsync($"{ApiRoutes.Path(route)}?search={Uri.EscapeDataString("selim a. eastwood +1 555 010 01 56 o-")}", Ct);
            Assert.Equal(HttpStatusCode.OK, phone.StatusCode);

            using var six = await rows.Client.GetAsync($"{ApiRoutes.Path(route)}?search={Uri.EscapeDataString("a b c d e f")}", Ct);
            var problem = await six.Content.ReadAsStringAsync(Ct);
            Assert.Equal(HttpStatusCode.BadRequest, six.StatusCode);
            Assert.Contains("search", problem, StringComparison.Ordinal);
            Assert.Contains($"{SearchQueries.MaxWords} words", problem, StringComparison.Ordinal);
        }
    }

    private const string PlanHint = "DISABLE_OPTIMIZER_ROWGOAL";

    /// <summary>The statements that carry what was typed: one of their parameters holds the word (the pattern a name or a phone is matched against).</summary>
    private static List<(string Sql, List<SqlParameter> Parameters)> Carrying(List<(string Sql, List<SqlParameter> Parameters)> statements, string word) =>
        [.. statements.Where(s => s.Parameters.Any(p => p.Value is string text && text.Contains(word, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>
    /// Judged on the statements of the request itself (StatementCapture.OfAsync), and of those on the ones that search: the statements that carry
    /// the word typed are sent with the hint, and nothing else is. A request runs more than its endpoint's statements (the identity layer's session
    /// and daily demo checks, see the next test), so "every statement of a search request has the hint" was never true; it only held while another
    /// test of the class had made the first signed-in request.
    /// </summary>
    [Fact]
    public async Task EveryStatementWithASearch_IsSentWithTheSearchHint_AndAListWithoutOneIsNot()
    {
        var rows = await RowsAsync();
        foreach (var route in new[] { ApiRoutes.Donors, ApiRoutes.Patients, ApiRoutes.Appointments, ApiRoutes.Requests, ApiRoutes.Receipts, ApiRoutes.Staff, ApiRoutes.LookupDonors, ApiRoutes.LookupRequests, ApiRoutes.ReceiptCounts, ApiRoutes.AppointmentViews, ApiRoutes.RequestViews })
        {
            var (searched, withSearch) = await fx.Statements.OfAsync(rows.Client, ApiRoutes.Path(route) + "?search=eastwood", Ct);
            using (searched)
            {
                Assert.Equal(HttpStatusCode.OK, searched.StatusCode);
            }

            var searching = Carrying(withSearch, "eastwood");
            Assert.True(searching.Count > 0, $"{route}: no statement of the request carries the word typed");
            Assert.All(searching, s => Assert.Contains(PlanHint, s.Sql, StringComparison.Ordinal));
            Assert.All(withSearch.Except(searching), s => Assert.DoesNotContain(PlanHint, s.Sql, StringComparison.Ordinal));

            var (listed, withoutSearch) = await fx.Statements.OfAsync(rows.Client, ApiRoutes.Path(route), Ct);
            using (listed)
            {
                Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            }

            Assert.True(withoutSearch.Count > 0, $"{route}: the request sent no statement");
            Assert.All(withoutSearch, s => Assert.DoesNotContain(PlanHint, s.Sql, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The identity layer in front of every signed-in request asks once per UTC day whether the demo data is due for its daily reset
    /// (IdentityMiddleware, DemoDirectory.EnsureDailyResetAsync), inside whichever request comes first that day: also a search. Its statements (the
    /// DemoState row, the server's clock) are then really sent inside the search request. They are not search statements: they get no hint, the
    /// search's own do, the search answers as on any other request, and the next request of the day does not ask again.
    /// </summary>
    [Fact]
    public async Task TheDailyDemoCheck_RunsInsideTheFirstSignedInRequestOfADay_AlsoWhenItIsASearch_AndIsNotASearchStatement()
    {
        var rows = await RowsAsync();
        var day = TimeSpan.FromDays(1);
        fx.Clock.Advance(day); // tomorrow: nobody has signed in yet today (the fixture keeps the reset itself from being due)
        try
        {
            var staff = await fx.AddStaffAsync(Role.Staff); // a session of that day
            var url = $"{ApiRoutes.Path(ApiRoutes.Donors)}?search=eastwood&pageSize=200";

            var (first, statements) = await fx.Statements.OfAsync(staff.Client, url, Ct);
            using (first)
            {
                var body = await first.Content.ReadAsStringAsync(Ct);
                Assert.True(first.IsSuccessStatusCode, body);
                Assert.Contains(JsonElement.Parse(body).GetProperty("items").EnumerateArray(), d => d.GetProperty("id").GetInt32() == rows["donor:eastwood"]);
            }

            var daily = statements.Where(s => StatementCapture.IsIdentityCheck(s.Sql)).ToList();
            Assert.Contains(daily, s => s.Sql.Contains("[DemoState]", StringComparison.Ordinal));
            Assert.Contains(daily, s => s.Sql.Contains("SYSUTCDATETIME()", StringComparison.Ordinal));
            Assert.All(daily, s => Assert.DoesNotContain(PlanHint, s.Sql, StringComparison.Ordinal));
            var searching = Carrying(statements, "eastwood");
            Assert.NotEmpty(searching);
            Assert.All(searching, s => Assert.Contains(PlanHint, s.Sql, StringComparison.Ordinal));

            var (second, again) = await fx.Statements.OfAsync(staff.Client, url, Ct);
            using (second)
            {
                Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            }

            Assert.DoesNotContain(again, s => s.Sql.Contains("SYSUTCDATETIME()", StringComparison.Ordinal) || s.Sql.Contains("[DemoState]", StringComparison.Ordinal));
            Assert.NotEmpty(Carrying(again, "eastwood"));
        }
        finally
        {
            fx.Clock.Advance(-day); // the other tests' sessions are of today
        }
    }

    /// <summary>
    /// A number alone is ambiguous by the rule, and a test that needs ONE record must not search by it: digits are the record's own number, and also a
    /// word of a name ("Unknown Male 3") and, from three digits, a part of a phone. The receipt's code names the record and nothing else.
    /// (RequestStockTests searched receipts by a bare id and asserted a single hit; it found a second receipt whenever another test's donor was
    /// called "... 7" while the receipt was number 7.)
    /// </summary>
    [Fact]
    public async Task ANumberAlone_FindsTheRecordOfThatNumber_AndTheNamesThatHoldIt_TheCodeFindsTheRecordOnly()
    {
        var rows = await RowsAsync();
        var number = rows["receipt:mukherjee"];
        await using var db = fx.Db.NewContext();
        var staff = await db.Staff.FindAsync([rows["staff:crowhurst"]], Ct);
        var namesake = TestRows.Donor($"Unknown Male {number}", Gender.Male, BloodType.BNeg);
        namesake.Phone = null;
        var visit = TestRows.Appointment(namesake, Seed.Slot(-5, 12));
        visit.Status = AppointmentStatus.Completed;
        var other = TestRows.Donation(staff!, visit);
        db.AddRange(namesake, visit, other);
        await db.SaveChangesAsync(Ct);
        Assert.NotEqual(number, other.Id);

        async Task<HashSet<int>> ReceiptsAsync(string search)
        {
            using var response = await rows.Client.GetAsync($"{ApiRoutes.Path(ApiRoutes.Receipts)}?search={Uri.EscapeDataString(search)}&pageSize=200", Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.IsSuccessStatusCode, text);
            return [.. JsonElement.Parse(text).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32())];
        }

        var byNumber = await ReceiptsAsync(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(number, byNumber); // the receipt of that number
        Assert.Contains(other.Id, byNumber); // and the receipt of the donor whose name holds it

        Assert.Equal([number], await ReceiptsAsync($"RC-{number}"));
        Assert.Equal([number], await ReceiptsAsync($"rc-{number:0000}")); // as the lists print it, in either case
    }
}
