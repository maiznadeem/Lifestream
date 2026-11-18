using Lifestream.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Data.Queries;

/// <summary>
/// Name search for the list screens (DB-05). A search is a <b>prefix</b> match ("ay" finds "Ayşe Nur"): the name columns
/// carry the case- and accent-insensitive collation <c>Latin1_General_100_CI_AI</c> (the "normalized name") and an index, and
/// <c>LIKE 'term%'</c> on an indexed column is an index seek. A contains-search (<c>%term%</c>) cannot use an index and is not
/// offered. Do not use <c>string.StartsWith</c> with a variable: EF translates it to <c>LEFT(col, LEN(@p)) = @p</c>, which
/// reads the whole index.
/// </summary>
public static class SearchQueries
{
    private const string Escape = "\\";

    /// <summary>The LIKE pattern for "starts with <paramref name="term"/>", with the wildcard characters in the term escaped.</summary>
    public static string PrefixPattern(string term) =>
        term.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "%";

    public static IQueryable<Donor> NameStartsWith(this IQueryable<Donor> source, string term)
    {
        var pattern = PrefixPattern(term);
        return source.Where(d => EF.Functions.Like(d.Name, pattern, Escape));
    }

    public static IQueryable<Patient> NameStartsWith(this IQueryable<Patient> source, string term)
    {
        var pattern = PrefixPattern(term);
        return source.Where(p => EF.Functions.Like(p.Name, pattern, Escape));
    }

    public static IQueryable<Staff> NameStartsWith(this IQueryable<Staff> source, string term)
    {
        var pattern = PrefixPattern(term);
        return source.Where(s => EF.Functions.Like(s.DisplayName, pattern, Escape));
    }

    /// <summary>
    /// Ids of the donors whose name starts with the term: a seek on <c>Donor.Name</c>. For callers that want the ids first (the API
    /// folds the same seek into its one list statement as <c>DonorId IN (...)</c>).
    /// </summary>
    public static Task<List<int>> DonorIdsByNamePrefixAsync(LifestreamDbContext db, string term, CancellationToken cancellationToken = default) =>
        db.Donors.AsNoTracking().NameStartsWith(term).Select(d => d.Id).ToListAsync(cancellationToken);

    /// <summary>Ids of the patients whose name starts with the term (a seek on <c>Patient.Name</c>).</summary>
    public static Task<List<int>> PatientIdsByNamePrefixAsync(LifestreamDbContext db, string term, CancellationToken cancellationToken = default) =>
        db.Patients.AsNoTracking().NameStartsWith(term).Select(p => p.Id).ToListAsync(cancellationToken);

    /// <summary>
    /// The receipts a list search finds, as (id, date, kind, blood type) rows: the receipt with that id (when the term is a number), the
    /// receipts of the staff members whose name starts with the term, and of the donors (through their appointments) and patients
    /// (through their requests) whose name or contact starts with the term. Each source is a chain of joins that starts at a name or
    /// phone index seek and ends at an index on the receipt's foreign key that carries these four columns, so the hits are found,
    /// filtered by kind, blood type and date, counted and ordered without reading the receipt table; only the page's own rows are then
    /// fetched by id. The sources are joined by <c>UNION</c> because a single <c>OR</c> across them cannot use any index.
    /// </summary>
    public static IQueryable<ReceiptHit> ReceiptsMatching(LifestreamDbContext db, string term, int? id)
    {
        var pattern = PrefixPattern(term);
        var byDonor =
            from d in db.Donors.Where(d => EF.Functions.Like(d.Name, pattern, Escape) || EF.Functions.Like(d.Phone!, pattern, Escape))
            join a in db.Appointments on d.Id equals a.DonorId
            join r in db.Receipts.Where(r => r.AppointmentId != null) on a.Id equals r.AppointmentId
            select new ReceiptHit { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType };
        var byPatient =
            from p in db.Patients.Where(p => EF.Functions.Like(p.Name, pattern, Escape) || EF.Functions.Like(p.Phone!, pattern, Escape))
            join q in db.Requests on p.Id equals q.PatientId
            join r in db.Receipts.Where(r => r.RequestId != null) on q.Id equals r.RequestId
            select new ReceiptHit { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType };
        var byStaff =
            from s in db.Staff.Where(s => EF.Functions.Like(s.DisplayName, pattern, Escape))
            join r in db.Receipts on s.Id equals r.StaffId
            select new ReceiptHit { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType };
        var hits = byDonor.Union(byPatient).Union(byStaff);
        return id is { } receiptId
            ? hits.Union(db.Receipts.Where(r => r.Id == receiptId).Select(r => new ReceiptHit { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType }))
            : hits;
    }
}

/// <summary>What a receipt search keeps of each match: enough to filter, count and order the matches.</summary>
public sealed class ReceiptHit
{
    public int Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public Domain.ReceiptKind Kind { get; set; }
    public Domain.BloodType BloodType { get; set; }
}
