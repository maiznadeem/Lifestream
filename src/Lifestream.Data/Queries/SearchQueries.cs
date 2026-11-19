using System.Globalization;
using System.Linq.Expressions;
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

    /// <summary>
    /// What a list search term can match, decided once for every list (DB-05): a name or contact PREFIX is always tried against names; a phone is
    /// matched only when the term has at least <see cref="MinPhoneDigits"/> digits (so "+", "+1" and "+1-555", which every seeded phone starts with,
    /// search names only and never read the whole table); a record id is matched only when the WHOLE term is a number made of digits
    /// ("+1" and "1-5" are not ids; <c>int.TryParse</c> would read "+1" as 1).
    /// </summary>
    public sealed record Term(string Text, string Pattern, string? PhonePattern, int? Id);

    /// <summary>The fewest digits a term needs before it is also matched against phone numbers.</summary>
    public const int MinPhoneDigits = 7;

    /// <summary>Applies the shared rule to a trimmed, non-empty search term.</summary>
    public static Term ParseTerm(string term)
    {
        var text = term.Trim();
        var pattern = PrefixPattern(text);
        var phone = text.Count(char.IsAsciiDigit) >= MinPhoneDigits ? pattern : null;
        var id = text.All(char.IsAsciiDigit) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : (int?)null;
        return new Term(text, pattern, phone, id);
    }

    /// <summary>Donors whose name starts with the term, or (for a long enough term) whose phone does, or whose id it is.</summary>
    public static IQueryable<Donor> Matching(this IQueryable<Donor> source, Term term)
    {
        var pattern = term.Pattern;
        Expression<Func<Donor, bool>> name = d => EF.Functions.Like(d.Name, pattern, Escape);
        return source.Where(AnyOf(name, term.PhonePattern is { } phonePattern ? d => EF.Functions.Like(d.Phone!, phonePattern, Escape) : null, term.Id is { } id ? d => d.Id == id : null));
    }

    /// <summary>Donors by name or phone only (never by id), for lists that search through their donors.</summary>
    public static IQueryable<Donor> NameOrPhoneMatching(this IQueryable<Donor> source, Term term) => source.Matching(term with { Id = null });

    /// <summary>Patients whose name starts with the term, or (for a long enough term) whose phone does, or whose id it is.</summary>
    public static IQueryable<Patient> Matching(this IQueryable<Patient> source, Term term)
    {
        var pattern = term.Pattern;
        Expression<Func<Patient, bool>> name = p => EF.Functions.Like(p.Name, pattern, Escape);
        return source.Where(AnyOf(name, term.PhonePattern is { } phonePattern ? p => EF.Functions.Like(p.Phone!, phonePattern, Escape) : null, term.Id is { } id ? p => p.Id == id : null));
    }

    /// <summary>Patients by name or phone only (never by id), for lists that search through their patients.</summary>
    public static IQueryable<Patient> NameOrPhoneMatching(this IQueryable<Patient> source, Term term) => source.Matching(term with { Id = null });

    // OR of the predicates that are present (the first always is), each over its own parameter, joined over the first one's.
    private static Expression<Func<T, bool>> AnyOf<T>(Expression<Func<T, bool>> first, params Expression<Func<T, bool>>?[] others)
    {
        var body = first.Body;
        foreach (var other in others.OfType<Expression<Func<T, bool>>>())
        {
            body = Expression.OrElse(body, new ParameterReplacer(other.Parameters[0], first.Parameters[0]).Visit(other.Body));
        }

        return Expression.Lambda<Func<T, bool>>(body, first.Parameters);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

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
    /// (through their requests) whose name starts with the term, or whose phone does when the term is long enough to be a phone (<see cref="Term"/>). Each source is a chain of joins that starts at a name or
    /// phone index seek and ends at an index on the receipt's foreign key that carries these four columns, so the hits are found,
    /// filtered by kind, blood type and date, counted and ordered without reading the receipt table; only the page's own rows are then
    /// fetched by id. The sources are joined by <c>UNION</c> because a single <c>OR</c> across them cannot use any index.
    /// </summary>
    public static IQueryable<ReceiptHit> ReceiptsMatching(LifestreamDbContext db, Term term)
    {
        var pattern = term.Pattern;
        var id = term.Id;
        var byDonor =
            from d in db.Donors.NameOrPhoneMatching(term)
            join a in db.Appointments on d.Id equals a.DonorId
            join r in db.Receipts.Where(r => r.AppointmentId != null) on a.Id equals r.AppointmentId
            select new ReceiptHit { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType };
        var byPatient =
            from p in db.Patients.NameOrPhoneMatching(term)
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
