using System.Globalization;
using System.Text;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Domain;

namespace Lifestream.Api.Tests.Data;

/// <summary>
/// DB-05 through the REAL API: every list, lookup, stock and dashboard request below goes over HTTP into the API hosted on a 100x
/// database on a real SQL Server. An EF command interceptor records the SQL the API sends (with its parameters); each statement is
/// replayed under <c>SET STATISTICS XML</c> and the actual plan must contain no Table Scan or Clustered Index Scan on a table with more
/// than 5,000 rows. The matrix uses COMMON filter values (Fulfilled requests, Completed appointments, blood type OPos), 1- and 2-letter
/// name searches, every documented sort, page 1 and a deep page. Each list request must also be one database call (API-22).
/// Set LIFESTREAM_PLAN_CAPTURE to a file path to write one line per request (operators, pages read, SQL).
/// The cases are split over this class and <see cref="ApiPlanMatrixPart2Tests"/> to <see cref="ApiPlanMatrixPart4Tests"/>, each with its own copy of
/// the 100x database and host, so the four parts run at the same time (the cases of one class run one after the other).
/// </summary>
public sealed class ApiPlanMatrixTests(ScaleApiFixture fx) : IClassFixture<ScaleApiFixture>
{
    /// <summary>How many classes share the cases.</summary>
    public const int Parts = 4;

    /// <summary>Every <see cref="Parts"/>-th case of <see cref="Cases"/>, starting at <paramref name="part"/>.</summary>
    public static TheoryData<string> CasesOfPart(int part)
    {
        var data = new TheoryData<string>();
        foreach (var (row, index) in Cases().Select((row, index) => (row, index)))
        {
            if (index % Parts == part)
            {
                data.Add(row.Data);
            }
        }

        return data;
    }

    private const int BigTableRows = 5_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record ListShape(string Route, string[] Sorts, string[] Filters, int DeepPage);

    private static readonly string[] Letters = ["a", "A", "m", "S", "Ay", "Al", "ma", "12"];

