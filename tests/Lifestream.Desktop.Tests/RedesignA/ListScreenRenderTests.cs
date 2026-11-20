using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Appointments;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Appointments;
using Lifestream.Desktop.Features.Donors;
using Lifestream.Desktop.Features.Requests;
using Lifestream.Desktop.Features.ListScreens;
using Lifestream.Desktop.Features.Patients;
using Lifestream.Desktop.Theme;
using Microsoft.Extensions.DependencyInjection;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// Renders of the list screens in direction A (Donors, Patients, Appointments, Requests) at 1440 x 900, maximized on a 1920 x 1080 screen and in a 1024 x 700 window,
/// light and dark, from the design data through the real view models and views (off-screen; no window is shown). The render is the page area of the A shell (the window
/// without the sidebar or rail and the title bar), with the shell's page padding, so the side panel bleeds to the edges as in the app. Each render is checked for the
/// facts a broken layout would lose: nothing clipped, the panel's width for the breakpoint, the columns that fold away.
/// </summary>
[Trait("Category", "RenderA")]
[Collection("Wpf")] // the renders switch the app's theme: never at the same time as another WPF test
public sealed class ListScreenRenderTests
{
    /// <summary>Where the renders go: LIFESTREAM_CAPTURE_DIR, else the run's fix-round captures folder, else the test output.</summary>
    public static string Directory { get; } = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } env
        ? env
        : System.IO.Directory.Exists(@"G:\Claude\workspace\lifestream\run") ? @"G:\Claude\workspace\lifestream\run\captures\redesign-a\fix" : Hosted.CaptureDirectory;

    public static TheoryData<string, string, int, int> Sizes => new()
    {
        { "light", "1440", 1440, 900 },
        { "dark", "1440", 1440, 900 },
        { "light", "max", 1920, 1080 },
        { "dark", "max", 1920, 1080 },
        { "light", "narrow", 1024, 700 },
        { "dark", "narrow", 1024, 700 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Donors_RendersDirectionA(string theme, string size, int width, int height)
    {
        var result = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            ListScreensDesignData.ConfigureDonors(rig.Api);
            var vm = ActivatorUtilities.CreateInstance<DonorsViewModel>(rig.Provider);
            vm.Attach(null, new ListScreensClock());
            var view = new DonorsView { DataContext = vm };
            using var host = Hosted.Create(PageArea(view, width, height, out var area), area.Width, area.Height);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() =>
            {
                vm.SelectedItem = vm.Items[0];
                return vm.PendingDetail;
            });
            host.Settle(600);
            var png = Save(host, $"donors-{size}-{theme}.png");
            var panel = host.Find<SidePanel>("Panel");
            var grid = host.Find<LifestreamDataGrid>("Grid");
            var book = host.All<Button>().First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Book appointment");
            var add = host.Find<Button>("AddButton");
            var primaries = host.All<Button>().Count(b => b.IsVisible && ReferenceEquals(b.Style, b.TryFindResource("Button.Primary")));
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            var badges = host.All<Badge>().Where(b => b.IsVisible).Select(b => (b.Text, b.Kind, b.Icon)).ToList();
            if (!badges.Any(b => b.Text == "First-time"))
            {
                // In a compact window with the panel open the toolbar's chips take a second line, and the first-time donor's row is below the fold: bring it
                // into view to read its pill, then go back to the top for the states that follow.
                grid.ScrollIntoView(vm.Items.First(d => d.Name == "Elio Bergström"));
                host.Settle(250);
                badges.AddRange(host.All<Badge>().Where(b => b.IsVisible).Select(b => (b.Text, b.Kind, b.Icon)));
                grid.ScrollIntoView(vm.Items[0]);
                host.Settle(250);
            }

            var facts = (Png: png, PanelWidth: panel.ActualWidth, Panel: Hosted.BoundsOf(panel, host.Element), Book: Hosted.BoundsOf(book, host.Element), area,
                Columns: grid.Columns.Where(c => c.Visibility == Visibility.Visible).Select(c => c.Header as string).ToList(), Texts: texts, Card: vm.EligibilityCard,
                Name: grid.Columns.First(c => (c.Header as string) == "Name").ActualWidth, AddIsSecondary: ReferenceEquals(add.Style, add.TryFindResource("Button.Secondary")),
                Primaries: primaries, Badges: badges, ShownHistory: vm.VisibleHistory.Count, AllHistory: 0);

            // The whole history ("Show all 11"), and the list with no panel open (the header's action is the primary again).
            vm.ShowAllHistoryCommand.Execute(null);
            host.Settle(300);
            if (size == "1440")
            {
                Save(host, $"donors-history-{size}-{theme}.png");
            }

            facts.AllHistory = vm.VisibleHistory.Count;
            vm.CloseDetailCommand.Execute(null);
            host.Settle(300);
            var addAlone = ReferenceEquals(add.Style, add.TryFindResource("Button.Primary"));
            if (size == "1440")
            {
                Save(host, $"donors-nopanel-{size}-{theme}.png");
            }

            return (facts, addAlone);
        });

        var (facts, addAlone) = result;
        var compact = width < Responsive.StandardWidth;
        Assert.Equal(compact ? 352 : 420, facts.PanelWidth);
        Assert.True(facts.Panel.Right <= facts.area.Width + 0.5 && facts.Book.Bottom <= facts.area.Height, $"the panel is clipped: {facts.Panel}");
        Assert.Contains("Adrian Mukherjee", facts.Texts);
        Assert.Contains("Donor D-0175", facts.Texts);
        Assert.Contains("Eligible to donate", facts.Texts);
        Assert.Equal("Last donated 29 Jul 2026, 113 days ago.", facts.Card?.Message);
        Assert.Contains(("Until 29 Jan", BadgeKind.Neutral, IconKind.Clock), facts.Badges); // Bianca Whitlock, last donated 31 Oct: a clock with the date
        Assert.Contains(("First-time", BadgeKind.Info, IconKind.Info), facts.Badges); // Elio Bergström
        if (!compact)
        {
            Assert.Contains(("Not eligible", BadgeKind.Critical, IconKind.Alert), facts.Badges); // Fenna Galloway, 61 (below the fold in a compact window)
        }

        Assert.Equal(["Name", "Group", "Eligibility"], facts.Columns.Take(3));
        Assert.True(facts.Name <= 320.5, $"Name takes {facts.Name}; it is capped at 320 so the details share the room");
        // Paging: 50 of 118 on page 1 of 3, the range with an en dash.
        Assert.Contains("Showing 1\u201350 of 118", facts.Texts);
        Assert.Contains("Page 1 of 3", facts.Texts);
        // One primary: the panel's "Book appointment"; the header's "Add donor" is secondary while the panel is open, primary again once it closes.
        Assert.True(facts.AddIsSecondary);
        Assert.Equal(1, facts.Primaries);
        Assert.True(addAlone);
        // The donations: the total in the details, the history with bag codes and where each bag went.
        Assert.Contains("11 donations, 11 units in all", facts.Texts);
        Assert.Contains("Donation history", facts.Texts);
        Assert.Contains("Issued 12 Aug to Quinn Crowhurst", facts.Texts);
        Assert.Contains("B-4187", facts.Texts);
        Assert.Contains("Show all 11", facts.Texts);
        Assert.Equal(DonorsViewModel.HistoryPreviewCount, facts.ShownHistory);
        Assert.Equal(11, facts.AllHistory);
        if (!compact)
        {
            Assert.Contains("Donations", facts.Columns);
        }

        if (width >= 1920)
        {
            Assert.Contains("Age", facts.Columns);
        }

        if (compact)
        {
            Assert.DoesNotContain("Phone", facts.Columns);
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Patients_RendersDirectionA(string theme, string size, int width, int height)
    {
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            PatientDesignData.Configure(rig.Api);
            var vm = ActivatorUtilities.CreateInstance<PatientsViewModel>(rig.Provider);
            vm.Attach(null, new ListScreensClock());
            var view = new PatientsView { DataContext = vm };
            using var host = Hosted.Create(PageArea(view, width, height, out var area), area.Width, area.Height);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() =>
            {
                vm.SelectedItem = vm.Items[2];
                return vm.PendingDetail;
            });
            host.Settle(600);
            Save(host, $"patients-{size}-{theme}.png");
            var panel = host.Find<SidePanel>("Panel");
            var grid = host.Find<LifestreamDataGrid>("Grid");
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            var badges = host.All<Badge>().Where(b => b.IsVisible).Select(b => (b.Text, b.Kind)).ToList();
            var fulfils = host.All<Button>().Count(b => b.IsVisible && AutomationProperties.GetName(b) == "Fulfil this request");
            var enabledFulfils = host.All<Button>().Count(b => b.IsVisible && b.IsEnabled && AutomationProperties.GetName(b) == "Fulfil this request");
            return (PanelWidth: panel.ActualWidth, Panel: Hosted.BoundsOf(panel, host.Element), area, Texts: texts, Badges: badges, Fulfils: fulfils, EnabledFulfils: enabledFulfils, RowBorn: vm.Items[2].DateOfBirth, PanelBorn: vm.Detail?.DateOfBirth,
                Columns: grid.Columns.Where(c => c.Visibility == Visibility.Visible).Select(c => c.Header as string).ToList(),
                Name: grid.Columns.First(c => (c.Header as string) == "Name").ActualWidth);
        });

        Assert.Equal(width < Responsive.StandardWidth ? 352 : 420, facts.PanelWidth);
        Assert.True(facts.Panel.Right <= facts.area.Width + 0.5, $"the panel is clipped: {facts.Panel}");
        Assert.Contains("Patient P-0053", facts.Texts);
        Assert.Contains("2 requests waiting for blood", facts.Texts);
        Assert.Contains("2 waiting", facts.Texts);
        Assert.Equal(["Name", "Group", "Requests"], facts.Columns.Take(3));
        Assert.True(facts.Name <= 320.5, $"Name takes {facts.Name}");
        // Each waiting request with its cover (no O− in stock) and a Fulfil action; and the groups the patient can receive.
        Assert.Contains("Needed by 22 Nov 2026", facts.Texts);
        Assert.Contains("Needed by 30 Nov 2026", facts.Texts);
        Assert.Contains(("No stock", BadgeKind.Critical), facts.Badges);
        Assert.Contains(("In 3 days", BadgeKind.Warning), facts.Badges);
        Assert.Equal(2, facts.Fulfils);
        Assert.Equal(0, facts.EnabledFulfils); // no O− in stock: nothing to fulfil with (design-qa-2 N13)
        Assert.Contains("No O\u2212 in stock. The request stays open until stock arrives.", facts.Texts);
        Assert.Equal(facts.RowBorn, facts.PanelBorn); // the row and the panel give the same date of birth (one record, design-qa-2 N3)
        Assert.Contains("Can receive", facts.Texts);
        Assert.Contains("O\u2212", facts.Texts);
        Assert.Contains("Page 1 of 1", facts.Texts);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Appointments_RendersDirectionA(string theme, string size, int width, int height)
    {
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            AppointmentDesignData.Configure(rig.Api);
            var vm = ActivatorUtilities.CreateInstance<AppointmentsViewModel>(rig.Provider);
            vm.Attach(null, new ListScreensClock());
            vm.Zone = TimeZoneInfo.Utc;
            var view = new AppointmentsView { DataContext = vm };
            using var host = Hosted.Create(PageArea(view, width, height, out var area), area.Width, area.Height);
            Hosted.Await(() => vm.LoadAsync());
            // The Today agenda with nothing open: Record donation on every appointment still to come, emphasised on the Next one and quiet on the later ones.
            host.Settle(400);
            Save(host, $"appointments-today-{size}-{theme}.png");
            var checkIns = host.All<Button>().Count(b => b.IsVisible && AutomationProperties.GetName(b) == "Check in")
                + host.All<TextBlock>().Count(t => t.IsVisible && t.Text == "Check in");
            var recordButtons = host.All<Button>().Where(b => b.IsVisible && AutomationProperties.GetName(b) == "Record donation").ToList();
            var records = recordButtons.Count;
            var emphasised = recordButtons.Where(b => ReferenceEquals(b.Style, b.TryFindResource("Button.Secondary.Small"))).ToList();
            var nextRow = host.All<DataGridRow>().FirstOrDefault(r => r.IsVisible && r.Item is Lifestream.Contracts.Appointments.AppointmentListItem { DonorName: "Asher Devereux" });
            var emphasisedOnNext = emphasised.Count == 1 && nextRow is not null && nextRow.IsAncestorOf(emphasised[0]);
            var quiet = recordButtons.Count(b => ReferenceEquals(b.Style, b.TryFindResource("Button.Subtle.Small")));
            Hosted.Await(() =>
            {
                vm.SelectedItem = vm.Items.First(a => a.DonorName == "Asher Devereux");
                return vm.PendingDetail;
            });
            host.Settle(600);
            Save(host, $"appointments-{size}-{theme}.png");
            var panel = host.Find<SidePanel>("Panel");
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            var badges = host.All<Badge>().Where(b => b.IsVisible).Select(b => b.Text).ToList();
            var dayTexts = host.All<TextBlock>().Where(t => t.IsVisible && t.Text.StartsWith("Today,", StringComparison.Ordinal)).ToList();
            var todayInk = dayTexts.All(t => ReferenceEquals(t.Foreground, t.TryFindResource("Brush.Ink")));
            var facts = (PanelWidth: panel.ActualWidth, Panel: Hosted.BoundsOf(panel, host.Element), area, Texts: texts, Badges: badges, Banner: vm.DonorEligibility.Headline,
                CheckIns: checkIns, Records: records, EmphasisedOnNext: emphasisedOnNext, Quiet: quiet, TodayInk: todayInk, DayHeaders: dayTexts.Count, UpcomingTexts: new List<string>(), UpcomingError: (string?)null);

            // The Upcoming tab: the days after today, grouped by day.
            vm.CloseDetailCommand.Execute(null);
            AppointmentDesignData.Show(rig.Api, AppointmentView.Upcoming);
            Hosted.Await(() =>
            {
                vm.IsUpcomingView = true; // the tab, as a click sets it (inside the dispatcher, as in the app)
                return vm.PendingRefresh;
            });
            host.Settle(500);
            Save(host, $"appointments-upcoming-{size}-{theme}.png");
            facts.UpcomingTexts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            facts.UpcomingError = vm.HasError ? $"{vm.ErrorMessage} {vm.ErrorDetail}" : null;
            return facts;
        });

        Assert.Equal(width < Responsive.StandardWidth ? 352 : 420, facts.PanelWidth);
        Assert.True(facts.Panel.Right <= facts.area.Width + 0.5, $"the panel is clipped: {facts.Panel}");
        Assert.Contains("Today, Thu 19 Nov", facts.Texts);
        Assert.True(facts.DayHeaders > 0 && facts.TodayInk, "today's day header is Ink, not blood red");
        Assert.Contains("11:00", facts.Texts);
        Assert.Contains("Thu 19 Nov 2026, 11:00", facts.Texts);
        Assert.Contains("Appointment A-0905", facts.Texts);
        Assert.Equal("Eligible to donate", facts.Banner); // last donated 3 Aug: 108 days before the appointment
        Assert.Contains("Eligible to donate", facts.Texts);
        // Tabs with counts; the agenda's marks and inline actions.
        Assert.Contains("Today 8", facts.Texts);
        Assert.Contains("Upcoming 3", facts.Texts);
        Assert.Contains("Past 2", facts.Texts);
        Assert.Contains("Next", facts.Texts);
        Assert.Contains("First-time", facts.Badges);
        Assert.Equal(0, facts.CheckIns); // the API has no check-in state: no button or text says "Check in"
        Assert.Equal(5, facts.Records); // Asher (Next), Gideon, Elio, Anders and Hana
        Assert.True(facts.EmphasisedOnNext, "the Next row's Record donation is not the one emphasised (secondary) button");
        Assert.Equal(4, facts.Quiet); // the later rows keep the quiet button
        Assert.True(facts.UpcomingError is null, facts.UpcomingError);
        Assert.Contains("Tomorrow, Fri 20 Nov", facts.UpcomingTexts);
        Assert.DoesNotContain("Today, Thu 19 Nov", facts.UpcomingTexts);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Requests_RendersDirectionA(string theme, string size, int width, int height)
    {
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            RequestDesignData.Configure(rig.Api);
            var vm = ActivatorUtilities.CreateInstance<RequestsViewModel>(rig.Provider);
            vm.Attach(null, new ListScreensClock());
            var view = new RequestsView { DataContext = vm };
            using var host = Hosted.Create(PageArea(view, width, height, out var area), area.Width, area.Height);
            Hosted.Await(() => vm.LoadAsync());
            var order = vm.Items.Select(r => r.PatientName).ToList();
            Hosted.Await(() =>
            {
                vm.SelectedItem = vm.Items.First(r => r.Id == 148);
                return vm.PendingDetail;
            });
            host.Settle(300);
            Save(host, $"requests-{size}-{theme}.png");
            var badges = host.All<Badge>().Where(b => b.IsVisible).Select(b => (b.Text, b.Kind)).ToList();
            var grid = host.Find<LifestreamDataGrid>("Grid");
            var columns = grid.Columns.Where(c => c.Visibility == Visibility.Visible).Select(c => c.Header as string).ToList();
            var listTexts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();

            // The fulfil step, in the side panel.
            Hosted.Await(() =>
            {
                _ = vm.FulfilCommand.ExecuteAsync();
                return Task.WhenAll(vm.Fulfilment!.PendingStock, vm.Fulfilment!.PendingBags);
            });
            host.Settle(400);
            Save(host, $"requests-fulfil-{size}-{theme}.png");
            var panel = host.Find<SidePanel>("Panel");
            var panelWidth = panel.ActualWidth;
            var panelBounds = Hosted.BoundsOf(panel, host.Element);
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            var buttons = host.All<Button>().Where(b => b.IsVisible).Select(b => b.Content as string).ToList();
            var step = host.All<FulfilRequestDialogView>().Single();
            var footer = host.All<Button>().Where(b => b.IsVisible && step.IsAncestorOf(b) && b.Name is "PrimaryButton" or "CancelButton").Select(b => Hosted.BoundsOf(b, host.Element)).ToList();
            var bagList = host.All<ItemsControl>().First(i => i.Name == "BagList");
            var bagRows = Enumerable.Range(0, bagList.Items.Count).Select(i => ((FrameworkElement)bagList.ItemContainerGenerator.ContainerFromIndex(i)).ActualHeight).ToList();
            Hosted.Await(() => vm.Fulfilment!.CancelCommand.ExecuteAsync());
            host.Settle(100);
            var closed = vm.Fulfilment is null;

            // The Fulfilled tab: closed requests show their status, and no urgency or cover.
            vm.CloseDetailCommand.Execute(null);
            RequestDesignData.Show(rig.Api, Contracts.Requests.RequestView.Fulfilled);
            Hosted.Await(() =>
            {
                vm.IsFulfilledView = true;
                return vm.PendingRefresh;
            });
            host.Settle(400);
            Save(host, $"requests-fulfilled-{size}-{theme}.png");
            var closedColumns = grid.Columns.Where(c => c.Visibility == Visibility.Visible).Select(c => c.Header as string).ToList();
            return (PanelWidth: panelWidth, Panel: panelBounds, area, Texts: texts, ListTexts: listTexts, Badges: badges, Closed: closed, Order: order,
                Columns: columns, ClosedColumns: closedColumns, Buttons: buttons, Footer: footer, BagRows: bagRows);
        });

        Assert.Equal(width < Responsive.StandardWidth ? 352 : 420, facts.PanelWidth);
        Assert.True(facts.Panel.Right <= facts.area.Width + 0.5, $"the panel is clipped: {facts.Panel}");
        Assert.Equal("Leilani Okoro", facts.Order[0]); // overdue first, then by the date needed
        Assert.Contains(("Today", BadgeKind.Critical), facts.Badges);
        Assert.Contains(("Overdue 2 days", BadgeKind.Critical), facts.Badges);
        Assert.Contains(("Tomorrow", BadgeKind.Warning), facts.Badges);
        Assert.Contains(("In 3 days", BadgeKind.Warning), facts.Badges);
        Assert.Contains(("Can fulfil", BadgeKind.Success), facts.Badges); // Petra: 4 AB+ for 3
        Assert.Contains(("No stock", BadgeKind.Critical), facts.Badges); // Leilani: no AB−
        Assert.Contains(("1 short", BadgeKind.Warning), facts.Badges); // Talia: 2 A+ for 3
        Assert.Contains("Cover", facts.Columns);
        Assert.DoesNotContain("Status", facts.Columns); // an open view's rows are all pending
        Assert.Contains("Request R-0148", facts.ListTexts);
        Assert.Contains("Open 6", facts.ListTexts); // the tabs, with their counts
        Assert.Contains("Due in 48 h 3", facts.ListTexts);
        Assert.Contains("Fulfilled 2", facts.ListTexts);
        Assert.Contains("Fulfil this request", facts.Texts);
        Assert.Contains("In stock: 4 units of AB+", facts.Texts);
        Assert.Contains("B-0601", facts.Texts); // the oldest bag first
        Assert.Contains("Collected 14 Oct 2026 from Elio Bergstr\u00f6m", facts.Texts);
        Assert.Contains("Not now", facts.Buttons);
        Assert.DoesNotContain("Cancel", facts.Buttons);

        // "Fulfil request" and "Not now" stay in view at every size (1024 x 700 included: the step's content scrolls above them), and a bag is two lines (design-qa-2 N4).
        Assert.Equal(2, facts.Footer.Count);
        Assert.All(facts.Footer, b => Assert.True(b.Top >= facts.Panel.Top && b.Bottom <= facts.Panel.Bottom - 16, $"a footer button at {b} is outside the panel {facts.Panel}"));
        Assert.Equal(3, facts.BagRows.Count);
        Assert.All(facts.BagRows, h => Assert.True(h <= 66, $"a bag row is {h} DIP tall: more than two lines"));
        Assert.True(facts.Closed);
        Assert.Contains("Status", facts.ClosedColumns);
        Assert.DoesNotContain("Cover", facts.ClosedColumns);
        Assert.DoesNotContain("Urgency", facts.ClosedColumns);
    }

    /// <summary>The error state of a list (design-qa 21): the cause and the fix in plain words, Retry, and no count in the header while nothing is loaded.</summary>
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Donors_ErrorState_NamesTheCauseAndTheFix_AndHidesTheCount(string theme)
    {
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            rig.Api.On("GetDonorsAsync", Client.Api.ApiResult<Contracts.PagedResult<Contracts.Donors.DonorListItem>>.Fail(new Client.Api.ApiProblem(0, Client.Api.ClientProblemCodes.Unreachable, "No route to host", "corr-3")));
            var vm = ActivatorUtilities.CreateInstance<DonorsViewModel>(rig.Provider);
            vm.Attach(null, new ListScreensClock());
            var view = new DonorsView { DataContext = vm };
            using var host = Hosted.Create(PageArea(view, 1440, 900, out var area), area.Width, area.Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(400);
            Save(host, $"donors-error-1440-{theme}.png");
            var header = host.Find<PageHeader>("Header");
            return (Texts: host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList(), vm.HeaderCount, HeaderCount: header.Count, Error: vm.HasError);
        });

        Assert.True(facts.Error);
        Assert.Contains("Can't reach the server. Check your internet connection, then retry.", facts.Texts);
        Assert.Null(facts.HeaderCount); // no "0" while the list could not be loaded
        Assert.Null(facts.Item3);
    }

    public static TheoryData<string, string, int, int> EditorSizes => new()
    {
        { "light", "1440", 1440, 900 },
        { "dark", "narrow", 1024, 700 },
    };

    /// <summary>The donor form opens in the side panel, in place of the details: it fits the panel and its Save is on screen.</summary>
    [Theory]
    [MemberData(nameof(EditorSizes))]
    public void DonorEditor_InTheSidePanel(string theme, string size, int width, int height)
    {
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            ListScreensDesignData.ConfigureDonors(rig.Api);
            var vm = ActivatorUtilities.CreateInstance<DonorsViewModel>(rig.Provider);
            vm.Attach(null, new ListScreensClock());
            var view = new DonorsView { DataContext = vm };
            using var host = Hosted.Create(PageArea(view, width, height, out var area), area.Width, area.Height);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() =>
            {
                vm.SelectedItem = vm.Items[0];
                return vm.PendingDetail;
            });
            Hosted.Await(() => vm.EditCommand.ExecuteAsync());
            host.Settle(600);
            Save(host, $"donors-editor-{size}-{theme}.png");
            var panel = host.Find<SidePanel>("Panel");
            var save = host.All<Button>().First(b => b.IsVisible && (b.Content as string ?? AutomationProperties.GetName(b))?.StartsWith("Save", StringComparison.Ordinal) == true);
            var fieldsRight = host.All<Control>().Where(c => c.IsVisible && c is TextBox or ComboBox && c.IsDescendantOf(panel)).Max(c => Hosted.BoundsOf(c, host.Element).Right);
            return (Editing: vm.IsEditing, Panel: Hosted.BoundsOf(panel, host.Element), Save: Hosted.BoundsOf(save, host.Element), area, FieldsRight: fieldsRight);
        });

        Assert.True(facts.Editing);
        Assert.True(facts.Save.Right <= facts.Panel.Right && facts.Save.Left >= facts.Panel.Left, $"Save is outside the panel: {facts.Save} in {facts.Panel}");
        Assert.True(facts.Save.Bottom <= facts.area.Height, $"Save is below the window: {facts.Save}");
        Assert.True(facts.FieldsRight <= facts.Panel.Right - 23, $"a field runs past the panel's padding: right edge {facts.FieldsRight} in {facts.Panel}");
    }

    /// <summary>The keyboard of the list and its panel: Enter on a selected row moves the focus into the panel (its primary action); Esc in the table closes the panel.</summary>
    [Fact]
    public void Donors_EnterFocusesThePanel_EscClosesIt()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            ListScreensDesignData.ConfigureDonors(rig.Api);
            var vm = ActivatorUtilities.CreateInstance<DonorsViewModel>(rig.Provider);
            vm.Attach(null, new ListScreensClock());
            var view = new DonorsView { DataContext = vm };
            using var host = Hosted.Create(PageArea(view, 1440, 900, out var area), area.Width, area.Height);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() =>
            {
                vm.SelectedItem = vm.Items[0];
                return vm.PendingDetail;
            });
            host.Settle(300);
            var grid = host.Find<LifestreamDataGrid>("Grid");
            var source = PresentationSource.FromVisual(grid)!;
            grid.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Enter)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent,
            });
            host.Settle(200);
            var panel = host.Find<SidePanel>("Panel");
            var first = ListKeys.FirstFocusable((DependencyObject)panel.Content);
            var focusedName = first is null ? null : AutomationProperties.GetName(first);
            grid.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent,
            });
            host.Settle(200);
            return (focusedName, FocusWithin: first?.IsKeyboardFocusWithin == true || first?.IsFocused == true, Open: vm.IsDetailOpen, Selected: vm.SelectedItem);
        });

        Assert.Equal("Book appointment", facts.focusedName); // the panel's first control is its primary action
        Assert.False(facts.Open); // Esc closed the panel
        Assert.Null(facts.Selected);
    }

    /// <summary>
    /// The page area of the A shell around a page: the window minus the sidebar (the rail below 1280 DIP) and the 44 DIP title bar, the canvas colour, the shell's text
    /// defaults, the breakpoint of the window's width, and the page host's padding (the side panel bleeds through it, lists:PanelBleed).
    /// </summary>
    public static FrameworkElement PageArea(FrameworkElement page, int windowWidth, int windowHeight, out Size area)
    {
        var compact = windowWidth < Responsive.StandardWidth;
        area = new Size(windowWidth - (compact ? 52 : 236), windowHeight - 44);
        var root = new Grid { UseLayoutRounding = true, SnapsToDevicePixels = true };
        root.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
        root.SetResourceReference(TextElement.FontFamilyProperty, "Font.Family");
        root.SetResourceReference(TextElement.FontSizeProperty, "FontSize.Body");
        root.SetResourceReference(TextElement.ForegroundProperty, "Brush.Ink");
        TextDefaults.ApplyTo(root); // as every window of the app
        Responsive.SetBreakpoint(root, Responsive.For(windowWidth));
        var host = new ContentControl
        {
            Name = PanelBleed.HostName,
            Content = page,
            Margin = compact ? new Thickness(22, 18, 16, 16) : new Thickness(32, 24, 32, 24),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Focusable = false,
        };
        root.Children.Add(host);
        return root;
    }

    private static byte[] Save(Hosted host, string name)
    {
        RenderChecks.Check(host, name);
        if (name.StartsWith("donors-", StringComparison.Ordinal) && !name.Contains("error", StringComparison.Ordinal))
        {
            // The screen's own chips are in the toolbar at every size (in a compact window with the panel open they wrap onto a second line).
            var chips = host.All<Lifestream.Desktop.Features.ListScreens.FilterChip>().Where(c => c.IsVisible).Select(c => AutomationProperties.GetName(c)).ToList();
            Assert.Contains("Eligibility filter", chips);
            Assert.Contains("Last donated filter", chips);
        }

        var png = host.Capture();
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path.Combine(Directory, name), png);
        return png;
    }
}
