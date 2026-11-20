using Lifestream.Client.Api;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Stock;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Lookups;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

/// <summary>
/// The command palette's search (real-app walk 4 and 5): a search that finds nothing says so (the released palette showed an empty strip), and a
/// blood group, the first thing a blood bank searches by, opens the lists on that group (it found nothing: the search matches names and phones).
/// </summary>
public sealed class PaletteSearchTests
{
    private static (Rig Rig, ShellViewModel Shell) Shell(Role role = Role.Admin)
    {
        var rig = new Rig();
        rig.SignIn(role);
        rig.Api.On("SearchRecordsAsync", ApiResult<IReadOnlyList<RecordOption>>.Ok([]));
        return (rig, rig.Get<ShellViewModel>());
    }

    [Fact]
    public async Task SearchThatFindsNothing_SaysSo_OnceTheSearchHasEnded()
    {
        var (rig, shell) = Shell();
        using var a = rig;
        using var b = shell;
        var palette = shell.Palette;
        palette.Open();

        palette.Query = "zzzqqq";
        await palette.PendingSearch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(rig.Api.CountOf("SearchRecordsAsync") >= 1); // the records were searched
        Assert.False(palette.IsSearching);
        Assert.Empty(palette.Items);
        Assert.Equal("Nothing matches “zzzqqq”. Try a name, a phone number, a blood group or a request number.", palette.EmptyText); // released 2.1.2: null, an empty strip

        palette.Query = "don";
        await palette.PendingSearch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(palette.Items);
        Assert.Null(palette.EmptyText);
    }

    [Theory]
    [InlineData("O-")]
    [InlineData("o−")]
    [InlineData(" O - ")]
    public async Task BloodGroup_TypedIntoThePalette_OffersTheListsOnThatGroup_AndOpensThem(string typed)
    {
        var (rig, shell) = Shell();
        using var a = rig;
        using var b = shell;
        var palette = shell.Palette;
        palette.Open();

        palette.Query = typed;
        await palette.PendingSearch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            ["Donors with O−", "Patients with O−", "Requests for O−", "O− bags in stock"],
            palette.Items.Where(i => i.Group == "Blood group").Select(i => i.Title)); // released 2.1.2: nothing at all
        Assert.Null(palette.EmptyText);

        await palette.RunAsync(palette.Items.Single(i => i.Title == "Donors with O−"));
        var donors = Assert.IsType<DonorsViewModel>(shell.CurrentPage);
        Assert.Equal(BloodType.ONeg, donors.BloodTypeFilter?.Value);

        palette.Open();
        palette.Query = typed;
        await palette.RunAsync(palette.Items.Single(i => i.Title == "O− bags in stock"));
        var stock = Assert.IsType<StockViewModel>(shell.CurrentPage);
        Assert.Equal(BloodType.ONeg, stock.BloodTypeFilter?.Value);
    }

    [Fact]
    public async Task BloodGroup_OffersOnlyTheScreensThePersonHas()
    {
        var (rig, shell) = Shell(Role.Staff);
        using var a = rig;
        using var b = shell;
        shell.Palette.Open();

        shell.Palette.Query = "AB+";
        await shell.Palette.PendingSearch.WaitAsync(TimeSpan.FromSeconds(5));

        var screens = shell.Items.Select(i => i.Title).ToHashSet();
        var offered = shell.Palette.Items.Where(i => i.Group == "Blood group").Select(i => i.Title).ToList();
        Assert.NotEmpty(offered);
        Assert.Equal(screens.Contains("Stock"), offered.Contains("AB+ bags in stock"));
        Assert.Equal(screens.Contains("Donors"), offered.Contains("Donors with AB+"));
    }
}
