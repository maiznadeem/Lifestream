using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Shell;
using Lifestream.Desktop.Features.Appointments;
using Lifestream.Desktop.Features.Profile;
using Lifestream.Desktop.Features.Receipts;
using Lifestream.Desktop.Features.Requests;
using Lifestream.Desktop.Features.Staff;
using Lifestream.Desktop.Services;
using Lifestream.Desktop.Shell;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>The part 2 screens inside the real shell: found by the view locator, opened from the sidebar with exactly one load, named for assistive technology, and captured over the fake API for development (the README screenshots come from the real demo dataset, see EndToEnd/DemoScreenshotTests.cs).</summary>
[Collection("Wpf")]
public sealed class Part2ShellScreenTests
{
    private const int Width = 1366;
    private const int Height = 768;

    private static readonly (string Title, string File, Type ViewModel, Type View)[] Screens =
    [
        ("Appointments", "appointments.png", typeof(AppointmentsViewModel), typeof(AppointmentsView)),
        ("Requests", "requests.png", typeof(RequestsViewModel), typeof(RequestsView)),
        ("Receipts", "receipts.png", typeof(ReceiptsViewModel), typeof(ReceiptsView)),
        ("Staff", "staff.png", typeof(StaffViewModel), typeof(StaffView)),
    ];

    private static (Hosted Host, ShellViewModel Shell, Lifestream.Client.Tests.Fakes.Rig Rig) OpenShell()
    {
        var rig = Part2Harness.NewRig();
        var shell = rig.Get<ShellViewModel>();
        var host = Hosted.Create(new ShellView { DataContext = shell }, Width, Height);
        return (host, shell, rig);
    }

    [Fact]
    public void ViewLocator_FindsTheViewOfEveryPart2ViewModel_EditorAndDialog()
    {
        Assert.Equal(typeof(AppointmentsView), ViewLocator.Resolve(typeof(AppointmentsViewModel)));
        Assert.Equal(typeof(AppointmentEditorView), ViewLocator.Resolve(typeof(AppointmentEditorViewModel)));
        Assert.Equal(typeof(RecordDonationDialogView), ViewLocator.Resolve(typeof(RecordDonationDialogViewModel)));
        Assert.Equal(typeof(RequestsView), ViewLocator.Resolve(typeof(RequestsViewModel)));
        Assert.Equal(typeof(RequestEditorView), ViewLocator.Resolve(typeof(RequestEditorViewModel)));
        Assert.Equal(typeof(FulfilRequestDialogView), ViewLocator.Resolve(typeof(FulfilRequestDialogViewModel)));
        Assert.Equal(typeof(ReceiptsView), ViewLocator.Resolve(typeof(ReceiptsViewModel)));
        Assert.Equal(typeof(ReceiptViewerView), ViewLocator.Resolve(typeof(ReceiptViewerViewModel)));
        Assert.Equal(typeof(StaffView), ViewLocator.Resolve(typeof(StaffViewModel)));
        Assert.Equal(typeof(StaffEditorView), ViewLocator.Resolve(typeof(StaffEditorViewModel)));
        Assert.Equal(typeof(ResetPasswordDialogView), ViewLocator.Resolve(typeof(ResetPasswordDialogViewModel)));
        Assert.Equal(typeof(ProfileView), ViewLocator.Resolve(typeof(ProfileViewModel)));
        Assert.Equal(typeof(ChangePasswordDialogView), ViewLocator.Resolve(typeof(ChangePasswordDialogViewModel)));
        Assert.All(NavigationCatalog.Default, d => Assert.DoesNotContain("Placeholder", d.ViewModelType.Name, StringComparison.Ordinal)); // every sidebar entry is a real screen now
        Assert.Equal(typeof(ProfileViewModel), NavigationCatalog.ProfileViewModelType);
    }

