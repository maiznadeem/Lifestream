using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Data.Seed;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Dashboard;
using Lifestream.Desktop.Shell;
using Lifestream.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Desktop.Tests.EndToEnd;

/// <summary>
/// INFRA-19: the README and Store screenshots, rendered off-screen through the real client from the real demo dataset. <c>tools/generate-screenshots.ps1</c> creates a fresh local
/// fixture database filled by <see cref="DemoDataGenerator"/> (what the deployed demo holds), starts the real API over it and sets <c>LIFESTREAM_SHOTS_API</c>,
/// <c>LIFESTREAM_SHOTS_TODAY</c> (the date the data is relative to) and <c>LIFESTREAM_CAPTURE_DIR</c>. The test opens the sign-in screen, presses "Try the demo", and opens every screen of the
/// sidebar as the demo account would. A capture is only written for a screen that finished loading with data: an error or empty state, an unloaded view model or a dashboard whose
/// counts differ from the demo dataset's fails the test and writes nothing. No fake data is involved (the fake API and DemoData.cs are not used here).
/// All of it runs on one UI thread, as in the app. Nothing is shown on the desktop and no focus is taken.
/// </summary>
[Collection("Wpf")]
public sealed class DemoScreenshotTests
{
    private const int Width = 1366;
    private const int Height = 768;

    private static readonly string[] Screens = ["Dashboard", "Donors", "Patients", "Appointments", "Requests", "Stock", "Receipts", "Staff"];

    /// <summary>What an off-screen view must not be showing when it is captured for the README: an error, an empty state, a skeleton or a failed message.</summary>
    internal static List<string> Problems(Hosted host, ViewModelBase page, string screen)
    {
        var problems = new List<string>();
        if (page.State != LoadState.Ready)
        {
            problems.Add($"{screen}: the view model is {page.State}{(string.IsNullOrEmpty(page.ErrorMessage) ? "" : " (" + page.ErrorMessage + ")")}, not Ready");
        }

        foreach (var error in host.All<ErrorState>().Where(e => e.IsVisible))
        {
            problems.Add($"{screen}: an error state is on screen ('{error.Message}'; {error.Detail})");
        }

        if (host.All<EmptyState>().Any(e => e.IsVisible))
        {
            problems.Add($"{screen}: an empty state is on screen");
        }

        if (host.All<Skeleton>().Any(s => s.IsVisible && s.ActualHeight > 0))
        {
            problems.Add($"{screen}: a skeleton is still on screen");
        }

        return problems;
    }

    /// <summary>The counts the dashboard must show for the demo dataset generated for <paramref name="today"/>.</summary>
    internal static Dictionary<string, int> ExpectedCards(DateOnly today)
    {
        var set = new DemoDataGenerator().Generate(today);
        return new()
        {
            ["Donors"] = set.Donors.Count,
            ["Patients"] = set.Patients.Count,
            ["Pending appointments"] = set.Appointments.Count(a => a.Status == AppointmentStatus.Pending),
            ["Pending requests"] = set.Requests.Count(r => r.Status == RequestStatus.Pending),
        };
    }

    /// <summary>The dashboard counts that differ from <paramref name="expected"/> (empty when they match); a card that is missing counts as different.</summary>
    internal static List<string> CardMismatches(IReadOnlyCollection<KpiCard> cards, IReadOnlyDictionary<string, int> expected) =>
        [.. expected.Select(e => cards.FirstOrDefault(c => c.Title == e.Key) is { } card && card.Value == e.Value
            ? null
            : $"dashboard card '{e.Key}' shows {cards.FirstOrDefault(c => c.Title == e.Key)?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "nothing"}, the demo dataset has {e.Value}").OfType<string>()];

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is not set: tools/generate-screenshots.ps1 starts the local API over a fresh demo database and sets it.");

