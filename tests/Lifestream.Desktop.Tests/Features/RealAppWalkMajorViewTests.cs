using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Desktop.Features.ListScreens;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// The defects of the real-app walk of 2.1.0 that only show on screen (run/agents/real-app-walk.md), in the shared detail pane: 12 (the "..." menu's Delete
/// item had no text), 18 (the pane drew "Select a donor to see their details." over the record it was showing) and 10 (the row of a record opened from another screen
/// was out of view). The window is never shown.
/// </summary>
[Collection("Wpf")]
public sealed class RealAppWalkMajorViewTests
{
    [Fact]
    public void DetailPane_TheMoreMenusDeleteItem_HasItsText_AndItsNameForAScreenReader()
    {
        var f = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<DonorsViewModel>(rig);
            var view = FeatureHarness.DonorsView(vm);
            using var host = Hosted.Create(view, 1440, 900);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() =>
            {
                vm.SelectedItem = vm.Items[0]; // the side panel builds its content when it first opens
                return vm.PendingDetail;
            });
            host.Settle(300);
            var pane = host.Find<RecordDetailPane>("DetailPane"); // by name: the menu is not opened here
            var item = (MenuItem)pane.FindName("DeleteItem");
            // The item's text is what its header shows: a string, or the text of the element in it.
            var text = item.Header as string ?? (item.Header as TextBlock)?.Text;
            return (Text: text, Name: AutomationProperties.GetName(item));
        });

        Assert.Equal("Delete donor", f.Text); // released 2.1.0: nothing (the header was bound to an ancestor that a context menu does not have)
        Assert.Equal("Delete donor", f.Name);
    }

    [Fact]
    public void DetailPane_ShowingARecordThatHasNoRowInTheList_DoesNotDrawTheSelectPlaceholderOverIt()
    {
        var f = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            rig.Api.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(new([], 0, 1, 50))); // the record is not in the list's view
            var vm = FeatureHarness.Create<DonorsViewModel>(rig);
            vm.ReceiveParameter(new ListPrefilter(RecordId: 1)); // opened by id, as a link from another screen or a record that left the view
            using var host = Hosted.Create(FeatureHarness.DonorsView(vm), 1440, 900);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.PumpUntil(() => vm.HasDetail, 5000);
            host.Settle(300);
            var pane = host.Find<RecordDetailPane>("DetailPane");
            var placeholder = (TextBlock)pane.FindName("NoSelection");
            var body = (FrameworkElement)pane.FindName("DetailBody");
            var shown = (HasDetail: vm.HasDetail, NoRow: vm.SelectedItem is null, Placeholder: placeholder.IsVisible, Body: body.IsVisible);

            vm.CloseDetailCommand.Execute(null);
            host.Settle(200);
            return (shown, PlaceholderWhenEmpty: placeholder.Visibility == Visibility.Visible, BodyWhenEmpty: body.Visibility == Visibility.Visible);
        });

        Assert.True(f.shown.HasDetail);
        Assert.True(f.shown.NoRow);
        Assert.True(f.shown.Body);
        Assert.False(f.shown.Placeholder, "the pane says \"Select a donor\" over the donor it is showing"); // released 2.1.0: both were drawn
        Assert.True(f.PlaceholderWhenEmpty); // nothing to show: the placeholder is back
        Assert.False(f.BodyWhenEmpty);
    }

    [Fact]
    public void List_ARecordOpenedByAnotherScreen_HasItsRowSelected_AndBroughtIntoView()
    {
        // Real-app walk, item 10: a donor opened from the search palette was in the panel while her row, far down the list, was out of view.
        var f = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig(); // 50 donors on the page, ids 1 to 50
            rig.Api.DonorById = id => ApiResult<DonorDetail>.Ok(DemoData.DonorDetail(id));
            var vm = FeatureHarness.Create<DonorsViewModel>(rig);
            vm.ReceiveParameter(new ListPrefilter(RecordId: 45));
            var view = FeatureHarness.DonorsView(vm);
            using var host = Hosted.Create(view, 1440, 900);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.PumpUntil(() => vm.HasDetail, 5000);
            host.Settle(600);
            var grid = (DataGrid)view.FindName("Grid");
            var item = vm.Items.Single(i => i.Id == 45);
            var row = grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
            var bounds = row is null ? Rect.Empty : Hosted.BoundsOf(row, grid);
            return (Selected: ReferenceEquals(grid.SelectedItem, item), Realized: row is not null, bounds.Top, bounds.Bottom, Height: grid.ActualHeight, Rows: vm.Items.Count);
        });

        Assert.Equal(50, f.Rows);
        Assert.True(f.Selected, "the opened donor's row is not selected");
        Assert.True(f.Realized, "the opened donor's row is not in view (the table has not made it)");
        Assert.True(f.Top >= 0 && f.Bottom <= f.Height + 0.5, $"the opened donor's row is outside the table: {f.Top:N0} to {f.Bottom:N0} of {f.Height:N0}");
    }
}
