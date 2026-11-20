using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Lifestream.Client.Auth;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Shell;
using Lifestream.Desktop.Tests.Features;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// The real-app walk's defects 3 and 1, at the level of the views, through the automation peers a screen reader gets: every item of a list
/// has a name a person can read (2.1.0 to 2.1.2 read out "NavGroup { Title = Donations, Items = ... }", "Lifestream.Client.Shell.NavItem",
/// "StockMeter { BloodType = APos, ... }", and a data grid's rows and cells as their record's C# text), and a sidebar item chosen with UI
/// Automation's Select opens its page.
/// </summary>
public sealed partial class RealAppDefectViewTests
{
    /// <summary>The name of every element under <paramref name="root"/>, as UI Automation reports it.</summary>
    private static List<string> AutomationNames(UIElement root)
    {
        var names = new List<string>();
        void Walk(AutomationPeer peer, int depth)
        {
            names.Add(peer.GetName() ?? string.Empty);
            if (depth < 60 && peer.GetChildren() is { } children)
            {
                foreach (var child in children)
                {
                    Walk(child, depth + 1);
                }
            }
        }

        Walk(UIElementAutomationPeer.CreatePeerForElement(root) ?? throw new InvalidOperationException("the view has no automation peer"), 0);
        return names;
    }

    /// <summary>A record's or a class's C# text: "Type { A = 1, ... }", a namespace-qualified type name, or WPF's "Item: ..., Column Display Index: n".</summary>
    private static bool IsCSharpText(string name) =>
        CSharpRecord().IsMatch(name) || name.StartsWith("Lifestream.", StringComparison.Ordinal) || name.Contains("Column Display Index", StringComparison.Ordinal)
        || name.StartsWith("System.", StringComparison.Ordinal);

    [GeneratedRegex(@"\w+ \{ \w+ = ")]
    private static partial Regex CSharpRecord();

    [Fact]
    public void ListItems_OfTheShellTheDashboardTheSignInScreenAndADataGrid_AreNamedForScreenReaders_NeverAfterTheirCSharpType()
    {
        var f = StaHost.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));

            // The shell, on the dashboard, with the notifications open.
            List<string> shellNames;
            using (var rig = SignedIn(Role.Admin))
            using (var shell = rig.Get<ShellViewModel>())
            {
                Hosted.Await(() => shell.OpenByTitleAsync("Dashboard"));
                shell.Items.Single(i => i.Title == "Requests").Badge = "40";
                var view = new ShellView { DataContext = shell };
                using var host = Hosted.Create(view, 1440, 900);
                host.Settle(400);
                shell.Notifications.ToggleCommand.Execute(null);
                host.Settle(400);
                shellNames = AutomationNames(view);
            }

            // The dashboard with the demo data (today's appointments, the stock meters, what needs attention).
            List<string> dashboardNames;
            using (var rig = FeatureHarness.NewRig())
            {
                var vm = FeatureHarness.Create<DashboardViewModel>(rig);
                var view = FeatureHarness.DashboardView(vm);
                using var host = Hosted.Create(view, 1366, 900);
                Hosted.Await(() => vm.LoadAsync());
                host.Settle(400);
                dashboardNames = AutomationNames(view);
            }

            // The sign-in screen, wide enough for the compatibility matrix.
            List<string> signInNames;
            using (var rig = new Rig(dispatcher: WpfTestUi.Dispatcher()))
            {
                var view = new LoginView { DataContext = rig.Get<LoginViewModel>() };
                using var host = Hosted.Create(view, 1240, 800);
                host.Settle(300);
                signInNames = AutomationNames(view);
            }

            // A list screen: the rows and cells of the shared data grid.
            List<string> donorNames;
            using (var rig = FeatureHarness.NewRig())
            {
                var vm = FeatureHarness.Create<DonorsViewModel>(rig);
                var view = FeatureHarness.DonorsView(vm);
                using var host = Hosted.Create(view, 1366, 736);
                Hosted.Await(() => vm.LoadAsync());
                host.Settle(500);
                donorNames = AutomationNames(view);
            }

            return (shellNames, dashboardNames, signInNames, donorNames);
        });

        var all = f.shellNames.Concat(f.dashboardNames).Concat(f.signInNames).Concat(f.donorNames).ToList();
        Assert.Empty(all.Where(IsCSharpText).Distinct().Take(8)); // released 2.1.2: NavGroup { ... }, Lifestream.Client.Shell.NavItem, StockMeter { ... }, DonorListItem { ... }

        // And what a screen reader does read: the sidebar's groups and items...
        Assert.Contains("Main", f.shellNames);
        Assert.Contains("Donations", f.shellNames);
        Assert.Contains("Donors", f.shellNames);
        Assert.Contains("Requests, 40 open", f.shellNames);

        // ...the dashboard's rows...
        Assert.Contains(f.dashboardNames, n => n.StartsWith("A+: ", StringComparison.Ordinal)); // a stock meter

        // ...the matrix of the sign-in screen...
        Assert.Contains("Donor A+", f.signInNames);
        Assert.Contains("A+ to A+: compatible", f.signInNames);
        Assert.Contains("A+ to O−: not compatible", f.signInNames);

        // ...and a row of a list with what it shows.
        Assert.Contains(f.donorNames, n => n.StartsWith(DemoData.Name(1), StringComparison.Ordinal) && n.Contains(", ", StringComparison.Ordinal));
    }

    [Fact]
    public void SidebarItem_ChosenWithUiAutomationSelect_OpensItsPage_AndTheHighlightKeepsFollowingThePage()
    {
        var f = StaHost.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            using var rig = SignedIn(Role.Admin);
            using var shell = rig.Get<ShellViewModel>();
            Hosted.Await(() => shell.OpenByTitleAsync("Dashboard"));
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(view, 1440, 900);
            host.Settle(300);
            RadioButton Item(string title) => host.All<RadioButton>().Single(r => r.DataContext is NavItem item && item.Title == title);
            void Select(string title) =>
                ((ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(Item(title)).GetPattern(PatternInterface.SelectionItem)).Select();
            string Checked() => string.Join(",", host.All<RadioButton>().Where(r => r.DataContext is NavItem && r.IsChecked == true).Select(r => ((NavItem)r.DataContext).Title));

            // What a screen reader's default action does for a radio button: Select. No click, no command.
            Select("Donors");
            host.Settle(500);
            var afterSelect = (shell.PageTitle, Checked());

            // The page changes by another way (the palette, a dashboard card): the highlight follows, so the Select did not cut the item's binding.
            Hosted.Await(() => shell.OpenByTitleAsync("Stock"));
            host.Settle(300);
            var afterNavigating = (shell.PageTitle, Checked());

            Select("Patients");
            host.Settle(500);
            return (afterSelect, afterNavigating, AfterSecondSelect: (shell.PageTitle, Checked()));
        });

        Assert.Equal(("Donors", "Donors"), f.afterSelect); // released 2.1.0: the highlight on Donors, the page still the Dashboard
        Assert.Equal(("Stock", "Stock"), f.afterNavigating);
        Assert.Equal(("Patients", "Patients"), f.AfterSecondSelect);
    }
}
