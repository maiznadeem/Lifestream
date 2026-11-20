using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Appointments;
using Lifestream.Desktop.Features.Donors;
using Lifestream.Desktop.Features.Patients;
using Lifestream.Desktop.Features.Requests;
using Lifestream.Desktop.Theme;
using Microsoft.Extensions.DependencyInjection;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// A list's columns come out the same whether its rows arrive before or after the work the first layout queued. In the app the server always answers after
/// it; a render test's fake answers within the same few milliseconds, so which came first was a matter of the machine's load. The Appointments table lost that
/// race on a busy CI runner: its Time column stayed at the grid's minimum width and "09:00" ended in an ellipsis (a grouped WPF DataGrid does not know its
/// own width until its first row exists, so it squeezed the fixed columns and never gave the width back).
/// </summary>
[Trait("Category", "RenderA")]
[Collection("Wpf")]
public sealed class ListColumnsLateRowsTests
{
    public static TheoryData<string, int, int> Cases
    {
        get
        {
            var cases = new TheoryData<string, int, int>();
            foreach (var screen in new[] { "donors", "patients", "appointments", "requests" })
            {
                cases.Add(screen, 1440, 900);
                cases.Add(screen, 1920, 1080);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheColumns_AreTheSame_WhenTheRowsArriveAfterTheFirstLayoutsWork(string screen, int width, int height)
    {
        var early = Render(screen, width, height, rowsArriveLate: false);
        var late = Render(screen, width, height, rowsArriveLate: true);

        Assert.True(late.Problems.Count == 0, $"{screen} {width}x{height}, rows arriving late: {string.Join(" | ", late.Problems)}");
        Assert.True(early.Problems.Count == 0, $"{screen} {width}x{height}: {string.Join(" | ", early.Problems)}");
        Assert.NotEmpty(late.Columns);
        // A column declared with a width has it; and the rest share the room as they do when the rows are there from the start.
        Assert.All(late.Columns.Where(c => c.Declared is not null), c => Assert.Equal(c.Declared!.Value, c.Actual));
        Assert.Equal(early.Columns, late.Columns);
    }

    /// <summary>The time of day is not cut at any hour: the widest digits of the font fit the Time column, with the "Next" mark under it.</summary>
    [Fact]
    public void TheTimeColumn_FitsTheWidestTime()
    {
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            AppointmentDesignData.Configure(rig.Api);
            var vm = ActivatorUtilities.CreateInstance<AppointmentsViewModel>(rig.Provider);
            vm.Attach(null, new ListScreensClock());
            vm.Zone = TimeZoneInfo.Utc;
            var view = new AppointmentsView { DataContext = vm };
            using var host = Hosted.Create(ListScreenRenderTests.PageArea(view, 1440, 900, out var area), area.Width, area.Height);
            StaHost.Drain();
            Hosted.Await(() => vm.LoadAsync());
            Settle(host);
            var cell = host.All<DataGridCell>().First(c => c.IsVisible && c.Column.DisplayIndex == 0);
            var text = Hosted.Descendants(cell).OfType<TextBlock>().First();
            var room = text.ActualWidth;
            var widest = 0.0;
            for (var hour = 0; hour < 24; hour++)
            {
                foreach (var minute in new[] { 0, 30 })
                {
                    widest = Math.Max(widest, new System.Windows.Media.FormattedText(
                        $"{hour:00}:{minute:00}", System.Globalization.CultureInfo.InvariantCulture, text.FlowDirection,
                        new System.Windows.Media.Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, System.Windows.Media.Brushes.Black, null,
                        System.Windows.Media.TextOptions.GetTextFormattingMode(text), System.Windows.Media.VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace);
                }
            }

            return (Room: room, Widest: widest, Column: cell.Column.ActualWidth, Minimum: cell.Column.MinWidth);
        });

        Assert.Equal(76, facts.Column);
        Assert.Equal(76, facts.Minimum); // the grid may not take width from it
        Assert.True(facts.Widest <= facts.Room, $"the widest time is {facts.Widest} wide and the Time cell has {facts.Room}");
    }

    private static (List<string> Problems, List<(string? Header, double Actual, double? Declared)> Columns) Render(string screen, int width, int height, bool rowsArriveLate) =>
        StaHost.Run(() =>
        {
            ThemeService.Apply(ThemeMode.Light);
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            var (view, model) = Screen(screen, rig);
            using var host = Hosted.Create(ListScreenRenderTests.PageArea(view, width, height, out var area), area.Width, area.Height);
            if (rowsArriveLate)
            {
                StaHost.Drain(); // everything the first layout queued has run before the server answers
            }

            Hosted.Await(() => model.LoadAsync());
            Settle(host);
            var grid = host.Find<LifestreamDataGrid>("Grid");
            var columns = grid.Columns.Where(c => c.Visibility == Visibility.Visible)
                .Select(c => (c.Header as string, c.ActualWidth, c.Width.IsAbsolute ? c.Width.Value : (double?)null)).ToList();
            var problems = RenderChecks.Problems(host, $"{screen}-late-rows");
            // A blood group tag has the room it asks for: WPF clips an element that was given less, and a clipped tag loses its right edge.
            foreach (var tag in host.All<BloodGroupTag>().Where(t => t.IsVisible && host.All<DataGridCell>().Any(c => c.IsAncestorOf(t))))
            {
                if (System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(tag) is not null)
                {
                    problems.Add($"the blood group tag \"{tag.Text}\" is clipped by its column ({tag.ActualWidth} wide)");
                }
            }

            return (problems, columns);
        });

    /// <summary>The queued work runs to the end, with the layout it asks for (no time has to pass: nothing here waits on a clock).</summary>
    private static void Settle(Hosted host)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            StaHost.Drain();
            host.Element.UpdateLayout();
        }
    }

    private static (FrameworkElement View, ViewModelBase Model) Screen(string screen, Rig rig)
    {
        switch (screen)
        {
            case "donors":
            {
                ListScreensDesignData.ConfigureDonors(rig.Api);
                var vm = ActivatorUtilities.CreateInstance<DonorsViewModel>(rig.Provider);
                vm.Attach(null, new ListScreensClock());
                return (new DonorsView { DataContext = vm }, vm);
            }

            case "patients":
            {
                PatientDesignData.Configure(rig.Api);
                var vm = ActivatorUtilities.CreateInstance<PatientsViewModel>(rig.Provider);
                vm.Attach(null, new ListScreensClock());
                return (new PatientsView { DataContext = vm }, vm);
            }

            case "appointments":
            {
                AppointmentDesignData.Configure(rig.Api);
                var vm = ActivatorUtilities.CreateInstance<AppointmentsViewModel>(rig.Provider);
                vm.Attach(null, new ListScreensClock());
                vm.Zone = TimeZoneInfo.Utc;
                return (new AppointmentsView { DataContext = vm }, vm);
            }

            default:
            {
                RequestDesignData.Configure(rig.Api);
                var vm = ActivatorUtilities.CreateInstance<RequestsViewModel>(rig.Provider);
                vm.Attach(null, new ListScreensClock());
                return (new RequestsView { DataContext = vm }, vm);
            }
        }
    }
}
