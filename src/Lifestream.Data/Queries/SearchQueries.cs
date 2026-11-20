using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Data.Queries;

/// <summary>
/// What a search finds, the same for every list and for the pickers and the command palette, which ask the same filters.
/// <para>
/// The text typed is split into words at white space. A record is found when EVERY word matches somewhere in what the list shows of it; the
/// order of the words does not matter. What one word is matched against depends on what it looks like:
/// </para>
/// <list type="bullet">
/// <item>a blood group ("O+", "ab-", "A−", also "0+" with a zero), on a list that shows one: that blood group, and nothing else;</item>
/// <item>the list's own record code ("D-0041" on Donors, "RC-0007" on Receipts; letters, a hyphen, digits): the record with that number;</item>
/// <item>anything else: a name that CONTAINS the word ("eastw" finds "Selim Eastwood", "ali" finds "Aaliyah"), without regard to case or accents,
/// as the name columns' collation <c>Latin1_General_100_CI_AI</c> compares. A word of fewer than <see cref="MinLettersInsideAWord"/> characters is
/// looked for only where a word of the name STARTS ("a" finds "Aaliyah Mukherjee" and "Selim Abernathy", not "Dalia Novak"): one letter is
/// inside nine names in ten, which finds nothing and is the one search that costs (see Cost);</item>
/// <item>digits, with or without the punctuation of a phone number ("0156", "555-0156", "+15550156", "(555)"), are looked for in names like any
/// word ("Unknown Male 3"), and ALSO as the record's number when the word is digits only, and inside phone numbers when there are at least
/// <see cref="MinPhoneDigits"/> of them (the punctuation and spaces of the stored number are ignored, so "5550156" finds "+1-555-0156"). Words of
/// digits that follow each other are ONE number typed with spaces, as the lists show it: "+1 (555) 019-2834" and "555 0156" are looked for as
/// 15550192834 and 5550156.</item>
/// </list>
/// <para>
/// Appointments are searched by their donor's name and phone, requests by their patient's, receipts by the donor's or patient's name and phone and
/// by the name of the staff member who issued them; staff by name, user name and code, and not by phone (a staff member's phone is private).
/// </para>
/// <para>
/// Cost. A name that contains a word cannot be found by an index seek on the columns there are (only a prefix of the WHOLE name can, which is why
/// "Eastwood" used not to find "Selim Eastwood"): the name index of Donor, Patient or Staff is read whole, 46 pages for 4,000 donors. That is the
/// price of any match after the first letter of the name, a later word's first letter as much as the middle of a word, so matching inside words
/// costs nothing more. The big tables (Appointment, Request, Receipt) are never searched by text: they are reached through the ids of the people
/// found. SQL Server cannot estimate how many names contain a word (it assumes 9 %, whatever the word), so the statements are built to cost about
/// the same whether a word matches nobody or everybody; see <c>docs/database.md</c>, "Search". What a search costs is then the rows it finds in
/// the big tables: a word inside nine names in ten finds nine receipts in ten, and has every one of them joined, counted and sorted (100 to 135 ms
/// for the receipt list at 100x, against 60 before a search looked inside names). That is why one and two letters match word starts only: "a"
/// then finds a fifth of the names.
/// </para>
/// </summary>
public static partial class SearchQueries
{
    private const string Escape = "\\";

    /// <summary>The fewest digits a word needs before it is looked for inside phone numbers.</summary>
    public const int MinPhoneDigits = 3;

    /// <summary>The fewest characters a word needs before it is looked for inside the words of a name; a shorter one matches where a word of the name starts.</summary>
    public const int MinLettersInsideAWord = 3;

    /// <summary>The most words one search may have (each one is another condition of the statement; a full name and a phone number are four).</summary>
    public const int MaxWords = 5;

    /// <summary>
    /// The tag every statement with a search carries (EF writes it as a comment in front of the SQL). The API adds the query hint a search needs to
    /// the statements that carry it (<c>SearchPlanInterceptor</c>); the tag is set here, so no caller can forget it.
    /// </summary>
    public const string PlanTag = "search";

