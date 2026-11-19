using Lifestream.Api.Auth;
using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Patients;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Features;

public static class PatientEndpoints
{
    private static readonly SortMap<Patient> Sorts = new SortMap<Patient>(p => p.Id, SortKeys.Name)
        .Add(SortKeys.Name, p => p.Name);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.Patients, ListAsync);
        api.MapGet(ApiRoutes.PatientById, GetAsync);
        api.MapPost(ApiRoutes.Patients, CreateAsync);
        api.MapPatch(ApiRoutes.PatientById, UpdateAsync);
        api.MapDelete(ApiRoutes.PatientById, DeleteAsync);
        api.MapPost(ApiRoutes.PatientRestore, RestoreAsync);
    }

    internal static IQueryable<Patient> Filter(IQueryable<Patient> patients, string? search, BloodType? bloodType)
    {
        if (bloodType is { } type)
        {
            patients = patients.Where(p => p.BloodType == type);
        }

        if (Lists.CleanSearch(search) is { } term)
        {
            var like = Lists.PrefixPattern(term);
            patients = int.TryParse(term, out var id)
                ? patients.Where(p => p.Id == id || EF.Functions.Like(p.Name, like, "\\") || EF.Functions.Like(p.Phone!, like, "\\"))
                : patients.Where(p => EF.Functions.Like(p.Name, like, "\\") || EF.Functions.Like(p.Phone!, like, "\\"));
        }

        return patients;
    }

    private static async Task<PagedResult<PatientListItem>> ListAsync([AsParameters] PatientQuery q, LifestreamDbContext db, CancellationToken ct)
    {
        var (page, size) = Lists.Normalize(q.Page, q.PageSize);
        var filtered = Filter(db.Patients.AsNoTracking(), q.Search, q.BloodType);
        // The page of patients first, then the pending-request count of just those rows (joined back by id): a count written in the
        // same select would be worked out for every row the OFFSET skips as well.
        var pageOfPatients = Sorts.Apply(filtered, q.Sort).Skip((page - 1) * size).Take(size).ForSearch(q.Search);
        var joined = db.Patients.AsNoTracking().Join(pageOfPatients, p => p.Id, k => k.Id, (p, _) => p);
        var total = Lists.Total(filtered);
        var rows = await (from p in Sorts.Apply(joined, q.Sort)
                          from t in total
                          select new ListRow<PatientListItem>(
                              new PatientListItem(p.Id, p.Name, p.DateOfBirth, p.Gender, p.BloodType, p.Phone,
                                  db.Requests.Count(r => r.PatientId == p.Id && r.Status == RequestStatus.Pending)),
                              t))
            .ToListAsync(ct);
        return await Lists.BuildAsync(rows, page, size, () => filtered.CountAsync(ct));
    }

    private static async Task<PatientDetail> GetAsync(int id, LifestreamDbContext db, CancellationToken ct) =>
        ToDetail(await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw ApiException.NotFound("The patient"));

    private static async Task<Created<PatientDetail>> CreateAsync(HttpRequest http, LifestreamDbContext db, TimeProvider time, CurrentUser user, IOptions<DemoSettings> demo, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<CreatePatientRequest>(ct);
        new Check().Text("name", r.Name, 100).DateOfBirth("dateOfBirth", r.DateOfBirth, Dates.Today(time)).Enum("gender", r.Gender).Enum("bloodType", r.BloodType)
            .Phone("phone", r.Phone).Text("address", r.Address, 200, required: false).ThrowIfInvalid();

        var patient = await db.RunAsync(async token =>
        {
            await DemoLimits.EnsureRoomAsync(db, user, demo, DemoTable.Patients, token);
            var entity = new Patient
            {
                Name = r.Name.Trim(),
                DateOfBirth = r.DateOfBirth,
                Gender = r.Gender,
                BloodType = r.BloodType,
                Phone = DonorEndpoints.Blank(r.Phone),
                Address = DonorEndpoints.Blank(r.Address),
            };
            db.Patients.Add(entity);
            await db.SaveChangesAsync(token);
            return entity;
        }, ct);
        return TypedResults.Created($"{ApiRoutes.Prefix}/patients/{patient.Id}", ToDetail(patient));
    }

    private static async Task<PatientDetail> UpdateAsync(int id, HttpRequest http, LifestreamDbContext db, TimeProvider time, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<UpdatePatientRequest>(ct);
        var version = RowVersions.FromText(r.RowVersion);
        var check = new Check();
        if (r.Name.HasValue)
        {
            check.Text("name", r.Name.Value, 100);
        }

        if (r.DateOfBirth.HasValue)
        {
            check.DateOfBirth("dateOfBirth", r.DateOfBirth.Value, Dates.Today(time));
        }

        if (r.Gender.HasValue)
        {
            check.Enum("gender", r.Gender.Value);
        }

        if (r.BloodType.HasValue)
        {
            check.Enum("bloodType", r.BloodType.Value);
        }

        if (r.Phone.HasValue)
        {
            check.Phone("phone", r.Phone.Value);
        }

        if (r.Address.HasValue)
        {
            check.Text("address", r.Address.Value, 200, required: false);
        }

        check.ThrowIfInvalid();

        var patient = await db.RunAsync(async token =>
        {
            var entity = await db.Patients.FirstOrDefaultAsync(p => p.Id == id, token) ?? throw ApiException.NotFound("The patient");
            db.Entry(entity).Property(p => p.RowVersion).OriginalValue = version;
            if (r.Name.HasValue)
            {
                entity.Name = r.Name.Value!.Trim();
            }

            if (r.DateOfBirth.HasValue)
            {
                entity.DateOfBirth = r.DateOfBirth.Value;
            }

            if (r.Gender.HasValue)
            {
                entity.Gender = r.Gender.Value;
            }

            if (r.BloodType.HasValue)
            {
                entity.BloodType = r.BloodType.Value;
            }

            if (r.Phone.HasValue)
            {
                entity.Phone = DonorEndpoints.Blank(r.Phone.Value);
            }

            if (r.Address.HasValue)
            {
                entity.Address = DonorEndpoints.Blank(r.Address.Value);
            }

            await db.SaveChangesAsync(token);
            return entity;
        }, ct);
        return ToDetail(patient);
    }

    private static async Task<NoContent> DeleteAsync(int id, LifestreamDbContext db, TimeProvider time, CancellationToken ct)
    {
        await db.RunAsync(async token =>
        {
            var entity = await db.Patients.FirstOrDefaultAsync(p => p.Id == id, token) ?? throw ApiException.NotFound("The patient");
            await Tombstones.KeepAsync(db, entity, id, time, token);
            db.Patients.Remove(entity);
            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        return TypedResults.NoContent();
    }

    /// <summary>The Undo of a delete: the patient comes back with the same id and every value, as long as the delete was a moment ago.</summary>
    private static async Task<PatientDetail> RestoreAsync(int id, LifestreamDbContext db, TimeProvider time, CurrentUser user, IOptions<DemoSettings> demo, CancellationToken ct) =>
        ToDetail(await Tombstones.RestoreAsync(db, db.Patients, "Patient", id, "patient", time, token => DemoLimits.EnsureRoomAsync(db, user, demo, DemoTable.Patients, token), ct));

    private static PatientDetail ToDetail(Patient p) =>
        new(p.Id, p.Name, p.DateOfBirth, p.Gender, p.BloodType, p.Phone, p.Address, RowVersions.ToText(p.RowVersion));
}