    // Category=Perf as well: like the perf tests it needs an environment only a script provides (a local API over a fresh database), so the default run and CI's -notrait Category=Perf leave it out.
    [Fact]
    [Trait("Category", "Screenshots")]
    [Trait("Category", "Perf")]
    public void Screenshots_EveryScreen_FromTheRealDemoDataset_ThroughTheRealApi_NoErrorOrEmptyState_DashboardCountsMatch()
    {
        var api = Required("LIFESTREAM_SHOTS_API");
        var today = DateOnly.ParseExact(Required("LIFESTREAM_SHOTS_TODAY"), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var expected = ExpectedCards(today);

        var (problems, observed, saved) = StaHost.Run(() =>
        {
            var problems = new List<string>();
            var saved = new List<string>();
            Dictionary<string, int> observed = [];

            // As in the app, the UI thread has a dispatcher synchronization context: a command that awaits resumes (and raises its state changes) on it, never on a pool thread.
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));

            // As in the app: the chart warm-up is queued at start-up and long finished while the user looks at the sign-in screen.
            PerfTests.Pump(ChartWarmUp.Start(System.Windows.Threading.Dispatcher.CurrentDispatcher), 10_000);

            using var rig = RealRig.CreateSignedOut(api);
            var flow = rig.Provider.GetRequiredService<Lifestream.Client.Navigation.IAppFlow>();
            flow.Start();
            var login = rig.Windows.Login ?? throw new InvalidOperationException("the app flow did not open the sign-in screen");

            void Save(Hosted host, string name)
            {
                var png = host.Capture(name + ".png");
                var frame = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                if (frame.PixelWidth < Width || frame.PixelHeight < Height)
                {
                    problems.Add($"{name}.png is {frame.PixelWidth}x{frame.PixelHeight}");
                }

                saved.Add(name);
            }

            // 1. The sign-in screen, as a visitor first sees it.
            using (var loginHost = Hosted.Create(new LoginView { DataContext = login }, Width, Height))
            {
                loginHost.Settle(300);
                Save(loginHost, "login");
            }

            // 2. "Try the demo": the real command, against the real API; it opens the shell and its first screen.
            PerfTests.Pump(login.DemoCommand.ExecuteAsync());
            var session = rig.Provider.GetRequiredService<Lifestream.Client.Session.ISessionService>();
            if (session.User is not { IsDemo: true })
            {
                problems.Add("'Try the demo' did not sign in the demo account");
                return (problems, observed, saved);
            }

            var shell = rig.Windows.Main ?? throw new InvalidOperationException("'Try the demo' did not open the main window");
            using var host = Hosted.Create(new ShellView { DataContext = shell }, Width, Height);
            host.Settle(1500);

            foreach (var screen in Screens)
            {
                if (screen != "Dashboard")
                {
                    PerfTests.Pump(shell.OpenAsync(shell.Items.Single(i => i.Destination.Title == screen).Destination));
                    host.Settle(700);
                }
                else
                {
                    host.Settle(500);
                }

                var page = shell.CurrentPage ?? throw new InvalidOperationException($"{screen} did not open");
                if (page.PageTitle != screen)
                {
                    problems.Add($"{screen}: the shell is showing '{page.PageTitle}'");
                    continue;
                }

                if (page is DashboardViewModel dashboard)
                {
                    observed = dashboard.Cards.ToDictionary(c => c.Title, c => c.Value);
                    problems.AddRange(CardMismatches(dashboard.Cards, expected));
                    problems.AddRange(dashboard.Cards.Where(c => c.Title is "Units in stock").Where(c => c.Value <= 0).Select(c => "dashboard shows no units in stock"));
                }
                else if (page is not Lifestream.Client.Features.Stock.StockViewModel)
                {
                    // A list screen opens its first record so the detail pane shows real data too (the README shows it).
                    dynamic list = page;
                    if (list.Items.Count == 0)
                    {
                        problems.Add($"{screen}: the list has no rows");
                        continue;
                    }

                    PerfTests.Pump((Task)((Func<Task>)(() =>
                    {
                        list.SelectedItem = list.Items[0];
                        return (Task)list.PendingDetail;
                    }))());
                    host.Settle(600);
                }

                var screenProblems = Problems(host, page, screen);
                problems.AddRange(screenProblems);
                if (screenProblems.Count == 0)
                {
                    Save(host, screen.ToLowerInvariant());
                }
            }

            // 3. The account's own profile, from the account menu.
            PerfTests.Pump(shell.ProfileCommand.ExecuteAsync());
            host.Settle(700);
            var profile = shell.CurrentPage ?? throw new InvalidOperationException("the profile did not open");
            var profileProblems = Problems(host, profile, "Profile");
            problems.AddRange(profileProblems);
            if (profileProblems.Count == 0)
            {
                Save(host, "profile");
            }

            return (problems, observed, saved);
        });

