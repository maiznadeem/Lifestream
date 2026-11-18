using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Receipts;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Data.Queries;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Features;

public static class ReceiptEndpoints
{
    private static readonly SortMap<Receipt> Sorts = new SortMap<Receipt>(r => r.Id, "date", defaultDescending: true)
        .Add("date", r => r.CreatedAtUtc);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.Receipts, ListAsync);
        api.MapGet(ApiRoutes.ReceiptById, async (int id, LifestreamDbContext db, CancellationToken ct) => await LoadDetailAsync(db, id, ct));
    }

    private static readonly SortMap<ReceiptHit> HitSorts = new SortMap<ReceiptHit>(h => h.Id, "date", defaultDescending: true)
        .Add("date", h => h.CreatedAtUtc);

    private static async Task<PagedResult<ReceiptListItem>> ListAsync([AsParameters] ReceiptQuery q, LifestreamDbContext db, CancellationToken ct)
    {
        var (page, size) = Lists.Normalize(q.Page, q.PageSize);
        var (start, end) = Lists.DayRange(q.FromUtc, q.ToUtc);
        IQueryable<Receipt> pageOfReceipts;
        IQueryable<int> total;
        Func<Task<int>> countWhenEmpty;
        if (Lists.CleanSearch(q.Search) is { } term)
        {
            // A name search: the matches come from SearchQueries.ReceiptsMatching (index seeks that end in narrow indexes holding the
            // date, kind and blood type), are filtered, counted and paged there, and only the page's receipts are then read by id.
            var hits = SearchQueries.ReceiptsMatching(db, term, int.TryParse(term, out var id) ? id : null);
            if (q.Kind is { } hitKind)
            {
                hits = hits.Where(h => h.Kind == hitKind);
            }

            if (q.BloodType is { } hitType)
            {
                hits = hits.Where(h => h.BloodType == hitType);
            }

            if (start is { } hitStart)
            {
                hits = hits.Where(h => h.CreatedAtUtc >= hitStart);
            }

            if (end is { } hitEnd)
            {
                hits = hits.Where(h => h.CreatedAtUtc < hitEnd);
            }

            var pageOfHits = HitSorts.Apply(hits, q.Sort).Skip((page - 1) * size).Take(size);
            pageOfReceipts = Sorts.Apply(db.Receipts.AsNoTracking().Join(pageOfHits, r => r.Id, h => h.Id, (r, _) => r), q.Sort);
            total = Lists.Total(hits);
            countWhenEmpty = () => hits.CountAsync(ct);
        }
        else
        {
            IQueryable<Receipt> filtered = db.Receipts.AsNoTracking();
            if (q.Kind is { } kind)
            {
                filtered = filtered.Where(r => r.Kind == kind);
            }

            if (q.BloodType is { } type)
            {
                filtered = filtered.Where(r => r.BloodType == type);
            }

            if (start is { } s)
            {
                filtered = filtered.Where(r => r.CreatedAtUtc >= s);
            }

            if (end is { } e)
            {
                filtered = filtered.Where(r => r.CreatedAtUtc < e);
            }

            pageOfReceipts = Sorts.Apply(filtered, q.Sort).Skip((page - 1) * size).Take(size);
            total = Lists.Total(filtered);
            countWhenEmpty = () => filtered.CountAsync(ct);
        }

        var rows = await (from r in pageOfReceipts
                          from t in total
                          select new ListRow<ReceiptListItem>(
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
                              t))
            .ToListAsync(ct);
        return await Lists.BuildAsync(rows, page, size, countWhenEmpty);
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
