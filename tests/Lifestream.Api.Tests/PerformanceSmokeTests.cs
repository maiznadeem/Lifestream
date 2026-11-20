using System.Diagnostics;
using System.Text;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>
/// The timings, shared by the 100x and the 1x runs (DB-06): the same requests, the same budget, two dataset sizes. Warm API, real local
/// SQL Server. Tagged Category=Perf and left out of the default test run (see Lifestream.Api.Tests.csproj); run them alone with
/// <c>dotnet test --filter Category=Perf</c>. Each shape is measured once. Set LIFESTREAM_PERF_RESULTS to a file path to write the results table.
/// </summary>
internal static class PerfShapes
{
    private const int Warmup = 8;
    private const int Samples = 40;
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Timing(string Name, double P50, double P95, double Max, int Bytes);

    public static async Task<Timing> MeasureAsync(HttpClient client, string name, string template, string query)
    {
        var url = ApiRoutes.Path(template) + query;
        var bytes = 0;
        for (var i = 0; i < Warmup; i++)
        {
            using var warm = await client.GetAsync(url, Ct);
            Assert.True(warm.IsSuccessStatusCode, $"{name}: {(int)warm.StatusCode}");
        }

        var times = new List<double>();
        for (var i = 0; i < Samples; i++)
        {
            var watch = Stopwatch.StartNew();
            using var response = await client.GetAsync(url, Ct);
            var body = await response.Content.ReadAsByteArrayAsync(Ct);
            watch.Stop();
            Assert.True(response.IsSuccessStatusCode, $"{name}: {(int)response.StatusCode}");
            bytes = body.Length;
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return new Timing(name, times[times.Count / 2], times[(int)Math.Ceiling(times.Count * 0.95) - 1], times[^1], bytes);
    }

    public static void Report(IReadOnlyList<Timing> timings, string title, ApiFixture fx)
    {
        var path = Environment.GetEnvironmentVariable("LIFESTREAM_PERF_RESULTS");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"### {title}").AppendLine();
        sb.AppendLine("| Request | p50 ms | p95 ms | max ms | response bytes | budget |").AppendLine("|---|---|---|---|---|---|");
        foreach (var t in timings)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"| {t.Name} | {t.P50:F1} | {t.P95:F1} | {t.Max:F1} | {t.Bytes} | p95 < 100 ms {(t.P95 < 100 ? "met" : "MISSED")} |");
        }

        sb.AppendLine().AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Server: {fx.Db.ServerVersion.Split('\n')[0].Trim()}").AppendLine();
        File.AppendAllText(path, sb.ToString());
    }

    public static async Task RunListsAsync(ApiFixture fx, string label, int expectedReceipts)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion, StringComparison.Ordinal);
        Assert.Equal(expectedReceipts, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Receipt", Ct));
        var from = DateTime.UtcNow.AddDays(-60).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var cases = new (string Name, string Route, string Query)[]
        {
            ("GET donors (page 1)", ApiRoutes.Donors, ""),
            ("GET donors search=a", ApiRoutes.Donors, "?search=a"),
            ("GET donors bloodType=OPos sort=-lastDonated", ApiRoutes.Donors, "?bloodType=OPos&sort=-lastDonated"),
            ("GET donors page 60 (deep)", ApiRoutes.Donors, "?page=60"),
            ("GET patients (page 1)", ApiRoutes.Patients, ""),
            ("GET patients search=a", ApiRoutes.Patients, "?search=a"),
            ("GET patients page 60 (deep)", ApiRoutes.Patients, "?page=60"),
            ("GET appointments (page 1)", ApiRoutes.Appointments, ""),
            ("GET appointments status=Pending", ApiRoutes.Appointments, "?status=Pending"),
            ("GET appointments status=Completed bloodType=OPos", ApiRoutes.Appointments, "?status=Completed&bloodType=OPos"),
            ("GET appointments status=Completed bloodType=OPos sort=-placed page 100", ApiRoutes.Appointments, "?status=Completed&bloodType=OPos&sort=-placed&page=100"),
            ("GET appointments search=a, date range", ApiRoutes.Appointments, $"?search=a&fromUtc={from}&toUtc={to}"),
            ("GET appointments search=a sort=slot page 20", ApiRoutes.Appointments, "?search=a&sort=slot&page=20"),
            ("GET appointments search=Ay sort=slot page 3", ApiRoutes.Appointments, "?search=Ay&sort=slot&page=3"),
            ("GET requests (page 1)", ApiRoutes.Requests, ""),
            ("GET requests status=Pending", ApiRoutes.Requests, "?status=Pending"),
            ("GET requests status=Fulfilled bloodType=OPos sort=requiredBy", ApiRoutes.Requests, "?status=Fulfilled&bloodType=OPos&sort=requiredBy"),
            ("GET requests status=Fulfilled bloodType=OPos sort=-requiredBy page 40", ApiRoutes.Requests, "?status=Fulfilled&bloodType=OPos&sort=-requiredBy&page=40"),
            ("GET requests search=a", ApiRoutes.Requests, "?search=a"),
            ("GET requests search=a page 30", ApiRoutes.Requests, "?search=a&page=30"),
            ("GET receipts (page 1)", ApiRoutes.Receipts, ""),
            ("GET receipts kind=Donation, date range", ApiRoutes.Receipts, $"?kind=Donation&fromUtc={from}&toUtc={to}"),
            ("GET receipts kind=Donation bloodType=OPos", ApiRoutes.Receipts, "?kind=Donation&bloodType=OPos"),
            ("GET receipts search=a", ApiRoutes.Receipts, "?search=a"),
            ("GET receipts search=Ay", ApiRoutes.Receipts, "?search=Ay"),
            ("GET receipts search=a page 100", ApiRoutes.Receipts, "?search=a&page=100"),
            ("GET receipts page 100", ApiRoutes.Receipts, "?page=100"),
            ("GET receipts page 500 (deep)", ApiRoutes.Receipts, "?page=500"),
            ("GET appointments page 500 (deep)", ApiRoutes.Appointments, "?page=500"),
            ("GET requests page 250 (deep)", ApiRoutes.Requests, "?page=250"),
            ("GET appointments donor blood type", ApiRoutes.Appointments, "?bloodType=OPos"),
            ("GET staff pageSize=200", ApiRoutes.Staff, "?pageSize=200"),
            ("GET stock", ApiRoutes.Stock, ""),

            // What a member of staff types (SearchQueries): a last name, a part of a name, a part of a phone number, two words in either order, a blood
            // group, a code, and a text nobody has. "a" above now matches a fifth of the names (every word of a name that starts with it, where it
            // was the 13 % whose full name does), "555" every phone.
            ("GET donors search=eastwood (last name)", ApiRoutes.Donors, "?search=eastwood"),
            ("GET donors search=ali (inside a name)", ApiRoutes.Donors, "?search=ali"),
            ("GET donors search=0156 (part of a phone)", ApiRoutes.Donors, "?search=0156"),
            ("GET donors search=eastwood selim (two words)", ApiRoutes.Donors, "?search=eastwood%20selim"),
            ("GET donors search=O- (blood group)", ApiRoutes.Donors, "?search=O-"),
            ("GET donors search=zzzz (nobody)", ApiRoutes.Donors, "?search=zzzz"),
            ("GET patients search=eastwood (last name)", ApiRoutes.Patients, "?search=eastwood"),
            ("GET patients search=0156 (part of a phone)", ApiRoutes.Patients, "?search=0156"),
            ("GET appointments search=eastwood (last name)", ApiRoutes.Appointments, "?search=eastwood"),
            ("GET appointments search=ali sort=slot (inside a name)", ApiRoutes.Appointments, "?search=ali&sort=slot"),
            ("GET appointments search=0156 (part of a phone)", ApiRoutes.Appointments, "?search=0156"),
            ("GET appointments search=eastwood selim (two words)", ApiRoutes.Appointments, "?search=eastwood%20selim"),
            ("GET appointments search=555 (every phone)", ApiRoutes.Appointments, "?search=555"),
            ("GET appointments search=zzzz (nobody)", ApiRoutes.Appointments, "?search=zzzz"),
            ("GET requests search=eastwood (last name)", ApiRoutes.Requests, "?search=eastwood"),
            ("GET requests search=0156 (part of a phone)", ApiRoutes.Requests, "?search=0156"),
            ("GET requests search=555 (every phone)", ApiRoutes.Requests, "?search=555"),
            ("GET requests search=zzzz (nobody)", ApiRoutes.Requests, "?search=zzzz"),
            ("GET receipts search=eastwood (last name)", ApiRoutes.Receipts, "?search=eastwood"),
            ("GET receipts search=ali (inside a name)", ApiRoutes.Receipts, "?search=ali"),
            ("GET receipts search=0156 (part of a phone)", ApiRoutes.Receipts, "?search=0156"),
            ("GET receipts search=eastwood selim (two words)", ApiRoutes.Receipts, "?search=eastwood%20selim"),
            ("GET receipts search=555 (every phone)", ApiRoutes.Receipts, "?search=555"),
            ("GET receipts search=zzzz (nobody)", ApiRoutes.Receipts, "?search=zzzz"),
            ("GET receipts search=eastwood kind=Donation", ApiRoutes.Receipts, "?search=eastwood&kind=Donation"),
            ("GET staff search=eastwood (last name)", ApiRoutes.Staff, "?search=eastwood"),
            ("GET lookups/donors search=eastwood (palette)", ApiRoutes.LookupDonors, "?search=eastwood"),
            ("GET lookups/requests search=eastwood (palette)", ApiRoutes.LookupRequests, "?search=eastwood"),
        };

        var timings = new List<Timing>();
        foreach (var (name, route, query) in cases)
        {
            timings.Add(await MeasureAsync(admin.Client, name, route, query));
        }

        Report(timings, $"List endpoints at {label} (warm, local SQL Server)", fx);
        var slow = timings.Where(t => t.P95 >= Budget.TotalMilliseconds).Select(t => $"{t.Name}: p95 {t.P95:F1} ms").ToList();
        Assert.True(slow.Count == 0, string.Join("; ", slow));
    }

    public static async Task RunDashboardAsync(ApiFixture fx, string label)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var timings = new List<Timing>
        {
            await MeasureAsync(admin.Client, "GET dashboard (default 12 months, uncached)", ApiRoutes.Dashboard, ""),
            await MeasureAsync(admin.Client, "GET dashboard (36 months, uncached)", ApiRoutes.Dashboard, "?from=2023-12-01"),
        };
        Report(timings, $"Dashboard at {label} (warm, no cache)", fx);

        // The budget is for the default 12-month dashboard. The 36-month range is measured and reported for information only.
        var standard = timings[0];
        Assert.True(standard.P95 < Budget.TotalMilliseconds, $"{standard.Name}: p95 {standard.P95:F1} ms");
    }
}

