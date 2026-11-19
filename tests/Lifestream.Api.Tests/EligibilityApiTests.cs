using System.Globalization;
using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests;

/// <summary>
/// API-11: eligibility is age 18-60, at least 90 days since the last donation and no medical hold, computed on the server; an
/// ineligible donor can be given a slot only with an override reason, which is stored. Booking and rescheduling both judge the day
/// of the slot.
/// </summary>
public sealed class EligibilityApiTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly DaysAgo(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));

    private static string EligibilityRoute(int donorId) =>
        ApiRoutes.DonorEligibility.Replace("{id}", donorId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    [Fact]
    public async Task Eligibility_MedicalHold_MakesAnOtherwiseEligibleDonorIneligible_AndLiftingItRestoresIt()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, medicalHold: true);

        var held = await (await staff.Client.GetApi(EligibilityRoute(donor.Id))).Read<DonorEligibility>();
        Assert.False(held.IsEligible);
        Assert.Contains(Eligibility.MedicalHoldReason, held.Reasons);

        var detail = await (await staff.Client.GetApi(ApiRoutes.DonorById, donor.Id)).Read<DonorDetail>();
        Assert.True(detail.MedicalHold);
        using var lift = await staff.Client.PatchApi(ApiRoutes.DonorById, donor.Id, new UpdateDonorRequest(detail.RowVersion) { MedicalHold = new Optional<bool>(true, false) });
        var lifted = await lift.Read<DonorDetail>();
        Assert.False(lifted.MedicalHold);

        var after = await (await staff.Client.GetApi(EligibilityRoute(donor.Id))).Read<DonorEligibility>();
        Assert.True(after.IsEligible);
    }

    [Fact]
    public async Task Patch_WithoutMedicalHold_LeavesTheHoldAlone()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, medicalHold: true);
        var detail = await (await staff.Client.GetApi(ApiRoutes.DonorById, donor.Id)).Read<DonorDetail>();

        using var response = await staff.Client.PatchApi(ApiRoutes.DonorById, donor.Id, new UpdateDonorRequest(detail.RowVersion) { Address = new Optional<string?>(true, "12 Other Road") });

        Assert.True((await response.Read<DonorDetail>()).MedicalHold);
    }

    [Fact]
    public async Task CreateDonor_WithMedicalHold_IsStoredAndShownInListAndDetail()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var name = fx.NextName("Hold Donor ");
        var request = new CreateDonorRequest(name, DaysAgo(365 * 30), Gender.Female, BloodType.APos, "+1-555-0142", null, DonorFrequency.Regular) { MedicalHold = true };

        var created = await (await staff.Client.PostApi(ApiRoutes.Donors, request)).Read<DonorDetail>();

        Assert.True(created.MedicalHold);
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Donor WHERE Id = {created.Id} AND MedicalHold = 1", Ct));
        var list = await (await staff.Client.GetApi(ApiRoutes.Donors, "?search=" + Uri.EscapeDataString(name))).Read<PagedResult<DonorListItem>>();
        Assert.True(list.Items.Single().MedicalHold);

        var plain = await (await staff.Client.PostApi(ApiRoutes.Donors, request with { Name = fx.NextName("Plain Donor "), MedicalHold = false })).Read<DonorDetail>();
        Assert.False(plain.MedicalHold);
    }

    [Fact]
    public async Task Book_DonorOnMedicalHold_WithoutReason_409_WithReason_Succeeds_AndReasonIsStored()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, medicalHold: true);

        using var without = await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(donor.Id, Seed.Slot(6, 10), null));
        using var problem = await without.Problem(409, ProblemCodes.Ineligible);
        Assert.Contains(Eligibility.MedicalHoldReason, problem.RootElement.GetProperty("errors").GetProperty("reasons").EnumerateArray().Select(e => e.GetString()));

        var booked = await (await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(donor.Id, Seed.Slot(6, 10), "Cleared by the duty doctor."))).Read<AppointmentDetail>();
        Assert.Equal("Cleared by the duty doctor.", booked.OverrideReason);
        await using var db = fx.Db.NewContext();
        Assert.Equal("Cleared by the duty doctor.", await db.Appointments.Where(a => a.Id == booked.Id).Select(a => a.OverrideReason).SingleAsync(Ct));
    }

    [Fact]
    public async Task Book_Age60IsEligible_Age61IsNot_Threshold90Days_ServerSide()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var slot = Seed.Slot(7, 11);
        var slotDay = DateOnly.FromDateTime(slot);
        var age60 = await Seed.DonorAsync(fx, dob: slotDay.AddYears(-60));
        var age61 = await Seed.DonorAsync(fx, dob: slotDay.AddYears(-61).AddDays(-1));
        var day89 = await Seed.DonorAsync(fx, lastDonated: slotDay.AddDays(-89));
        var day90 = await Seed.DonorAsync(fx, lastDonated: slotDay.AddDays(-90));

        using var a = await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(age60.Id, slot, null));
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        using var b = await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(age61.Id, slot.AddMinutes(30), null));
        using var bp = await b.Problem(409, ProblemCodes.Ineligible);
        using var c = await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(day89.Id, slot.AddMinutes(60), null));
        using var cp = await c.Problem(409, ProblemCodes.Ineligible);
        using var d = await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(day90.Id, slot.AddMinutes(90), null));
        Assert.Equal(HttpStatusCode.Created, d.StatusCode);
    }

    [Fact]
    public async Task Reschedule_ToADayWhereTheDonorIsIneligible_WithoutAStoredReason_409_AndNothingChanges()
    {
        // The verifier's sequence: refused for day+1, booked for day+20 (eligible by then), then moved to day+1.
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, lastDonated: DaysAgo(88)); // eligible from day+2
        using var early = await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(donor.Id, Seed.Slot(1, 10), null));
        using var earlyProblem = await early.Problem(409, ProblemCodes.Ineligible);
        var booked = await (await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(donor.Id, Seed.Slot(20, 10), null))).Read<AppointmentDetail>();
        Assert.Null(booked.OverrideReason);

        using var moved = await staff.Client.PatchApi(ApiRoutes.AppointmentById, booked.Id, new RescheduleAppointmentRequest(booked.RowVersion, Seed.Slot(1, 10)));
        using var problem = await moved.Problem(409, ProblemCodes.Ineligible);
        Assert.True(problem.RootElement.GetProperty("errors").GetProperty("reasons").GetArrayLength() > 0);

        var unchanged = await (await staff.Client.GetApi(ApiRoutes.AppointmentById, booked.Id)).Read<AppointmentDetail>();
        Assert.Equal(Seed.Slot(20, 10), unchanged.SlotStartUtc);
        Assert.Null(unchanged.OverrideReason);

        // A day on which the donor is eligible is allowed.
        var fine = await (await staff.Client.PatchApi(ApiRoutes.AppointmentById, booked.Id, new RescheduleAppointmentRequest(booked.RowVersion, Seed.Slot(5, 10)))).Read<AppointmentDetail>();
        Assert.Equal(Seed.Slot(5, 10), fine.SlotStartUtc);
    }

    [Fact]
    public async Task Reschedule_DonorOnMedicalHold_NeedsTheStoredReason_AppointmentBookedWithOneMayMove()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        var plain = await (await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(donor.Id, Seed.Slot(8, 10), null))).Read<AppointmentDetail>();

        // The hold is put on after the booking: moving the appointment is judged again.
        await fx.Db.ExecuteAsync($"UPDATE Donor SET MedicalHold = 1 WHERE Id = {donor.Id}", Ct);
        using var refused = await staff.Client.PatchApi(ApiRoutes.AppointmentById, plain.Id, new RescheduleAppointmentRequest(plain.RowVersion, Seed.Slot(9, 10)));
        using var problem = await refused.Problem(409, ProblemCodes.Ineligible);

        var withReason = await Seed.DonorAsync(fx, medicalHold: true);
        var overridden = await (await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(withReason.Id, Seed.Slot(8, 11), "Doctor approved."))).Read<AppointmentDetail>();
        var moved = await (await staff.Client.PatchApi(ApiRoutes.AppointmentById, overridden.Id, new RescheduleAppointmentRequest(overridden.RowVersion, Seed.Slot(10, 11)))).Read<AppointmentDetail>();
        Assert.Equal(Seed.Slot(10, 11), moved.SlotStartUtc);
        Assert.Equal("Doctor approved.", moved.OverrideReason);
    }
}
