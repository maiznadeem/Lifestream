using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Appointments;
using Lifestream.Desktop.Features.Donors;
using Lifestream.Desktop.Features.Patients;
using Lifestream.Desktop.Features.Receipts;
using Lifestream.Desktop.Features.Requests;
using Lifestream.Desktop.Features.Staff;
using Lifestream.Desktop.Features.Stock;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// A closed side panel costs nothing when its screen is shown (docs/perf, the UI-thread gap of a first visit): its content is a <see cref="DeferredContent"/>,
/// built the first time the panel opens, and kept from then on.
/// </summary>
[Collection("Wpf")]
public sealed class DeferredContentTests
{
    private sealed record Person(string Name);

    [Fact]
    public void DeferredContent_InAClosedSidePanel_IsBuiltOnlyWhenThePanelFirstOpens_WithTheDataContext_AndStaysBuilt()
    {
        var (whileClosed, afterOpen, text, sameAfterReopen) = StaHost.Run(() =>
        {
            var template = (DataTemplate)XamlReader.Parse(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding Name}\" /></DataTemplate>");
            var content = new DeferredContent { Template = template };
            var panel = new SidePanel { Content = content };
            var stage = new Grid { DataContext = new Person("Ayse Ozturk") };
            stage.Children.Add(panel);
            using var host = Hosted.Create(stage, 900, 600);
            host.Settle(50);
            var closed = content.IsBuilt;

            panel.IsOpen = true;
            host.Settle(50);
            var built = content.Child;
            var shown = (built as TextBlock)?.Text;

            panel.IsOpen = false;
            host.Settle(50);
            panel.IsOpen = true;
            host.Settle(50);
            return (closed, built is not null, shown, ReferenceEquals(built, content.Child));
        });

        Assert.False(whileClosed);
        Assert.True(afterOpen);
        Assert.Equal("Ayse Ozturk", text);
        Assert.True(sameAfterReopen);
    }

    public static TheoryData<string> Screens => new() { "Donors", "Appointments", "Patients", "Requests", "Receipts", "Staff", "Stock" };

    [Theory]
    [MemberData(nameof(Screens))]
    public void EveryScreenWithASidePanel_BuildsNoneOfThePanelsContent_WhileThePanelIsClosed(string screen)
    {
        var (deferred, built, elementsInPanel) = StaHost.Run(() =>
        {
            FrameworkElement view = screen switch
            {
                "Donors" => new DonorsView(),
                "Appointments" => new AppointmentsView(),
                "Patients" => new PatientsView(),
                "Requests" => new RequestsView(),
                "Receipts" => new ReceiptsView(),
                "Staff" => new StaffView(),
                _ => new StockView(),
            };
            using var host = Hosted.Create(view, 1440, 900);
            host.Settle(50);
            var panel = host.Find<SidePanel>("Panel");
            var content = panel.Content as DeferredContent;
            return (content is not null, content?.IsBuilt, LogicalTreeHelper.GetChildren(panel).OfType<DependencyObject>().SelectMany(Logical).Count());
        });

        Assert.True(deferred, $"the {screen} panel's content is not a DeferredContent");
        Assert.False(built);
        Assert.Equal(1, elementsInPanel); // the DeferredContent itself, nothing under it
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root) =>
        new[] { root }.Concat(LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().SelectMany(Logical));
}
