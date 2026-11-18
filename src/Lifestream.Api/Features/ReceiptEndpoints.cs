using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Receipts;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Features;

public static class ReceiptEndpoints
{
    private static readonly SortMap<Receipt> Sorts = new SortMap<Receipt>(r => r.Id, "created", defaultDescending: true)
        .Add("created", r => r.CreatedAtUtc).Add("id", r => r.Id).Add("units", r => r.Units).Add("bloodType", r => r.BloodType);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.Receipts, ListAsync);
        api.MapGet(ApiRoutes.ReceiptById, async (int id, LifestreamDbContext db, CancellationToken ct) => await LoadDetailAsync(db, id, ct));
    }

    private static async Task<PagedResult<ReceiptListItem>> ListAsync([AsParameters] ReceiptQuery q, LifestreamDbContext db, CancellationToken ct)
    {
        var (page, size) = Lists.Normalize(q.Page, q.PageSize);
        IQueryable<Receipt> filtered = db.Receipts.AsNoTracking();
        if (q.Kind is { } kind)
        {
            filtered = filtered.Where(r => r.Kind == kind);
        }

        if (q.BloodType is { } type)
        {
            filtered = filtered.Where(r => r.BloodType == type);
        }

        var (start, end) = Lists.DayRange(q.FromUtc, q.ToUtc);
        if (start is { } s)
        {
            filtered = filtered.Where(r => r.CreatedAtUtc >= s);
        }

        if (end is { } e)
        {
            filtered = filtered.Where(r => r.CreatedAtUtc < e);
        }

        if (Lists.CleanSearch(q.Search) is { } term)
        {
            var like = Lists.PrefixPattern(term);
            var id = int.TryParse(term, out var parsed) ? parsed : -1;
            filtered = filtered.Where(r => r.Id == id
                || EF.Functions.Like(r.Staff!.DisplayName, like, "\\")
                || (r.Appointment != null && EF.Functions.Like(r.Appointment.Donor!.Name, like, "\\"))
                || (r.Request != null && EF.Functions.Like(r.Request.Patient!.Name, like, "\\")));
        }

        var rows = await Sorts.Apply(filtered, q.Sort).Skip((page - 1) * size).Take(size)
            .Select(r => new ListRow<ReceiptListItem>(
                new ReceiptListItem(
                    r.Id,
                    r.CreatedAtUtc,
                    r.Kind,
                    r.BloodType,
                    r.Units,
                    r.StaffId,
                    r.Staff!.DisplayName,
                    r.Kind == ReceiptKind.Donation ? r.Appointment!.Donor!.Name : r.Request!.Patient!.Name,
                    r.AppointmentId,
                    r.RequestId),
                filtered.Count()))
            .ToListAsync(ct);
        return await Lists.BuildAsync(rows, page, size, () => filtered.CountAsync(ct));
    }

    internal static async Task<ReceiptDetail> LoadDetailAsync(LifestreamDbContext db, int id, CancellationToken ct)
    {
        var r = await db.Receipts.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.CreatedAtUtc,
                x.Kind,
                x.BloodType,
                x.Units,
                x.StaffId,
                StaffName = x.Staff!.DisplayName,
                Party = x.Kind == ReceiptKind.Donation ? x.Appointment!.Donor!.Name : x.Request!.Patient!.Name,
                x.AppointmentId,
                x.RequestId,
                x.Notes,
            })
            .FirstOrDefaultAsync(ct) ?? throw ApiException.NotFound("The receipt");
        var bagIds = await db.BloodBags.AsNoTracking()
            .Where(b => r.Kind == ReceiptKind.Donation ? b.DonationReceiptId == id : b.IssueReceiptId == id)
            .OrderBy(b => b.Id).Select(b => b.Id).ToListAsync(ct);
        return new ReceiptDetail(r.Id, r.CreatedAtUtc, r.Kind, r.BloodType, r.Units, r.StaffId, r.StaffName, r.Party, r.AppointmentId, r.RequestId, r.Notes, bagIds);
    }
}
