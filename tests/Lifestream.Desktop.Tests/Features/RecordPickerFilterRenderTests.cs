using System.Windows.Automation;
using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Lookups;
using Lifestream.Contracts.Lookups;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Appointments;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// UI quirk (OWN-09 release prep): a record-picker filter with nothing chosen showed an empty box and a "Change" button. Now it shows the placeholder ("Any donor") and a "Choose"
/// action; "Change" and a clear (x) appear only once a record is chosen. Rendered off-screen from the fake API; the window is never shown.
/// </summary>
[Collection("Wpf")]
public sealed class RecordPickerFilterRenderTests
{
    private static string[] VisibleTexts(Hosted host) => [.. host.All<TextBlock>().Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text)];

    private static string[] VisibleButtons(Hosted host) => [.. host.All<Button>().Where(b => b.IsVisible).Select(b => b.Content as string ?? AutomationProperties.GetName(b))];

    private static Button Named(Hosted host, string name) => host.All<Button>().First(b => b.IsVisible && AutomationProperties.GetName(b) == name);

    private static Border Frame(RecordPicker picker)
    {
        var frame = new Border { Width = 320, Height = 80, Padding = new System.Windows.Thickness(12), Child = picker };
        frame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceCanvas");
        return frame;
    }

    private static RecordPicker NewFilter(string emptyText = "Any donor") => new() { Kind = RecordKind.Donor, Label = "Donor", EmptyText = emptyText };

    [Fact]
    public void FilterPicker_Empty_ShowsAnyDonorPlaceholderAndChoose_NotChange_NotClear_FakeApi()
    {
        using var rig = StaHostRig();
        var (texts, buttons) = StaHost.Run(() =>
        {
            using var host = Hosted.Create(Frame(NewFilter()), 320, 80);
            host.Settle(200);
            Part2Harness.Save(host, "RecordPickerFilter_Empty.png");
            return (VisibleTexts(host), VisibleButtons(host));
        });

        Assert.Contains("Any donor", texts);
        Assert.Contains("Choose", buttons);
        Assert.DoesNotContain("Change", buttons);
        Assert.DoesNotContain(buttons, b => b.Contains("Clear", StringComparison.Ordinal));
    }

    [Fact]
    public void FilterPicker_Chosen_ShowsChangeAndClear_NotChoose_AndClearEmptiesIt_FakeApi()
    {
        using var rig = StaHostRig();
        var facts = StaHost.Run(() =>
        {
            var picker = NewFilter();
            using var host = Hosted.Create(Frame(picker), 320, 80);
            host.Settle(200);
            picker.SelectedOption = new RecordOption(42, "Jane Q. Public", "B-");
            host.Settle(200);
            Part2Harness.Save(host, "RecordPickerFilter_Chosen.png");
            var chosenButtons = VisibleButtons(host);
            var chosenTexts = VisibleTexts(host);

            Hosted.Await(() => picker.ViewModel!.ClearCommand.ExecuteAsync());
            host.Settle(200);
            return (chosenButtons, chosenTexts, After: VisibleTexts(host), AfterButtons: VisibleButtons(host), picker.SelectedId);
        });

        Assert.Contains("Change", facts.chosenButtons);
        Assert.Contains("Clear selection", facts.chosenButtons);
        Assert.DoesNotContain("Choose", facts.chosenButtons);
        Assert.Contains("Jane Q. Public · D-0042 · B-", facts.chosenTexts); // "#42" until 2.1.3 (real-app walk 22)
        Assert.DoesNotContain("Any donor", facts.chosenTexts);
        Assert.Contains("Any donor", facts.After);
        Assert.Contains("Choose", facts.AfterButtons);
        Assert.Null(facts.SelectedId);
    }

    [Fact]
    public void FilterPicker_Choose_OpensTheSearchBoxWithItsHint_FakeApi()
    {
        using var rig = StaHostRig();
        var facts = StaHost.Run(() =>
        {
            var picker = NewFilter();
            using var host = Hosted.Create(Frame(picker), 320, 80);
            host.Settle(200);
            Named(host, "Choose record").Command!.Execute(null);
            host.Settle(300);
            var searchBoxes = host.All<TextBox>().Count(t => t.IsVisible);
            var hint = VisibleTexts(host).Contains("Search by name or ID");
            return (searchBoxes, hint, Choosing: picker.ViewModel!.IsChoosing);
        });

        Assert.Equal(1, facts.searchBoxes);
        Assert.True(facts.hint);
        Assert.True(facts.Choosing);
    }

    /// <summary>
    /// Direction A (audit A5): the Appointments donor filter is a chip of the command bar, not a field. With nothing chosen it reads "+ Donor" (no empty box, no Change, no
    /// clear); once a donor is chosen it reads "Donor: name" with an x that removes the filter.
    /// </summary>
    [Fact]
    public void AppointmentsView_DonorFilter_IsAChip_PlusDonorUntilChosen_ThenTheNameWithAClear_FakeApi()
    {
        using var rig = StaHostRig();
        var facts = StaHost.Run(() =>
        {
            var vm = AppointmentsModel(out var rigInner);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new AppointmentsView(), vm), 1366, 736);
            host.Settle(400);
            var chip = host.All<Lifestream.Desktop.Features.ListScreens.RecordFilterChip>().Single();
            string[] Read() => [.. Hosted.Descendants(chip).OfType<TextBlock>().Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text)];
            string[] Buttons() => [.. Hosted.Descendants(chip).OfType<Button>().Where(b => b.IsVisible).Select(b => b.Content as string ?? AutomationProperties.GetName(b))];
            var empty = (Texts: Read(), Buttons: Buttons(), Boxes: Hosted.Descendants(chip).OfType<TextBox>().Count(t => t.IsVisible));
            Part2Harness.Save(host, "AppointmentsView_DonorFilterEmpty.png");
            Hosted.Await(() =>
            {
                vm.DonorFilter = new RecordOption(101, "Jane Q. Public", "B-");
                return vm.PendingRefresh;
            });
            host.Settle(300);
            Part2Harness.Save(host, "AppointmentsView_DonorFilterChosen.png");
            var chosen = (Texts: Read(), Buttons: Buttons());
            rigInner.Dispose();
            return (empty, chosen);
        });

        Assert.Equal(["Donor"], facts.empty.Texts);
        Assert.Equal(0, facts.empty.Boxes); // a chip, not a fake text box
        Assert.DoesNotContain("Change", facts.empty.Buttons);
        Assert.DoesNotContain("Remove the donor filter", facts.empty.Buttons);
        Assert.Contains("Donor: Jane Q. Public", facts.chosen.Texts);
        Assert.Contains("Remove the donor filter", facts.chosen.Buttons);
    }

    [Fact]
    public void Picker_WithoutAViewModel_NeverShowsAnEmptyChipWithChange_FakeApi()
    {
        // The README screenshots once showed an empty box and "Change" because the picker had no view model (and so no data context) and its chip defaulted to visible.
        var previous = RecordPicker.ViewModelFactory;
        RecordPicker.ViewModelFactory = null;
        try
        {
            var buttons = StaHost.Run(() =>
            {
                using var host = Hosted.Create(Frame(NewFilter()), 320, 80);
                host.Settle(200);
                return VisibleButtons(host);
            });

            Assert.DoesNotContain("Change", buttons);
        }
        finally
        {
            RecordPicker.ViewModelFactory = previous;
        }
    }

    private static AppointmentsViewModel AppointmentsModel(out Lifestream.Client.Tests.Fakes.Rig rig)
    {
        rig = Part2Harness.NewRig();
        return Part2Harness.Create<AppointmentsViewModel>(rig);
    }

    /// <summary>Wires the picker to the fake API for the test's duration (the factory is static, so it is restored when the returned scope is disposed).</summary>
    private static PickerScope StaHostRig() => new();

    private sealed class PickerScope : IDisposable
    {
        private readonly Func<RecordKind, RecordPickerViewModel>? _previous = RecordPicker.ViewModelFactory;
        private readonly bool _inline = RecordPicker.InlineResults;

        public PickerScope()
        {
            var api = new Lifestream.Client.Tests.Fakes.FakeApiClient();
            api.On("SearchRecordsAsync", ApiResult<IReadOnlyList<RecordOption>>.Ok([new RecordOption(101, "Test Donor", "O+")]));
            RecordPicker.InlineResults = true;
            RecordPicker.ViewModelFactory = kind => new RecordPickerViewModel(api, kind, debounce: TimeSpan.FromMilliseconds(10));
        }

        public void Dispose()
        {
            RecordPicker.ViewModelFactory = _previous;
            RecordPicker.InlineResults = _inline;
        }
    }
}
