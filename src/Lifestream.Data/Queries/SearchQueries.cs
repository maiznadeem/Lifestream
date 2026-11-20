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
    /// A receipt stores its party (<see cref="Receipt.DonorId"/>, <see cref="Receipt.PatientId"/>), so the search goes from the people a word finds
    /// straight to their receipts (IX_Receipt_DonorId_CreatedAtUtc, IX_Receipt_PatientId_CreatedAtUtc, IX_Receipt_StaffId_CreatedAtUtc): no
    /// appointment and no request is read. Until 2.1.6 the party was reached through them, which joined all of both tables whenever a word could be
    /// in most phones (three to six digits) or there were two words: 110 to 140 ms at 100x.
    /// </para>
    /// <para>
    /// A blood group and a code are plain filters on the receipt. Of the words looked for in names the search starts from ONE, the one in the fewest
    /// names, in four parts that cannot overlap, put together with <c>UNION ALL</c> (nothing is found twice, so nothing has to be made distinct; a
    /// <c>UNION</c> has to, in memory sized for SQL Server's guess of 9 % of the people, and spilled to disk when a word was in most phones):
    /// the receipts of the staff found, those of the donors found whose staff member was NOT found (a donation has a donor and no patient), those
    /// of the patients found likewise (an issue the reverse), and the receipt with that number if nothing else found it.
    /// </para>
    /// <para>
    /// Each part starts at the people found, so a name is matched once per person and never once per receipt. With further words every part also
    /// joins the other people of its receipts once (the staff member of a donor's or patient's receipt; the donor or patient of a staff
    /// member's), and a further word is matched on the row. Looking every word up for every receipt found was measured and not used: 2,400 pages
    /// for a last name and a first name, 1,500 with only the staff member looked up.
    /// </para>
    /// </summary>
    public static IQueryable<ReceiptHit> ReceiptsMatching(LifestreamDbContext db, Query query, ReceiptKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        var readings = query.Words.Select(w => Read(w, Codes.Receipt, hasBloodGroup: true)).ToList();

        var receipts = kind is { } only ? db.Receipts.Where(r => r.Kind == only) : db.Receipts;
        foreach (var reading in readings.Where(r => r.TextPattern is null))
        {
            var number = reading.Id ?? 0; // no receipt has the number 0
            receipts = reading.BloodGroup is { } group ? receipts.Where(r => r.BloodType == group) : receipts.Where(r => r.Id == number);
        }

        // The words looked for in names, the one in the fewest names first (a real word or a long number before a short one, then the longest).
        var named = readings.Where(r => r.TextPattern is not null).OrderByDescending(IsInFewNames).ThenByDescending(r => r.TextPattern!.Length).ToList();
        if (named.Count == 0)
        {
            return receipts.Select(r => new ReceiptHit { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType }).TagWith(PlanTag);
        }

        var first = named[0];
        var further = named.Skip(1).ToList();
        var donors = db.Donors.Where(DonorFound(first));
        var patients = db.Patients.Where(PatientFound(first));
        var staff = db.Staff.Where(StaffFound(first, orUsername: false));

        // "DonorId is not null" is said, though the join implies it: SQL Server uses a filtered index for a scan only when the query states the filter.
        var ofDonors = receipts.Where(r => r.DonorId != null);
        var ofPatients = receipts.Where(r => r.PatientId != null);
        IQueryable<Row> byDonor, byPatient, byStaff;
        // The parts do not overlap: the staff part takes every receipt of the staff found, the donor and the patient part those whose staff
        // member was NOT found. That way round because what a search costs is matching the word against names (and phones), and "was not
        // found" matches a second time: the staff are a tenth of the people, and the other way round every donor and patient was matched twice.
        if (further.Count == 0)
        {
            byStaff = from s in staff join r in receipts on s.Id equals r.StaffId select new Row { Receipt = r };
            byDonor = from d in donors join r in ofDonors on (int?)d.Id equals r.DonorId where !staff.Any(s => s.Id == r.StaffId) select new Row { Receipt = r };
            byPatient = from p in patients join r in ofPatients on (int?)p.Id equals r.PatientId where !staff.Any(s => s.Id == r.StaffId) select new Row { Receipt = r };
        }
        else
        {
            // With further words every row carries the people of its receipt, joined once: the further words are then matched on the row, with
            // no lookup per receipt. A receipt always has its staff member; of donor and patient it has one.
            var staffNotFound = Expression.Lambda<Func<Row, bool>>(Expression.Not(On(StaffFound(first, orUsername: false), Expression.Property(RowParameter, nameof(Row.Staff)))), RowParameter);
            byStaff =
                from s in staff
                join r in receipts on s.Id equals r.StaffId
                join d in db.Donors on r.DonorId equals (int?)d.Id into receiptDonors
                from d in receiptDonors.DefaultIfEmpty()
                join p in db.Patients on r.PatientId equals (int?)p.Id into receiptPatients
                from p in receiptPatients.DefaultIfEmpty()
                select new Row { Receipt = r, Donor = d, Patient = p, Staff = s };
            byDonor = (
                from d in donors
                join r in ofDonors on (int?)d.Id equals r.DonorId
                join s in db.Staff on r.StaffId equals s.Id
                select new Row { Receipt = r, Donor = d, Staff = s }).Where(staffNotFound);
            byPatient = (
                from p in patients
                join r in ofPatients on (int?)p.Id equals r.PatientId
                join s in db.Staff on r.StaffId equals s.Id
                select new Row { Receipt = r, Patient = p, Staff = s }).Where(staffNotFound);
        }

        var byNumber = first.Id is { } firstNumber
            ? receipts.Where(r => r.Id == firstNumber && !donors.Any(d => (int?)d.Id == r.DonorId) && !patients.Any(p => (int?)p.Id == r.PatientId) && !staff.Any(s => s.Id == r.StaffId))
            : null;

        foreach (var word in further)
        {
            var number = word.Id ?? 0;
            byDonor = byDonor.Where(Matches(word, donor: true, patient: false, staff: true));
            byPatient = byPatient.Where(Matches(word, donor: false, patient: true, staff: true));
            byStaff = byStaff.Where(Matches(word, donor: true, patient: true, staff: true));
            if (byNumber is not null)
            {
                // One receipt at most: looked up.
                var wordDonors = db.Donors.Where(DonorFound(word));
                var wordPatients = db.Patients.Where(PatientFound(word));
                var wordStaff = db.Staff.Where(StaffFound(word, orUsername: false));
                byNumber = byNumber.Where(r =>
                    r.Id == number || wordDonors.Any(d => (int?)d.Id == r.DonorId) || wordPatients.Any(p => (int?)p.Id == r.PatientId) || wordStaff.Any(s => s.Id == r.StaffId));
            }
        }

        var hits = byStaff.Select(x => new ReceiptHit { Id = x.Receipt.Id, CreatedAtUtc = x.Receipt.CreatedAtUtc, Kind = x.Receipt.Kind, BloodType = x.Receipt.BloodType });
        if (kind != ReceiptKind.Issue)
        {
            hits = hits.Concat(byDonor.Select(x => new ReceiptHit { Id = x.Receipt.Id, CreatedAtUtc = x.Receipt.CreatedAtUtc, Kind = x.Receipt.Kind, BloodType = x.Receipt.BloodType }));
        }

        if (kind != ReceiptKind.Donation)
        {
            hits = hits.Concat(byPatient.Select(x => new ReceiptHit { Id = x.Receipt.Id, CreatedAtUtc = x.Receipt.CreatedAtUtc, Kind = x.Receipt.Kind, BloodType = x.Receipt.BloodType }));
        }

        if (byNumber is not null)
        {
            hits = hits.Concat(byNumber.Select(r => new ReceiptHit { Id = r.Id, CreatedAtUtc = r.CreatedAtUtc, Kind = r.Kind, BloodType = r.BloodType }));
        }

        return hits.TagWith(PlanTag);
    }

    /// <summary>
    /// A word that finds few people: any word looked for in names (a short one matches word starts only, a fifth of the names at most; a longer
    /// one is a part of few names), unless it is also three to six digits of a phone, which may be in every phone (an area code). Seven digits
    /// or more are few phones again.
    /// </summary>
    private static bool IsInFewNames(Reading reading) => reading.TextPattern is not null && reading.PhonePattern is not { Length: < 9 };

    /// <summary>A receipt on its way through the search, with the people of it that the part of the statement it is in has joined.</summary>
    private sealed class Row
    {
        public Receipt Receipt { get; set; } = null!;

        public Donor? Donor { get; set; }

        public Patient? Patient { get; set; }

        public Staff? Staff { get; set; }
    }

    /// <summary>
    /// "A further word matches this row": its number, or one of the people the row carries (a person the row does not have is null there, and
    /// nothing matches null).
    /// </summary>
    private static Expression<Func<Row, bool>> Matches(Reading word, bool donor, bool patient, bool staff)
    {
        var number = word.Id ?? 0; // no receipt has the number 0
        Expression<Func<Row, bool>> byNumber = x => x.Receipt.Id == number;
        var row = RowParameter;
        var body = On(byNumber, row);
        if (donor)
        {
            body = Expression.OrElse(body, On(DonorFound(word), Expression.Property(row, nameof(Row.Donor))));
        }

        if (patient)
        {
            body = Expression.OrElse(body, On(PatientFound(word), Expression.Property(row, nameof(Row.Patient))));
        }

        if (staff)
        {
            body = Expression.OrElse(body, On(StaffFound(word, orUsername: false), Expression.Property(row, nameof(Row.Staff))));
        }

        return Expression.Lambda<Func<Row, bool>>(body, row);
    }

    private static readonly ParameterExpression RowParameter = Expression.Parameter(typeof(Row), "x");

    /// <summary>The body of <paramref name="predicate"/> with <paramref name="target"/> in the place of its parameter.</summary>
    private static Expression On<T>(Expression<Func<T, bool>> predicate, Expression target) => new Substitute(predicate.Parameters[0], target).Visit(predicate.Body);

    private sealed class Substitute(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
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
