using System.Net;
using System.Text.Json;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests;

/// <summary>Donors and patients: create/read/update/delete, partial updates, concurrency, 404s, validation (one test row per rule).</summary>
public sealed class DonorPatientTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DonorJson(Action<Dictionary<string, object?>>? change = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Ayşe Nur Öztürk-Abdulrahman",
            ["dateOfBirth"] = "1990-04-12",
            ["gender"] = "Female",
            ["bloodType"] = "ONeg",
            ["phone"] = "+1-555-0142",
            ["address"] = "12 Sample Street, Demoville",
            ["frequency"] = "Regular",
        };
        change?.Invoke(body);
        return JsonSerializer.Serialize(body);
    }

    private async Task<(TestUser Admin, DonorDetail Donor)> NewDonorAsync(Action<Dictionary<string, object?>>? change = null)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var donor = await (await admin.Client.PostRaw(ApiRoutes.Donors, DonorJson(change))).Read<DonorDetail>();
        return (admin, donor);
    }

    [Fact]
    public async Task Donor_Create_Returns201WithSavedResource_NoListReloadNeeded()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var response = await admin.Client.PostRaw(ApiRoutes.Donors, DonorJson());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await response.Read<DonorDetail>();
        Assert.True(saved.Id > 0);
        Assert.Equal("Ayşe Nur Öztürk-Abdulrahman", saved.Name);
        Assert.False(string.IsNullOrEmpty(saved.RowVersion));
        Assert.EndsWith($"/donors/{saved.Id}", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        var read = await (await admin.Client.GetApi(ApiRoutes.DonorById, saved.Id)).Read<DonorDetail>();
        Assert.Equal(saved, read);
    }

    [Fact]
    public async Task Donor_Update_ReturnsSavedResource_WithNewRowVersion()
    {
        var (admin, donor) = await NewDonorAsync();
        var updated = await (await admin.Client.PatchApi(ApiRoutes.DonorById, donor.Id, new UpdateDonorRequest(donor.RowVersion, Name: "Renamed Donor"))).Read<DonorDetail>();
        Assert.Equal("Renamed Donor", updated.Name);
        Assert.NotEqual(donor.RowVersion, updated.RowVersion);
    }

    [Fact]
    public async Task Patch_OnlySuppliedFields_OthersUnchanged()
    {
        var (admin, donor) = await NewDonorAsync();
        await using (var before = fx.Db.NewContext())
        {
            var row = await before.Donors.AsNoTracking().SingleAsync(d => d.Id == donor.Id, Ct);
            var response = await admin.Client.PatchApi(ApiRoutes.DonorById, donor.Id, new UpdateDonorRequest(donor.RowVersion, BloodType: BloodType.APos));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var after = fx.Db.NewContext();
            var changed = await after.Donors.AsNoTracking().SingleAsync(d => d.Id == donor.Id, Ct);
            Assert.Equal(BloodType.APos, changed.BloodType);
            // Every column except the one supplied is identical, including the optional ones.
            Assert.Equal(
                (row.Name, row.DateOfBirth, row.Gender, row.Phone, row.Address, row.Frequency, row.LastDonated),
                (changed.Name, changed.DateOfBirth, changed.Gender, changed.Phone, changed.Address, changed.Frequency, changed.LastDonated));
        }
    }

    [Fact]
    public async Task Patch_ExplicitNull_ClearsAnOptionalField_AbsentFieldIsKept()
    {
        var (admin, donor) = await NewDonorAsync();
        var cleared = await (await admin.Client.PatchRaw(ApiRoutes.DonorById, donor.Id, $"{{\"rowVersion\":\"{donor.RowVersion}\",\"phone\":null}}")).Read<DonorDetail>();
        Assert.Null(cleared.Phone);
        Assert.Equal(donor.Address, cleared.Address);
        Assert.Equal(donor.Name, cleared.Name);
    }

    [Fact]
    public async Task Patch_WithStaleRowVersion_Returns409Concurrency()
    {
        var (admin, donor) = await NewDonorAsync();
        (await admin.Client.PatchApi(ApiRoutes.DonorById, donor.Id, new UpdateDonorRequest(donor.RowVersion, Name: "First Writer"))).Dispose();
        using var second = await admin.Client.PatchApi(ApiRoutes.DonorById, donor.Id, new UpdateDonorRequest(donor.RowVersion, Name: "Second Writer"));
        using var problem = await second.Problem(409, ProblemCodes.Concurrency);
        var current = await (await admin.Client.GetApi(ApiRoutes.DonorById, donor.Id)).Read<DonorDetail>();
        Assert.Equal("First Writer", current.Name);
    }

    [Fact]
    public async Task Delete_Donor_ThenGone_AndDonorWithAppointmentsIs409()
    {
        var (admin, donor) = await NewDonorAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteApi(ApiRoutes.DonorById, donor.Id)).StatusCode);
        using var gone = await admin.Client.GetApi(ApiRoutes.DonorById, donor.Id);
        using var p = await gone.Problem(404, ProblemCodes.NotFound);

        var (admin2, withAppointment) = await NewDonorAsync();
        await using (var db = fx.Db.NewContext())
        {
            db.Appointments.Add(new Lifestream.Data.Entities.Appointment { DonorId = withAppointment.Id, SlotStartUtc = DateTime.UtcNow.AddDays(9), Status = AppointmentStatus.Pending });
            await db.SaveChangesAsync(Ct);
        }

        using var conflict = await admin2.Client.DeleteApi(ApiRoutes.DonorById, withAppointment.Id);
        using var problem = await conflict.Problem(409, ProblemCodes.Conflict);
        Assert.DoesNotContain("FK_", problem.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eligibility_IsComputedOnTheServer()
    {
        var (admin, donor) = await NewDonorAsync(b => b["dateOfBirth"] = DateTime.UtcNow.AddYears(-17).ToString("yyyy-MM-dd"));
        var result = await (await admin.Client.GetApi(ApiRoutes.DonorEligibility, donor.Id)).Read<DonorEligibility>();
        Assert.False(result.IsEligible);
        Assert.Contains(result.Reasons, r => r.Contains("18", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task Donor_UpdateOrDelete_UnknownId_404(string verb)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var response = verb == "update"
            ? await admin.Client.PatchApi(ApiRoutes.DonorById, 987654, new UpdateDonorRequest("AAAAAAAAAAE=", Name: "x"))
            : await admin.Client.DeleteApi(ApiRoutes.DonorById, 987654);
        using var problem = await response.Problem(404, ProblemCodes.NotFound);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task Patient_UpdateOrDelete_UnknownId_404(string verb)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var response = verb == "update"
            ? await admin.Client.PatchApi(ApiRoutes.PatientById, 987654, new UpdatePatientRequest("AAAAAAAAAAE=", Name: "x"))
            : await admin.Client.DeleteApi(ApiRoutes.PatientById, 987654);
        using var problem = await response.Problem(404, ProblemCodes.NotFound);
    }

    [Fact]
    public async Task Patient_Crud_RoundTrip_AndPartialUpdate()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var body = new CreatePatientRequest("Test Patient Ünal", new DateOnly(1970, 2, 2), Gender.Other, BloodType.ABPos, "+1-555-0103", "3 Sample Road, Demoville");
        using var created = await admin.Client.PostApi(ApiRoutes.Patients, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var patient = await created.Read<PatientDetail>();
        var updated = await (await admin.Client.PatchApi(ApiRoutes.PatientById, patient.Id, new UpdatePatientRequest(patient.RowVersion, Address: Optional.Of<string?>(null)))).Read<PatientDetail>();
        Assert.Null(updated.Address);
        Assert.Equal(patient.Name, updated.Name);
        Assert.Equal(patient.Phone, updated.Phone);
        using var stale = await admin.Client.PatchApi(ApiRoutes.PatientById, patient.Id, new UpdatePatientRequest(patient.RowVersion, Name: "Late"));
        using var problem = await stale.Problem(409, ProblemCodes.Concurrency);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteApi(ApiRoutes.PatientById, patient.Id)).StatusCode);
    }

    [Fact]
    public async Task Search_IsAccentAndCaseInsensitive_AndMatchesTheStartOfTheName_NotTheMiddle()
    {
        var (admin, donor) = await NewDonorAsync();
        foreach (var term in new[] { "ayse", "AYŞE", "Ayşe Nur", "ayse nur ozturk" })
        {
            var hits = await (await admin.Client.GetApi(ApiRoutes.Donors, "?search=" + Uri.EscapeDataString(term))).Read<PagedResult<DonorListItem>>();
            Assert.Contains(hits.Items, d => d.Id == donor.Id);
        }

        // A prefix search: the middle of a name is not matched (no LIKE '%x%').
        var middle = await (await admin.Client.GetApi(ApiRoutes.Donors, "?search=" + Uri.EscapeDataString("nur"))).Read<PagedResult<DonorListItem>>();
        Assert.DoesNotContain(middle.Items, d => d.Id == donor.Id);

        // The id and the contact number match too.
        var byId = await (await admin.Client.GetApi(ApiRoutes.Donors, "?search=" + donor.Id)).Read<PagedResult<DonorListItem>>();
        Assert.Contains(byId.Items, d => d.Id == donor.Id);
        var byPhone = await (await admin.Client.GetApi(ApiRoutes.Donors, "?search=" + Uri.EscapeDataString("+1-555-0142"))).Read<PagedResult<DonorListItem>>();
        Assert.Contains(byPhone.Items, d => d.Id == donor.Id);
    }

    [Fact]
    public async Task Search_WildcardCharactersAreLiteral()
    {
        var (admin, donor) = await NewDonorAsync();
        var hits = await (await admin.Client.GetApi(ApiRoutes.Donors, "?search=" + Uri.EscapeDataString("%"))).Read<PagedResult<DonorListItem>>();
        Assert.DoesNotContain(hits.Items, d => d.Id == donor.Id);
    }

    // ---- validation: one row per rule (API-09) ----

    public static TheoryData<string, string, string> DonorRules => new()
    {
        { "name is required", "name", "" },
        { "name at most 100 characters", "name", new string('n', 101) },
        { "date of birth in the past: tomorrow", "dateOfBirth", "TOMORROW" },
        { "date of birth in the past: today", "dateOfBirth", "TODAY" },
        { "date of birth is not DateTime.MinValue", "dateOfBirth", "0001-01-01" },
        { "date of birth not before 1900", "dateOfBirth", "1850-01-01" },
        { "gender must be a real value", "gender", "Alien" },
        { "blood type must be a real value", "bloodType", "XPos" },
        { "frequency must be a real value", "frequency", "Sometimes" },
        { "phone must look like a phone number", "phone", "call me maybe" },
        { "phone at most 20 characters", "phone", "+1-555-0101-0101-0101-0101" },
        { "address at most 200 characters", "address", "EXTRA" },
    };

    [Theory]
    [MemberData(nameof(DonorRules))]
    public async Task Donor_Create_InvalidInput_400(string rule, string field, string value)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var date = DateTime.UtcNow;
        var actual = value switch
        {
            "TOMORROW" => date.AddDays(1).ToString("yyyy-MM-dd"),
            "TODAY" => date.ToString("yyyy-MM-dd"),
            "EXTRA" => new string('a', 201),
            _ => value,
        };
        using var response = await admin.Client.PostRaw(ApiRoutes.Donors, DonorJson(b => b[field] = actual));
        using var problem = await response.Problem(400, ProblemCodes.Validation);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"{rule}: expected an error on {field}: {problem.RootElement}");
    }

    [Theory]
    [InlineData("name")]
    [InlineData("dateOfBirth")]
    [InlineData("gender")]
    [InlineData("bloodType")]
    [InlineData("frequency")]
    public async Task Donor_Create_MissingRequiredOrNullDropdown_400(string field)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var missing = await admin.Client.PostRaw(ApiRoutes.Donors, DonorJson(b => b.Remove(field)));
        using var p1 = await missing.Problem(400, ProblemCodes.Validation);
        Assert.True(p1.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        using var nulled = await admin.Client.PostRaw(ApiRoutes.Donors, DonorJson(b => b[field] = null));
        using var p2 = await nulled.Problem(400, ProblemCodes.Validation);
        Assert.True(p2.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Donor_Create_NotJson_400()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var response = await admin.Client.PostRaw(ApiRoutes.Donors, "this is not json");
        using var problem = await response.Problem(400, ProblemCodes.Validation);
        Assert.DoesNotContain("JsonException", problem.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("dateOfBirth", "2999-01-01")]
    [InlineData("gender", "Robot")]
    [InlineData("bloodType", "Q")]
    [InlineData("phone", "abc")]
    public async Task Donor_Patch_InvalidInput_400(string field, string value)
    {
        var (admin, donor) = await NewDonorAsync();
        using var response = await admin.Client.PatchRaw(ApiRoutes.DonorById, donor.Id, JsonSerializer.Serialize(new Dictionary<string, object?> { ["rowVersion"] = donor.RowVersion, [field] = value }));
        using var problem = await response.Problem(400, ProblemCodes.Validation);
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("dateOfBirth", "0001-01-01")]
    [InlineData("gender", "Robot")]
    [InlineData("bloodType", "Q")]
    [InlineData("phone", "abc")]
    public async Task Patient_Create_InvalidInput_400(string field, string value)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Valid Name",
            ["dateOfBirth"] = "1970-02-02",
            ["gender"] = "Male",
            ["bloodType"] = "APos",
            ["phone"] = "+1-555-0103",
            ["address"] = "x",
        };
        body[field] = value;
        using var response = await admin.Client.PostRaw(ApiRoutes.Patients, JsonSerializer.Serialize(body));
        using var problem = await response.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task Patch_BadRowVersion_400()
    {
        var (admin, donor) = await NewDonorAsync();
        using var response = await admin.Client.PatchRaw(ApiRoutes.DonorById, donor.Id, "{\"rowVersion\":\"!!!\",\"name\":\"x\"}");
        using var problem = await response.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task NonLatinAndLongNames_RoundTrip_AndAreSearchable()
    {
        var name60 = "Zeynep Çağla Şimşek-Öztürk Abdulrahman Kızılırmak Yıldırım";
        var (admin, donor) = await NewDonorAsync(b => b["name"] = name60);
        Assert.Equal(name60, donor.Name);
        var hits = await (await admin.Client.GetApi(ApiRoutes.Donors, "?search=zeynep%20cagla")).Read<PagedResult<DonorListItem>>();
        Assert.Contains(hits.Items, d => d.Id == donor.Id && d.Name == name60);
    }
}
