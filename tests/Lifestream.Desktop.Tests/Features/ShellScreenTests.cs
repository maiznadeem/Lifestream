using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Stock;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Desktop.Features.Dashboard;
using Lifestream.Desktop.Features.Donors;
using Lifestream.Desktop.Features.ListScreens;
using Lifestream.Desktop.Features.Patients;
using Lifestream.Desktop.Features.Stock;
using Lifestream.Desktop.Services;
using Lifestream.Desktop.Shell;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// The feature screens inside the real shell: found by the view locator, opened from the sidebar, and captured off-screen over the fake API with made-up
/// data (DemoData.cs) for development. They are not the README screenshots (INFRA-19): those come from the real demo dataset through the real API, see EndToEnd/DemoScreenshotTests.cs.
/// </summary>
[Collection("Wpf")]
public sealed class ShellScreenTests
{
    private const int Width = 1366;
    private const int Height = 768;

    private static readonly (string Title, string File, Type ViewModel, Type View)[] Screens =
    [
        ("Dashboard", "dashboard.png", typeof(DashboardViewModel), typeof(DashboardView)),
        ("Donors", "donors.png", typeof(DonorsViewModel), typeof(DonorsView)),
        ("Patients", "patients.png", typeof(PatientsViewModel), typeof(PatientsView)),
        ("Stock", "stock.png", typeof(StockViewModel), typeof(StockView)),
    ];

    private static (Hosted Host, ShellViewModel Shell, Rig Rig) OpenShell(int width = Width, int height = Height)
    {
        var rig = FeatureHarness.NewRig();
        rig.SignIn(Role.Admin);
        var shell = rig.Get<ShellViewModel>();
        var host = Hosted.Create(new ShellView { DataContext = shell }, width, height);
        return (host, shell, rig);
    }

    [Fact]
    public void ViewLocator_FindsTheViewOfEveryFeatureViewModel_AndOfTheEditors()
    {
        Assert.Equal(typeof(DashboardView), ViewLocator.Resolve(typeof(DashboardViewModel)));
        Assert.Equal(typeof(DonorsView), ViewLocator.Resolve(typeof(DonorsViewModel)));
        Assert.Equal(typeof(PatientsView), ViewLocator.Resolve(typeof(PatientsViewModel)));
        Assert.Equal(typeof(StockView), ViewLocator.Resolve(typeof(StockViewModel)));
        Assert.Equal(typeof(DonorEditorView), ViewLocator.Resolve(typeof(DonorEditorViewModel)));
        Assert.Equal(typeof(PatientEditorView), ViewLocator.Resolve(typeof(PatientEditorViewModel)));
        Assert.All(NavigationCatalog.Default.Where(d => Screens.Any(sc => sc.Title == d.Title)), d => Assert.DoesNotContain("Placeholder", d.ViewModelType.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Shell_OpensEachFeatureScreen_FromTheSidebar_WithItsViewAndOneLoad_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            var (host, shell, rig) = OpenShell();
            using (host)
            using (rig)
            {
                _ = shell.StartAsync();
                host.Settle(500);
                var result = new List<(string Title, Type? View, string Heading)>();
                foreach (var screen in Screens)
                {
                    var item = shell.Items.Single(i => i.Destination.Title == screen.Title);
                    if (screen.Title != "Dashboard")
                    {
                        Hosted.Await(() => shell.OpenAsync(item.Destination));
                        host.Settle(500);
                    }

                    var page = host.Find<ContentControl>("PageHost");
                    var view = host.All<FrameworkElement>().FirstOrDefault(e => e.GetType() == screen.View);
                    result.Add((screen.Title, view?.GetType(), shell.PageTitle));
                    Assert.NotNull(view);
                    Assert.Same(shell.CurrentPage, view.DataContext); // one view model per visit, set once by the host
                    Assert.NotNull(page);
                }

                return (result, Dashboard: rig.Api.CountOf("GetDashboardAsync"), Donors: rig.Api.CountOf("GetDonorsAsync"), Patients: rig.Api.CountOf("GetPatientsAsync"), Stock: rig.Api.CountOf("GetStockAsync"));
            }
        });

        Assert.Equal(Screens.Select(s => s.Title), facts.result.Select(r => r.Title));
        Assert.All(facts.result, r => Assert.Equal(r.Title, r.Heading));
        Assert.Equal((1, 1, 1, 1), (facts.Dashboard, facts.Donors, facts.Patients, facts.Stock)); // exactly one call per data set per visit
    }

