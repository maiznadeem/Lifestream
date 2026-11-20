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
/// The major defects of the real-app walk of 2.1.0 that only show on screen (run/agents/real-app-walk.md), in the shared detail pane: 12 (the "..." menu's Delete
/// item had no text) and 18 (the pane drew "Select a donor to see their details." over the record it was showing). The window is never shown.
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
            host.Settle(300);
            var pane = (RecordDetailPane)view.FindName("DetailPane"); // by name: the side panel is closed until a donor is chosen, and the menu is not opened here
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
}
