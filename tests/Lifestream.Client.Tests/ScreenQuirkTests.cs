using System.Text.RegularExpressions;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Tests.Features;
using Lifestream.Client.Validation;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

/// <summary>Small screen quirks found by a verifier: the detail placeholder wording, the phone rule, and the staff list that must not show columns the list never fills (fake API).</summary>
public sealed partial class ScreenQuirkTests
{
    // ---- "Select a appointment to see their details." ----------------------------------------------------------------------

    [Fact]
    public void NoSelectionText_UsesTheRightArticleAndPronoun_ForEveryListScreen()
    {
        var rig = new Part2Rig();

        Assert.Equal("Select a donor to see their details.", rig.Base.Donors().NoSelectionText);
        Assert.Equal("Select a patient to see their details.", rig.Base.Patients().NoSelectionText);
        Assert.Equal("Select a staff member to see their details.", rig.Staff().NoSelectionText);
        Assert.Equal("Select an appointment to see its details.", rig.Appointments().NoSelectionText);
        Assert.Equal("Select a request to see its details.", rig.Requests().NoSelectionText);
        Assert.Equal("Select a receipt to see its details.", rig.Receipts().NoSelectionText);
    }

    // ---- the phone rule and its message must agree -------------------------------------------------------------------------

    [Theory]
    [InlineData("(555)0101")]
    [InlineData("(555) 010-1010")]
    [InlineData("+1 (555) 010-1010")]
    [InlineData("555-0101")]
    [InlineData("5550101")]
    [InlineData("+15550101")]
    [InlineData("+12345678901234567890")]
    public void PhoneRule_AcceptsEverythingTheMessagePromises(string phone) => Assert.Null(PhoneRule.Validate(phone));

    [Theory]
    [InlineData("abc")]
    [InlineData("1234")]
    [InlineData("++15550101")]
    [InlineData("555.0101")]
    [InlineData("555+0101")]
    [InlineData("+123456789012345678901")]
    [InlineData("555 0101 ext 2")]
    public void PhoneRule_RefusesWhatTheMessageDoesNotAllow(string phone) => Assert.Equal(PhoneRule.Message, PhoneRule.Validate(phone));

    [Fact]
    public void PhoneRule_IsCharacterForCharacterTheApisRule_AndTheMessageStatesIt()
    {
        var api = File.ReadAllText(RepoPaths.Find("src/Lifestream.Api/Common/Validation.cs"));
        var match = ApiPhonePattern().Match(api);

        Assert.True(match.Success, "the API's phone pattern was not found in Validation.cs");
        Assert.Equal(PhoneRule.Pattern, match.Groups[1].Value);
        Assert.Contains("5 to 20", PhoneRule.Message, StringComparison.Ordinal);
        Assert.Contains("brackets", PhoneRule.Message, StringComparison.Ordinal);
        Assert.Contains("+", PhoneRule.Message, StringComparison.Ordinal);
    }

    [GeneratedRegex("""GeneratedRegex\(@"([^"]+)"\)\]\s*private static partial Regex PhonePattern""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ApiPhonePattern();

    [Fact]
    public void EveryPhoneField_UsesTheOneRule_ForDonorPatientStaffAndProfile()
    {
        var rig = new Part2Rig();
        var forms = new (string Name, Func<FormViewModelBase> Make, Action<FormViewModelBase, string> Set)[]
        {
            ("donor", () => rig.Base.NewDonorForm(), (f, v) => ((Lifestream.Client.Features.Donors.DonorEditorViewModel)f).Phone = v),
            ("patient", () => rig.Base.NewPatientForm(), (f, v) => ((Lifestream.Client.Features.Patients.PatientEditorViewModel)f).Phone = v),
            ("staff", () => StaffEditorViewModel.ForNew(rig.Api, rig.Toasts, rig.Dialogs, rig.Clock), (f, v) => ((StaffEditorViewModel)f).Phone = v),
        };
        foreach (var (name, make, set) in forms)
        {
            var form = make();
            set(form, "(555)0101");
            Assert.True(form.ValidateOnBlur("Phone"), $"{name}: '(555)0101' follows the API's rule");
            set(form, "555.0101");
            Assert.False(form.ValidateOnBlur("Phone"));
            Assert.Equal(PhoneRule.Message, form.GetError("Phone"));
        }

        var profile = rig.Profile();
        profile.Phone = "(555)0101";
        Assert.True(profile.ValidateOnBlur("Phone"));
        profile.Phone = "555.0101";
        Assert.False(profile.ValidateOnBlur("Phone"));
        Assert.Equal(PhoneRule.Message, profile.GetError("Phone"));
    }

    // ---- staff: no list column for a field the list never fills ---------------------------------------------------------------

    [Fact]
    public void StaffRow_HasNoSalaryOrPhone_BecauseTheListEndpointNeverFillsThem()
    {
        var members = typeof(StaffRow).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(members, m => m.Contains("Salary", StringComparison.Ordinal) || m.Contains("Phone", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Role.Admin, true)]
    [InlineData(Role.Staff, false)]
    public async Task StaffDetailPane_ShowsSalaryAndPhone_FromTheDetailEndpoint_ForAnAdminOnly(Role role, bool shown)
    {
        var rig = new Part2Rig(role);
        rig.Fake.On("GetStaffAsync", ApiResult<Contracts.PagedResult<Contracts.Staff.StaffListItem>>.Ok(Samples2.StaffPage(Samples2.StaffRow(2, "Staff 002", salary: null))));
        rig.Fake.On("GetStaffMemberAsync", ApiResult<Contracts.Staff.StaffDetail>.Ok(Samples2.Staff(2)));
        var vm = rig.Staff();
        await vm.LoadAsync();

        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.Equal(1, rig.Recorder.CountOf("GetStaffMemberAsync")); // loaded from the detail endpoint
        var labels = vm.DetailFacts.Select(f => f.Label).ToList();
        Assert.Equal(shown, labels.Contains("Salary"));
        Assert.Equal(shown, labels.Contains("Phone"));
        if (shown)
        {
            Assert.Equal("3,000", vm.DetailFacts.Single(f => f.Label == "Salary").Value);
            Assert.Equal("+1-555-0150", vm.DetailFacts.Single(f => f.Label == "Phone").Value);
        }
    }
}
