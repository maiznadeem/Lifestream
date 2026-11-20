using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Lookups;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Lookups;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// Real-app walk 22: pickers and forms showed a record as "#41" where every list, panel and receipt shows "D-0041". The picker's rows and its
/// chosen-record chip use the record's code, by the kind of record it picks.
/// </summary>
[Collection("Wpf")]
public sealed class RecordPickerCodeTests
{
    [Theory]
    [InlineData(RecordKind.Donor, "D-0041", "D-1123")]
    [InlineData(RecordKind.Patient, "P-0041", "P-1123")]
    [InlineData(RecordKind.Staff, "S-0041", "S-1123")]
    public void RecordPicker_ShowsTheRecordsCode_InItsRowsAndInTheChosenChip(RecordKind kind, string first, string second)
    {
        var api = new FakeApiClient();
        api.On("SearchRecordsAsync", ApiResult<IReadOnlyList<RecordOption>>.Ok([new RecordOption(41, "Sunita Ndiaye", "B−"), new RecordOption(1123, "Sunita Park", "O+")]));
        RecordPicker.InlineResults = true; // never open a popup window on the desktop
        RecordPicker.ViewModelFactory = k => new RecordPickerViewModel(api, k, debounce: TimeSpan.FromMilliseconds(10));

        var f = StaHost.Run(() =>
        {
            var picker = new RecordPicker { Kind = kind, Label = "Record" };
            using var host = Hosted.Create(new StackPanel { Children = { picker } }, 500, 400);
            host.Settle(100);
            var vm = picker.ViewModel!;
            vm.SearchText = "sun";
            Hosted.PumpUntil(() => !vm.IsSearching && vm.Options.Count == 2, 3000);
            host.Settle(200);
            var codes = host.All<TextBlock>().Where(t => t.IsVisible && t.Name == "OptionCode").Select(t => t.Text).ToList();

            vm.Select(vm.Options[0]);
            host.Settle(150);
            var chip = host.All<TextBlock>().First(t => t.IsVisible && t.Text.StartsWith("Sunita Ndiaye", StringComparison.Ordinal)).Text;
            var hashes = host.All<TextBlock>().Where(t => t.IsVisible && t.Text.Contains('#', StringComparison.Ordinal)).Select(t => t.Text).ToList();
            return (codes, chip, hashes);
        });

        Assert.Equal([first, second], f.codes); // released 2.1.2: "#41", "#1123"
        Assert.Equal($"Sunita Ndiaye · {first} · B−", f.chip);
        Assert.Empty(f.hashes);
    }
}
