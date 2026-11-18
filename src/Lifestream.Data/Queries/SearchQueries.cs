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
    /// Ids of the donors whose name starts with the term: a seek on <c>Donor.Name</c>. Appointment search is two steps: these ids,
    /// then <c>Appointments.Where(a =&gt; ids.Contains(a.DonorId))</c>, a seek on <c>IX_Appointment_DonorId_SlotStartUtc</c> per
    /// id. In one statement the optimizer prefers to scan the appointment table for the join.
    /// </summary>
    public static Task<List<int>> DonorIdsByNamePrefixAsync(LifestreamDbContext db, string term, CancellationToken cancellationToken = default) =>
        db.Donors.AsNoTracking().NameStartsWith(term).Select(d => d.Id).ToListAsync(cancellationToken);

    /// <summary>Ids of the patients whose name starts with the term; the request search is two steps in the same way.</summary>
    public static Task<List<int>> PatientIdsByNamePrefixAsync(LifestreamDbContext db, string term, CancellationToken cancellationToken = default) =>
        db.Patients.AsNoTracking().NameStartsWith(term).Select(p => p.Id).ToListAsync(cancellationToken);

    private static IQueryable<Appointment> DonorNameStartsWith(this IQueryable<Appointment> source, LifestreamDbContext db, string term) =>
        source.Where(a => db.Donors.NameStartsWith(term).Select(d => d.Id).Contains(a.DonorId));

    private static IQueryable<Request> PatientNameStartsWith(this IQueryable<Request> source, LifestreamDbContext db, string term) =>
        source.Where(r => db.Patients.NameStartsWith(term).Select(p => p.Id).Contains(r.PatientId));

    /// <summary>
    /// Receipts whose party (the donor of a donation, the patient of an issue) has a name starting with the term. Two seek
    /// paths joined by <c>UNION</c> (a single <c>OR</c> across both would scan): donor name to appointment to receipt, and
    /// patient name to request to receipt.
    /// </summary>
    public static IQueryable<Receipt> PartyNameStartsWith(this IQueryable<Receipt> source, LifestreamDbContext db, string term)
    {
        var viaDonor = db.Receipts.Where(r => db.Appointments.DonorNameStartsWith(db, term).Select(a => a.Id).Contains(r.AppointmentId!.Value)).Select(r => r.Id);
        var viaPatient = db.Receipts.Where(r => db.Requests.PatientNameStartsWith(db, term).Select(q => q.Id).Contains(r.RequestId!.Value)).Select(r => r.Id);
        var ids = viaDonor.Union(viaPatient);
        return source.Where(r => ids.Contains(r.Id));
    }

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
