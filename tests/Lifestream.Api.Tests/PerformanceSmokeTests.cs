using System.Diagnostics;
using System.Text;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>
/// Warm-API timings against the 100x dataset (27,000 receipts, 30,000 appointments, 15,000 requests, 3,500 donors/patients) on a real
/// local SQL Server. Tagged Category=Perf: run locally, reported (not gated) in CI. Set LIFESTREAM_PERF_RESULTS to a file path to
/// write the results table.
/// </summary>
[Trait("Category", "Perf")]
public sealed class PerformanceSmokeTests(ScaleApiFixture fx) : IClassFixture<ScaleApiFixture>
{
    private const int Warmup = 8;
    private const int Samples = 40;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Timing(string Name, double P50, double P95, double Max, int Bytes);

    private static async Task<Timing> MeasureAsync(HttpClient client, string name, string template, string query)
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

    private static void Report(IReadOnlyList<Timing> timings, string title, ScaleApiFixture fx)
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

    [Fact]
    public async Task Perf_ListEndpoints_P95Under100ms_At100x()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        Assert.Equal(27_000, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Receipt", Ct));
        var from = DateTime.UtcNow.AddDays(-60).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var cases = new (string Name, string Route, string Query)[]
        {
            ("GET donors (page 1)", ApiRoutes.Donors, ""),
            ("GET donors search=a", ApiRoutes.Donors, "?search=a"),
            ("GET donors bloodType=OPos sort=-lastDonated", ApiRoutes.Donors, "?bloodType=OPos&sort=-lastDonated"),
            ("GET patients (page 1)", ApiRoutes.Patients, ""),
            ("GET patients search=a", ApiRoutes.Patients, "?search=a"),
            ("GET appointments (page 1)", ApiRoutes.Appointments, ""),
            ("GET appointments status=Pending", ApiRoutes.Appointments, "?status=Pending"),
            ("GET appointments search=a, date range", ApiRoutes.Appointments, $"?search=a&fromUtc={from}&toUtc={to}"),
            ("GET requests (page 1)", ApiRoutes.Requests, ""),
            ("GET requests status=Pending", ApiRoutes.Requests, "?status=Pending"),
            ("GET requests search=a", ApiRoutes.Requests, "?search=a"),
            ("GET receipts (page 1)", ApiRoutes.Receipts, ""),
            ("GET receipts kind=Donation, date range", ApiRoutes.Receipts, $"?kind=Donation&fromUtc={from}&toUtc={to}"),
            ("GET receipts search=a", ApiRoutes.Receipts, "?search=a"),
            ("GET receipts page 100", ApiRoutes.Receipts, "?page=100"),
            ("GET receipts page 500 (deep)", ApiRoutes.Receipts, "?page=500"),
            ("GET appointments page 500 (deep)", ApiRoutes.Appointments, "?page=500"),
            ("GET requests page 250 (deep)", ApiRoutes.Requests, "?page=250"),
            ("GET appointments donor blood type", ApiRoutes.Appointments, "?bloodType=OPos"),
            ("GET staff pageSize=200", ApiRoutes.Staff, "?pageSize=200"),
            ("GET stock", ApiRoutes.Stock, ""),
        };

        var timings = new List<Timing>();
        foreach (var (name, route, query) in cases)
        {
            timings.Add(await MeasureAsync(admin.Client, name, route, query));
        }

        Report(timings, "List endpoints at 100x (warm, local SQL Server)", fx);
        var slow = timings.Where(t => t.P95 >= Budget.TotalMilliseconds).Select(t => $"{t.Name}: p95 {t.P95:F1} ms").ToList();
        Assert.True(slow.Count == 0, string.Join("; ", slow));
    }

    [Fact]
    public async Task Perf_Dashboard_P95Under100ms_At100x()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var timings = new List<Timing>
        {
            await MeasureAsync(admin.Client, "GET dashboard (default 12 months, uncached)", ApiRoutes.Dashboard, ""),
            await MeasureAsync(admin.Client, "GET dashboard (36 months, uncached)", ApiRoutes.Dashboard, "?from=2023-12-01"),
        };
        Report(timings, "Dashboard at 100x (warm, no cache)", fx);

        // The budget is for the default 12-month dashboard. The 36-month range is measured and reported for information only.
        var standard = timings[0];
        Assert.True(standard.P95 < Budget.TotalMilliseconds, $"{standard.Name}: p95 {standard.P95:F1} ms");
    }
}