/// <summary>Warm-API timings against the 100x dataset (27,000 receipts, 30,000 appointments, 15,000 requests, 3,500 donors/patients). Category=Perf: run locally, reported (not gated) in CI.</summary>
[Trait("Category", "Perf")]
[Collection("Scale datasets: one class at a time")] // the 100x databases load 100,000 rows and the perf tests time requests, so they do not overlap
public sealed class PerformanceSmokeTests(ScaleApiFixture fx) : IClassFixture<ScaleApiFixture>
{
    [Fact]
    public Task Perf_ListEndpoints_P95Under100ms_At100x() => PerfShapes.RunListsAsync(fx, "100x", 27_000);

    [Fact]
    public Task Perf_Dashboard_P95Under100ms_At100x() => PerfShapes.RunDashboardAsync(fx, "100x");
}

/// <summary>
/// DB-06: the same timings against the 1x demo dataset (the data the public demo holds after each daily reset: 270 receipts, 300
/// appointments, 150 requests, 100 donors).
/// </summary>
[Trait("Category", "Perf")]
[Collection("Scale datasets: one class at a time")] // the 100x databases load 100,000 rows and the perf tests time requests, so they do not overlap
public sealed class PerformanceAtDemoSizeTests(DemoSizeApiFixture fx) : IClassFixture<DemoSizeApiFixture>
{
    [Fact]
    public Task Perf_ListEndpoints_P95Under100ms_AtDemoSize1x() => PerfShapes.RunListsAsync(fx, "1x demo size", 270);

    [Fact]
    public Task Perf_Dashboard_P95Under100ms_AtDemoSize1x() => PerfShapes.RunDashboardAsync(fx, "1x demo size");
}
