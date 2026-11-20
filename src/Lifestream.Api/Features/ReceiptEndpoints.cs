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
    private static readonly SortMap<Receipt> Sorts = new SortMap<Receipt>(r => r.Id, SortKeys.Date, defaultDescending: true)
        .Add(SortKeys.Date, r => r.CreatedAtUtc);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.Receipts, ListAsync);
        api.MapGet(ApiRoutes.ReceiptById, async (int id, LifestreamDbContext db, CancellationToken ct) => await LoadDetailAsync(db, id, ct));
    }

    private static readonly SortMap<ReceiptHit> HitSorts = new SortMap<ReceiptHit>(h => h.Id, SortKeys.Date, defaultDescending: true)
        .Add(SortKeys.Date, h => h.CreatedAtUtc);

    private static async Task<PagedResult<ReceiptListItem>> ListAsync([AsParameters] ReceiptQuery q, LifestreamDbContext db, CancellationToken ct)
    {
        var (page, size) = Lists.Normalize(q.Page, q.PageSize);
        var (start, end) = Lists.DayRange(q.FromUtc, q.ToUtc);
        IQueryable<Receipt> pageOfReceipts;
        IQueryable<int> total;
        if (Lists.ParseSearch(q.Search) is { } words)
        {
            // A search: the matches come from SearchQueries.ReceiptsMatching as (id, date, kind, blood type) rows read from narrow indexes, are
            // filtered, counted and paged here, and only the page's receipts are then read by id.
            var hits = SearchQueries.ReceiptsMatching(db, words, q.Kind);
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
        }

        // The total drives the statement and the page is joined to it, so a page past the end answers with the total and no rows, from ONE statement
        // (the indexed path above), instead of a second statement that counts the whole result again.
        var rows = await (from t in total
                          from r in pageOfReceipts.DefaultIfEmpty()
                          select new ListRow<ReceiptListItem?>(
                              r == null ? null : new ReceiptListItem(
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
        return Lists.BuildFromTotal(rows, page, size);
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