    [Fact]
    public void FeatureViews_AllInteractiveControls_HaveAutomationNames_FakeApi()
    {
        var unnamed = StaHost.Run(() =>
        {
            var offenders = new List<string>();
            var (host, shell, rig) = OpenShell();
            using (host)
            using (rig)
            {
                foreach (var screen in Screens)
                {
                    Hosted.Await(() => shell.OpenAsync(shell.Items.Single(i => i.Destination.Title == screen.Title).Destination));
                    host.Settle(500);
                    if (screen.Title == "Donors")
                    {
                        var donors = (DonorsViewModel)shell.CurrentPage!;
                        Hosted.Await(() =>
                        {
                            donors.SelectedItem = donors.Items[0];
                            return donors.PendingDetail;
                        });
                        Hosted.Await(() => donors.AddCommand.ExecuteAsync());
                        host.Settle(400);
                    }

                    var page = host.Find<ContentControl>("PageHost");
                    foreach (var element in host.All<FrameworkElement>().Where(e => page.IsAncestorOf(e) && e.IsVisible
                        && e is ButtonBase or TextBoxBase or ComboBox or CheckBox or Selector or DataGrid))
                    {
                        if (element is ScrollBar or Thumb or RepeatButton or DataGridColumnHeader or ToggleButton { TemplatedParent: ComboBox })
                        {
                            continue;
                        }

                        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
                        if (peer is not null && string.IsNullOrWhiteSpace(peer.GetName()) && string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)))
                        {
                            offenders.Add($"{screen.Title}: {element.GetType().Name} {element.Name}");
                        }
                    }
                }
            }

            return offenders;
        });

        Assert.True(unnamed.Count == 0, string.Join("; ", unnamed));
    }

    [Fact]
    public void Shell_CtrlN_OpensTheNewDonorForm_AndCtrlF_FocusesTheSearchBox_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            var (host, shell, rig) = OpenShell();
            using (host)
            using (rig)
            {
                Hosted.Await(() => shell.OpenAsync(shell.Items.Single(i => i.Destination.Title == "Donors").Destination));
                host.Settle(500);
                var donors = (DonorsViewModel)shell.CurrentPage!;
                var focused = 0;
                donors.FocusSearchRequested += (_, _) => focused++;

                Hosted.Await(() => shell.SearchCommand.ExecuteAsync());
                Hosted.Await(() => shell.NewRecordCommand.ExecuteAsync());
                host.Settle(400);
                return (focused, Editor: donors.Editor?.Title, FormShown: host.All<DonorEditorView>().Any(v => v.IsVisible), DetailShown: host.All<RecordDetailPane>().Any(p => p.IsVisible));
            }
        });

        Assert.Equal(1, facts.focused);
        Assert.Equal("New donor", facts.Editor);
        Assert.True(facts.FormShown);
        Assert.False(facts.DetailShown); // the form takes the detail pane's place
    }

    // Captures of each screen over the FAKE API and its made-up data (DemoData.cs), for looking at a view while developing. They are not the README screenshots: those come from
    // the real demo dataset through the real API (EndToEnd/DemoScreenshotTests.cs, tools/generate-screenshots.ps1, INFRA-19).

    [Theory]
    [InlineData("Dashboard", "dashboard.png")]
    [InlineData("Donors", "donors.png")]
    [InlineData("Patients", "patients.png")]
    [InlineData("Stock", "stock.png")]
    public void FeatureScreen_IsCapturedFromFakeApiData_NotForTheReadme(string title, string fileName)
    {
        var size = StaHost.Run(() =>
        {
            var (host, shell, rig) = OpenShell();
            using (host)
            using (rig)
            {
                if (title == "Dashboard")
                {
                    _ = shell.StartAsync();
                    host.Settle(1500);
                }
                else
                {
                    Hosted.Await(() => shell.OpenAsync(shell.Items.Single(i => i.Destination.Title == title).Destination));
                    host.Settle(700);
                    if (title is "Donors" or "Patients")
                    {
                        dynamic page = shell.CurrentPage!;
                        Hosted.Await(() =>
                        {
                            page.SelectedItem = page.Items[0];
                            return (Task)page.PendingDetail;
                        });
                        host.Settle(500);
                    }
                }

                var png = FeatureHarness.Save(host, fileName);
                var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(new MemoryStream(png), System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
            }
        });

        Assert.True(size.PixelWidth >= 1366 && size.PixelHeight >= 768, $"{fileName} is {size.PixelWidth}x{size.PixelHeight}");
        Assert.True(File.Exists(Path.Combine(FeatureHarness.CaptureDirectory, fileName)));
    }
}
