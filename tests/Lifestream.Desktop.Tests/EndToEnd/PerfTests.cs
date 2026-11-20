using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Staff;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Dashboard;
using Lifestream.Desktop.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Desktop.Tests.EndToEnd;

/// <summary>
/// The performance budgets of design section 5.5, measured end to end: the real client (real HTTP client, wake-aware handler, session, navigation and
/// view models) over a real socket to the real API, which runs over a real local SQL Server database filled by the deterministic scale generator.
/// All of it is tagged Category=Perf and left out of the default run; <c>tests/perf/run-perf.ps1</c> starts the API and the fixture database, runs them
/// phase by phase (PerfPhase) on a quiet machine and writes docs/perf/results.md. Views are hosted off-screen (message-only parent window): nothing is
/// ever shown and no focus is taken. A test records every figure first and asserts afterwards, so a miss still leaves all the numbers behind.
/// </summary>
[Collection("Wpf")]
[Trait("Category", "Perf")]
public sealed class PerfTests
{
    private const int Width = 1366;
    private const int Height = 736;

    private static readonly string[] Screens = ["Dashboard", "Donors", "Appointments", "Patients", "Requests", "Stock", "Receipts", "Staff", "Profile"];

    /// <summary>
    /// The screens of direction A that load more than one data set, with the one request each data set costs (the budget is exactly one call per data set:
    /// never one per row, never the same one twice). A screen that is not listed loads one data set with one call.
    /// Dashboard: the figures and charts, the stock, today's appointments, the open requests. Appointments, Requests and Receipts: the list and the counts
    /// of its tabs; Requests also the stock its Cover column is worked out from.
    /// </summary>
    private static readonly Dictionary<string, string[]> DataSets = new()
    {
        ["Dashboard"] = ["GET /api/v1/dashboard", "GET /api/v1/stock", "GET /api/v1/appointments", "GET /api/v1/requests"],
        ["Appointments"] = ["GET /api/v1/appointments", "GET /api/v1/appointments/views"],
        ["Requests"] = ["GET /api/v1/requests", "GET /api/v1/requests/views", "GET /api/v1/stock"],
        ["Receipts"] = ["GET /api/v1/receipts", "GET /api/v1/receipts/counts"],
    };

    // ---- pumping without sleeping, so the UI thread is idle (and measurable) whenever nothing needs doing ----

    internal static void Pump(Task task, int timeoutMilliseconds = 30_000)
    {
        if (task.IsCompleted)
        {
            return;
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        var timeout = new DispatcherTimer(TimeSpan.FromMilliseconds(timeoutMilliseconds), DispatcherPriority.Normal, (_, _) => frame.Continue = false, dispatcher);
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        if (!task.IsCompleted)
        {
            throw new TimeoutException($"the load did not finish within {timeoutMilliseconds} ms");
        }

        task.GetAwaiter().GetResult();
    }

    internal static void PumpFor(int milliseconds)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Normal, (_, _) => frame.Continue = false, dispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static Task Open(Lifestream.Client.Shell.ShellViewModel shell, string title) =>
        title == "Profile"
            ? shell.ProfileCommand.ExecuteAsync()
            : shell.NavigateCommand.ExecuteAsync(shell.Items.First(i => i.Title == title));

    private static (RealRig Rig, Hosted Host) HostShell()
    {
        // As in the app: the chart warm-up is queued at start-up and has long finished while the user types the password.
        Pump(ChartWarmUp.Start(Dispatcher.CurrentDispatcher), 10_000);
        var rig = RealRig.Create();
        var host = Hosted.Create(new ShellView { DataContext = rig.Shell }, Width, Height);
        host.Settle(100);
        return (rig, host);
    }

    // ---- API: list endpoints and the dashboard at the fixture's size ----

    private static async Task<HttpClient> SignedInHttpAsync()
    {
        var http = new HttpClient { BaseAddress = new Uri(PerfEnv.ApiBase), Timeout = TimeSpan.FromSeconds(60) };
        using var response = await http.PostAsJsonAsync(ApiRoutes.Path(ApiRoutes.AuthLogin), new LoginRequest(PerfEnv.User, PerfEnv.Password), ContractJson.CreateOptions());
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ContractJson.CreateOptions()) ?? throw new InvalidOperationException("empty sign-in response");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return http;
    }

