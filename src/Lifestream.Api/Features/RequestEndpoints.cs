using Lifestream.Api.Auth;
using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Data;
using Lifestream.Data.Queries;
using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Features;

public static class RequestEndpoints
{
    private static readonly SortMap<Request> Sorts = new SortMap<Request>(r => r.Id, SortKeys.Placed, defaultDescending: true)
        .Add(SortKeys.Placed, r => r.PlacedAtUtc).Add(SortKeys.RequiredBy, r => r.RequiredBy);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.Requests, ListAsync);
        api.MapGet(ApiRoutes.RequestById, async (int id, LifestreamDbContext db, CancellationToken ct) => await LoadDetailAsync(db, id, ct));
        api.MapPost(ApiRoutes.Requests, CreateAsync);
        api.MapPatch(ApiRoutes.RequestById, UpdateAsync);
        api.MapPost(ApiRoutes.RequestFulfil, FulfilAsync);
        api.MapPost(ApiRoutes.RequestCancel, CancelAsync);
        api.MapDelete(ApiRoutes.RequestById, DeleteAsync);
    }

    // RequestQuery carries no dates, so the date filters arrive as two extra optional query values (fromUtc, toUtc) on the placed date.
    private static async Task<PagedResult<RequestListItem>> ListAsync([AsParameters] RequestQuery q, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, LifestreamDbContext db, CancellationToken ct)
    {
        var (page, size) = Lists.Normalize(q.Page, q.PageSize);
        IQueryable<Request> filtered = db.Requests.AsNoTracking();
        if (q.PatientId is { } patientId)
        {
            filtered = filtered.Where(r => r.PatientId == patientId);
        }

        if (q.Status is { } status)
        {
            filtered = filtered.Where(r => r.Status == status);
        }

        if (q.BloodType is { } type)
        {
            filtered = filtered.Where(r => r.BloodType == type);
        }

        var (start, end) = Lists.DayRange(fromUtc, toUtc);
        if (start is { } s)
        {
            filtered = filtered.Where(r => r.PlacedAtUtc >= s);
        }

        if (end is { } e)
        {
            filtered = filtered.Where(r => r.PlacedAtUtc < e);
        }

        if (Lists.CleanSearch(q.Search) is { } term)
        {
            // Patient ids by name (and phone, for a term long enough to be one: prefix seeks), then the requests of those patients; a term that is a whole number also matches the id.
            var parsed = SearchQueries.ParseTerm(term);
            var patientIds = db.Patients.NameOrPhoneMatching(parsed).Select(p => p.Id);
            filtered = parsed.Id is { } id
                ? filtered.Where(r => r.Id == id || patientIds.Contains(r.PatientId))
                : filtered.Where(r => patientIds.Contains(r.PatientId));
        }

        var total = Lists.Total(filtered);
        var rows = await (from r in Sorts.Apply(filtered, q.Sort).Skip((page - 1) * size).Take(size).ForSearch(q.Search)
                          from t in total
                          select new ListRow<RequestListItem>(
                              new RequestListItem(r.Id, r.PatientId, r.Patient!.Name, r.BloodType, r.Units, r.PlacedAtUtc, r.RequiredBy, r.Status),
                              t))
            .ToListAsync(ct);
        return await Lists.BuildAsync(rows, page, size, () => filtered.CountAsync(ct));
    }

    internal static async Task<RequestDetail> LoadDetailAsync(LifestreamDbContext db, int id, CancellationToken ct)
    {
        var r = await db.Requests.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.PatientId,
                PatientName = x.Patient!.Name,
                x.BloodType,
                x.Units,
                x.PlacedAtUtc,
                x.RequiredBy,
                x.Status,
                ReceiptId = db.Receipts.Where(c => c.RequestId == x.Id).Select(c => (int?)c.Id).FirstOrDefault(),
                x.RowVersion,
            })
            .FirstOrDefaultAsync(ct) ?? throw ApiException.NotFound("The request");
        return new RequestDetail(r.Id, r.PatientId, r.PatientName, r.BloodType, r.Units, r.PlacedAtUtc, r.RequiredBy, r.Status, r.ReceiptId, RowVersions.ToText(r.RowVersion));
    }

    private static async Task<Created<RequestDetail>> CreateAsync(HttpRequest http, LifestreamDbContext db, TimeProvider time, CurrentUser user, IOptions<DemoSettings> demo, IOptions<ClinicSettings> clinic, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<CreateRequestRequest>(ct);
        var today = Dates.Today(time);
        var check = new Check().Positive("patientId", r.PatientId).Range("units", r.Units, 1, clinic.Value.MaxRequestUnits);
        if (r.RequiredBy == default || r.RequiredBy.Year < 2000)
        {
            check.Fail("requiredBy", "is required");
        }
        else if (r.RequiredBy < today)
        {
            check.Fail("requiredBy", "must not be in the past");
        }
        else if (r.RequiredBy > today.AddYears(1))
        {
            check.Fail("requiredBy", "must be within a year");
        }

        check.ThrowIfInvalid();

        var id = await db.RunAsync(async token =>
        {
            await DemoLimits.EnsureRoomAsync(db, user, demo, DemoTable.Requests, token);
            var bloodType = await db.Patients.AsNoTracking().Where(p => p.Id == r.PatientId).Select(p => (BloodType?)p.BloodType).FirstOrDefaultAsync(token)
                ?? throw ApiException.Validation("patientId", "does not exist");
            var request = new Request { PatientId = r.PatientId, BloodType = bloodType, Units = r.Units, RequiredBy = r.RequiredBy, Status = RequestStatus.Pending };
            db.Requests.Add(request);
            await db.SaveChangesAsync(token);
            return request.Id;
        }, ct);
        return TypedResults.Created($"{ApiRoutes.Prefix}/requests/{id}", await LoadDetailAsync(db, id, ct));
    }

    private static async Task<RequestDetail> UpdateAsync(int id, HttpRequest http, LifestreamDbContext db, TimeProvider time, IOptions<ClinicSettings> clinic, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<UpdateRequestRequest>(ct);
        var version = RowVersions.FromText(r.RowVersion);
        var check = new Check();
        if (r.Units.HasValue)
        {
            check.Range("units", r.Units.Value, 1, clinic.Value.MaxRequestUnits);
        }

        if (r.RequiredBy.HasValue && (r.RequiredBy.Value.Year < 2000 || r.RequiredBy.Value < Dates.Today(time)))
        {
            check.Fail("requiredBy", "must not be in the past");
        }

        check.ThrowIfInvalid();

        await db.RunAsync(async token =>
        {
            var request = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, token) ?? throw ApiException.NotFound("The request");
            db.Entry(request).Property(x => x.RowVersion).OriginalValue = version;
            if (request.Status != RequestStatus.Pending)
            {
                throw ApiException.Conflict("Only a pending request can be changed.");
            }

            if (r.Units.HasValue)
            {
                request.Units = r.Units.Value;
            }

            if (r.RequiredBy.HasValue)
            {
                request.RequiredBy = r.RequiredBy.Value;
            }

            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        return await LoadDetailAsync(db, id, ct);
    }

    private static async Task<RequestDetail> CancelAsync(int id, LifestreamDbContext db, CancellationToken ct)
    {
        await db.RunAsync(async token =>
        {
            var request = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, token) ?? throw ApiException.NotFound("The request");
            if (request.Status != RequestStatus.Pending)
            {
                throw ApiException.Conflict("Only a pending request can be cancelled.");
            }

            request.Status = RequestStatus.Cancelled;
            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        return await LoadDetailAsync(db, id, ct);
    }

    private static async Task<NoContent> DeleteAsync(int id, LifestreamDbContext db, CancellationToken ct)
    {
        await db.RunAsync(async token =>
        {
            var request = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, token) ?? throw ApiException.NotFound("The request");
            db.Requests.Remove(request);
            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Issues the oldest unexpired bags of the request's blood type, in one transaction: the issue receipt (staff id from the token),
    /// the bags marked issued, the request fulfilled. Too little stock rejects the whole thing and no bag changes.
    /// </summary>
    private static async Task<Created<ReceiptDetail>> FulfilAsync(int id, LifestreamDbContext db, CurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var staffId = await user.StaffIdAsync(ct);
        var receiptId = await db.RunAsync(async token =>
        {
            await db.LockAsync($"request:{id}", token);
            var request = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, token) ?? throw ApiException.NotFound("The request");
            if (request.Status != RequestStatus.Pending)
            {
                throw ApiException.Conflict("Only a pending request can be fulfilled.");
            }

            var receipt = new Receipt
            {
                Kind = ReceiptKind.Issue,
                BloodType = request.BloodType,
                Units = request.Units,
                StaffId = staffId,
                RequestId = request.Id,
            };
            db.Receipts.Add(receipt);
            request.Status = RequestStatus.Fulfilled;
            await db.SaveChangesAsync(token);

            var issued = await BloodBagSql.IssueAsync(db, receipt.Id, request.BloodType, request.Units, Dates.Today(time), token);
            if (issued < request.Units)
            {
                throw BloodBagSql.InsufficientStock();
            }

            return receipt.Id;
        }, ct);
        return TypedResults.Created($"{ApiRoutes.Prefix}/receipts/{receiptId}", await ReceiptEndpoints.LoadDetailAsync(db, receiptId, ct));
    }
}