    /// <summary>
    /// The tag of a search that reaches a big table through the ids of the people found (<c>DonorId IN (...)</c>) and through nothing else: it is
    /// to be run from the people found, one index seek per person (nested loops). SQL Server would join the whole index of the big table instead,
    /// because it assumes 360 of 4,000 donors for any word: 15 ms where the seeks take 7 for a name, and the same 31 ms for a word in most names.
    /// </summary>
    public const string SeekPlanTag = "search-seek";

    /// <summary>The letters of each list's record codes, as the screens show them ("D-0175").</summary>
    public static class Codes
    {
        public const string Donor = "D";
        public const string Patient = "P";
        public const string Appointment = "A";
        public const string Request = "R";
        public const string Receipt = "RC";
        public const string Staff = "S";
    }

    /// <summary>One word of a search and everything it could be read as; the list decides which reading applies (<see cref="Reading"/>).</summary>
    /// <param name="Text">The word as typed.</param>
    /// <param name="ContainsPattern">The LIKE pattern for "contains the word", wildcards escaped.</param>
    /// <param name="Number">The record number, when the word is digits only (and a positive int).</param>
    /// <param name="PhonePattern">The LIKE pattern for "the digits of the phone contain these digits", when the word is digits and phone punctuation with enough digits.</param>
    /// <param name="IsNumberLike">The word is digits and phone punctuation only.</param>
    /// <param name="CodeLetters">The letters of a record code ("D" of "d-0041"), upper case; <see cref="CodeNumber"/> is its number.</param>
    /// <param name="CodeNumber">The number of a record code.</param>
    /// <param name="BloodGroup">The blood group the word spells.</param>
    public sealed record Word(string Text, string ContainsPattern, int? Number, string? PhonePattern, bool IsNumberLike, string? CodeLetters, int? CodeNumber, BloodType? BloodGroup);

    /// <summary>A parsed search: its words, all of which must match.</summary>
    public sealed record Query(IReadOnlyList<Word> Words);

    /// <summary>
    /// How one word is matched on one list. A blood group stands alone, and so does the number of the list's own code (then there is no text). Every
    /// other word has a text, looked for in names, and digits may also have the record's number and a phone pattern: any one of them is a match.
    /// </summary>
    /// <param name="BloodGroup">The blood group to match.</param>
    /// <param name="Id">The record number to match.</param>
    /// <param name="PhonePattern">The LIKE pattern the digits of a phone must match.</param>
    /// <param name="TextPattern">The LIKE pattern for "the name contains the word".</param>
    /// <param name="AtWordStart">The text is short: it matches only where a word of the name starts (<see cref="MinLettersInsideAWord"/>).</param>
    public readonly record struct Reading(BloodType? BloodGroup, int? Id, string? PhonePattern, string? TextPattern, bool AtWordStart = false);