    /// <summary>Round trip over loopback, request sent to body read: an upper bound of the server's own time (it includes the HTTP client and the socket).</summary>
    private static async Task<(double P95, double Median, int Bytes)> TimeAsync(HttpClient http, string url, string name)
    {
        const int Warmup = 8;
        const int Samples = 40;
        var bytes = 0;
        for (var i = 0; i < Warmup; i++)
        {
            using var warm = await http.GetAsync(url);
            Assert.True(warm.IsSuccessStatusCode, $"{name}: {(int)warm.StatusCode}");
        }

        var times = new List<double>();
        for (var i = 0; i < Samples; i++)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await http.GetAsync(url);
            var body = await response.Content.ReadAsByteArrayAsync();
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.True(response.IsSuccessStatusCode, $"{name}: {(int)response.StatusCode}");
            bytes = body.Length;
        }

        return (PerfLog.P95(times), PerfLog.Median(times), bytes);
    }

    [Fact]
    [Trait("PerfPhase", "lists")]
    public async Task Perf_ListEndpointsAndDashboard_P95Under100ms_OverHttp_RealLocalSqlServer()
    {
        using var http = await SignedInHttpAsync();

        // Prove the dataset: the receipt total of the fixture database is the one the script created (27,000 at 100x, 270 at 1x).
        using (var probe = await http.GetAsync(ApiRoutes.Path(ApiRoutes.Receipts) + "?pageSize=1"))
        {
            var page = await probe.Content.ReadFromJsonAsync<Lifestream.Contracts.PagedResult<System.Text.Json.JsonElement>>(ContractJson.CreateOptions());
            Assert.Equal(PerfEnv.ExpectedReceipts, page!.Total);
        }

        var from = DateTime.UtcNow.AddDays(-60).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
            ("GET appointments page 500 (deep)", ApiRoutes.Appointments, "?page=500"),
            ("GET requests (page 1)", ApiRoutes.Requests, ""),
            ("GET requests status=Pending", ApiRoutes.Requests, "?status=Pending"),
            ("GET requests status=Fulfilled bloodType=OPos sort=requiredBy", ApiRoutes.Requests, "?status=Fulfilled&bloodType=OPos&sort=requiredBy"),
            ("GET requests search=a", ApiRoutes.Requests, "?search=a"),
            ("GET requests page 250 (deep)", ApiRoutes.Requests, "?page=250"),
            ("GET receipts (page 1)", ApiRoutes.Receipts, ""),
            ("GET receipts kind=Donation, date range", ApiRoutes.Receipts, $"?kind=Donation&fromUtc={from}&toUtc={to}"),
            ("GET receipts kind=Donation bloodType=OPos", ApiRoutes.Receipts, "?kind=Donation&bloodType=OPos"),
            ("GET receipts search=a", ApiRoutes.Receipts, "?search=a"),
            ("GET receipts search=Ay", ApiRoutes.Receipts, "?search=Ay"),
            ("GET receipts page 100", ApiRoutes.Receipts, "?page=100"),
            ("GET receipts page 500 (deep)", ApiRoutes.Receipts, "?page=500"),
            ("GET staff pageSize=200", ApiRoutes.Staff, "?pageSize=200"),
            ("GET stock", ApiRoutes.Stock, ""),
        };

        PerfLog.Expect([.. cases.Select(c => "lists " + c.Name), "dashboard"]);
        var failures = new List<string>();
        foreach (var (name, route, query) in cases)
        {
            var (p95, median, bytes) = await TimeAsync(http, ApiRoutes.Path(route) + query, name);
            if (!PerfLog.Record("lists", name, "p95 of 40 warm requests", 40, p95, "ms", "< 100", p95 < 100, $"median {median:F1} ms, {bytes} bytes", "lists " + name))
            {
                failures.Add($"{name}: p95 {p95:F1} ms");
            }
        }

        var (dashP95, dashMedian, dashBytes) = await TimeAsync(http, ApiRoutes.Path(ApiRoutes.Dashboard), "GET dashboard");
        if (!PerfLog.Record("dashboard", "GET dashboard (default 12 months, no cache)", "p95 of 40 warm requests", 40, dashP95, "ms", "< 100", dashP95 < 100, $"median {dashMedian:F1} ms, {dashBytes} bytes"))
        {
            failures.Add($"dashboard: p95 {dashP95:F1} ms");
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    [Trait("PerfPhase", "staff")]
    public async Task Perf_StaffList_110Staff_Under20KB_NoImageBytes_RealLocalSqlServer()
    {
        using var http = await SignedInHttpAsync();
        using var response = await http.GetAsync(ApiRoutes.Path(ApiRoutes.Staff) + "?pageSize=200");
        var body = await response.Content.ReadAsByteArrayAsync();
        var page = System.Text.Json.JsonSerializer.Deserialize<PagedResult<StaffListItem>>(body, ContractJson.CreateOptions())!;
        var text = Encoding.UTF8.GetString(body);

        Assert.Equal(110, page.Total); // the 10x fixture: exactly 110 staff with the generator's realistic names
        var noImage = !text.Contains("base64", StringComparison.OrdinalIgnoreCase) && !text.Contains("content\"", StringComparison.OrdinalIgnoreCase) && !text.Contains("data:image", StringComparison.OrdinalIgnoreCase);
        var longest = page.Items.Max(s => s.DisplayName.Length);
        var pass = body.Length < 20 * 1024 && noImage;
        PerfLog.Record("staff-payload", "GET staff pageSize=200 (110 staff)", "size of one response", 1, body.Length / 1024.0, "KB", "< 20", pass, $"{body.Length} bytes, {page.Items.Count} rows, longest name {longest} chars, image bytes {(noImage ? "none" : "FOUND")}");
        Assert.Equal(110, page.Items.Count);
        Assert.True(pass, $"{body.Length} bytes, no image bytes: {noImage}");
    }

    // ---- screens: skeleton, calls, UI-thread gap, grid ----

    internal static string SkeletonKey(string screen) => "skeleton " + screen.ToLowerInvariant();

    /// <summary>
    /// Records the skeleton time of one screen: the slowest of its visits. A visit on which no skeleton was visible (<c>null</c>) is a skeleton that was never shown, so the
    /// screen is MISSED (a row with no value, never Infinity) and the test fails with "MISSED skeleton &lt;screen&gt;"; every later screen is still measured.
    /// </summary>
    internal static bool RecordSkeleton(string screen, int visits, IReadOnlyList<double?> times, out string problem)
    {
        var key = SkeletonKey(screen);
        var shown = times.Where(t => t is not null).Select(t => t!.Value).ToList();
        if (shown.Count < times.Count)
        {
            problem = $"MISSED {key}: no skeleton was visible after navigating to {screen} on {times.Count - shown.Count} of {times.Count} visits";
            PerfLog.Missed("skeleton", $"Skeleton after navigating to {screen}", $"max of {visits} visits (the first one is cold)", visits, "< 100", problem, key);
            return false;
        }

        var worst = shown.Max();
        var pass = worst < 100;
        PerfLog.Record("skeleton", $"Skeleton after navigating to {screen}", $"max of {visits} visits (the first one is cold)", visits, worst, "ms", "< 100", pass, $"first visit {shown[0]:F1} ms, median {PerfLog.Median(shown):F1} ms", key);
        problem = pass ? "" : $"{screen}: {worst:F1} ms";
        return pass;
    }

    [Fact]
    [Trait("PerfPhase", "screens")]
    public void Perf_EveryScreen_SkeletonVisibleWithin100ms_OfNavigating_RealApi()
    {
        const int Visits = 10;
        var failures = StaHost.Run(() =>
        {
            var (rig, host) = HostShell();
            using (rig)
            using (host)
            {
                rig.Probe.Latency = TimeSpan.FromMilliseconds(300); // keeps the load running long enough to see the skeleton; the skeleton itself does not wait for the network
                var problems = new List<string>();
                PerfLog.Expect([.. Screens.Select(s => SkeletonKey(s))]);
                foreach (var screen in Screens)
                {
                    var times = new List<double?>();
                    for (var i = 0; i < Visits; i++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var navigation = Open(rig.Shell, screen);
                        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, () => { }); // the binding, the template and the layout pass that put the skeleton on screen
                        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        var visible = host.All<Skeleton>().Any(s => s.IsVisible && s.ActualHeight > 0);
                        times.Add(visible ? elapsed : null);
                        Pump(navigation);
                        PumpFor(50);
                    }

                    if (!RecordSkeleton(screen, Visits, times, out var problem))
                    {
                        problems.Add(problem);
                    }
                }

                return problems;
            }
        });

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    [Trait("PerfPhase", "screens")]
    public void Perf_EveryScreen_ExactlyOneViewModel_OneContext_OneApiCallPerDataSet_NoCallOnTheUiThread_RealApi()
    {
        var problems = StaHost.Run(() =>
        {
            var (rig, host) = HostShell();
            using (rig)
            using (host)
            {
                var found = new List<string>();
                PerfLog.Expect([.. Screens.Select(s => "api-calls " + s.ToLowerInvariant()), "api-calls ui-thread"]);
                var navigation = rig.Provider.GetRequiredService<Lifestream.Client.Navigation.INavigationService>();
                var pageHost = host.Find<ContentControl>("PageHost");
                foreach (var screen in Screens)
                {
                    var changes = 0;
                    void OnChanged(object? s, EventArgs e) => changes++;
                    navigation.CurrentChanged += OnChanged;
                    rig.Probe.Reset();
                    var task = Open(rig.Shell, screen);
                    Pump(task);
                    PumpFor(400); // late calls (thumbnails, lookups) would land here
                    navigation.CurrentChanged -= OnChanged;

                    var calls = rig.Probe.Calls;
                    var page = Hosted.Descendants(pageHost).OfType<FrameworkElement>().FirstOrDefault(e => ReferenceEquals(e.DataContext, rig.Shell.CurrentPage) && e is UserControl);
                    var dataContextIsTheViewModel = page is not null;
                    // Exactly one call per data set of the screen: the listed ones, each once. Most screens have one data set; never a call per row.
                    var dataSets = DataSets.TryGetValue(screen, out var sets) ? sets : null;
                    var oneEach = dataSets is null ? calls.Count == 1 : calls.Order(StringComparer.Ordinal).SequenceEqual(dataSets.Order(StringComparer.Ordinal), StringComparer.Ordinal);
                    var pass = oneEach && changes == 1 && dataContextIsTheViewModel;
                    var limit = dataSets is null ? "== 1" : $"== {dataSets.Length}, one per data set ({string.Join(", ", dataSets.Select(s => s[(s.LastIndexOf("/v1/", StringComparison.Ordinal) + 4)..]))})";
                    PerfLog.Record("api-calls", $"{screen}: API calls for one screen load", "count of one load", 1, calls.Count, "calls", limit, pass, $"{string.Join(", ", calls)}; view models created {changes}; DataContext is the view model: {dataContextIsTheViewModel}", "api-calls " + screen.ToLowerInvariant());
                    if (!pass)
                    {
                        found.Add($"{screen}: {calls.Count} calls ({string.Join(", ", calls)}), {changes} view model(s), context ok {dataContextIsTheViewModel}");
                    }
                }

                var onUi = rig.Probe.CallsStartedOnUiThread;
                PerfLog.Record("api-calls", "API calls started on the UI thread (all screens)", "count over all loads", Screens.Length, onUi, "calls", "== 0", onUi == 0, key: "api-calls ui-thread");
                if (onUi != 0)
                {
                    found.Add($"{onUi} call(s) started on the UI thread");
                }

                return found;
            }
        });

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    [Trait("PerfPhase", "screens")]
    public void Perf_UiThreadGap_DuringLoadsOverA250msLink_Under50ms_RealApi()
    {
        var problems = StaHost.Run(() =>
        {
            var (rig, host) = HostShell();
            using (rig)
            using (host)
            {
                rig.Probe.Latency = TimeSpan.FromMilliseconds(250);
                var dispatcher = Dispatcher.CurrentDispatcher;
                var found = new List<string>();
                var overall = 0.0;
                var worstScreen = "";
                PerfLog.Expect([.. Screens.Select(s => "ui-gap " + s.ToLowerInvariant()), "ui-gap worst"]);
                foreach (var screen in Screens)
                {
                    // A thread of its own posts a no-op at input priority every few ms; how long each waits for the UI thread is the gap in which input would not be processed.
                    var running = true;
                    var worst = 0.0;
                    var probe = new Thread(() =>
                    {
                        while (Volatile.Read(ref running))
                        {
                            var posted = Stopwatch.GetTimestamp();
                            dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                            {
                                var waited = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                                if (waited > worst)
                                {
                                    worst = waited;
                                }
                            });
                            Thread.Sleep(3);
                        }
                    })
                    { IsBackground = true };
                    probe.Start();
                    var task = Open(rig.Shell, screen);
                    Pump(task);
                    PumpFor(300); // the rows are built and drawn after the data lands
                    Volatile.Write(ref running, false);
                    probe.Join();
                    PumpFor(20);

                    if (worst > overall)
                    {
                        overall = worst;
                        worstScreen = screen;
                    }

                    PerfLog.Record("ui-gap", $"UI thread gap while loading {screen} over 250 ms latency", "largest wait of an input-priority post during one load", 1, worst, "ms", "< 50", worst < 50, key: "ui-gap " + screen.ToLowerInvariant());
                    if (worst >= 50)
                    {
                        found.Add($"{screen}: {worst:F1} ms");
                    }
                }

                PerfLog.Record("ui-gap", "UI thread gap, worst of all screens", "max over 9 loads", Screens.Length, overall, "ms", "< 50", overall < 50, $"worst on {worstScreen}", "ui-gap worst");
                return found;
            }
        });

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ---- grid ----

    private sealed record Row(int Id, string Name, string Phone, string Status);

    [Fact]
    [Trait("PerfPhase", "screens")]
    public void Perf_DataGrid_10000Items_CreatesUnder300RowContainers_AndEveryListScreenUsesIt()
    {
        var (created, realized, total) = StaHost.Run(() =>
        {
            var rows = Enumerable.Range(1, 10_000).Select(i => new Row(i, $"Donor {i} Ayse Ozturk", $"+1-555-01{i % 100:00}", i % 2 == 0 ? "Completed" : "Pending")).ToList();
            var grid = new LifestreamDataGrid { Width = 900, Height = 560, ItemsSource = rows };
            grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)), SortMemberPath = "name" });
            grid.Columns.Add(new DataGridTextColumn { Header = "Phone", Binding = new Binding(nameof(Row.Phone)), SortMemberPath = "phone", Width = 120 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(Row.Status)), SortMemberPath = "status" });
            var seen = new HashSet<DataGridRow>();
            grid.LoadingRow += (_, e) => seen.Add(e.Row);
            var stage = new Grid { Width = 900, Height = 560 };
            stage.Children.Add(grid);
            using var host = Hosted.Create(stage, 900, 560);
            host.Settle(400);
            var scroll = host.All<ScrollViewer>().First(s => s.ScrollableHeight > 0);
            foreach (var fraction in new[] { 0.25, 0.5, 0.99 })
            {
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * fraction);
                host.Settle(150);
            }

            return (seen.Count, host.All<DataGridRow>().Count(), grid.Items.Count);
        });

        // Every list screen shows its rows in the one virtualising grid: no raw DataGrid, no ItemsControl of rows, in any view.
        var views = Directory.GetFiles(Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Features{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        var rawGrids = views.Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"<(?:DataGrid|ListView|GridView)[\s>]")).Select(Path.GetFileName).ToList();
        var listViewsWithoutOurGrid = new[] { "DonorsView", "PatientsView", "AppointmentsView", "RequestsView", "ReceiptsView", "StaffView" }
            .Where(v => !File.ReadAllText(views.Single(f => Path.GetFileNameWithoutExtension(f) == v)).Contains("LifestreamDataGrid", StringComparison.Ordinal)).ToList();

        var most = Math.Max(created, realized);
        var pass = most < 300 && rawGrids.Count == 0 && listViewsWithoutOurGrid.Count == 0;
        PerfLog.Record("grid", "DataGrid with 10,000 items: row containers created", "max of created and visible rows after scrolling to 25%, 50%, 99%", 1, most, "rows", "< 300", pass,
            $"{total} items; created {created}, in the visual tree {realized}; screens not using LifestreamDataGrid: {(listViewsWithoutOurGrid.Count == 0 ? "none" : string.Join(",", listViewsWithoutOurGrid))}; views with a raw grid: {(rawGrids.Count == 0 ? "none" : string.Join(",", rawGrids))}");
        Assert.Equal(10_000, total);
        Assert.Empty(rawGrids);
        Assert.Empty(listViewsWithoutOurGrid);
        Assert.InRange(most, 1, 299);
    }

    // ---- FEAT-27: the first dashboard of a fresh process ----

    [Fact]
    [Trait("PerfPhase", "cold")]
    public void Perf_ColdDashboard_FirstChartRenderOfAFreshProcess_RealApi()
    {
        var (arrivalToChartsMs, drawn, firstOfProcess, warmUpMs, skeletonMs) = StaHost.Run(() =>
        {
            var warmUpClock = Stopwatch.StartNew();
            var warmUp = ChartWarmUp.Start(Dispatcher.CurrentDispatcher); // what App.OnStartup does, before the login window is shown
            var rig = RealRig.Create(); // signing in; in the app the warm-up runs while the window waits for the user and while this call is in flight
            using (rig)
            {
                Pump(warmUp, 10_000);
                var warmedAfter = warmUpClock.ElapsedMilliseconds;
                var viewModel = ActivatorUtilities.CreateInstance<DashboardViewModel>(rig.Provider);
                viewModel.Attach(null, TimeProvider.System);
                var view = new DashboardView { DataContext = viewModel };
                view.SetResourceReference(Control.BackgroundProperty, "Brush.SurfaceCanvas");
                using var host = Hosted.Create(view, Width, Height);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var drawsBefore = DashboardView.ChartDrawCount;

                var navigated = Stopwatch.StartNew();
                var load = viewModel.LoadAsync();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, () => { });
                var skeleton = navigated.Elapsed.TotalMilliseconds;
                Pump(load);

                // The clock starts when the response had fully arrived (stamped under the HTTP client); applying the data and drawing are inside the measurement.
                // Each look renders the page into a bitmap (which is what forces the charts to draw) and counts the two series colours.
                var drawnNow = false;
                long drawnAt = 0;
                var deadline = Stopwatch.StartNew();
                while (!drawnNow && deadline.ElapsedMilliseconds < 3000)
                {
                    var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(view);
                    var pixels = new byte[Width * Height * 4];
                    bitmap.CopyPixels(pixels, Width * 4, 0);
                    drawnAt = Stopwatch.GetTimestamp();
                    // The two series colours of the theme in use (Brush.Chart1, Brush.Chart2), in numbers only a drawn chart produces.
                    var one = ((SolidColorBrush)view.FindResource("Brush.Chart1")).Color;
                    var two = ((SolidColorBrush)view.FindResource("Brush.Chart2")).Color;
                    drawnNow = CountNear(pixels, one.R, one.G, one.B) > 400 && CountNear(pixels, two.R, two.G, two.B) > 400;
                    if (!drawnNow)
                    {
                        PumpFor(1);
                    }
                }

                return (Stopwatch.GetElapsedTime(rig.Probe.LastArrival, drawnAt).TotalMilliseconds, drawnNow, drawsBefore == 0, warmedAfter, skeleton);
            }
        });

        PerfLog.Record("cold-dashboard", "Dashboard first chart render, fresh process", "single cold run (the script takes the median of the runs)", 1, arrivalToChartsMs, "ms", "< 300", drawn && arrivalToChartsMs < 300,
            $"charts drawn: {drawn}; first chart of the process: {firstOfProcess}; warm-up done {warmUpMs} ms after start; skeleton {skeletonMs:F1} ms after load started");
        Assert.True(drawn, "the charts were not drawn");
        Assert.True(firstOfProcess, "this was not the first dashboard of the process: run this test alone for a cold measurement");
    }

    private static int CountNear(byte[] bgra, byte red, byte green, byte blue)
    {
        var count = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (Math.Abs(bgra[i + 2] - red) <= 3 && Math.Abs(bgra[i + 1] - green) <= 3 && Math.Abs(bgra[i] - blue) <= 3 && ++count > 400)
            {
                return count;
            }
        }

        return count;
    }
}
