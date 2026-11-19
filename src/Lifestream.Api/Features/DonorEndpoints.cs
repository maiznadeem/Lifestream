using Lifestream.Api.Auth;
using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Features;

public static class DonorEndpoints
{
    private static readonly SortMap<Donor> Sorts = new SortMap<Donor>(d => d.Id, "name")
        .Add("name", d => d.Name).Add("lastDonated", d => d.LastDonated!);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.Donors, ListAsync);
        api.MapGet(ApiRoutes.DonorById, GetAsync);
        api.MapGet(ApiRoutes.DonorEligibility, EligibilityAsync);
        api.MapPost(ApiRoutes.Donors, CreateAsync);
        api.MapPatch(ApiRoutes.DonorById, UpdateAsync);
        api.MapDelete(ApiRoutes.DonorById, DeleteAsync);
    }

    internal static IQueryable<Donor> Filter(IQueryable<Donor> donors, string? search, BloodType? bloodType)
    {
        if (bloodType is { } type)
        {
            donors = donors.Where(d => d.BloodType == type);
        }

        if (Lists.CleanSearch(search) is { } term)
        {
            var like = Lists.PrefixPattern(term);
            var id = int.TryParse(term, out var parsed) ? parsed : -1;
            donors = donors.Where(d => EF.Functions.Like(d.Name, like, "\\") || EF.Functions.Like(d.Phone!, like, "\\") || d.Id == id);
        }

        return donors;
    }

    private static async Task<PagedResult<DonorListItem>> ListAsync([AsParameters] DonorQuery q, LifestreamDbContext db, CancellationToken ct)
    {
        var (page, size) = Lists.Normalize(q.Page, q.PageSize);
        var filtered = Filter(db.Donors.AsNoTracking(), q.Search, q.BloodType);
        var total = Lists.Total(filtered);
        var rows = await (from d in Sorts.Apply(filtered, q.Sort).Skip((page - 1) * size).Take(size).ForSearch(q.Search)
                          from t in total
                          select new ListRow<DonorListItem>(
                              new DonorListItem(d.Id, d.Name, d.DateOfBirth, d.Gender, d.BloodType, d.Phone, d.Frequency, d.LastDonated) { MedicalHold = d.MedicalHold },
                              t))
            .ToListAsync(ct);
        return await Lists.BuildAsync(rows, page, size, () => filtered.CountAsync(ct));
    }

    private static async Task<DonorDetail> GetAsync(int id, LifestreamDbContext db, CancellationToken ct) =>
        ToDetail(await db.Donors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiException.NotFound("The donor"));

    private static async Task<DonorEligibility> EligibilityAsync(int id, LifestreamDbContext db, TimeProvider time, CancellationToken ct)
    {
        var donor = await db.Donors.AsNoTracking().Where(d => d.Id == id).Select(d => new { d.DateOfBirth, d.LastDonated, d.MedicalHold }).FirstOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("The donor");
        var result = Eligibility.Evaluate(donor.DateOfBirth, donor.LastDonated, Dates.Today(time), donor.MedicalHold);
        return new DonorEligibility(result.IsEligible, result.Reasons, result.NextEligibleDate);
    }

    private static async Task<Created<DonorDetail>> CreateAsync(HttpRequest http, LifestreamDbContext db, TimeProvider time, CurrentUser user, IOptions<DemoSettings> demo, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<CreateDonorRequest>(ct);
        var today = Dates.Today(time);
        new Check().Text("name", r.Name, 100).DateOfBirth("dateOfBirth", r.DateOfBirth, today).Enum("gender", r.Gender).Enum("bloodType", r.BloodType)
            .Enum("frequency", r.Frequency).Phone("phone", r.Phone).Text("address", r.Address, 200, required: false).ThrowIfInvalid();

        var donor = await db.RunAsync(async token =>
        {
            await DemoLimits.EnsureRoomAsync(db, user, demo, DemoTable.Donors, token);
            var entity = new Donor
            {
                Name = r.Name.Trim(),
                DateOfBirth = r.DateOfBirth,
                Gender = r.Gender,
                BloodType = r.BloodType,
                Phone = Blank(r.Phone),
                Address = Blank(r.Address),
                Frequency = r.Frequency,
                MedicalHold = r.MedicalHold,
            };
            db.Donors.Add(entity);
            await db.SaveChangesAsync(token);
            return entity;
        }, ct);
        return TypedResults.Created($"{ApiRoutes.Prefix}/donors/{donor.Id}", ToDetail(donor));
    }

    private static async Task<DonorDetail> UpdateAsync(int id, HttpRequest http, LifestreamDbContext db, TimeProvider time, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<UpdateDonorRequest>(ct);
        var check = new Check();
        var version = RowVersions.FromText(r.RowVersion);
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

        if (r.Frequency.HasValue)
        {
            check.Enum("frequency", r.Frequency.Value);
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

        var donor = await db.RunAsync(async token =>
        {
            var entity = await db.Donors.FirstOrDefaultAsync(d => d.Id == id, token) ?? throw ApiException.NotFound("The donor");
            db.Entry(entity).Property(d => d.RowVersion).OriginalValue = version;
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

            if (r.Frequency.HasValue)
            {
                entity.Frequency = r.Frequency.Value;
            }

            if (r.Phone.HasValue)
            {
                entity.Phone = Blank(r.Phone.Value);
            }

            if (r.Address.HasValue)
            {
                entity.Address = Blank(r.Address.Value);
            }

            if (r.MedicalHold.HasValue)
            {
                entity.MedicalHold = r.MedicalHold.Value;
            }

            await db.SaveChangesAsync(token);
            return entity;
        }, ct);
        return ToDetail(donor);
    }

    private static async Task<NoContent> DeleteAsync(int id, LifestreamDbContext db, CancellationToken ct)
    {
        await db.RunAsync(async token =>
        {
            var entity = await db.Donors.FirstOrDefaultAsync(d => d.Id == id, token) ?? throw ApiException.NotFound("The donor");
            db.Donors.Remove(entity);
            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        return TypedResults.NoContent();
    }

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static DonorDetail ToDetail(Donor d) =>
        new(d.Id, d.Name, d.DateOfBirth, d.Gender, d.BloodType, d.Phone, d.Address, d.Frequency, d.LastDonated, RowVersions.ToText(d.RowVersion)) { MedicalHold = d.MedicalHold };
}