    private static readonly Dictionary<string, ListShape> Lists = new()
    {
        ["donors"] = new(ApiRoutes.Donors, ["name", "-name", "lastDonated", "-lastDonated"], [
            "", "bloodType=OPos", "bloodType=ONeg", .. Letters.Select(l => "search=" + l), "search=a&bloodType=OPos", "search=Al&bloodType=APos",
            "eligible=true", "eligible=false", "lastDonated=Within3Months", "lastDonated=Within12Months", "lastDonated=Over12Months", "lastDonated=Never",
            "eligible=true&bloodType=OPos", "search=a&eligible=true&lastDonated=Over12Months",
            "search=eastwood", "search=0156", "search=ali&bloodType=OPos", "search=eastwood%20selim&eligible=true",
        ], 60),
        ["patients"] = new(ApiRoutes.Patients, ["name", "-name"], ["", "bloodType=OPos", .. Letters.Select(l => "search=" + l), "search=a&bloodType=OPos", "search=eastwood", "search=0156&bloodType=OPos"], 60),
        ["appointments"] = new(
            ApiRoutes.Appointments,
            ["slot", "-slot", "placed", "-placed"],
            [
                "",
                "status=Completed",
                "status=Pending",
                "status=Cancelled",
                "status=NoShow",
                "bloodType=OPos",
                "bloodType=ONeg",
                "status=Completed&bloodType=OPos",
                "status=Pending&bloodType=ABNeg",
                "donorId={donor}",
                "donorId={donor}&status=Completed",
                "fromUtc={from}&toUtc={to}",
                "fromUtc={from}",
                "toUtc={to}",
                "status=Completed&fromUtc={from}&toUtc={to}",
                "bloodType=OPos&fromUtc={from}&toUtc={to}",
                "status=Completed&bloodType=OPos&fromUtc={from}&toUtc={to}",
                .. Letters.Select(l => "search=" + l),
                "search=a&status=Completed&bloodType=OPos",
                "view=Today",
                "view=Today&utcOffsetMinutes=-300",
                "view=Upcoming&utcOffsetMinutes=-300",
                "view=Past&utcOffsetMinutes=-300",
                "view=Cancelled",
                "view=Past&bloodType=OPos",
                "view=Upcoming&donorId={donor}",
                "search=a&view=Past&utcOffsetMinutes=-300",
                "search=eastwood",
                "search=0156&status=Completed",
                "search=ali&view=Past&utcOffsetMinutes=-300",
                "search=eastwood%20selim&bloodType=OPos",
                "search=O-&status=Completed",
                "search=a%20e%20i%20o%20n",
                "search=a%20selim%20eastwood",
            ],
            500),
        ["requests"] = new(
            ApiRoutes.Requests,
            ["placed", "-placed", "requiredBy", "-requiredBy"],
            [
                "",
                "status=Fulfilled",
                "status=Pending",
                "status=Cancelled",
                "bloodType=OPos",
                "bloodType=ONeg",
                "status=Fulfilled&bloodType=OPos",
                "status=Fulfilled&bloodType=APos",
                "status=Pending&bloodType=OPos",
                "status=Cancelled&bloodType=OPos",
                "patientId={patient}",
                "patientId={patient}&status=Fulfilled",
                "fromUtc={from}&toUtc={to}",
                "status=Fulfilled&fromUtc={from}&toUtc={to}",
                .. Letters.Select(l => "search=" + l),
                "search=a&status=Fulfilled&bloodType=OPos",
                "view=Open",
                "view=DueSoon&utcOffsetMinutes=-300",
                "view=Fulfilled",
                "view=Cancelled",
                "view=Open&bloodType=OPos",
                "view=DueSoon&patientId={patient}",
                "search=a&view=Open",
                "search=eastwood",
                "search=0156&status=Fulfilled",
                "search=ali&view=Fulfilled",
                "search=O-&status=Fulfilled",
            ],
            250),
        ["receipts"] = new(
            ApiRoutes.Receipts,
            ["date", "-date"],
            [
                "",
                "kind=Donation",
                "kind=Issue",
                "bloodType=OPos",
                "kind=Donation&bloodType=OPos",
                "kind=Issue&bloodType=OPos",
                "fromUtc={from}&toUtc={to}",
                "kind=Donation&fromUtc={from}&toUtc={to}",
                "bloodType=OPos&fromUtc={from}&toUtc={to}",
                .. Letters.Select(l => "search=" + l),
                "search=a&kind=Donation&bloodType=OPos",
                "search=Al&fromUtc={from}&toUtc={to}",
                "search=eastwood",
                "search=0156&kind=Donation",
                "search=ali&kind=Issue&bloodType=OPos",
                "search=eastwood%20selim&fromUtc={from}&toUtc={to}",
                "search=O-&kind=Donation",
                "search=a%20e%20i%20o%20n", // the most words a search may have: the largest statement there is
                "search=a%20selim%20eastwood",
            ],
            500),
    };

    /// <summary>
    /// The terms the verifier found scanning (a phone prefix every seeded phone starts with, and what int.TryParse reads as an id), single letters, a whole phone and a
    /// digit; each is sent at page 1, a mid page and a page past the end, at the default page size and at 200 (a past-end page is its own request path).
    /// </summary>
    private static readonly string[] SearchTerms =
    [
        "+", "+1", "+1-555", "+1-555-0101", "a", "e", "o", "z", "1",

        // What a member of staff types: a last name, a part of a name, a part of a phone number (four digits, then three that every seeded phone has),
        // two words in the other order, a blood group, record codes, and a text nobody has. "a" above matches a fifth of the names, "555" every phone.
        "eastwood", "ali", "0156", "555", "eastwood selim", "O-", "D-0041", "RC-0041", "zzzz",
    ];

