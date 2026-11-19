using System.Text.Json;
using System.Text.RegularExpressions;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Staff;
using Lifestream.Contracts;
using Lifestream.Contracts.Lookups;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// API-45: every dropdown of the client is filled from the shared contracts' lists, the very lists the API returns from <c>lookups/enums</c>
/// (the API's own test checks that response equals <see cref="EnumLookups.Create"/>), so a client dropdown equals the server's list in values, order and text.
/// </summary>
public sealed class SharedListsTests
{
    private static readonly EnumLookups Server = RoundTrip(EnumLookups.Create());

    /// <summary>What the client receives from the server: the lists written and read with the one JSON configuration both sides use.</summary>
    private static EnumLookups RoundTrip(EnumLookups lookups) =>
        JsonSerializer.Deserialize<EnumLookups>(JsonSerializer.Serialize(lookups, ContractJson.CreateOptions()), ContractJson.CreateOptions())!;

    private static IEnumerable<(string Value, string Display)> Expected(IReadOnlyList<EnumOption> list) => list.Select(o => (o.Value, o.Display));

    private static IEnumerable<(string Value, string Display)> Shown<T>(IEnumerable<Choice<T>> choices) => choices.Select(c => (c.Value!.ToString()!, c.Display));

    private static IEnumerable<(string Value, string Display)> ShownFilter<T>(IEnumerable<Lifestream.Client.Common.FilterOption<T>> options)
        where T : struct =>
        options.Skip(1).Select(o => (o.Value!.Value.ToString()!, o.Display)); // the first entry is the explicit "All"

    [Fact]
    public void FormDropdowns_EqualTheServersLists_FakeApi()
    {
        var rig = new FeatureRig();
        var donor = rig.NewDonorForm();
        var patient = rig.NewPatientForm();
        var staff = StaffEditorViewModel.ForNew(rig.Api, rig.Toasts, rig.Dialogs, rig.Clock);

        Assert.Equal(Expected(Server.Genders), Shown(donor.GenderChoices));
        Assert.Equal(Expected(Server.BloodTypes), Shown(donor.BloodTypeChoices));
        Assert.Equal(Expected(Server.DonorFrequencies), Shown(donor.FrequencyChoices));
        Assert.Equal(Expected(Server.Genders), Shown(patient.GenderChoices));
        Assert.Equal(Expected(Server.BloodTypes), Shown(patient.BloodTypeChoices));
        Assert.Equal(Expected(Server.Genders), Shown(staff.GenderChoices));
        Assert.Equal(Expected(Server.Designations), Shown(staff.DesignationChoices));
        Assert.Equal(Expected(Server.Shifts), Shown(staff.ShiftChoices));
        Assert.Equal(Expected(Server.Roles), Shown(staff.RoleChoices));
    }

    [Fact]
    public void FilterDropdowns_EqualTheServersLists_FakeApi()
    {
        var rig = new FeatureRig();
        var part2 = new Part2Rig();

        Assert.Equal(Expected(Server.BloodTypes), ShownFilter(rig.Donors().BloodTypeOptions));
        Assert.Equal(Expected(Server.BloodTypes), ShownFilter(rig.Patients().BloodTypeOptions));
        Assert.Equal(Expected(Server.BloodTypes), ShownFilter(rig.Stock().BloodTypeOptions));
        Assert.Equal(Expected(Server.AppointmentStatuses), ShownFilter(part2.Appointments().StatusOptions));
        Assert.Equal(Expected(Server.RequestStatuses), ShownFilter(part2.Requests().StatusOptions));
        Assert.Equal(Expected(Server.ReceiptKinds), ShownFilter(part2.Receipts().KindOptions));
    }

    [Fact]
    public void TheDropdownsTheScreensShow_ComeFromTheSharedLists_NotFromAnEnumListedByTheScreen()
    {
        // Source scan: only SharedLists may list an enum or the blood types; a screen that does is a dropdown the server's list can't reach.
        var root = Path.GetDirectoryName(RepoPaths.Find("src/Lifestream.Client/Lifestream.Client.csproj"))!;
        var listing = new Regex(@"Enum\.GetValues|Enum\.GetNames|BloodTypeExtensions\.All", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "Features"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && Path.GetFileName(f) != "SharedLists.cs")
            .Where(f => listing.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ServerOrder_IsTheOrderTheClientShows_ForEveryList()
    {
        // The wire values are the enum names; parsing them back must give the same sequence the screens use.
        Assert.Equal(Server.BloodTypes.Select(o => o.Value), SharedLists.BloodTypes.Select(c => c.Value.ToString()));
        Assert.Equal(Server.AppointmentStatuses.Select(o => o.Value), SharedLists.AppointmentStatuses.Select(c => c.Value.ToString()));
        Assert.Equal(Server.RequestStatuses.Select(o => o.Value), SharedLists.RequestStatuses.Select(c => c.Value.ToString()));
        Assert.Equal(Server.ReceiptKinds.Select(o => o.Value), SharedLists.ReceiptKinds.Select(c => c.Value.ToString()));
        Assert.Equal(["Pending", "Completed", "Cancelled", "NoShow"], SharedLists.AppointmentStatuses.Select(c => c.Value.ToString()));
        Assert.Equal("No show", SharedLists.AppointmentStatuses.Single(c => c.Value.ToString() == "NoShow").Display);
    }
}
