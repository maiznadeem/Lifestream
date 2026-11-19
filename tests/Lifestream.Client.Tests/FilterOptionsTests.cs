using Lifestream.Client.Common;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

public sealed class FilterOptionsTests
{
    [Fact]
    public void FilterOptions_WithAll_PutsAnExplicitAllFirst_ThenTypedOptions()
    {
        var options = FilterOptions.WithAll(BloodTypeExtensions.All, b => b.ToDisplay());

        Assert.Equal(9, options.Count);
        Assert.Equal("All", options[0].Display);
        Assert.True(options[0].IsAll);
        Assert.Null(options[0].Value); // "All" removes the filter: it is null, not an empty string
        Assert.Equal(["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"], options.Skip(1).Select(o => o.Display));
        Assert.All(options.Skip(1), o => Assert.False(o.IsAll));
        Assert.Equal(BloodType.ONeg, options[^1].Value);
    }

    [Fact]
    public void FilterOptions_WorksForAnyEnum_AndAllowsAnotherAllText()
    {
        var options = FilterOptions.WithAll(Enum.GetValues<AppointmentStatus>(), s => s.ToString(), "Any status");

        Assert.Equal("Any status", options[0].Display);
        Assert.Equal(5, options.Count);
        Assert.Equal("Any status", options[0].ToString());
    }

    [Fact]
    public void ChartTable_CarriesTheSameNumbersAsTheChart()
    {
        var table = new ChartTable(["Month", "Donated", "Issued"], [["Jan 2026", "12", "9"], ["Feb 2026", "0", "4"]]);

        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(["Feb 2026", "0", "4"], table.Rows[1]);
    }
}