        Assert.True(problems.Count == 0, string.Join("; ", problems));
        Assert.Equal(["login", "dashboard", "donors", "patients", "appointments", "requests", "stock", "receipts", "staff", "profile"], saved);

        // What the images show, for the manifest the script writes next to them.
        File.WriteAllText(
            Path.Combine(Hosted.CaptureDirectory, "dataset.json"),
            JsonSerializer.Serialize(new { dataset = "demo", generator = nameof(DemoDataGenerator), today = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), expected, dashboard = observed }));
    }

    // ---- the checks themselves can fail: they are exercised here without an API ----

    [Fact]
    public void CardMismatches_ReportsAWrongOrMissingDashboardCount()
    {
        var expected = new Dictionary<string, int> { ["Donors"] = 40, ["Patients"] = 35 };
        KpiCard[] right = [new("Donors", 40, "", "Donors", new(), ""), new("Patients", 35, "", "Patients", new(), "")];
        KpiCard[] wrong = [new("Donors", 128, "", "Donors", new(), "")];

        Assert.Empty(CardMismatches(right, expected));
        Assert.Equal(
            ["dashboard card 'Donors' shows 128, the demo dataset has 40", "dashboard card 'Patients' shows nothing, the demo dataset has 35"],
            CardMismatches(wrong, expected));
    }

    [Fact]
    public void ExpectedCards_AreTheDemoDatasetsCounts_FromTheGenerator()
    {
        var expected = ExpectedCards(new DateOnly(2026, 11, 19));

        Assert.Equal(SeedSizes.Donors, expected["Donors"]);
        Assert.Equal(SeedSizes.Patients, expected["Patients"]);
        Assert.Equal(SeedSizes.PendingAppointments, expected["Pending appointments"]);
        Assert.Equal(SeedSizes.PendingRequests, expected["Pending requests"]);
    }

    [Fact]
    public void Problems_FlagsAnErrorStateAndAnEmptyState_OnAScreenThatFailedToLoad()
    {
        var found = StaHost.Run(() =>
        {
            var viewModel = new FailedPage();
            var surface = new LoadSurface { State = LoadState.Error, ErrorMessage = "Something went wrong while loading this screen.", ErrorDetail = "The calling thread cannot access this object because a different thread owns it.", Content = new TextBlock { Text = "x" } };
            using var host = Hosted.Create(surface, Width, Height);
            host.Settle(300);
            var errors = Problems(host, viewModel, "Dashboard");

            surface.State = LoadState.Empty;
            host.Settle(300);
            viewModel.State = LoadState.Empty;
            var empties = Problems(host, viewModel, "Donors");
            return (errors, empties);
        });

        Assert.Contains(found.errors, p => p.Contains("an error state is on screen", StringComparison.Ordinal) && p.Contains("different thread", StringComparison.Ordinal));
        Assert.Contains(found.errors, p => p.Contains("not Ready", StringComparison.Ordinal));
        Assert.Contains(found.empties, p => p.Contains("an empty state is on screen", StringComparison.Ordinal));
    }

    private sealed class FailedPage : ViewModelBase
    {
        public FailedPage()
        {
            State = LoadState.Error;
        }

        public override string PageTitle => "Dashboard";
    }
}
