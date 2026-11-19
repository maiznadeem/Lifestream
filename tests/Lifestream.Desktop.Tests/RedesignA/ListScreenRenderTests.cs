using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Tests.Fakes;
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
public sealed class ListScreenRenderTests
{
    /// <summary>Where the renders go: LIFESTREAM_CAPTURE_DIR, else the run's phase 2 captures folder, else the test output.</summary>
    public static string Directory { get; } = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } env
        ? env
        : System.IO.Directory.Exists(@"G:\Claude\workspace\lifestream\run") ? @"G:\Claude\workspace\lifestream\run\captures\redesign-a\phase2" : Hosted.CaptureDirectory;

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
            host.Settle(600);
            var png = Save(host, $"donors-{size}-{theme}.png");
            var panel = host.Find<SidePanel>("Panel");
            var grid = host.Find<LifestreamDataGrid>("Grid");
            var book = host.All<Button>().First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Book appointment");
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            return (Png: png, PanelWidth: panel.ActualWidth, Panel: Hosted.BoundsOf(panel, host.Element), Book: Hosted.BoundsOf(book, host.Element), area,
                Columns: grid.Columns.Where(c => c.Visibility == Visibility.Visible).Select(c => c.Header as string).ToList(), Texts: texts, Card: vm.EligibilityCard,
                Eligibility: grid.Columns.First(c => (c.Header as string) == "Eligibility").ActualWidth);
        });

        var compact = width < Responsive.StandardWidth;
        Assert.Equal(compact ? 352 : 420, facts.PanelWidth);
        Assert.True(facts.Panel.Right <= facts.area.Width + 0.5 && facts.Book.Bottom <= facts.area.Height, $"the panel is clipped: {facts.Panel}");
        Assert.Contains("Adrian Mukherjee", facts.Texts);
        Assert.Contains("Donor D-0175", facts.Texts);
        Assert.Contains("Eligible to donate", facts.Texts);
        Assert.Equal("Last donated 29 Jul 2026, 113 days ago.", facts.Card?.Message);
        Assert.Contains("Until 29 Jan", facts.Texts); // Bianca Whitlock, last donated 31 Oct
        Assert.Contains("First-time", facts.Texts); // Elio Bergström
        Assert.Equal(["Name", "Group", "Eligibility"], facts.Columns.Take(3));
        Assert.Equal(130, facts.Eligibility, 0.5); // fixed columns keep their width after the side panel opens (ListColumns.RestoreWidths)
        if (width >= 1920)
        {
            Assert.Contains("Donates", facts.Columns);
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
            return (PanelWidth: panel.ActualWidth, Panel: Hosted.BoundsOf(panel, host.Element), area, Texts: texts,
                Columns: grid.Columns.Where(c => c.Visibility == Visibility.Visible).Select(c => c.Header as string).ToList(),
                Requests: grid.Columns.First(c => (c.Header as string) == "Requests").ActualWidth);
        });

        Assert.Equal(width < Responsive.StandardWidth ? 352 : 420, facts.PanelWidth);
        Assert.True(facts.Panel.Right <= facts.area.Width + 0.5, $"the panel is clipped: {facts.Panel}");
        Assert.Contains("Patient P-0053", facts.Texts);
        Assert.Contains("2 requests waiting for blood", facts.Texts);
        Assert.Contains("2 waiting", facts.Texts);
        Assert.Equal(["Name", "Group", "Requests"], facts.Columns.Take(3));
        Assert.Equal(140, facts.Requests, 0.5);
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
            Hosted.Await(() =>
            {
                vm.SelectedItem = vm.Items[5];
                return vm.PendingDetail;
            });
            host.Settle(600);
            Save(host, $"appointments-{size}-{theme}.png");
            var panel = host.Find<SidePanel>("Panel");
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            return (PanelWidth: panel.ActualWidth, Panel: Hosted.BoundsOf(panel, host.Element), area, Texts: texts, Banner: vm.DonorEligibility.Headline);
        });

        Assert.Equal(width < Responsive.StandardWidth ? 352 : 420, facts.PanelWidth);
        Assert.True(facts.Panel.Right <= facts.area.Width + 0.5, $"the panel is clipped: {facts.Panel}");
        Assert.Contains("Today, Thu 19 Nov", facts.Texts);
        Assert.Contains("Yesterday, Wed 18 Nov", facts.Texts);
        Assert.Contains("11:00", facts.Texts);
        Assert.Contains("Thu 19 Nov 2026, 11:00", facts.Texts);
        Assert.Equal("Eligible to donate", facts.Banner); // last donated 3 Aug: 108 days before the appointment
        Assert.Contains("Eligible to donate", facts.Texts);
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
            Hosted.Await(() =>
            {
                vm.SelectedItem = vm.Items[0];
                return vm.PendingDetail;
            });
            host.Settle(300);
            Save(host, $"requests-{size}-{theme}.png");
            var badges = host.All<Badge>().Where(b => b.IsVisible).Select(b => (b.Text, b.Kind)).ToList();

            // The fulfil step, in the side panel.
            Hosted.Await(() =>
            {
                _ = vm.FulfilCommand.ExecuteAsync();
                return vm.Fulfilment!.PendingStock;
            });
            host.Settle(400);
            Save(host, $"requests-fulfil-{size}-{theme}.png");
            var panel = host.Find<SidePanel>("Panel");
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Hosted.Await(() => vm.Fulfilment!.CancelCommand.ExecuteAsync());
            host.Settle(100);
            return (PanelWidth: panel.ActualWidth, Panel: Hosted.BoundsOf(panel, host.Element), area, Texts: texts, Badges: badges, Closed: vm.Fulfilment is null);
        });

        Assert.Equal(width < Responsive.StandardWidth ? 352 : 420, facts.PanelWidth);
        Assert.True(facts.Panel.Right <= facts.area.Width + 0.5, $"the panel is clipped: {facts.Panel}");
        Assert.Contains(("Today", BadgeKind.Critical), facts.Badges);
        Assert.Contains(("Overdue 2 days", BadgeKind.Critical), facts.Badges);
        Assert.Contains(("Tomorrow", BadgeKind.Warning), facts.Badges);
        Assert.Contains(("In 3 days", BadgeKind.Warning), facts.Badges);
        Assert.Contains("Fulfil this request", facts.Texts);
        Assert.Contains("In stock: 4 units of AB+", facts.Texts);
        Assert.True(facts.Closed);
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
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
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
        var png = host.Capture();
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path.Combine(Directory, name), png);
        return png;
    }
}