    private static readonly string[] SearchVariants = ["p1", "mid", "past", "s200p1", "s200mid", "s200past"];

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var list in new[] { "donors", "patients", "appointments", "requests", "receipts" })
        {
            foreach (var term in SearchTerms)
            {
                foreach (var variant in SearchVariants)
                {
                    data.Add($"search-matrix|{list}|{term}|{variant}");
                }
            }
        }

        foreach (var (list, shape) in Lists)
        {
            foreach (var filter in shape.Filters)
            {
                foreach (var sort in shape.Sorts)
                {
                    data.Add($"{list}|{filter}|{sort}|1");
                    data.Add($"{list}|{filter}|{sort}|deep");
                }
            }
        }

        data.Add("page-size-200|donors");
        data.Add("page-size-200|appointments");
        data.Add("page-size-200|requests");
        data.Add("page-size-200|receipts");
        foreach (var lookup in new[] { "donors", "patients", "staff", "appointments", "requests" })
        {
            foreach (var term in new[] { "a", "A", "Al", "1", "eastwood", "ali", "0156", "O-" })
            {
                data.Add($"lookup|{lookup}|{term}");
            }
        }

        data.Add("staff|none");
        data.Add("staff|search=a");
        data.Add("staff|search=eastwood");
        data.Add("staff|search=0156");
        data.Add("staff|search=eastwood%20selim");
        data.Add("stock|none");
        data.Add("dashboard|default");
        data.Add("dashboard|36-months");
        data.Add("dashboard|offset");
        data.Add("eligibility|donor");
        data.Add("slots|today");
        data.Add("stock-bags|OPos");
        data.Add("stock-bags|ABNeg");
        data.Add("receipt-counts|");
        data.Add("receipt-counts|bloodType=OPos");
        data.Add("receipt-counts|search=a");
        data.Add("receipt-counts|search=Al&bloodType=APos");
        data.Add("receipt-counts|search=eastwood");
        data.Add("receipt-counts|search=0156&bloodType=OPos");
        data.Add("receipt-counts|search=555");
        data.Add("receipt-counts|fromUtc={from}&toUtc={to}");

        // A saved view opened without a sort (the open request views then list the request needed first), and the counts of the saved views.
        foreach (var view in Enum.GetNames<AppointmentView>())
        {
            data.Add($"view-default|appointments|view={view}&utcOffsetMinutes=-300");
        }

        foreach (var view in Enum.GetNames<RequestView>())
        {
            data.Add($"view-default|requests|view={view}&utcOffsetMinutes=-300");
        }

        foreach (var filter in new[] { "", "utcOffsetMinutes=-300", "bloodType=OPos", "search=a", "search=Al&bloodType=APos", "donorId={donor}", "search=eastwood", "search=0156", "search=555" })
        {
            data.Add("appointment-views|" + filter);
        }

        foreach (var filter in new[] { "", "utcOffsetMinutes=-300", "bloodType=OPos", "search=a", "search=Al&bloodType=APos", "patientId={patient}", "search=eastwood", "search=0156", "search=555" })
        {
            data.Add("request-views|" + filter);
        }

        return data;
    }

    [Fact]
    public void Matrix_CoversEveryFilterAndSortOfEveryQueryRecord()
    {
        var records = new Dictionary<string, (Type Type, string[] Properties)>
        {
            ["donors"] = (typeof(DonorQuery), ["Search", "BloodType", "Eligible", "LastDonated"]),
            ["patients"] = (typeof(PatientQuery), ["Search", "BloodType"]),
            ["appointments"] = (typeof(AppointmentQuery), ["Search", "DonorId", "Status", "BloodType", "FromUtc", "ToUtc", "View", "UtcOffsetMinutes"]),
            ["requests"] = (typeof(RequestQuery), ["Search", "PatientId", "Status", "BloodType", "View", "UtcOffsetMinutes"]),
            ["receipts"] = (typeof(ReceiptQuery), ["Search", "Kind", "BloodType", "FromUtc", "ToUtc"]),
        };
        foreach (var (list, (type, expected)) in records)
        {
            var actual = type.GetProperties().Select(p => p.Name).Where(n => n is not ("Page" or "PageSize" or "Sort")).Order().ToArray();
            Assert.Equal(expected.Order().ToArray(), actual);
            var filters = Lists[list].Filters;
            Assert.All(expected, name => Assert.Contains(filters, f => f.Contains(char.ToLowerInvariant(name[0]) + name[1..], StringComparison.Ordinal)));
        }

        // Every value of the new filters is in the matrix: each saved view, each donation recency, eligible and not.
        Assert.All(Enum.GetNames<AppointmentView>(), v => Assert.Contains(Lists["appointments"].Filters, f => f.Contains("view=" + v, StringComparison.Ordinal)));
        Assert.All(Enum.GetNames<RequestView>(), v => Assert.Contains(Lists["requests"].Filters, f => f.Contains("view=" + v, StringComparison.Ordinal)));
        Assert.All(Enum.GetNames<DonationRecency>(), v => Assert.Contains("lastDonated=" + v, Lists["donors"].Filters));
        Assert.Contains("eligible=true", Lists["donors"].Filters);
        Assert.Contains("eligible=false", Lists["donors"].Filters);

        // The common values the earlier verification found scanning must be in the matrix.
        Assert.Contains("status=Fulfilled&bloodType=OPos", Lists["requests"].Filters);
        Assert.Contains("status=Completed&bloodType=OPos", Lists["appointments"].Filters);
        Assert.Contains("search=a", Lists["receipts"].Filters);
        Assert.Contains("search=Ay", Lists["receipts"].Filters);
    }

    [Fact]
    public async Task RanAgainstTheScaleDataset_OnARealSqlServer_ThroughTheApi()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion, StringComparison.Ordinal);
        Assert.Equal(27_000, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Receipt", Ct));
        Assert.Equal(30_000, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Appointment", Ct));
        Assert.Equal(15_000, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Request", Ct));

        // The data really holds the common values the matrix filters on (a rare value would hide a scan).
        Assert.True(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Request WHERE Status = 'Fulfilled'", Ct) > 5_000);
        Assert.True(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Appointment WHERE Status = 'Completed'", Ct) > 5_000);
        Assert.True(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Appointment WHERE DonorBloodType = 'OPos'", Ct) > 5_000);
    }

    [Theory]
    [MemberData(nameof(CasesOfPart), 0)]
    public Task ApiRequest_AtScale_UsesNoTableScan_AndOneDatabaseCall(string @case) => CheckAsync(fx, @case);

    /// <summary>One case of the matrix against the given fixture's host and database.</summary>
    internal static async Task CheckAsync(ScaleApiFixture fx, string @case)
    {
        var admin = await AdminAsync(fx);
        var sizes = (await fx.Db.RowsAsync(
            "SELECT t.name, SUM(p.rows) FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) GROUP BY t.name", Ct))
            .ToDictionary(r => (string)r[0]!, r => Convert.ToInt64(r[1], CultureInfo.InvariantCulture));
        var url = await UrlAsync(fx, @case, admin.Client);

        var (response, sent) = await fx.Statements.OfAsync(admin.Client, url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {body}");
        // Only this request's statements are here (StatementCapture). Of those, the identity layer's are not the data call of the click: the session
        // check of a token whose cached answer has expired and, on the first signed-in request of a day, the demo-reset check (tiny tables and the
        // server's clock). Every other statement is.
        var statements = sent.Where(s => !StatementCapture.IsIdentityCheck(s.Sql)).ToList();
        Assert.NotEmpty(statements);

        var plans = new List<ExecutedPlan>();
        var parameterText = new List<string>();
        foreach (var (sql, parameters) in statements)
        {
            plans.Add(await PlanHelper.ReplayAsync(fx.Db.ConnectionString, sql, parameters, Ct));
            parameterText.Add(string.Join(", ", parameters.Select(p => $"{p.ParameterName} {p.SqlDbType}({p.Size})={p.Value}")));
        }

        Record(url, plans, parameterText);

        var kind = @case.Split('|')[0];
        if (kind == "search-matrix")
        {
            // Receipts: every variant, a page past the end included, is one database call (the total drives the statement; there is no fallback count).
            // The other lists keep their fallback count for a page past the end, which must also seek (checked below), but are one call in range.
            var parts = @case.Split('|');
            if (parts[1] == "receipts" || parts[3] is "p1" or "s200p1")
            {
                Assert.True(plans.Count == 1, $"{url}: {plans.Count} database calls, expected 1:\n{string.Join("\n---\n", plans.Select(p => p.Sql))}");
            }
        }

        if (kind is "donors" or "patients" or "appointments" or "requests" or "receipts" or "page-size-200" or "staff" or "view-default" or "appointment-views" or "request-views")
        {
            // API-22: a filter click is one database call (page and total together); the deep pages here are inside the data.
            Assert.True(plans.Count == 1, $"{url}: {plans.Count} database calls, expected 1:\n{string.Join("\n---\n", plans.Select(p => p.Sql))}");
        }

        foreach (var plan in plans)
        {
            var scans = plan.TableScans()
                .Select(t => t.Trim('[', ']'))
                .Where(t => sizes.TryGetValue(t, out var rows) && rows > BigTableRows)
                .ToList();
            Assert.True(scans.Count == 0, $"{url}: scans {string.Join(", ", scans)} on a table over {BigTableRows} rows\n{plan.Sql}");
        }
    }

    private static readonly SemaphoreSlim AdminLock = new(1, 1);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScaleApiFixture, TestUser> Admins = [];

    /// <summary>One signed-in admin per fixture for its part of the matrix (the fixture is shared by the class).</summary>
    private static async Task<TestUser> AdminAsync(ScaleApiFixture fx)
    {
        await AdminLock.WaitAsync(Ct);
        try
        {
            if (!Admins.TryGetValue(fx, out var admin))
            {
                admin = await fx.AddStaffAsync(Role.Admin, "planadmin");

                // The first request of a session also checks the session (refresh-token row, demo state); that is not a filter click.
                (await admin.Client.GetAsync(ApiRoutes.Path(ApiRoutes.Me), Ct)).EnsureSuccessStatusCode();
                Admins.Add(fx, admin);
            }

            return admin;
        }
        finally
        {
            AdminLock.Release();
        }
    }

    private static async Task<string> UrlAsync(ScaleApiFixture fx, string @case, HttpClient client)
    {
        var parts = @case.Split('|');
        var from = DateTime.UtcNow.AddDays(-120).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = DateTime.UtcNow.AddDays(-60).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string Fill(string text) => text
            .Replace("{from}", from, StringComparison.Ordinal)
            .Replace("{to}", to, StringComparison.Ordinal)
            .Replace("{donor}", "1", StringComparison.Ordinal)
            .Replace("{patient}", "1", StringComparison.Ordinal);

        switch (parts[0])
        {
            case "search-matrix":
                var term = "search=" + Uri.EscapeDataString(parts[2]);
                return ApiRoutes.Path(Lists[parts[1]].Route) + "?" + term + parts[3] switch
                {
                    "p1" => "",
                    "mid" => "&page=10",
                    "past" => "&page=100000",
                    "s200p1" => "&pageSize=200&page=1",
                    "s200mid" => "&pageSize=200&page=60",
                    _ => "&pageSize=200&page=100000",
                };
            case "page-size-200":
                return ApiRoutes.Path(Lists[parts[1]].Route) + "?pageSize=200&page=2";
            case "lookup":
                return ApiRoutes.Path("lookups/" + parts[1]) + "?search=" + Uri.EscapeDataString(parts[2]);
            case "staff":
                return ApiRoutes.Path(ApiRoutes.Staff) + (parts[1] == "none" ? "?pageSize=200" : "?pageSize=200&" + parts[1]);
            case "stock":
                return ApiRoutes.Path(ApiRoutes.Stock);
            case "dashboard":
                return ApiRoutes.Path(ApiRoutes.Dashboard) + parts[1] switch
                {
                    "36-months" => "?from=2023-12-01",
                    "offset" => "?utcOffsetMinutes=-300",
                    _ => "",
                };
            case "eligibility":
                var donorId = await fx.Db.ScalarAsync<int>("SELECT MIN(Id) FROM Donor", Ct);
                return ApiRoutes.Path(ApiRoutes.DonorEligibility, donorId);
            case "slots":
                return ApiRoutes.Path(ApiRoutes.AppointmentSlots) + "?date=" + DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case "stock-bags":
                return ApiRoutes.Path(ApiRoutes.StockBags) + "?bloodType=" + parts[1];
            case "receipt-counts":
                return ApiRoutes.Path(ApiRoutes.ReceiptCounts) + (parts[1].Length == 0 ? "" : "?" + Fill(parts[1]));
            case "view-default":
                return ApiRoutes.Path(Lists[parts[1]].Route) + "?" + parts[2];
            case "appointment-views":
                return ApiRoutes.Path(ApiRoutes.AppointmentViews) + (parts[1].Length == 0 ? "" : "?" + Fill(parts[1]));
            case "request-views":
                return ApiRoutes.Path(ApiRoutes.RequestViews) + (parts[1].Length == 0 ? "" : "?" + Fill(parts[1]));
            default:
                var shape = Lists[parts[0]];
                var query = new List<string>();
                if (parts[1].Length > 0)
                {
                    query.Add(Fill(parts[1]));
                }

                query.Add("sort=" + Uri.EscapeDataString(parts[2]));
                var baseUrl = ApiRoutes.Path(shape.Route) + "?" + string.Join('&', query);
                if (parts[3] != "deep")
                {
                    return baseUrl + "&page=" + parts[3];
                }

                // The deep page: page 261 at most, but never past the end of this filter's results (a page past the end is a different,
                // two-call path that is not a filter click).
                var first = await client.GetAsync(baseUrl + "&page=1", Ct);
                var total = System.Text.Json.JsonElement.Parse(await first.Content.ReadAsStringAsync(Ct)).GetProperty("total").GetInt32();
                first.Dispose();
                var lastPage = Math.Max(1, (total + 49) / 50);
                return baseUrl + "&page=" + Math.Min(shape.DeepPage, lastPage);
        }
    }

    private static readonly object Gate = new();

    private static void Record(string url, List<ExecutedPlan> plans, List<string> parameterText)
    {
        var path = Environment.GetEnvironmentVariable("LIFESTREAM_PLAN_CAPTURE");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var sb = new StringBuilder();
        sb.Append("API ").Append(url).Append(" | statements=").Append(plans.Count).Append(" | reads=[")
            .Append(string.Join(',', plans.Select(p => p.LogicalReads))).Append("] | scans=[")
            .Append(string.Join(',', plans.SelectMany(p => p.TableScans()))).Append("] | ops=[")
            .Append(string.Join(',', plans.SelectMany(p => p.Operators().Select(o => o.Physical + (o.Table is null ? "" : ":" + o.Table))).Distinct())).AppendLine("]");
        for (var i = 0; i < plans.Count; i++)
        {
            sb.Append("   SQL: ").AppendLine(plans[i].Sql.ReplaceLineEndings(" "));
            sb.Append("   PARAMS: ").AppendLine(parameterText[i]);
            sb.Append("   PLAN: ").AppendLine(plans[i].Describe());
        }

        lock (Gate)
        {
            File.AppendAllText(path, sb.ToString());
        }
    }
}

/// <summary>The second quarter of the cases of <see cref="ApiPlanMatrixTests"/>, on its own copy of the 100x database.</summary>
public sealed class ApiPlanMatrixPart2Tests(ScaleApiFixture fx) : IClassFixture<ScaleApiFixture>
{
    [Theory]
    [MemberData(nameof(ApiPlanMatrixTests.CasesOfPart), 1, MemberType = typeof(ApiPlanMatrixTests))]
    public Task ApiRequest_AtScale_UsesNoTableScan_AndOneDatabaseCall(string @case) => ApiPlanMatrixTests.CheckAsync(fx, @case);
}

/// <summary>The third quarter of the cases of <see cref="ApiPlanMatrixTests"/>, on its own copy of the 100x database.</summary>
public sealed class ApiPlanMatrixPart3Tests(ScaleApiFixture fx) : IClassFixture<ScaleApiFixture>
{
    [Theory]
    [MemberData(nameof(ApiPlanMatrixTests.CasesOfPart), 2, MemberType = typeof(ApiPlanMatrixTests))]
    public Task ApiRequest_AtScale_UsesNoTableScan_AndOneDatabaseCall(string @case) => ApiPlanMatrixTests.CheckAsync(fx, @case);
}

/// <summary>The last quarter of the cases of <see cref="ApiPlanMatrixTests"/>, on its own copy of the 100x database.</summary>
public sealed class ApiPlanMatrixPart4Tests(ScaleApiFixture fx) : IClassFixture<ScaleApiFixture>
{
    [Theory]
    [MemberData(nameof(ApiPlanMatrixTests.CasesOfPart), 3, MemberType = typeof(ApiPlanMatrixTests))]
    public Task ApiRequest_AtScale_UsesNoTableScan_AndOneDatabaseCall(string @case) => ApiPlanMatrixTests.CheckAsync(fx, @case);
}
