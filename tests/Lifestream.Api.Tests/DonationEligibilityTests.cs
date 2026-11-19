using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Receipts;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests;

/// <summary>
/// API-11 and FEAT-16 (against the real API and a real SQL Server database): recording a donation judges the donor today. An ineligible donor
/// (under 90 days, age outside 18-60, medical hold) needs an override reason, which is stored on the appointment; a reschedule onto a day the donor
/// is ineligible on takes the reason from its own request (FEAT-13).
/// </summary>
public sealed class DonationEligibilityTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly DaysAgo(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));

    [Fact]
    public async Task RecordDonation_RecentDonor_WithoutReason_409_NothingIsRecorded()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, lastDonated: DaysAgo(45));
        var appointment = await Seed.AppointmentAsync(fx, donor, Seed.Slot(3, 10));

        using var response = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(1, null), appointment.Id);
        using var problem = await response.Problem(409, ProblemCodes.Ineligible);

        Assert.True(problem.RootElement.GetProperty("errors").GetProperty("reasons").GetArrayLength() > 0);
        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Receipt WHERE AppointmentId = {appointment.Id}", Ct));
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Appointment WHERE Id = {appointment.Id} AND Status = 'Pending'", Ct));
    }

    [Fact]
    public async Task RecordDonation_RecentDonor_WithReason_Succeeds_AndStoresTheReasonOnTheAppointment()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, lastDonated: DaysAgo(45));
        var appointment = await Seed.AppointmentAsync(fx, donor, Seed.Slot(3, 11));

        var receipt = await (await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(1, null) { OverrideReason = "Urgent need, doctor agreed." }, appointment.Id)).Read<ReceiptDetail>();

        Assert.True(receipt.Id > 0);
        var stored = await (await staff.Client.GetApi(ApiRoutes.AppointmentById, appointment.Id)).Read<AppointmentDetail>();
        Assert.Equal("Urgent need, doctor agreed.", stored.OverrideReason);
        Assert.Equal(AppointmentStatus.Completed, stored.Status);
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("tooYoung")]
    [InlineData("tooOld")]
    public async Task RecordDonation_MedicalHoldOrAgeOutsideTheRange_NeedsAReason(string kind)
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = kind switch
        {
            "hold" => await Seed.DonorAsync(fx, medicalHold: true),
            "tooYoung" => await Seed.DonorAsync(fx, dob: DaysAgo(365 * 17)),
            _ => await Seed.DonorAsync(fx, dob: DaysAgo((365 * 62) + 20)),
        };
        var appointment = await Seed.AppointmentAsync(fx, donor, Seed.Slot(4, kind == "hold" ? 9 : kind == "tooYoung" ? 10 : 11));

        using var response = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(1, null), appointment.Id);
        using var problem = await response.Problem(409, ProblemCodes.Ineligible);
    }

    [Fact]
    public async Task RecordDonation_EligibleDonor_NeedsNoReason_AndStoresNone()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, lastDonated: DaysAgo(120));
        var appointment = await Seed.AppointmentAsync(fx, donor, Seed.Slot(3, 12));

        using var response = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(1, null), appointment.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null((await (await staff.Client.GetApi(ApiRoutes.AppointmentById, appointment.Id)).Read<AppointmentDetail>()).OverrideReason);
    }

    [Fact]
    public async Task Reschedule_OntoAnIneligibleDay_WithAReasonInTheRequest_Succeeds_AndStoresIt()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, lastDonated: DaysAgo(88)); // eligible from day+2
        var booked = await (await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(donor.Id, Seed.Slot(20, 10), null))).Read<AppointmentDetail>();

        using var refused = await staff.Client.PatchApi(ApiRoutes.AppointmentById, booked.Id, new RescheduleAppointmentRequest(booked.RowVersion, Seed.Slot(1, 10)));
        using var problem = await refused.Problem(409, ProblemCodes.Ineligible);

        var moved = await (await staff.Client.PatchApi(ApiRoutes.AppointmentById, booked.Id, new RescheduleAppointmentRequest(booked.RowVersion, Seed.Slot(1, 10)) { OverrideReason = "Travelling next week." })).Read<AppointmentDetail>();
        Assert.Equal(Seed.Slot(1, 10), moved.SlotStartUtc);
        Assert.Equal("Travelling next week.", moved.OverrideReason);
        await using var db = fx.Db.NewContext();
        Assert.Equal("Travelling next week.", await db.Appointments.Where(a => a.Id == booked.Id).Select(a => a.OverrideReason).SingleAsync(Ct));
    }

    [Fact]
    public async Task Reschedule_OntoAnEligibleDay_IgnoresTheReason()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        var booked = await (await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(donor.Id, Seed.Slot(6, 10), null))).Read<AppointmentDetail>();

        var moved = await (await staff.Client.PatchApi(ApiRoutes.AppointmentById, booked.Id, new RescheduleAppointmentRequest(booked.RowVersion, Seed.Slot(7, 10)) { OverrideReason = "Not needed." })).Read<AppointmentDetail>();

        Assert.Null(moved.OverrideReason);
    }
}