    [Fact]
    public void Shell_OpensEachPart2Screen_FromTheSidebar_WithItsViewAndOneLoad_FakeApi()
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
                    Hosted.Await(() => shell.OpenAsync(shell.Items.Single(i => i.Destination.Title == screen.Title).Destination));
                    host.Settle(500);
                    var view = host.All<FrameworkElement>().FirstOrDefault(e => e.GetType() == screen.View);
                    result.Add((screen.Title, view?.GetType(), shell.PageTitle));
                    Assert.NotNull(view);
                    Assert.Same(shell.CurrentPage, view.DataContext); // one view model per visit, set once by the host
                }

                Hosted.Await(() => shell.ProfileCommand.ExecuteAsync());
                host.Settle(500);
                var profile = host.All<ProfileView>().FirstOrDefault();
                return (result, Profile: profile is not null && ReferenceEquals(profile.DataContext, shell.CurrentPage), ProfileTitle: shell.PageTitle,
                    Calls: (rig.Api.CountOf("GetAppointmentsAsync"), rig.Api.CountOf("GetRequestsAsync"), rig.Api.CountOf("GetReceiptsAsync"), rig.Api.CountOf("GetStaffAsync"), rig.Api.CountOf("GetProfileAsync")));
            }
        });

        Assert.Equal(Screens.Select(s => s.Title), facts.result.Select(r => r.Title));
        Assert.All(facts.result, r => Assert.Equal(r.Title, r.Heading));
        Assert.True(facts.Profile);
        Assert.Equal("Profile", facts.ProfileTitle);
        Assert.Equal((1, 1, 1, 1, 1), facts.Calls); // exactly one call per data set per visit
    }

    [Fact]
    public void Part2Views_AllInteractiveControls_HaveAutomationNames_FakeApi()
    {
        var unnamed = StaHost.Run(() =>
        {
            var offenders = new List<string>();
            var (host, shell, rig) = OpenShell();
            using (host)
            using (rig)
            {
                void Check(string where)
                {
                    var page = host.Find<ContentControl>("PageHost");
                    foreach (var element in host.All<FrameworkElement>().Where(e => page.IsAncestorOf(e) && e.IsVisible
                        && e is ButtonBase or TextBoxBase or ComboBox or CheckBox or Selector or DataGrid or Lifestream.Desktop.Controls.PasswordField))
                    {
                        if (element is ScrollBar or Thumb or RepeatButton or DataGridColumnHeader or ToggleButton { TemplatedParent: ComboBox } or ToggleButton { TemplatedParent: Lifestream.Desktop.Controls.PasswordField })
                        {
                            continue;
                        }

                        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
                        if (peer is not null && string.IsNullOrWhiteSpace(peer.GetName()) && string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)))
                        {
                            offenders.Add($"{where}: {element.GetType().Name} {element.Name}");
                        }
                    }
                }

                foreach (var screen in Screens)
                {
                    Hosted.Await(() => shell.OpenAsync(shell.Items.Single(i => i.Destination.Title == screen.Title).Destination));
                    host.Settle(500);
                    dynamic page = shell.CurrentPage!;
                    Hosted.Await(() =>
                    {
                        page.SelectedItem = page.Items[0];
                        return (Task)page.PendingDetail;
                    });
                    host.Settle(300);
                    Check(screen.Title + " (detail)");
                    if (screen.Title != "Receipts")
                    {
                        Hosted.Await(() => (Task)page.AddCommand.ExecuteAsync());
                        host.Settle(500);
                        Check(screen.Title + " (form)");
                    }
                }

                Hosted.Await(() => shell.ProfileCommand.ExecuteAsync());
                host.Settle(500);
                Check("Profile");
            }

            return offenders;
        });

        Assert.True(unnamed.Count == 0, string.Join("; ", unnamed));
    }

    // Captures of each screen over the FAKE API and its made-up data (DemoData.cs), for looking at a view while developing. They are not the README screenshots: those come from
    // the real demo dataset through the real API (EndToEnd/DemoScreenshotTests.cs, tools/generate-screenshots.ps1, INFRA-19).

    [Theory]
    [InlineData("Appointments", "appointments.png")]
    [InlineData("Requests", "requests.png")]
    [InlineData("Receipts", "receipts.png")]
    [InlineData("Staff", "staff.png")]
    [InlineData("Profile", "profile.png")]
    public void Part2Screen_IsCapturedFromFakeApiData_NotForTheReadme(string title, string fileName)
    {
        var size = StaHost.Run(() =>
        {
            var (host, shell, rig) = OpenShell();
            using (host)
            using (rig)
            {
                if (title == "Profile")
                {
                    Hosted.Await(() => shell.ProfileCommand.ExecuteAsync());
                    host.Settle(700);
                }
                else
                {
                    Hosted.Await(() => shell.OpenAsync(shell.Items.Single(i => i.Destination.Title == title).Destination));
                    host.Settle(700);
                    dynamic page = shell.CurrentPage!;
                    Hosted.Await(() =>
                    {
                        page.SelectedItem = page.Items[0];
                        return (Task)page.PendingDetail;
                    });
                    host.Settle(600);
                }

                var png = Part2Harness.Save(host, fileName);
                var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(new MemoryStream(png), System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
            }
        });

        Assert.True(size.PixelWidth >= 1366 && size.PixelHeight >= 768, $"{fileName} is {size.PixelWidth}x{size.PixelHeight}");
        Assert.True(File.Exists(Path.Combine(Part2Harness.CaptureDirectory, fileName)));
    }
}
