using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Appointments;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Lookups;
using Lifestream.Desktop.Features.Appointments;
using Lifestream.Desktop.Features.ListScreens;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// The row a list brings into view after a save is readable whole. Night-iso's isolated real-app walk: after booking from the panel the list moved to
/// Upcoming and selected the new row at its bottom edge, under the footer's 20 px fade (shown while more rows lie below), so the row read as half
/// hidden. Measured in the real app (run/agents/night-minors.md): row 786-830, viewport 263-830, more rows below. The window is never shown.
/// </summary>
[Collection("Wpf")]
public sealed class ListRevealTests
{
    [Fact]
    public void AppointmentBookedFromThePanel_ItsRowOnUpcoming_IsWhollyInView_AndClearOfTheFooterFade()
    {
        var f = StaHost.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            using var rig = Part2Harness.NewRig(); // 50 appointments on the page, three a day; the booked one is row 25, mid-page, with rows below it
            var booked = DemoData2.AppointmentDetail(25);
            rig.Api.On("BookAppointmentAsync", ApiResult<AppointmentDetail>.Ok(booked));
            rig.Api.On("GetAppointmentAsync", ApiResult<AppointmentDetail>.Ok(booked));
            var vm = Part2Harness.Create<AppointmentsViewModel>(rig);
            var view = new AppointmentsView();
            using var host = Hosted.Create(Part2Harness.OnCanvas(view, vm), 1440, 900);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() => vm.AddCommand.ExecuteAsync());
            host.Settle(300);
            var form = (AppointmentEditorViewModel)vm.Editor!;
            Hosted.Await(() =>
            {
                form.DonorOption = new RecordOption(101, DemoData.Name(1), "O+");
                return form.PendingDonor;
            });
            Hosted.Await(() =>
            {
                form.Date = DemoData2.BookingDay;
                return form.PendingSlots;
            });
            form.SelectedSlot = form.Slots.First(s => s.IsAvailable);
            Hosted.Await(() => form.SaveCommand.ExecuteAsync());
            Hosted.PumpUntil(() => vm.Editor is null && vm.SelectedItem?.Id == 25, 5000);
            host.Settle(600); // the refresh, then the reveal (queued at ContextIdle)

            var grid = (DataGrid)view.FindName("Grid");
            var viewer = Hosted.Descendants(grid).OfType<ScrollViewer>().First(v => ReferenceEquals(v.TemplatedParent, grid));
            var viewport = Hosted.Descendants(viewer).OfType<ScrollContentPresenter>().First();
            var row = Hosted.Descendants(grid).OfType<DataGridRow>().FirstOrDefault(r => r.IsVisible && r.Item is AppointmentListItem { Id: 25 });
            var fade = Hosted.Descendants(view).OfType<ListFooter>().SelectMany(Hosted.Descendants).OfType<Rectangle>().Single(r => r.Name == "Fade");

            // Everything measured in the view and given from the top of the rows' viewport (the fade is the footer's, not inside the viewport).
            Rect In(FrameworkElement e) => e.TransformToAncestor(view).TransformBounds(new Rect(e.RenderSize));
            var top = In(viewport).Top;
            var bounds = row is null ? Rect.Empty : In(row);
            var fadeTop = fade.IsVisible ? In(fade).Top - top : double.PositiveInfinity;
            return (Selected: vm.SelectedItem?.Id, View: vm.View, Found: row is not null, Top: bounds.Top - top, Bottom: bounds.Bottom - top, Height: viewport.ActualHeight, FadeTop: fadeTop);
        });

        Assert.Equal(25, f.Selected);
        Assert.Equal(AppointmentView.Upcoming, f.View); // the list moved to the tab that holds the new appointment
        Assert.True(f.Found, "the booked row is not in view at all");
        Assert.True(f.Top >= -0.5 && f.Bottom <= f.Height + 0.5, $"the booked row {f.Top:N1}-{f.Bottom:N1} is cut by the list's edge (0-{f.Height:N1})");
        Assert.True(f.Bottom <= f.FadeTop + 0.5, $"the booked row {f.Top:N1}-{f.Bottom:N1} lies under the footer's fade (from {f.FadeTop:N1})"); // the walk: 523-567 under 547-567
    }
}
