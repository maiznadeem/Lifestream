using Lifestream.Api.Auth;
using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Receipts;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Features;

public static class AppointmentEndpoints
{
    private static readonly SortMap<Appointment> Sorts = new SortMap<Appointment>(a => a.Id, "slot", defaultDescending: true)
        .Add("slot", a => a.SlotStartUtc).Add("placed", a => a.PlacedAtUtc);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.Appointments, ListAsync);
        api.MapGet(ApiRoutes.AppointmentSlots, SlotsAsync);
        api.MapGet(ApiRoutes.AppointmentById, async (int id, LifestreamDbContext db, CancellationToken ct) => await LoadDetailAsync(db, id, ct));
        api.MapPost(ApiRoutes.Appointments, BookAsync);
        api.MapPatch(ApiRoutes.AppointmentById, RescheduleAsync);
        api.MapPost(ApiRoutes.AppointmentDonation, RecordDonationAsync);
        api.MapPost(ApiRoutes.AppointmentCancel, CancelAsync);
        api.MapDelete(ApiRoutes.AppointmentById, DeleteAsync);
    }

    private static async Task<PagedResult<AppointmentListItem>> ListAsync([AsParameters] AppointmentQuery q, LifestreamDbContext db, CancellationToken ct)
    {
        var (page, size) = Lists.Normalize(q.Page, q.PageSize);
        IQueryable<Appointment> filtered = db.Appointments.AsNoTracking();
        if (q.DonorId is { } donorId)
        {
            filtered = filtered.Where(a => a.DonorId == donorId);
        }

        if (q.Status is { } status)
        {
            filtered = filtered.Where(a => a.Status == status);
        }

        if (q.BloodType is { } type)
        {
            filtered = filtered.Where(a => a.DonorBloodType == type);
        }

        var (start, end) = Lists.DayRange(q.FromUtc, q.ToUtc);
        if (start is { } s)
        {
            filtered = filtered.Where(a => a.SlotStartUtc >= s);
        }

        if (end is { } e)
        {
            filtered = filtered.Where(a => a.SlotStartUtc < e);
        }

        if (Lists.CleanSearch(q.Search) is { } term)
        {
            // Donor ids by name or contact (two prefix seeks), then the appointments of those donors; a numeric term also matches the id.
            var like = Lists.PrefixPattern(term);
            var donorIds = db.Donors.Where(d => EF.Functions.Like(d.Name, like, "\\") || EF.Functions.Like(d.Phone!, like, "\\")).Select(d => d.Id);
            filtered = int.TryParse(term, out var id)
                ? filtered.Where(a => a.Id == id || donorIds.Contains(a.DonorId))
                : filtered.Where(a => donorIds.Contains(a.DonorId));
        }

        var total = Lists.Total(filtered);
        var rows = await (from a in Sorts.Apply(filtered, q.Sort).Skip((page - 1) * size).Take(size).ForSearch(q.Search)
                          from t in total
                          select new ListRow<AppointmentListItem>(
                              new AppointmentListItem(a.Id, a.DonorId, a.Donor!.Name, a.Donor.BloodType, a.PlacedAtUtc, a.SlotStartUtc, a.Status),
                              t))
            .ToListAsync(ct);
        return await Lists.BuildAsync(rows, page, size, () => filtered.CountAsync(ct));
    }

    private static async Task<List<SlotInfo>> SlotsAsync([FromQuery] DateOnly? date, LifestreamDbContext db, ClinicSchedule clinic, CancellationToken ct)
    {
        if (date is not { } day || day.Year < 2000)
        {
            throw ApiException.Validation("date", "is required (yyyy-MM-dd)");
        }

        Dates.CheckWindow(day, "date");

        var slots = clinic.SlotsOn(day).ToList();
        if (slots.Count == 0)
        {
            return [];
        }

        var first = slots[0];
        var last = slots[^1];
        var taken = await db.Appointments.AsNoTracking()
            .Where(a => a.SlotStartUtc >= first && a.SlotStartUtc <= last && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Completed))
            .Select(a => a.SlotStartUtc).ToListAsync(ct);
        var takenSet = taken.Select(t => t.Ticks).ToHashSet();
        return [.. slots.Select(s => new SlotInfo(s, takenSet.Contains(s.Ticks)))];
    }

    internal static async Task<AppointmentDetail> LoadDetailAsync(LifestreamDbContext db, int id, CancellationToken ct)
    {
        var a = await db.Appointments.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.DonorId,
                DonorName = x.Donor!.Name,
                DonorBloodType = x.Donor.BloodType,
                x.PlacedAtUtc,
                x.SlotStartUtc,
                x.Status,
                x.OverrideReason,
                ReceiptId = db.Receipts.Where(r => r.AppointmentId == x.Id).Select(r => (int?)r.Id).FirstOrDefault(),
                x.RowVersion,
            })
            .FirstOrDefaultAsync(ct) ?? throw ApiException.NotFound("The appointment");
        return new AppointmentDetail(a.Id, a.DonorId, a.DonorName, a.DonorBloodType, a.PlacedAtUtc, a.SlotStartUtc, a.Status, a.OverrideReason, a.ReceiptId, RowVersions.ToText(a.RowVersion));
    }

    private static void CheckSlot(Check check, ClinicSchedule clinic, DateTime slotUtc, DateTime nowUtc)
    {
        if (slotUtc == default || slotUtc.Year < 2000)
        {
            check.Fail("slotStartUtc", "is required");
        }
        else if (slotUtc <= nowUtc)
        {
            check.Fail("slotStartUtc", "must be in the future");
        }
        else if (clinic.WhyNotBookable(slotUtc) is { } why)
        {
            check.Fail("slotStartUtc", why);
        }
    }

    /// <summary>An ineligible donor can be given a slot only with an override reason (stored on the appointment); without one it is a 409 listing the reasons.</summary>
    private static void RequireReasonWhenIneligible(EligibilityResult eligibility, string? reason)
    {
        if (!eligibility.IsEligible && reason is null)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                ProblemCodes.Ineligible,
                "The donor is not eligible on that day. Give an override reason to book anyway.",
                new Dictionary<string, string[]> { ["reasons"] = [.. eligibility.Reasons] });
        }
    }

    private static async Task<Created<AppointmentDetail>> BookAsync(HttpRequest http, LifestreamDbContext db, ClinicSchedule clinic, TimeProvider time, CurrentUser user, IOptions<DemoSettings> demo, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<BookAppointmentRequest>(ct);
        var slot = Dates.Utc(r.SlotStartUtc);
        var now = time.GetUtcNow().UtcDateTime;
        var check = new Check().Positive("donorId", r.DonorId).Text("overrideReason", r.OverrideReason, 300, required: false);
        CheckSlot(check, clinic, slot, now);
        check.ThrowIfInvalid();

        var id = await db.RunAsync(async token =>
        {
            await DemoLimits.EnsureRoomAsync(db, user, demo, DemoTable.Appointments, token);
            var donor = await db.Donors.AsNoTracking().Where(d => d.Id == r.DonorId).Select(d => new { d.DateOfBirth, d.LastDonated, d.MedicalHold }).FirstOrDefaultAsync(token)
                ?? throw ApiException.Validation("donorId", "does not exist");

            // Eligibility is judged on the appointment day, so a donor who becomes eligible by then can book it.
            var eligibility = Eligibility.Evaluate(donor.DateOfBirth, donor.LastDonated, DateOnly.FromDateTime(slot), donor.MedicalHold);
            var reason = DonorEndpoints.Blank(r.OverrideReason);
            RequireReasonWhenIneligible(eligibility, reason);

            await db.LockAsync($"slot:{slot.Ticks}", token);
            var taken = await db.Appointments.AnyAsync(a => a.SlotStartUtc == slot && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Completed), token);
            if (taken)
            {
                throw ApiException.Conflict("That slot is already taken.", ProblemCodes.SlotTaken);
            }

            var appointment = new Appointment
            {
                DonorId = r.DonorId,
                SlotStartUtc = slot,
                Status = AppointmentStatus.Pending,
                OverrideReason = eligibility.IsEligible ? null : reason,
            };
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync(token);
            return appointment.Id;
        }, ct);
        return TypedResults.Created($"{ApiRoutes.Prefix}/appointments/{id}", await LoadDetailAsync(db, id, ct));
    }

    /// <summary>
    /// Moves a pending appointment. A value at exactly midnight carries only a date (the clinic has no midnight slot), so the
    /// existing time of day is kept; any other value is the new slot.
    /// </summary>
    private static async Task<AppointmentDetail> RescheduleAsync(int id, HttpRequest http, LifestreamDbContext db, ClinicSchedule clinic, TimeProvider time, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<RescheduleAppointmentRequest>(ct);
        var version = RowVersions.FromText(r.RowVersion);
        var requested = Dates.Utc(r.SlotStartUtc);
        if (requested == default || requested.Year < 2000)
        {
            throw ApiException.Validation("slotStartUtc", "is required");
        }

        await db.RunAsync(async token =>
        {
            var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id, token) ?? throw ApiException.NotFound("The appointment");
            db.Entry(appointment).Property(a => a.RowVersion).OriginalValue = version;
            if (appointment.Status != AppointmentStatus.Pending)
            {
                throw ApiException.Conflict("Only a pending appointment can be moved.");
            }

            var slot = requested.TimeOfDay == TimeSpan.Zero ? requested.Date + appointment.SlotStartUtc.TimeOfDay : requested;
            slot = DateTime.SpecifyKind(slot, DateTimeKind.Utc);
            var check = new Check();
            CheckSlot(check, clinic, slot, time.GetUtcNow().UtcDateTime);
            check.ThrowIfInvalid();

            // The new day is judged like a booking: a donor who is ineligible on it needs the override reason the appointment already
            // carries (a reschedule request has no field for a new one).
            var donor = await db.Donors.AsNoTracking().Where(d => d.Id == appointment.DonorId).Select(d => new { d.DateOfBirth, d.LastDonated, d.MedicalHold }).FirstAsync(token);
            RequireReasonWhenIneligible(
                Eligibility.Evaluate(donor.DateOfBirth, donor.LastDonated, DateOnly.FromDateTime(slot), donor.MedicalHold),
                DonorEndpoints.Blank(appointment.OverrideReason));

            await db.LockAsync($"slot:{slot.Ticks}", token);
            var taken = await db.Appointments.AnyAsync(
                a => a.Id != id && a.SlotStartUtc == slot && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Completed), token);
            if (taken)
            {
                throw ApiException.Conflict("That slot is already taken.", ProblemCodes.SlotTaken);
            }

            appointment.SlotStartUtc = slot;
            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        return await LoadDetailAsync(db, id, ct);
    }

    private static async Task<AppointmentDetail> CancelAsync(int id, LifestreamDbContext db, CancellationToken ct)
    {
        await db.RunAsync(async token =>
        {
            var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id, token) ?? throw ApiException.NotFound("The appointment");
            if (appointment.Status != AppointmentStatus.Pending)
            {
                throw ApiException.Conflict("Only a pending appointment can be cancelled.");
            }

            appointment.Status = AppointmentStatus.Cancelled;
            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        return await LoadDetailAsync(db, id, ct);
    }

    private static async Task<NoContent> DeleteAsync(int id, LifestreamDbContext db, CancellationToken ct)
    {
        await db.RunAsync(async token =>
        {
            var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id, token) ?? throw ApiException.NotFound("The appointment");
            db.Appointments.Remove(appointment);
            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Records the donation for exactly this appointment, in one transaction: the receipt (staff id from the token), N bags in one
    /// INSERT, the appointment completed, the donor's last-donated date. A second attempt is refused.
    /// </summary>
    private static async Task<Created<ReceiptDetail>> RecordDonationAsync(int id, HttpRequest http, LifestreamDbContext db, CurrentUser user, TimeProvider time, IOptions<ClinicSettings> clinic, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<RecordDonationRequest>(ct);
        new Check().Range("units", r.Units, 1, clinic.Value.MaxDonationUnits).Text("notes", r.Notes, 500, required: false).ThrowIfInvalid();
        var staffId = await user.StaffIdAsync(ct);

        var receiptId = await db.RunAsync(async token =>
        {
            await db.LockAsync($"appointment:{id}", token);
            var appointment = await db.Appointments.Include(a => a.Donor).FirstOrDefaultAsync(a => a.Id == id, token) ?? throw ApiException.NotFound("The appointment");
            if (appointment.Status == AppointmentStatus.Completed || await db.Receipts.AnyAsync(x => x.AppointmentId == id, token))
            {
                throw ApiException.Conflict("This appointment already has a donation receipt.", ProblemCodes.AlreadyReceipted);
            }

            if (appointment.Status != AppointmentStatus.Pending)
            {
                throw ApiException.Conflict("Only a pending appointment can receive a donation.");
            }

            var now = time.GetUtcNow().UtcDateTime;
            var donor = appointment.Donor!;
            var receipt = new Receipt
            {
                Kind = ReceiptKind.Donation,
                BloodType = donor.BloodType,
                Units = r.Units,
                StaffId = staffId,
                AppointmentId = appointment.Id,
                Notes = DonorEndpoints.Blank(r.Notes),
            };
            db.Receipts.Add(receipt);
            appointment.Status = AppointmentStatus.Completed;
            var today = DateOnly.FromDateTime(now);
            if (donor.LastDonated is not { } last || last < today)
            {
                donor.LastDonated = today;
            }

            await db.SaveChangesAsync(token);
            await BloodBagSql.InsertAsync(db, receipt.Id, donor.BloodType, now, r.Units, token);
            return receipt.Id;
        }, ct);
        return TypedResults.Created($"{ApiRoutes.Prefix}/receipts/{receiptId}", await ReceiptEndpoints.LoadDetailAsync(db, receiptId, ct));
    }
}
