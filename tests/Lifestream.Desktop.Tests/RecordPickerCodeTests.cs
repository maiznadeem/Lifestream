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
            // Wait for what is looked at, not for a fixed time: on a busy machine the rows and the chip can take longer than any settle.
            List<string> Codes() => [.. host.All<TextBlock>().Where(t => t.IsVisible && t.Name == "OptionCode" && t.Text.Length > 0).Select(t => t.Text)];
            TextBlock? Chip() => host.All<TextBlock>().FirstOrDefault(t => t.IsVisible && t.Name != "OptionCode" && t.Text.StartsWith("Sunita Ndiaye ·", StringComparison.Ordinal));
            Hosted.PumpUntil(() => !vm.IsSearching && vm.Options.Count == 2 && Codes().Count == 2, 10000);
            var codes = Codes();

            vm.Select(vm.Options[0]);
            Hosted.PumpUntil(() => Chip() is not null, 10000);
            host.Settle(100);
            var chip = Chip()?.Text;
            var hashes = host.All<TextBlock>().Where(t => t.IsVisible && t.Text.Contains('#', StringComparison.Ordinal)).Select(t => t.Text).ToList();
            return (codes, chip, hashes);
        });

        Assert.Equal([first, second], f.codes); // released 2.1.2: "#41", "#1123"
        Assert.Equal($"Sunita Ndiaye · {first} · B−", f.chip);
        Assert.Empty(f.hashes);
    }
}
