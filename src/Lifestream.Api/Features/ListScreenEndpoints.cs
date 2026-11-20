using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Requests;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Features;

/// <summary>
/// Read-only additions for direction A's list screens: a donor's donations with their bags, the counts of the Appointments and Requests saved views, and the bags a fulfil
/// would issue. Each is a fixed, small number of statements (never one per row), and every list is capped.
/// </summary>
public static class ListScreenEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.DonorDonations, DonationsAsync);
        api.MapGet(ApiRoutes.AppointmentViews, AppointmentViewsAsync);
        api.MapGet(ApiRoutes.RequestViews, RequestViewsAsync);
        api.MapGet(ApiRoutes.RequestBags, BagsAsync);
    }

    /// <summary>The donor's donations, newest first (at most <see cref="DonorDonations.MaxItems"/>), each with its bags and where each bag went; plus the totals. Three statements.</summary>
    private static async Task<DonorDonations> DonationsAsync(int id, LifestreamDbContext db, CancellationToken ct)
    {
        if (!await db.Donors.AsNoTracking().AnyAsync(d => d.Id == id, ct))
        {
            throw ApiException.NotFound("The donor");
        }

        var receipts = db.Receipts.AsNoTracking().Where(r => r.Kind == ReceiptKind.Donation && r.Appointment!.DonorId == id);
        var totals = await receipts.GroupBy(_ => 1).Select(g => new { Count = g.Count(), Units = g.Sum(r => r.Units) }).FirstOrDefaultAsync(ct);
        var newest = await receipts.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id).Take(DonorDonations.MaxItems)
            .Select(r => new { r.Id, r.AppointmentId, r.CreatedAtUtc, r.Units })
            .ToListAsync(ct);
        var ids = newest.Select(r => r.Id).ToList();
        var bags = ids.Count == 0
            ? []
            : await db.BloodBags.AsNoTracking().Where(b => ids.Contains(b.DonationReceiptId)).OrderBy(b => b.Id)
                .Select(b => new
                {
                    b.DonationReceiptId,
                    Bag = new DonatedBag(
                        b.Id,
                        b.Status,
                        b.ExpiresOn,
                        b.IssueReceipt != null ? b.IssueReceipt.CreatedAtUtc : null,
                        b.IssueReceipt != null ? b.IssueReceipt.RequestId : null,
                        b.IssueReceipt != null && b.IssueReceipt.Request != null ? b.IssueReceipt.Request.PatientId : null,
                        b.IssueReceipt != null && b.IssueReceipt.Request != null ? b.IssueReceipt.Request.Patient!.Name : null),
                })
                .ToListAsync(ct);
        var byReceipt = bags.ToLookup(b => b.DonationReceiptId, b => b.Bag);
        return new DonorDonations(
            totals?.Count ?? 0,
            totals?.Units ?? 0,
            [.. newest.Select(r => new DonationHistoryItem(r.Id, r.AppointmentId, r.CreatedAtUtc, r.Units, [.. byReceipt[r.Id]]))]);
    }

    /// <summary>How many appointments each saved view holds, with the list's search, donor and blood group filters. One statement.</summary>
    private static async Task<AppointmentViewCounts> AppointmentViewsAsync(
        [FromQuery] string? search,
        [FromQuery] int? donorId,
        [FromQuery] BloodType? bloodType,
        [FromQuery] int? utcOffsetMinutes,
        LifestreamDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var (dayStart, dayEnd) = LocalDayUtc(time, utcOffsetMinutes);
        var filtered = AppointmentEndpoints.Filter(db, search, donorId, bloodType);
        var counts = await filtered.GroupBy(_ => 1).Select(g => new AppointmentViewCounts(
                g.Count(a => a.SlotStartUtc >= dayStart && a.SlotStartUtc < dayEnd && a.Status != AppointmentStatus.Cancelled),
                g.Count(a => a.SlotStartUtc >= dayEnd && a.Status == AppointmentStatus.Pending),
                g.Count(a => a.SlotStartUtc < dayStart && a.Status != AppointmentStatus.Cancelled),
                g.Count(a => a.Status == AppointmentStatus.Cancelled)))
            .FirstOrDefaultAsync(ct);
        return counts ?? new AppointmentViewCounts(0, 0, 0, 0);
    }

    /// <summary>How many requests each saved view holds, with the list's search, patient and blood group filters. One statement.</summary>
    private static async Task<RequestViewCounts> RequestViewsAsync(
        [FromQuery] string? search,
        [FromQuery] int? patientId,
        [FromQuery] BloodType? bloodType,
        [FromQuery] int? utcOffsetMinutes,
        LifestreamDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var dueBy = DueSoonBy(time, utcOffsetMinutes);
        var filtered = RequestEndpoints.Filter(db, search, patientId, bloodType);
        var counts = await filtered.GroupBy(_ => 1).Select(g => new RequestViewCounts(
                g.Count(r => r.Status == RequestStatus.Pending),
                g.Count(r => r.Status == RequestStatus.Pending && r.RequiredBy <= dueBy),
                g.Count(r => r.Status == RequestStatus.Fulfilled),
                g.Count(r => r.Status == RequestStatus.Cancelled)))
            .FirstOrDefaultAsync(ct);
        return counts ?? new RequestViewCounts(0, 0, 0, 0);
    }

    /// <summary>
    /// The bags a fulfil of this request would issue now: the oldest unexpired available bags of its blood group, in the order the fulfil statement takes them
    /// (<see cref="BloodBagSql.IssueOldestFirst"/>), at most the request's units; and how many units of the group are available. Three statements.
    /// </summary>
    private static async Task<FulfilPreview> BagsAsync(int id, LifestreamDbContext db, TimeProvider time, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().Where(r => r.Id == id).Select(r => new { r.BloodType, r.Units }).FirstOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("The request");
        var today = Dates.Today(time);
        var usable = db.BloodBags.AsNoTracking().Where(b => b.BloodType == request.BloodType && b.Status == BagStatus.Available && b.ExpiresOn >= today);
        var available = await usable.CountAsync(ct);
        var bags = await usable.OrderBy(b => b.CollectedAtUtc).ThenBy(b => b.Id).Take(request.Units)
            .Select(b => new SuggestedBag(
                b.Id,
                b.CollectedAtUtc,
                b.ExpiresOn,
                b.DonationReceipt!.Appointment != null ? b.DonationReceipt.Appointment.DonorId : null,
                b.DonationReceipt.Appointment != null ? b.DonationReceipt.Appointment.Donor!.Name : null))
            .ToListAsync(ct);
        return new FulfilPreview(request.Units, available, bags);
    }

    /// <summary>The caller's local "today" from the offset (UTC when absent); the offset must be a real one (within 14 hours of UTC).</summary>
    internal static DateOnly LocalToday(TimeProvider time, int? utcOffsetMinutes)
    {
        var offset = utcOffsetMinutes ?? 0;
        if (offset is < -840 or > 840)
        {
            throw ApiException.Validation("utcOffsetMinutes", "must be between -840 and 840");
        }

        return DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddMinutes(offset));
    }

    /// <summary>The caller's local day as a half-open UTC range.</summary>
    internal static (DateTime Start, DateTime End) LocalDayUtc(TimeProvider time, int? utcOffsetMinutes)
    {
        var today = LocalToday(time, utcOffsetMinutes);
        var start = DateTime.SpecifyKind(today.ToDateTime(TimeOnly.MinValue).AddMinutes(-(utcOffsetMinutes ?? 0)), DateTimeKind.Utc);
        return (start, start.AddDays(1));
    }

    /// <summary>"Due in 48 h": a pending request needed by the day after tomorrow at the latest (overdue ones included).</summary>
    internal static DateOnly DueSoonBy(TimeProvider time, int? utcOffsetMinutes) => LocalToday(time, utcOffsetMinutes).AddDays(2);

    /// <summary>The appointments of one saved view (null: all of them).</summary>
    internal static IQueryable<Appointment> InView(IQueryable<Appointment> appointments, AppointmentView? view, TimeProvider time, int? utcOffsetMinutes)
    {
        if (view is not { } chosen)
        {
            return appointments;
        }

        var (start, end) = LocalDayUtc(time, utcOffsetMinutes);
        return chosen switch
        {
            AppointmentView.Today => appointments.Where(a => a.SlotStartUtc >= start && a.SlotStartUtc < end && a.Status != AppointmentStatus.Cancelled),
            AppointmentView.Upcoming => appointments.Where(a => a.SlotStartUtc >= end && a.Status == AppointmentStatus.Pending),
            AppointmentView.Past => appointments.Where(a => a.SlotStartUtc < start && a.Status != AppointmentStatus.Cancelled),
            _ => appointments.Where(a => a.Status == AppointmentStatus.Cancelled),
        };
    }

    /// <summary>The requests of one saved view (null: all of them).</summary>
    internal static IQueryable<Request> InView(IQueryable<Request> requests, RequestView? view, TimeProvider time, int? utcOffsetMinutes)
    {
        if (view is not { } chosen)
        {
            return requests;
        }

        var dueBy = DueSoonBy(time, utcOffsetMinutes);
        return chosen switch
        {
            RequestView.Open => requests.Where(r => r.Status == RequestStatus.Pending),
            RequestView.DueSoon => requests.Where(r => r.Status == RequestStatus.Pending && r.RequiredBy <= dueBy),
            RequestView.Fulfilled => requests.Where(r => r.Status == RequestStatus.Fulfilled),
            _ => requests.Where(r => r.Status == RequestStatus.Cancelled),
        };
    }

    /// <summary>
    /// Donors who are, or are not, eligible to donate today, by the same rules as <see cref="Eligibility.Evaluate"/> (no hold, aged 18 to 60, at least 90 days since the
    /// last donation), written as date comparisons so the filter stays in SQL.
    /// </summary>
    internal static IQueryable<Donor> WhereEligible(IQueryable<Donor> donors, bool eligible, DateOnly today)
    {
        var oldEnough = LatestBirthDateForAge(today, Eligibility.MinAgeYears); // born on or before this: at least 18
        var tooOld = LatestBirthDateForAge(today, Eligibility.MaxAgeYears + 1); // born on or before this: 61 or older
        var lastAllowed = today.AddDays(-Eligibility.MinDaysBetweenDonations);
        return eligible
            ? donors.Where(d => !d.MedicalHold && d.DateOfBirth <= oldEnough && d.DateOfBirth > tooOld && (d.LastDonated == null || d.LastDonated <= lastAllowed))
            : donors.Where(d => d.MedicalHold || d.DateOfBirth > oldEnough || d.DateOfBirth <= tooOld || (d.LastDonated != null && d.LastDonated > lastAllowed));
    }

    /// <summary>The latest birth date of someone who is at least <paramref name="years"/> old today, as <see cref="Eligibility.AgeOn"/> counts (a 29 February birthday included).</summary>
    public static DateOnly LatestBirthDateForAge(DateOnly today, int years)
    {
        var latest = today.AddYears(-years);
        while (Eligibility.AgeOn(latest.AddDays(1), today) >= years)
        {
            latest = latest.AddDays(1);
        }

        return latest;
    }

    /// <summary>Donors who last donated in a window counted back from today (or never).</summary>
    internal static IQueryable<Donor> WhereLastDonated(IQueryable<Donor> donors, DonationRecency recency, DateOnly today)
    {
        var threeMonths = today.AddMonths(-3);
        var twelveMonths = today.AddMonths(-12);
        return recency switch
        {
            DonationRecency.Within3Months => donors.Where(d => d.LastDonated != null && d.LastDonated >= threeMonths),
            DonationRecency.Within12Months => donors.Where(d => d.LastDonated != null && d.LastDonated < threeMonths && d.LastDonated >= twelveMonths),
            DonationRecency.Over12Months => donors.Where(d => d.LastDonated != null && d.LastDonated < twelveMonths),
            _ => donors.Where(d => d.LastDonated == null),
        };
    }
}