    [GeneratedRegex(@"^(AB|A|B|O|0)([+\-−])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BloodGroupWord();

    [GeneratedRegex(@"^([A-Za-z]{1,2})-(\d{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeWord();

    [GeneratedRegex(@"^[0-9+\-().]*[0-9][0-9+\-().]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLikeWord();

    /// <summary>Splits a trimmed, non-empty search text into its words and works out what each could be.</summary>
    public static Query Parse(string search)
    {
        ArgumentNullException.ThrowIfNull(search);
        var words = new List<Word>();
        string? number = null; // the words of digits read so far that follow each other: one number typed with spaces
        foreach (var text in search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (NumberLikeWord().IsMatch(text) && !BloodGroupWord().IsMatch(text))
            {
                number = number is null ? text : number + " " + text;
                continue;
            }

            if (number is not null)
            {
                words.Add(ParseWord(number));
                number = null;
            }

            words.Add(ParseWord(text));
        }

        if (number is not null)
        {
            words.Add(ParseWord(number));
        }

        return new Query(words);
    }

    private static Word ParseWord(string text)
    {
        BloodType? group = null;
        if (BloodGroupWord().Match(text) is { Success: true } g)
        {
            var letters = g.Groups[1].Value.ToUpperInvariant().Replace('0', 'O');
            group = Enum.Parse<BloodType>(letters + (g.Groups[2].Value == "+" ? "Pos" : "Neg"));
        }

        string? codeLetters = null;
        int? codeNumber = null;
        if (CodeWord().Match(text) is { Success: true } c && PositiveInt(c.Groups[2].Value) is { } n)
        {
            codeLetters = c.Groups[1].Value.ToUpperInvariant();
            codeNumber = n;
        }

        var numberLike = text.Contains(' ', StringComparison.Ordinal) || NumberLikeWord().IsMatch(text);
        var digits = string.Concat(text.Where(char.IsAsciiDigit));
        return new Word(
            text,
            "%" + EscapeLike(text) + "%",
            numberLike && digits.Length == text.Length ? PositiveInt(text) : null,
            numberLike && digits.Length >= MinPhoneDigits ? "%" + digits + "%" : null,
            numberLike,
            codeLetters,
            codeNumber,
            group);
    }

    // Digits only: "+1" and "1-5" are not numbers (int.TryParse with the default styles would read "+1" as 1).
    private static int? PositiveInt(string digits) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;

    /// <summary>The word with LIKE's wildcard characters escaped.</summary>
    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);

    /// <summary>How <paramref name="word"/> is matched on the list whose record codes start with <paramref name="codeLetters"/>.</summary>
    public static Reading Read(Word word, string codeLetters, bool hasBloodGroup)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (hasBloodGroup && word.BloodGroup is { } group)
        {
            return new Reading(group, null, null, null);
        }

        if (word.CodeLetters == codeLetters)
        {
            return new Reading(null, word.CodeNumber, null, null);
        }

        // Every other word is looked for in names; digits also as the record's number and inside phones.
        return new Reading(null, word.Number, word.PhonePattern, word.ContainsPattern, AtWordStart: word.Text.Length < MinLettersInsideAWord);
    }

    /// <summary>
    /// The pattern for "a word of the name starts with the text", to be matched against the name with a space put in front of it: the text after
    /// a space or a hyphen ("bl" finds "Renée Eastman-Blackwood" and "Blake Novak"). ONE pattern on purpose: written as three (the start of the
    /// name, after a space, after a hyphen) SQL Server adds up three guesses and expects a third of the names, where it expects 9 % for one
    /// pattern as for any other word. Made from the "contains" pattern, which is the escaped text between two %.
    /// </summary>
    private static string WordStart(string containsPattern, char alsoAfter = '-') => "%[ " + alsoAfter + "]" + containsPattern[1..];

    // ---- the people a word finds ----

    /// <summary>
    /// The donors a word finds through themselves: its text in the name, or its digits in the phone. The phone is compared by its digits
    /// ("+1-555-0156" as "15550156"; phone validation allows digits, spaces, + ( ) and -).
    /// </summary>
    private static Expression<Func<Donor, bool>> DonorFound(Reading reading)
    {
        var (name, phone) = (reading.AtWordStart ? WordStart(reading.TextPattern!) : reading.TextPattern!, reading.PhonePattern);
        return (reading.AtWordStart, phone) switch
        {
            (false, null) => d => EF.Functions.Like(d.Name, name, Escape),
            (true, null) => d => EF.Functions.Like(" " + d.Name, name, Escape),
            (false, _) => d => EF.Functions.Like(d.Name, name, Escape) || EF.Functions.Like(d.Phone!.Replace("-", "").Replace(" ", "").Replace("+", "").Replace("(", "").Replace(")", ""), phone!),
            (true, _) => d => EF.Functions.Like(" " + d.Name, name, Escape) || EF.Functions.Like(d.Phone!.Replace("-", "").Replace(" ", "").Replace("+", "").Replace("(", "").Replace(")", ""), phone!),
        };
    }

    /// <summary>The patients a word finds through themselves (as <see cref="DonorFound"/>).</summary>
    private static Expression<Func<Patient, bool>> PatientFound(Reading reading)
    {
        var (name, phone) = (reading.AtWordStart ? WordStart(reading.TextPattern!) : reading.TextPattern!, reading.PhonePattern);
        return (reading.AtWordStart, phone) switch
        {
            (false, null) => p => EF.Functions.Like(p.Name, name, Escape),
            (true, null) => p => EF.Functions.Like(" " + p.Name, name, Escape),
            (false, _) => p => EF.Functions.Like(p.Name, name, Escape) || EF.Functions.Like(p.Phone!.Replace("-", "").Replace(" ", "").Replace("+", "").Replace("(", "").Replace(")", ""), phone!),
            (true, _) => p => EF.Functions.Like(" " + p.Name, name, Escape) || EF.Functions.Like(p.Phone!.Replace("-", "").Replace(" ", "").Replace("+", "").Replace("(", "").Replace(")", ""), phone!),
        };
    }

    /// <summary>
    /// The staff a word finds through themselves: its text in the display name and, with <paramref name="orUsername"/>, in the user name (a short
    /// text where the user name or a part of it after a dot starts, "oz" in "ayse.ozturk"). NEVER their phone: a staff member's phone is private
    /// (the list does not carry it and only an administrator sees it, in the detail), and a search that found people by it would tell everyone
    /// who may read the list whose number contains which digits.
    /// </summary>
    private static Expression<Func<Staff, bool>> StaffFound(Reading reading, bool orUsername)
    {
        var name = reading.AtWordStart ? WordStart(reading.TextPattern!) : reading.TextPattern!;
        var username = reading.AtWordStart ? WordStart(reading.TextPattern!, alsoAfter: '.') : reading.TextPattern!;
        return (reading.AtWordStart, orUsername) switch
        {
            (false, false) => s => EF.Functions.Like(s.DisplayName, name, Escape),
            (false, true) => s => EF.Functions.Like(s.DisplayName, name, Escape) || EF.Functions.Like(s.Username, username, Escape),
            (true, false) => s => EF.Functions.Like(" " + s.DisplayName, name, Escape),
            (true, true) => s => EF.Functions.Like(" " + s.DisplayName, name, Escape) || EF.Functions.Like(" " + s.Username, username, Escape),
        };
    }

    /// <summary>"<paramref name="first"/> OR <paramref name="second"/>" as one predicate over the first one's parameter.</summary>
    private static Expression<Func<T, bool>> Or<T>(Expression<Func<T, bool>> first, Expression<Func<T, bool>> second) =>
        Expression.Lambda<Func<T, bool>>(Expression.OrElse(first.Body, new ParameterReplacer(second.Parameters[0], first.Parameters[0]).Visit(second.Body)), first.Parameters);

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    // ---- the lists of people: one statement over the table itself ----

    /// <summary>Donors that match every word: name, phone, blood group, donor code or number.</summary>
    public static IQueryable<Donor> Matching(this IQueryable<Donor> source, Query query)
    {
        ArgumentNullException.ThrowIfNull(query);
        foreach (var word in query.Words)
        {
            source = Read(word, Codes.Donor, hasBloodGroup: true) switch
            {
                { BloodGroup: { } group } => source.Where(d => d.BloodType == group),
                { TextPattern: null, Id: var id } => source.Where(d => d.Id == id),
                { Id: { } id } reading => source.Where(Or(d => d.Id == id, DonorFound(reading))),
                var reading => source.Where(DonorFound(reading)),
            };
        }

        return source.TagWith(PlanTag);
    }

    /// <summary>Patients that match every word: name, phone, blood group, patient code or number.</summary>
    public static IQueryable<Patient> Matching(this IQueryable<Patient> source, Query query)
    {
        ArgumentNullException.ThrowIfNull(query);
        foreach (var word in query.Words)
        {
            source = Read(word, Codes.Patient, hasBloodGroup: true) switch
            {
                { BloodGroup: { } group } => source.Where(p => p.BloodType == group),
                { TextPattern: null, Id: var id } => source.Where(p => p.Id == id),
                { Id: { } id } reading => source.Where(Or(p => p.Id == id, PatientFound(reading))),
                var reading => source.Where(PatientFound(reading)),
            };
        }

        return source.TagWith(PlanTag);
    }

    /// <summary>Staff that match every word: display name or user name, staff code or number. Staff have no blood group, so "A-" is a text here; and their phone is not searched (<see cref="StaffFound"/>).</summary>
    public static IQueryable<Staff> Matching(this IQueryable<Staff> source, Query query)
    {
        ArgumentNullException.ThrowIfNull(query);
        foreach (var word in query.Words)
        {
            source = Read(word, Codes.Staff, hasBloodGroup: false) switch
            {
                { TextPattern: null, Id: var id } => source.Where(s => s.Id == id),
                { Id: { } id } reading => source.Where(Or(s => s.Id == id, StaffFound(reading, orUsername: true))),
                var reading => source.Where(StaffFound(reading, orUsername: true)),
            };
        }

        return source.TagWith(PlanTag);
    }

    // ---- lists searched through a person: the ids of the people found, then the rows of those people ----

    /// <summary>
    /// Appointments that match every word: the donor's name and phone, the donor's blood group (the appointment's own copy of it), the appointment
    /// code or number. The words that can only match through the donor share ONE subquery over Donor (<c>DonorId IN (...)</c>). A word of digits
    /// may be the appointment's number instead: "the appointment with that number, and the other appointments of the donors found", two sets with
    /// nothing in common, so <c>UNION ALL</c>. Written as <c>Id = n OR DonorId IN (...)</c> the statement read 60,000 pages at 100x (SQL Server
    /// answers an OR over a subquery row by row).
    /// </summary>
    public static IQueryable<Appointment> Matching(this IQueryable<Appointment> source, LifestreamDbContext db, Query query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        IQueryable<Donor>? donors = null;
        foreach (var word in query.Words)
        {
            switch (Read(word, Codes.Appointment, hasBloodGroup: true))
            {
                case { BloodGroup: { } group }:
                    source = source.Where(a => a.DonorBloodType == group);
                    break;
                case { TextPattern: null, Id: var id }:
                    source = source.Where(a => a.Id == id);
                    break;
                case { Id: { } id } reading:
                    var found = db.Donors.Where(DonorFound(reading)).Select(d => d.Id);
                    source = source.Where(a => a.Id == id).Concat(source.Where(a => a.Id != id && found.Contains(a.DonorId)));
                    break;
                case var reading:
                    donors = (donors ?? db.Donors).Where(DonorFound(reading));
                    break;
            }
        }

        if (donors is not null)
        {
            var ids = donors.Select(d => d.Id);
            source = source.Where(a => ids.Contains(a.DonorId));
        }

        return source.TagWith(SeekPlanTag);
    }

    /// <summary>Requests that match every word: the patient's name and phone, the request's blood group, the request code or number. Built like the appointment search.</summary>
    public static IQueryable<Request> Matching(this IQueryable<Request> source, LifestreamDbContext db, Query query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        IQueryable<Patient>? patients = null;
        foreach (var word in query.Words)
        {
            switch (Read(word, Codes.Request, hasBloodGroup: true))
            {
                case { BloodGroup: { } group }:
                    source = source.Where(r => r.BloodType == group);
                    break;
                case { TextPattern: null, Id: var id }:
                    source = source.Where(r => r.Id == id);
                    break;
                case { Id: { } id } reading:
                    var found = db.Patients.Where(PatientFound(reading)).Select(p => p.Id);
                    source = source.Where(r => r.Id == id).Concat(source.Where(r => r.Id != id && found.Contains(r.PatientId)));
                    break;
                case var reading:
                    patients = (patients ?? db.Patients).Where(PatientFound(reading));
                    break;
            }
        }

        if (patients is not null)
        {
            var ids = patients.Select(p => p.Id);
            source = source.Where(r => ids.Contains(r.PatientId));
        }

        return source.TagWith(SeekPlanTag);
    }

    // ---- receipts ----

    /// <summary>
    /// The receipts a search finds, as (id, date, kind, blood type) rows: every word must match the receipt's donor or patient (name, phone), the staff
    /// member who issued it (name), its blood group, or its code or number.
    /// <para>
    /// A search whose longest word is a text, or seven digits or more of a phone, which is nearly every search, starts from the receipts that word
    /// finds (<see cref="ReceiptsOfOneWord"/>, the statement the search had before: 43 ms for a name at 100x) and checks the other words on those
    /// (<see cref="ReceiptsOfOneWordWithParties"/>). The longest word first because it is in the fewest names.
    /// </para>
    /// <para>
    /// Any other search may find most receipts (a few digits: an area code is in every phone; one or two letters with other words) and is answered
    /// by a statement that costs the same whatever it finds (78 ms). A receipt has exactly one party (a donation its appointment's donor, an
    /// issue its request's patient; the table's CHECK says so), so the two kinds are worked out apart and put together with <c>UNION ALL</c>:
    /// nothing is found twice, so nothing has to be made distinct. Each half is the receipts of its kind joined to their appointment or request
    /// for the person's id, then LEFT JOINed, per word, to the ids of the people and of the staff that match the word. With the list's
    /// <paramref name="kind"/> filter the other half is not built at all.
    /// </para>
    /// <para>
    /// Measured and not used: <c>EXISTS (...) OR EXISTS (...)</c> per receipt (SQL Server runs it once per receipt: minutes); counting the words
    /// that missed and filtering once at the end (60 ms for every extra word); one join per person table with a bit per word (the name match is
    /// then worked out once per receipt: 150 ms for one word); four chains that do not overlap, run as index seeks (160 to 440 ms).
    /// </para>
    /// </summary>
    public static IQueryable<ReceiptHit> ReceiptsMatching(LifestreamDbContext db, Query query, ReceiptKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        var readings = query.Words.OrderByDescending(w => w.Text.Length).Select(w => Read(w, Codes.Receipt, hasBloodGroup: true)).ToList();
        if (readings.Count == 1 && IsInFewNames(readings[0]))
        {
            return ReceiptsOfOneWord(db, readings[0], kind).TagWith(PlanTag);
        }

        if (readings.Count > 1 && IsInFewNames(readings[0]) && !readings[0].AtWordStart)
        {
            var found = ReceiptsOfOneWordWithParties(db, readings[0], kind);
            foreach (var reading in readings.Skip(1))
            {
                found = Keep(found, db, reading, donors: true, patients: true);
            }

            return found.Select(x => new ReceiptHit { Id = x.Id, CreatedAtUtc = x.CreatedAtUtc, Kind = x.Kind, BloodType = x.BloodType }).TagWith(PlanTag);
        }

        var donations =
            from r in db.Receipts
            where r.Kind == ReceiptKind.Donation
            join a in db.Appointments on r.AppointmentId equals (int?)a.Id
            select new ReceiptParties { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType, StaffId = r.StaffId, DonorId = (int?)a.DonorId, PatientId = (int?)null };
        var issues =
            from r in db.Receipts
            where r.Kind == ReceiptKind.Issue
            join q in db.Requests on r.RequestId equals (int?)q.Id
            select new ReceiptParties { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType, StaffId = r.StaffId, DonorId = (int?)null, PatientId = (int?)q.PatientId };
        foreach (var reading in readings)
        {
            donations = Keep(donations, db, reading, donors: true, patients: false);
            issues = Keep(issues, db, reading, donors: false, patients: true);
        }

        var parties = kind switch
        {
            ReceiptKind.Donation => donations,
            ReceiptKind.Issue => issues,
            _ => donations.Concat(issues),
        };
        return parties.Select(x => new ReceiptHit { Id = x.Id, CreatedAtUtc = x.CreatedAtUtc, Kind = x.Kind, BloodType = x.BloodType }).TagWith(PlanTag);
    }

    /// <summary>
    /// A word that finds few people: any word looked for in names (a short one matches word starts only, a fifth of the names at most; a longer
    /// one is a part of few names), unless it is also three to six digits of a phone, which may be in every phone (an area code). Seven digits
    /// or more are few phones again (the fewest the search took for a phone before).
    /// </summary>
    private static bool IsInFewNames(Reading reading) => reading.TextPattern is not null && reading.PhonePattern is not { Length: < 9 };

    /// <summary>
    /// The receipts ONE word finds, as chains that start at the people found and are put together with <c>UNION</c>: the donors' receipts (donor,
    /// appointment, receipt), the patients' (patient, request, receipt), those of the staff whose name contains the word, and the receipt with that
    /// number. Only the receipts of the people found pass through the joins, which is why it is the quickest statement for a word in few names. It
    /// is NOT used for a word that may be in most phones (three to six digits, an area code): SQL Server sizes the memory of the <c>UNION</c> for
    /// 9 % of the people, and with 91 % it spills to disk (500 ms at 100x).
    /// </summary>
    private static IQueryable<ReceiptHit> ReceiptsOfOneWord(LifestreamDbContext db, Reading reading, ReceiptKind? kind)
    {
        var byDonor =
            from d in db.Donors.Where(DonorFound(reading))
            join a in db.Appointments on d.Id equals a.DonorId
            join r in db.Receipts.Where(r => r.AppointmentId != null) on a.Id equals r.AppointmentId
            select new ReceiptHit { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType };
        var byPatient =
            from p in db.Patients.Where(PatientFound(reading))
            join q in db.Requests on p.Id equals q.PatientId
            join r in db.Receipts.Where(r => r.RequestId != null) on q.Id equals r.RequestId
            select new ReceiptHit { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType };
        var byStaff =
            from s in db.Staff.Where(StaffFound(reading, orUsername: false))
            join r in db.Receipts on s.Id equals r.StaffId
            select new ReceiptHit { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType };
        var hits = byDonor.Union(byPatient).Union(byStaff);
        if (reading.Id is { } number)
        {
            hits = hits.Union(db.Receipts.Where(r => r.Id == number).Select(r => new ReceiptHit { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType }));
        }

        return kind is { } only ? hits.Where(h => h.Kind == only) : hits;
    }

    /// <summary>
    /// <see cref="ReceiptsOfOneWord"/> for a search of several words: the same chains, each also carrying the ids of the receipt's donor or patient
    /// and of its staff member, so the other words are checked on the receipts found (<see cref="Keep"/>) without the receipt table being passed
    /// over again. The donor's and the patient's chain have the id at hand; the staff chain looks the receipt's appointment or request up for it.
    /// </summary>
    private static IQueryable<ReceiptParties> ReceiptsOfOneWordWithParties(LifestreamDbContext db, Reading reading, ReceiptKind? kind)
    {
        var byDonor =
            from d in db.Donors.Where(DonorFound(reading))
            join a in db.Appointments on d.Id equals a.DonorId
            join r in db.Receipts.Where(r => r.AppointmentId != null) on a.Id equals r.AppointmentId
            select new ReceiptParties { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType, StaffId = r.StaffId, DonorId = (int?)d.Id, PatientId = (int?)null };
        var byPatient =
            from p in db.Patients.Where(PatientFound(reading))
            join q in db.Requests on p.Id equals q.PatientId
            join r in db.Receipts.Where(r => r.RequestId != null) on q.Id equals r.RequestId
            select new ReceiptParties { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType, StaffId = r.StaffId, DonorId = (int?)null, PatientId = (int?)p.Id };
        var byStaff = WithParties(
            from s in db.Staff.Where(StaffFound(reading, orUsername: false))
            join r in db.Receipts on s.Id equals r.StaffId
            select r,
            db);
        var rows = byDonor.Union(byPatient).Union(byStaff);
        if (reading.Id is { } number)
        {
            rows = rows.Union(WithParties(db.Receipts.Where(r => r.Id == number), db));
        }

        return kind is { } only ? rows.Where(x => x.Kind == only) : rows;
    }

    /// <summary>Receipts with the id of their donor (through the appointment) or patient (through the request).</summary>
    private static IQueryable<ReceiptParties> WithParties(IQueryable<Receipt> receipts, LifestreamDbContext db) =>
        from r in receipts
        join a in db.Appointments on r.AppointmentId equals (int?)a.Id into appointments
        from a in appointments.DefaultIfEmpty()
        join q in db.Requests on r.RequestId equals (int?)q.Id into requests
        from q in requests.DefaultIfEmpty()
        select new ReceiptParties { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType, StaffId = r.StaffId, DonorId = (int?)a.DonorId, PatientId = (int?)q.PatientId };

    /// <summary>
    /// The receipts one word keeps: those with the blood group or the number; or, LEFT JOINed to the ids of the donors, patients and staff the word
    /// finds, those where one of them is there. <paramref name="donors"/> and <paramref name="patients"/> say which of the two the rows can have
    /// (a half of one kind has only one of them, so the other join is left out).
    /// </summary>
    private static IQueryable<ReceiptParties> Keep(IQueryable<ReceiptParties> rows, LifestreamDbContext db, Reading reading, bool donors, bool patients)
    {
        if (reading.BloodGroup is { } group)
        {
            return rows.Where(x => x.BloodType == group);
        }

        var id = reading.Id ?? 0; // no receipt has the number 0
        if (reading.TextPattern is null)
        {
            return rows.Where(x => x.Id == id);
        }

        var staff = db.Staff.Where(StaffFound(reading, orUsername: false)).Select(s => (int?)s.Id);
        var donorIds = db.Donors.Where(DonorFound(reading)).Select(d => (int?)d.Id);
        var patientIds = db.Patients.Where(PatientFound(reading)).Select(p => (int?)p.Id);
        if (donors && patients)
        {
            return rows
                .LeftJoin(donorIds, x => x.DonorId, found => found, (x, donor) => new { Row = x, Donor = donor })
                .LeftJoin(patientIds, x => x.Row.PatientId, found => found, (x, patient) => new { x.Row, x.Donor, Patient = patient })
                .LeftJoin(staff, x => (int?)x.Row.StaffId, found => found, (x, member) => new { x.Row, x.Donor, x.Patient, Member = member })
                .Where(x => x.Donor != null || x.Patient != null || x.Member != null || x.Row.Id == id)
                .Select(x => x.Row);
        }

        return donors
            ? rows
                .LeftJoin(donorIds, x => x.DonorId, found => found, (x, donor) => new { Row = x, Party = donor })
                .LeftJoin(staff, x => (int?)x.Row.StaffId, found => found, (x, member) => new { x.Row, x.Party, Member = member })
                .Where(x => x.Party != null || x.Member != null || x.Row.Id == id)
                .Select(x => x.Row)
            : rows
                .LeftJoin(patientIds, x => x.PatientId, found => found, (x, patient) => new { Row = x, Party = patient })
                .LeftJoin(staff, x => (int?)x.Row.StaffId, found => found, (x, member) => new { x.Row, x.Party, Member = member })
                .Where(x => x.Party != null || x.Member != null || x.Row.Id == id)
                .Select(x => x.Row);
    }

    /// <summary>A receipt on its way through the search: what the list filters and orders by, and the ids its words are matched through.</summary>
    private sealed class ReceiptParties
    {
        public int Id { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public ReceiptKind Kind { get; set; }
        public BloodType BloodType { get; set; }
        public int StaffId { get; set; }
        public int? DonorId { get; set; }
        public int? PatientId { get; set; }
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
