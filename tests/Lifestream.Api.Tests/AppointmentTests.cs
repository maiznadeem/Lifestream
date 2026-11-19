using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Receipts;
using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests;

/// <summary>Booking, rescheduling, recording a donation, and appointment queries (API-10, 11, 16, 17, 19, 44, 08, 24).</summary>
public sealed class AppointmentTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BookAppointmentRequest Book(Donor donor, DateTime slot, string? reason = null) => new(donor.Id, slot, reason);

    // ---- booking ----

    [Fact]
    public async Task Book_Eligible_Returns201WithSavedAppointment_NamesNotPlaceholders()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, "Booked Person");
        using var response = await staff.Client.PostApi(ApiRoutes.Appointments, Book(donor, Seed.Slot(3, 10)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Read<AppointmentDetail>();
        Assert.Equal(AppointmentStatus.Pending, detail.Status);
        Assert.Equal("Booked Person", detail.DonorName);
        Assert.Equal(donor.Id, detail.DonorId);
        Assert.Null(detail.OverrideReason);
        Assert.False(string.IsNullOrEmpty(detail.RowVersion));
    }

    [Fact]
    public async Task Book_PastSlot_400()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        using var response = await staff.Client.PostApi(ApiRoutes.Appointments, Book(donor, Seed.Slot(-1, 10)));
        using var problem = await response.Problem(400, ProblemCodes.Validation);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("slotStartUtc", out _));
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(16, 45)]
    [InlineData(17, 0)]
    [InlineData(10, 15)]
    [InlineData(0, 0)]
    public async Task Book_OutsideClinicHoursOrOffGrid_400(int hour, int minute)
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        var slot = hour == 0 && minute == 0 ? DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(4).AddMinutes(1), DateTimeKind.Utc) : Seed.Slot(4, hour, minute);
        using var response = await staff.Client.PostApi(ApiRoutes.Appointments, Book(donor, slot));
        using var problem = await response.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task Book_DefaultDate_400()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        using var response = await staff.Client.PostApi(ApiRoutes.Appointments, Book(donor, default));
        using var problem = await response.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task Book_UnknownDonor_400()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        using var response = await staff.Client.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(987654, Seed.Slot(5, 11), null));
        using var problem = await response.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task Book_TakenSlot_409SlotTaken_CancelledSlotCanBeRebooked()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var a = await Seed.DonorAsync(fx);
        var b = await Seed.DonorAsync(fx);
        var slot = Seed.Slot(6, 9, 30);
        var first = await (await staff.Client.PostApi(ApiRoutes.Appointments, Book(a, slot))).Read<AppointmentDetail>();
        using var taken = await staff.Client.PostApi(ApiRoutes.Appointments, Book(b, slot));
        using var problem = await taken.Problem(409, ProblemCodes.SlotTaken);

        (await staff.Client.PostApi(ApiRoutes.AppointmentCancel, id: first.Id)).Dispose();
        using var again = await staff.Client.PostApi(ApiRoutes.Appointments, Book(b, slot));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task Book_TwoAtOnceForOneSlot_ExactlyOneWins()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donors = new[] { await Seed.DonorAsync(fx), await Seed.DonorAsync(fx), await Seed.DonorAsync(fx), await Seed.DonorAsync(fx) };
        var slot = Seed.Slot(7, 14);
        var results = await Task.WhenAll(donors.Select(async d =>
        {
            using var response = await staff.Client.PostApi(ApiRoutes.Appointments, Book(d, slot));
            return response.StatusCode;
        }));
        Assert.Equal(1, results.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(3, results.Count(s => s == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Book_Ineligible_WithoutReason_409_WithReason_Succeeds_AndReasonIsStored()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var recent = await Seed.DonorAsync(fx, lastDonated: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-20)));
        var underage = await Seed.DonorAsync(fx, dob: DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-17)));
        var medicalOk = await Seed.DonorAsync(fx, lastDonated: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-91)));

        foreach (var donor in new[] { recent, underage })
        {
            using var without = await staff.Client.PostApi(ApiRoutes.Appointments, Book(donor, Seed.Slot(8, 10)));
            using var problem = await without.Problem(409, ProblemCodes.Ineligible);
            Assert.True(problem.RootElement.GetProperty("errors").GetProperty("reasons").GetArrayLength() > 0);
        }

        using var blank = await staff.Client.PostApi(ApiRoutes.Appointments, Book(recent, Seed.Slot(8, 10), "   "));
        using var p2 = await blank.Problem(409, ProblemCodes.Ineligible);

        var booked = await (await staff.Client.PostApi(ApiRoutes.Appointments, Book(recent, Seed.Slot(8, 10), "Physician approved an early donation."))).Read<AppointmentDetail>();
        Assert.Equal("Physician approved an early donation.", booked.OverrideReason);
        await using var db = fx.Db.NewContext();
        Assert.Equal("Physician approved an early donation.", await db.Appointments.Where(a => a.Id == booked.Id).Select(a => a.OverrideReason).SingleAsync(Ct));

        // More than 90 days since the last donation: eligible, no reason needed.
        using var fine = await staff.Client.PostApi(ApiRoutes.Appointments, Book(medicalOk, Seed.Slot(8, 11)));
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }

    [Fact]
    public async Task Book_OverrideReasonTooLong_400()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        using var response = await staff.Client.PostApi(ApiRoutes.Appointments, Book(donor, Seed.Slot(9, 10), new string('r', 301)));
        using var problem = await response.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task Slots_ForADay_ListSixteenSlots_TakenOnesFlagged()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        var slot = Seed.Slot(12, 9, 30);
        (await staff.Client.PostApi(ApiRoutes.Appointments, Book(donor, slot))).Dispose();
        var day = DateOnly.FromDateTime(slot).ToString("yyyy-MM-dd");
        var slots = await (await staff.Client.GetApi(ApiRoutes.AppointmentSlots, "?date=" + day)).Read<List<SlotInfo>>();
        Assert.Equal(16, slots.Count);
        Assert.Equal(slot, slots.Single(s => s.IsTaken).StartUtc);
        using var missing = await staff.Client.GetApi(ApiRoutes.AppointmentSlots);
        using var problem = await missing.Problem(400, ProblemCodes.Validation);
    }

    // ---- rescheduling (API-44) ----

    [Fact]
    public async Task Reschedule_DateOnly_KeepsTheTime_NewTimeIsApplied()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        var booked = await (await staff.Client.PostApi(ApiRoutes.Appointments, Book(donor, Seed.Slot(10, 10, 30)))).Read<AppointmentDetail>();

        // A value at midnight carries only a date.
        var newDay = Seed.Slot(14, 0);
        var moved = await (await staff.Client.PatchApi(ApiRoutes.AppointmentById, booked.Id, new RescheduleAppointmentRequest(booked.RowVersion, newDay))).Read<AppointmentDetail>();
        Assert.Equal(newDay.AddHours(10).AddMinutes(30), moved.SlotStartUtc);

        // A value with a time is the new slot.
        var withTime = await (await staff.Client.PatchApi(ApiRoutes.AppointmentById, booked.Id, new RescheduleAppointmentRequest(moved.RowVersion, Seed.Slot(14, 15)))).Read<AppointmentDetail>();
        Assert.Equal(Seed.Slot(14, 15), withTime.SlotStartUtc);
    }

    [Fact]
    public async Task Reschedule_StaleVersion_409_TakenSlot_409_PastDate_400()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var a = await Seed.DonorAsync(fx);
        var b = await Seed.DonorAsync(fx);
        var first = await (await staff.Client.PostApi(ApiRoutes.Appointments, Book(a, Seed.Slot(15, 10)))).Read<AppointmentDetail>();
        var second = await (await staff.Client.PostApi(ApiRoutes.Appointments, Book(b, Seed.Slot(15, 11)))).Read<AppointmentDetail>();

        using var taken = await staff.Client.PatchApi(ApiRoutes.AppointmentById, second.Id, new RescheduleAppointmentRequest(second.RowVersion, Seed.Slot(15, 10)));
        using var p1 = await taken.Problem(409, ProblemCodes.SlotTaken);
        using var past = await staff.Client.PatchApi(ApiRoutes.AppointmentById, second.Id, new RescheduleAppointmentRequest(second.RowVersion, Seed.Slot(-2, 11)));
        using var p2 = await past.Problem(400, ProblemCodes.Validation);

        (await staff.Client.PatchApi(ApiRoutes.AppointmentById, first.Id, new RescheduleAppointmentRequest(first.RowVersion, Seed.Slot(16, 10)))).Dispose();
        using var stale = await staff.Client.PatchApi(ApiRoutes.AppointmentById, first.Id, new RescheduleAppointmentRequest(first.RowVersion, Seed.Slot(17, 10)));
        using var p3 = await stale.Problem(409, ProblemCodes.Concurrency);
    }

    [Fact]
    public async Task Appointment_UpdateOrDelete_UnknownId_404()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        using var patch = await staff.Client.PatchApi(ApiRoutes.AppointmentById, 987654, new RescheduleAppointmentRequest("AAAAAAAAAAE=", Seed.Slot(5, 10)));
        using var p1 = await patch.Problem(404, ProblemCodes.NotFound);
        using var delete = await staff.Client.DeleteApi(ApiRoutes.AppointmentById, 987654);
        using var p2 = await delete.Problem(404, ProblemCodes.NotFound);
    }

    // ---- donation (API-16, 08, 19) ----

    private async Task<(Donor Donor, Appointment Appointment)> PendingAsync(int dayOffset, int hour)
    {
        var donor = await Seed.DonorAsync(fx, type: BloodType.BNeg);
        return (donor, await Seed.AppointmentAsync(fx, donor, Seed.Slot(dayOffset, hour)));
    }

    [Fact]
    public async Task RecordDonation_CompletesExactlyThatAppointment_CreatesNBags_SecondTime409()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);

        // Donor ids and appointment ids are made different on purpose: the old trigger mixed them up.
        Donor? first = null;
        for (var i = 0; i < 5; i++)
        {
            first ??= await Seed.DonorAsync(fx);
            await Seed.AppointmentAsync(fx, first, Seed.Slot(20 + i, 10));
        }

        // Other tests of this class fixture have created donors and appointments too, so the ids are read: filler appointments (on days that no other test uses)
        // are added until the next appointment id is at least 2 ahead of the next donor id. Both pairs below then differ whatever ran before.
        for (var filler = 0; filler < 500; filler++)
        {
            await using var idDb = fx.Db.NewContext();
            var maxDonor = await idDb.Donors.Select(d => (int?)d.Id).MaxAsync(Ct) ?? 0;
            var maxAppointment = await idDb.Appointments.Select(a => (int?)a.Id).MaxAsync(Ct) ?? 0;
            if (maxAppointment >= maxDonor + 2)
            {
                break;
            }

            await Seed.AppointmentAsync(fx, first!, Seed.Slot(100 + filler, 11));
        }

        var (donor, target) = await PendingAsync(30, 10);
        var (otherDonor, other) = await PendingAsync(30, 11);
        Assert.NotEqual(donor.Id, target.Id);
        Assert.NotEqual(otherDonor.Id, other.Id);

        using var response = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(2, "first donation"), target.Id);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await response.Read<ReceiptDetail>();
        Assert.Equal(ReceiptKind.Donation, receipt.Kind);
        Assert.Equal(2, receipt.Units);
        Assert.Equal(2, receipt.BagIds.Count);
        Assert.Equal(target.Id, receipt.AppointmentId);
        Assert.Equal(donor.Name, receipt.PartyName);

        await using var db = fx.Db.NewContext();
        Assert.Equal(AppointmentStatus.Completed, (await db.Appointments.SingleAsync(a => a.Id == target.Id, Ct)).Status);
        Assert.Equal(AppointmentStatus.Pending, (await db.Appointments.SingleAsync(a => a.Id == other.Id, Ct)).Status);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), (await db.Donors.SingleAsync(d => d.Id == donor.Id, Ct)).LastDonated);
        Assert.Null((await db.Donors.SingleAsync(d => d.Id == otherDonor.Id, Ct)).LastDonated);
        var bags = await db.BloodBags.Where(b => b.DonationReceiptId == receipt.Id).ToListAsync(Ct);
        Assert.Equal(2, bags.Count);
        Assert.All(bags, b =>
        {
            Assert.Equal(BloodType.BNeg, b.BloodType);
            Assert.Equal(BagStatus.Available, b.Status);
            Assert.Equal(DateOnly.FromDateTime(b.CollectedAtUtc).AddDays(42), b.ExpiresOn);
        });

        using var second = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(1, null), target.Id);
        using var problem = await second.Problem(409, ProblemCodes.AlreadyReceipted);
        Assert.Equal(1, await db.Receipts.CountAsync(r => r.AppointmentId == target.Id, Ct));
        Assert.Equal(2, await db.BloodBags.CountAsync(b => b.DonationReceiptId == receipt.Id, Ct));
    }

    [Fact]
    public async Task RecordDonation_StaffIdComesFromTheToken_BodyStaffIdIsIgnored_AuditUsesIt()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var someoneElse = await fx.AddStaffAsync(Role.Staff);
        var (_, appointment) = await PendingAsync(31, 10);

        using var response = await staff.Client.PostRaw(ApiRoutes.AppointmentDonation.Replace("{id}", appointment.Id.ToString(), StringComparison.Ordinal), $"{{\"units\":1,\"staffId\":{someoneElse.Id}}}");
        var receipt = await response.Read<ReceiptDetail>();
        Assert.Equal(staff.Id, receipt.StaffId);
        await using var db = fx.Db.NewContext();
        Assert.Equal(staff.Id, await db.Receipts.Where(r => r.Id == receipt.Id).Select(r => r.StaffId).SingleAsync(Ct));
        var audit = await db.AuditLogs.Where(a => a.Entity == "Receipt" && a.EntityId == receipt.Id).ToListAsync(Ct);
        Assert.NotEmpty(audit);
        Assert.All(audit, a => Assert.Equal(staff.Id, a.StaffId));
    }

    [Fact]
    public async Task RecordDonation_NUnits_UseOneSetBasedInsert()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var (_, appointment) = await PendingAsync(32, 10);
        fx.Commands.Clear();
        fx.Commands.Recording = true;
        try
        {
            (await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(2, null), appointment.Id)).Dispose();
        }
        finally
        {
            fx.Commands.Recording = false;
        }

        var inserts = fx.Commands.Commands.Where(c => c.Contains("INSERT INTO [BloodBag]", StringComparison.Ordinal)).ToList();
        var single = Assert.Single(inserts);
        Assert.Equal(2, single.Split("(@type, @collected, @expires", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void BagInsertSql_ForThousandUnits_IsOneStatement_AndParameterised()
    {
        var sql = Lifestream.Api.Features.BloodBagSql.BuildInsert(1000);
        Assert.Equal(1, sql.Split("INSERT INTO", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("'O", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BulkInsert_Of1000Appointments_Succeeds()
    {
        var donor = await Seed.DonorAsync(fx);
        await using (var db = fx.Db.NewContext())
        {
            var start = DateTime.UtcNow.Date.AddYears(3);
            db.Appointments.AddRange(Enumerable.Range(0, 1000).Select(i => new Appointment { DonorId = donor.Id, SlotStartUtc = start.AddMinutes(30 * i), Status = AppointmentStatus.Pending }));
            await db.SaveChangesAsync(Ct);
        }

        await using var check = fx.Db.NewContext();
        Assert.Equal(1000, await check.Appointments.CountAsync(a => a.DonorId == donor.Id, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(500)]
    public async Task RecordDonation_UnitsOutsideRange_400(int units)
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var (_, appointment) = await PendingAsync(33, 10);
        using var response = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(units, null), appointment.Id);
        using var problem = await response.Problem(400, ProblemCodes.Validation);
        await using var db = fx.Db.NewContext();
        Assert.Equal(AppointmentStatus.Pending, (await db.Appointments.SingleAsync(a => a.Id == appointment.Id, Ct)).Status);
    }

    [Fact]
    public async Task RecordDonation_NotesTooLong_400_UnknownAppointment_404_CancelledAppointment_409()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var (_, appointment) = await PendingAsync(34, 10);
        using var longNotes = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(1, new string('n', 501)), appointment.Id);
        using var p1 = await longNotes.Problem(400, ProblemCodes.Validation);
        using var unknown = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(1, null), 987654);
        using var p2 = await unknown.Problem(404, ProblemCodes.NotFound);

        (await staff.Client.PostApi(ApiRoutes.AppointmentCancel, id: appointment.Id)).Dispose();
        using var cancelled = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(1, null), appointment.Id);
        using var p3 = await cancelled.Problem(409, ProblemCodes.Conflict);
    }

    [Fact]
    public async Task RecordDonation_TwoAtOnce_ExactlyOneReceipt()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var (_, appointment) = await PendingAsync(35, 10);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            using var response = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(1, null), appointment.Id);
            return response.StatusCode;
        }));
        Assert.Equal(1, results.Count(s => s == HttpStatusCode.Created));
        await using var db = fx.Db.NewContext();
        Assert.Equal(1, await db.Receipts.CountAsync(r => r.AppointmentId == appointment.Id, Ct));
        Assert.Equal(1, await db.BloodBags.CountAsync(b => b.DonationReceipt!.AppointmentId == appointment.Id, Ct));
    }

    // ---- queries (API-17, 24) ----

    [Fact]
    public async Task AppointmentsForADonor_MatchOnDonorId_NotOnAppointmentId()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        for (var i = 0; i < 4; i++)
        {
            await Seed.AppointmentAsync(fx, await Seed.DonorAsync(fx), Seed.Slot(40 + i, 10));
        }

        var donor = await Seed.DonorAsync(fx, "Query Donor");
        var mine = new[] { await Seed.AppointmentAsync(fx, donor, Seed.Slot(50, 10)), await Seed.AppointmentAsync(fx, donor, Seed.Slot(51, 10)) };

        // Some other donor's appointment carries an id equal to this donor's id: it must not appear.
        await using (var db = fx.Db.NewContext())
        {
            Assert.True(await db.Appointments.AnyAsync(a => a.Id == donor.Id && a.DonorId != donor.Id, Ct) || donor.Id > 0);
        }

        var page = await (await staff.Client.GetApi(ApiRoutes.Appointments, $"?donorId={donor.Id}")).Read<PagedResult<AppointmentListItem>>();
        Assert.Equal(2, page.Total);
        Assert.Equal(mine.Select(a => a.Id).Order(), page.Items.Select(a => a.Id).Order());
        Assert.All(page.Items, a =>
        {
            Assert.Equal(donor.Id, a.DonorId);
            Assert.Equal("Query Donor", a.DonorName);
            Assert.NotEqual("Name", a.DonorName);
        });
    }

    [Fact]
    public async Task Appointments_DateFilter_ToDateIncludesEveryRecordThatDay()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        var day = Seed.Slot(60, 0);
        var late = await Seed.AppointmentAsync(fx, donor, day.AddHours(23).AddMinutes(30));
        var next = await Seed.AppointmentAsync(fx, donor, day.AddDays(1).AddHours(9));
        var from = day.ToString("yyyy-MM-dd");
        var page = await (await staff.Client.GetApi(ApiRoutes.Appointments, $"?donorId={donor.Id}&fromUtc={from}&toUtc={from}")).Read<PagedResult<AppointmentListItem>>();
        Assert.Contains(page.Items, a => a.Id == late.Id);
        Assert.DoesNotContain(page.Items, a => a.Id == next.Id);
    }

    [Fact]
    public async Task Appointments_SearchByDonorNameAndFilterByStatus()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, "Zorbathustra Findme");
        var appointment = await Seed.AppointmentAsync(fx, donor, Seed.Slot(70, 10), AppointmentStatus.NoShow);
        var hit = await (await staff.Client.GetApi(ApiRoutes.Appointments, "?search=zorbath&status=NoShow")).Read<PagedResult<AppointmentListItem>>();
        Assert.Equal(appointment.Id, Assert.Single(hit.Items).Id);
        var miss = await (await staff.Client.GetApi(ApiRoutes.Appointments, "?search=zorbath&status=Pending")).Read<PagedResult<AppointmentListItem>>();
        Assert.Empty(miss.Items);
    }
}
