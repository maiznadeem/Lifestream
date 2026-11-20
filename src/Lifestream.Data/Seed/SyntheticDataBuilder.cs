using Lifestream.Data.Entities;
using Lifestream.Domain;

namespace Lifestream.Data.Seed;

/// <summary>Row counts at scale factor 1. The 100x dataset is exactly 100 times these.</summary>
public static class SeedSizes
{
    public const int Staff = 11;
    // Close to the old database's 32 donors and 35 patients, so the 100x dataset keeps their proportions (3,500 each).
    public const int Donors = 40;
    public const int Patients = 35;
    public const int CompletedAppointments = 180;
    public const int CancelledAppointments = 30;
    public const int NoShowAppointments = 20;
    public const int PendingAppointments = 70;
    public const int FulfilledRequests = 90;
    public const int PendingRequests = 40;
    public const int CancelledRequests = 20;

    public const int Appointments = CompletedAppointments + CancelledAppointments + NoShowAppointments + PendingAppointments;
    public const int Requests = FulfilledRequests + PendingRequests + CancelledRequests;

    /// <summary>One donation receipt per completed appointment plus one issue receipt per fulfilled request: 270 at 1x, 27,000 at 100x.</summary>
    public const int Receipts = CompletedAppointments + FulfilledRequests;
}

/// <summary>
/// The clinic days around a reset. Yesterday, today and tomorrow (UTC) each hold one appointment in every hour the clinic is open, so a viewer in any time zone
/// finds a clinic day in their own "today"; and what is over at the reset instant is decided by <see cref="DonationMinutes"/>.
/// </summary>
public static class DemoClinicDay
{
    /// <summary>The appointments each of yesterday, today and tomorrow is sure to hold: one per clinic hour (09 to 16 UTC), on the hour or the half hour.</summary>
    public const int Appointments = 8;

    /// <summary>
    /// A donation is recorded at most this many minutes after its slot started. An appointment whose slot started this long or longer before the reset is over
    /// (completed, or the day's one no-show); any other is pending.
    /// </summary>
    public const int DonationMinutes = 45;
}

/// <summary>Everything one generator run produces. Entities reference each other by navigation; ids are assigned by the database.</summary>
public sealed class DemoDataSet
{
    public List<Staff> Staff { get; } = [];
    public List<Donor> Donors { get; } = [];
    public List<Patient> Patients { get; } = [];
    public List<Appointment> Appointments { get; } = [];
    public List<Request> Requests { get; } = [];
    public List<Receipt> Receipts { get; } = [];
    public List<BloodBag> BloodBags { get; } = [];

    public int TotalRows => Staff.Count + Donors.Count + Patients.Count + Appointments.Count + Requests.Count + Receipts.Count + BloodBags.Count;

    public void AddTo(LifestreamDbContext db)
    {
        db.Staff.AddRange(Staff);
        db.Donors.AddRange(Donors);
        db.Patients.AddRange(Patients);
        db.Appointments.AddRange(Appointments);
        db.Requests.AddRange(Requests);
        db.Receipts.AddRange(Receipts);
        db.BloodBags.AddRange(BloodBags);
    }
}

/// <summary>
/// Builds a synthetic clinic as it stands at one instant (the moment the demo is reset), deterministically: the same instant and factor
/// always give the same rows, in the same order. It is the single place seed data comes from (see <see cref="SyntheticVocabulary"/>).
/// <para>
/// The people and the random draws depend on the UTC date only; the time of day decides how far the clinic's day has got. The three
/// clinic days around the instant (yesterday, today and tomorrow, in UTC) each hold a full day of appointments, so a viewer in any time zone
/// has appointments in their own "today" (see <see cref="AroundToday"/>). An appointment is over (<see cref="AppointmentStatus.Completed"/>
/// with its receipt and bags, or the day's one no-show) only when its slot started <see cref="DonationMinutes"/> or more before the instant;
/// no row carries a time after the instant.
/// </para>
/// </summary>
internal static class SyntheticDataBuilder
{
    // Clinic day: 16 half-hour slots from 09:00 to 16:30 UTC (the API's default Clinic:Hours, Clinic:SlotMinutes and Clinic:TimeZone).
    private const int SlotsPerDay = 16;

    /// <summary>The clinic's opening hour in UTC.</summary>
    private const int OpeningHourUtc = 9;

    private const int AppointmentsPerClinicDay = DemoClinicDay.Appointments;

    private const int DonationMinutes = DemoClinicDay.DonationMinutes;

    /// <summary>Donors who have never donated: the last appointment of today and of tomorrow is theirs (a first-time donor still to come).</summary>
    private const int FirstTimeDonors = 2;

    /// <summary>Mixed into the seed for the random stream of the days around today.</summary>
    private const ulong AroundTodayStream = 0x5EED_70DA_C11E_1C00UL;

    /// <summary>The demo history reaches this far back: the twelve months of the dashboard chart plus the part of the month before them.</summary>
    internal const int HistoryDays = 365;

    /// <summary>The oldest days of the history in which nothing is issued yet: the first donations have to build stock first.</summary>
    private const int IssueFreeDays = 30;

    // The hash of a random password that was generated once and thrown away: no demo row has a password anyone knows.
    // The demo login hands out a token without a password (see the design, section 5.3).
    internal const string DiscardedPasswordHash =
        "AQAAAAIACSfAAAAAEHSV0dUX6BPclTfVbB6jeIYt9ux4slOawl42iEu2F/yductjtVkFnvDRzibAdzn08g==";

    // Blood type frequencies, in BloodType enum order: A+, A-, B+, B-, AB+, AB-, O+, O-.
    private static readonly int[] TypeWeights = [34, 6, 9, 2, 3, 1, 37, 8];

    /// <summary>The clinic as it stands at 00:00 UTC of <paramref name="today"/>: the appointments of that day are all still to come.</summary>
    public static DemoDataSet Build(DateOnly today, int factor) => Build(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), factor);

    /// <summary>The clinic as it stands at <paramref name="nowUtc"/> (the reset instant).</summary>
    public static DemoDataSet Build(DateTime nowUtc, int factor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var today = DateOnly.FromDateTime(nowUtc);
        var seed = ((ulong)today.DayNumber << 16) ^ (ulong)factor;
        var rng = new DeterministicRandom(seed);
        // The days around today draw from a stream of their own, so the history's draws do not depend on them.
        var aroundRng = new DeterministicRandom(seed ^ AroundTodayStream);
        var set = new DemoDataSet();
        // At least a full year of history, so the dashboard's twelve monthly buckets all have data; bigger scale datasets keep their longer span.
        var pastDays = Math.Max(HistoryDays, 110 * ((factor + 5) / 6));
        var futureDays = 30 * ((factor + 3) / 4);

        BuildStaff(set, rng, today, factor);
        BuildPeople(set, rng, today, factor);
        var donationAppointments = BuildAppointments(set, rng, aroundRng, nowUtc, factor, pastDays, futureDays);
        BuildDonations(set, rng, donationAppointments);
        BuildRequestsAndIssues(set, rng, nowUtc, factor, pastDays);
        SetDonorHistory(set);
        return set;
    }

    private static void BuildStaff(DemoDataSet set, DeterministicRandom rng, DateOnly today, int factor)
    {
        var count = SeedSizes.Staff * factor;
        var order = Shuffled(count, rng, NameSpace(count));
        for (var i = 0; i < count; i++)
        {
            var isDemo = i == 0;
            var isAdmin = !isDemo && i % 11 is 1 or 2;
            var name = isDemo ? SyntheticVocabulary.DemoStaffName : SyntheticVocabulary.PersonName(order[i]);
            set.Staff.Add(new Staff
            {
                DisplayName = name,
                Username = isDemo ? "demo" : SyntheticVocabulary.UsernameFor(name),
                Credential = new StaffCredential { PasswordHash = DiscardedPasswordHash },
                Gender = PickGender(rng),
                DateOfBirth = today.AddDays(-rng.Between(22 * 365, 60 * 365)),
                Designation = isAdmin ? Designation.Admin : rng.Pick([Designation.Doctor, Designation.Worker, Designation.Assistant]),
                Shift = rng.Pick([Shift.Morning, Shift.Evening, Shift.FullTime]),
                Role = isAdmin ? Role.Admin : Role.Staff,
                Salary = rng.Between(60, 180) * 500,
                Phone = SyntheticVocabulary.Phone(rng.Next(100)),
                IsDemo = isDemo,
            });
        }
    }

    private static void BuildPeople(DemoDataSet set, DeterministicRandom rng, DateOnly today, int factor)
    {
        var donors = SeedSizes.Donors * factor;
        var donorOrder = Shuffled(donors, rng, NameSpace(donors));
        for (var i = 0; i < donors; i++)
        {
            set.Donors.Add(new Donor
            {
                Name = SyntheticVocabulary.PersonName(donorOrder[i]),
                DateOfBirth = today.AddDays(-rng.Between(18 * 365 + 30, 59 * 365)),
                Gender = PickGender(rng),
                BloodType = i < 8 ? (BloodType)i : PickBloodType(rng),
                Phone = SyntheticVocabulary.Phone(rng.Next(100)),
                Address = SyntheticVocabulary.Address(rng.Between(1, 400), rng.Next(6)),
                Frequency = DonorFrequency.Occasional,
            });
        }

        var patients = SeedSizes.Patients * factor;
        var patientOrder = Shuffled(patients, rng, NameSpace(patients));
        for (var i = 0; i < patients; i++)
        {
            set.Patients.Add(new Patient
            {
                Name = SyntheticVocabulary.PersonName(patientOrder[i]),
                DateOfBirth = today.AddDays(-rng.Between(365, 88 * 365)),
                Gender = PickGender(rng),
                BloodType = i < 8 ? (BloodType)i : PickBloodType(rng),
                Phone = SyntheticVocabulary.Phone(rng.Next(100)),
                Address = SyntheticVocabulary.Address(rng.Between(1, 400), rng.Next(6)),
            });
        }
    }

    /// <summary>
    /// Creates every appointment with a unique slot and returns the completed ones (each gets a donation receipt). The history ends two days ago and the
    /// open bookings start the day after tomorrow; yesterday, today and tomorrow are the full clinic days of <see cref="AroundToday"/>. The rows of those
    /// three days come out of the same totals, so the dataset holds exactly <see cref="SeedSizes"/> times the factor of every status at any time of day.
    /// </summary>
    private static List<Appointment> BuildAppointments(DemoDataSet set, DeterministicRandom rng, DeterministicRandom aroundRng, DateTime nowUtc, int factor, int pastDays, int futureDays)
    {
        var completed = new List<Appointment>();
        var todayStart = nowUtc.Date;
        var donors = DonorRoles.Assign(set.Donors, aroundRng);
        var around = AroundToday(aroundRng, nowUtc, donors);

        var completedInHistory = (SeedSizes.CompletedAppointments * factor) - around.Count(a => a.Status == AppointmentStatus.Completed);
        var noShowsInHistory = (SeedSizes.NoShowAppointments * factor) - around.Count(a => a.Status == AppointmentStatus.NoShow);
        var cancelled = SeedSizes.CancelledAppointments * factor;
        var pastCount = completedInHistory + cancelled + noShowsInHistory;
        var historyStart = todayStart.AddDays(-pastDays);
        var recent = todayStart.AddDays(-Eligibility.MinDaysBetweenDonations);
        var pastSlots = SlotsOverMonths(pastCount, rng, historyStart, pastDays - 1);
        for (var i = 0; i < pastCount; i++)
        {
            var status = i < completedInHistory ? AppointmentStatus.Completed
                : i < completedInHistory + cancelled ? AppointmentStatus.Cancelled
                : AppointmentStatus.NoShow;
            var slot = SlotTime(historyStart, pastSlots[i]);
            // A donor who comes in today or tomorrow has not given blood in the 90 days before; one who comes for the first time never has.
            var donor = status != AppointmentStatus.Completed ? rng.Pick(set.Donors)
                : slot >= recent ? rng.Pick(donors.Regulars)
                : rng.Pick(donors.Returning);
            var appointment = new Appointment
            {
                Donor = donor,
                DonorBloodType = donor.BloodType,
                SlotStartUtc = slot,
                PlacedAtUtc = slot.AddDays(-rng.Between(1, 14)).AddMinutes(rng.Between(-120, 120)),
                Status = status,
                OverrideReason = status == AppointmentStatus.Completed && rng.Chance(0.02) ? "Physician approved an early donation." : null,
            };
            set.Appointments.Add(appointment);
            if (status == AppointmentStatus.Completed)
            {
                completed.Add(appointment);
            }
        }

        set.Appointments.AddRange(around);
        completed.AddRange(around.Where(a => a.Status == AppointmentStatus.Completed));

        var pendingCount = (SeedSizes.PendingAppointments * factor) - around.Count(a => a.Status == AppointmentStatus.Pending);
        var futureSlots = Shuffled(pendingCount, rng, modulus: futureDays * SlotsPerDay);
        for (var i = 0; i < pendingCount; i++)
        {
            var donor = rng.Pick(set.Donors);
            set.Appointments.Add(new Appointment
            {
                Donor = donor,
                DonorBloodType = donor.BloodType,
                SlotStartUtc = SlotTime(todayStart.AddDays(2), futureSlots[i]),
                PlacedAtUtc = NotAfter(todayStart.AddDays(-rng.Between(0, 6)).AddMinutes(rng.Between(0, 59)), nowUtc),
                Status = AppointmentStatus.Pending,
            });
        }

        return completed;
    }

    /// <summary>
    /// The clinic days of yesterday, today and tomorrow (UTC), oldest first: in each, one appointment in every hour the clinic is open, on the hour or the half hour.
    /// <para>
    /// Why three days: the data of a reset is seen while the UTC date is still the reset's, and a viewer's local day is a 24-hour window around "now", so it lies inside
    /// those three days. A 24-hour window holds every time of day once, so it holds one appointment for each clinic hour (at least 7 of the 8: local midnight can fall
    /// between an hour and its half hour and split one pair). None of them is cancelled, so the Today view, the dashboard and the sidebar count all have them.
    /// </para>
    /// <para>
    /// Status: yesterday is over (seven donations and one no-show). Today, an appointment whose slot started <see cref="DonationMinutes"/> or more before
    /// <paramref name="nowUtc"/> is over; the others, and all of tomorrow's, are pending. The last appointment of today and of tomorrow is a first-time donor's.
    /// </para>
    /// </summary>
    private static List<Appointment> AroundToday(DeterministicRandom rng, DateTime nowUtc, DonorRoles donors)
    {
        var appointments = new List<Appointment>(3 * AppointmentsPerClinicDay);
        var todayStart = nowUtc.Date;
        var yesterday = Shuffled(AppointmentsPerClinicDay, rng, donors.Regulars.Count);
        for (var day = -1; day <= 1; day++)
        {
            // One of the day's appointments (never the first, never one of the last two) is a no-show once its time has passed.
            var noShow = rng.Between(1, AppointmentsPerClinicDay - 3);
            for (var hour = 0; hour < AppointmentsPerClinicDay; hour++)
            {
                var slot = todayStart.AddDays(day).AddHours(OpeningHourUtc + hour).AddMinutes(30 * rng.Next(2));
                var isOver = slot.AddMinutes(DonationMinutes) <= nowUtc;
                var donor = day < 0 ? donors.Regulars[yesterday[hour]]
                    : hour == AppointmentsPerClinicDay - 1 ? donors.FirstTime[day]
                    : donors.Eligible[(day * (AppointmentsPerClinicDay - 1)) + hour];
                appointments.Add(new Appointment
                {
                    Donor = donor,
                    DonorBloodType = donor.BloodType,
                    SlotStartUtc = slot,
                    // Booked on an earlier day: two days or more before the slot is before today for tomorrow's appointments too.
                    PlacedAtUtc = slot.AddDays(-rng.Between(2, 14)).AddMinutes(rng.Between(-120, 120)),
                    Status = !isOver ? AppointmentStatus.Pending : hour == noShow ? AppointmentStatus.NoShow : AppointmentStatus.Completed,
                });
            }
        }

        return appointments;
    }

    /// <summary>
    /// Which donors the days around today draw on. <see cref="FirstTime"/> never appear in a completed appointment of the history; <see cref="Eligible"/> (today's and
    /// tomorrow's other donors) have none in the last <see cref="Eligibility.MinDaysBetweenDonations"/> days; <see cref="Regulars"/> are everybody else, and
    /// <see cref="Returning"/> is everybody who has donated before. The first eight donors (one per blood group) stay regulars, so every group keeps a donor who
    /// gives blood all year.
    /// </summary>
    private sealed record DonorRoles(List<Donor> FirstTime, List<Donor> Eligible, List<Donor> Regulars, List<Donor> Returning)
    {
        public static DonorRoles Assign(List<Donor> all, DeterministicRandom rng)
        {
            var groups = BloodTypeExtensions.All.Count;
            var eligible = 2 * (AppointmentsPerClinicDay - 1);
            var order = Shuffled(FirstTimeDonors + eligible, rng, all.Count - groups);
            var firstTime = order[..FirstTimeDonors].Select(i => all[groups + i]).ToList();
            var today = order[FirstTimeDonors..].Select(i => all[groups + i]).ToList();
            var taken = new HashSet<Donor>(firstTime.Concat(today));
            return new DonorRoles(firstTime, today, [.. all.Where(d => !taken.Contains(d))], [.. all.Where(d => !firstTime.Contains(d))]);
        }
    }

    /// <summary>The time itself, or the same time a day earlier when it would be after the reset: nothing in the dataset is stamped in the future.</summary>
    private static DateTime NotAfter(DateTime time, DateTime nowUtc) => time <= nowUtc ? time : time.AddDays(-1);

    private static void BuildDonations(DemoDataSet set, DeterministicRandom rng, List<Appointment> completed)
    {
        var staff = set.Staff.Where(s => !s.IsDemo).ToList();
        foreach (var appointment in completed)
        {
            var units = rng.Chance(0.5) ? 1 : 2;
            var when = appointment.SlotStartUtc.AddMinutes(rng.Between(15, DonationMinutes));
            var receipt = new Receipt
            {
                CreatedAtUtc = when,
                Kind = ReceiptKind.Donation,
                BloodType = appointment.Donor!.BloodType,
                Units = units,
                Staff = rng.Pick(staff),
                Appointment = appointment,
            };
            set.Receipts.Add(receipt);
            for (var u = 0; u < units; u++)
            {
                set.BloodBags.Add(NewBag(receipt, when));
            }
        }
    }

    private static BloodBag NewBag(Receipt donation, DateTime collectedAtUtc) => new()
    {
        BloodType = donation.BloodType,
        CollectedAtUtc = collectedAtUtc,
        ExpiresOn = StockRules.ExpiryFor(collectedAtUtc),
        Status = BagStatus.Available,
        DonationReceipt = donation,
    };

    private static void BuildRequestsAndIssues(DemoDataSet set, DeterministicRandom rng, DateTime nowUtc, int factor, int pastDays)
    {
        var todayStart = nowUtc.Date;
        var today = DateOnly.FromDateTime(nowUtc);
        var staff = set.Staff.Where(s => !s.IsDemo).ToList();
        var patientsByType = set.Patients.GroupBy(p => p.BloodType).ToDictionary(g => g.Key, g => g.ToList());

        // Bags in collection order, released into per-type FIFO queues as time passes.
        var bags = set.BloodBags.OrderBy(b => b.CollectedAtUtc).ToList();
        var queues = Enum.GetValues<BloodType>().ToDictionary(t => t, _ => new Queue<BloodBag>());
        var next = 0;

        // Fulfilled requests are issued on days in the last (pastDays - IssueFreeDays) days, oldest first, so early donations build stock.
        // The window always reaches back to the first day of the dashboard's oldest month (11 months before this one), which on Jan 30 and 31 is
        // further back than 335 days, so that month has issues too. Their days are spread over the months with the same month-to-month variation as the donations.
        var fulfilled = SeedSizes.FulfilledRequests * factor;
        var oldestDashboardMonth = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-11);
        var issueWindowStart = DateTimeMin(todayStart.AddDays(-(pastDays - IssueFreeDays)), oldestDashboardMonth);
        var issueWindowDays = (int)(todayStart - issueWindowStart).TotalDays;
        var issueDays = DaysOverMonths(fulfilled, rng, issueWindowStart, issueWindowDays)
            .Select(day => issueWindowStart.AddDays(day).AddHours(9).AddMinutes(rng.Between(0, 479)))
            .Order()
            .ToList();

        foreach (var issuedAt in issueDays)
        {
            while (next < bags.Count && bags[next].CollectedAtUtc <= issuedAt)
            {
                queues[bags[next].BloodType].Enqueue(bags[next]);
                next++;
            }

            var issueDate = DateOnly.FromDateTime(issuedAt);
            foreach (var queue in queues.Values)
            {
                while (queue.Count > 0 && queue.Peek().ExpiresOn < issueDate)
                {
                    queue.Dequeue();
                }
            }

            var candidates = queues.Where(q => q.Value.Count > 0).Select(q => q.Key).ToList();
            if (candidates.Count == 0)
            {
                // No stock at all on that day: the request stays Pending and never reaches the receipt count.
                set.Requests.Add(NewRequest(rng, rng.Pick(set.Patients), issuedAt.AddDays(-1), RequestStatus.Pending));
                continue;
            }

            var type = PickWeighted(rng, candidates);
            var units = Math.Min(rng.Chance(0.75) ? 1 : 2, queues[type].Count);
            var patient = rng.Pick(patientsByType[type]);
            var request = NewRequest(rng, patient, issuedAt.AddDays(-rng.Between(1, 5)), RequestStatus.Fulfilled);
            request.Units = units;
            set.Requests.Add(request);
            var issue = new Receipt
            {
                CreatedAtUtc = issuedAt,
                Kind = ReceiptKind.Issue,
                BloodType = type,
                Units = units,
                Staff = rng.Pick(staff),
                Request = request,
            };
            set.Receipts.Add(issue);
            for (var u = 0; u < units; u++)
            {
                var bag = queues[type].Dequeue();
                bag.Status = BagStatus.Issued;
                bag.IssueReceipt = issue;
            }
        }

        // Bags that were never issued and have passed their expiry are written off (half of them, the rest stay as an audit trail).
        foreach (var bag in set.BloodBags.Where(b => b.Status == BagStatus.Available && b.ExpiresOn < today))
        {
            if (rng.Chance(0.5))
            {
                bag.Status = BagStatus.Discarded;
            }
        }

        for (var i = 0; i < SeedSizes.PendingRequests * factor; i++)
        {
            set.Requests.Add(NewRequest(rng, rng.Pick(set.Patients), NotAfter(todayStart.AddDays(-rng.Between(0, 10)).AddMinutes(rng.Between(0, 59)), nowUtc), RequestStatus.Pending, today));
        }

        for (var i = 0; i < SeedSizes.CancelledRequests * factor; i++)
        {
            set.Requests.Add(NewRequest(rng, rng.Pick(set.Patients), todayStart.AddDays(-rng.Between(5, pastDays - IssueFreeDays)), RequestStatus.Cancelled));
        }
    }

    private static Request NewRequest(DeterministicRandom rng, Patient patient, DateTime placedAtUtc, RequestStatus status, DateOnly? notBefore = null)
    {
        var required = DateOnly.FromDateTime(placedAtUtc).AddDays(rng.Between(3, 14));
        if (notBefore is { } floor && required < floor)
        {
            required = floor.AddDays(rng.Between(1, 7));
        }

        return new Request
        {
            Patient = patient,
            BloodType = patient.BloodType,
            Units = rng.Between(1, 4),
            PlacedAtUtc = placedAtUtc,
            RequiredBy = required,
            Status = status,
        };
    }

    private static DateTime DateTimeMin(DateTime a, DateTime b) => a <= b ? a : b;

    private static void SetDonorHistory(DemoDataSet set)
    {
        var donations = set.Receipts.Where(r => r.Kind == ReceiptKind.Donation).GroupBy(r => r.Appointment!.Donor!);
        foreach (var group in donations)
        {
            group.Key.LastDonated = DateOnly.FromDateTime(group.Max(r => r.CreatedAtUtc));
            group.Key.Frequency = group.Count() >= 3 ? DonorFrequency.Regular : DonorFrequency.Occasional;
        }
    }

    private static DateTime SlotTime(DateTime firstDay, int slotIndex) =>
        firstDay.AddDays(slotIndex / SlotsPerDay).AddHours(9).AddMinutes(30 * (slotIndex % SlotsPerDay));

    /// <summary>
    /// <paramref name="count"/> distinct clinic slot indexes (day * slots per day + slot) in random order over <paramref name="days"/> days from <paramref name="firstDay"/>, spread over the
    /// calendar months with <see cref="MonthShares"/>: a month never holds more slots than it has, and no two rows share a slot.
    /// </summary>
    private static int[] SlotsOverMonths(int count, DeterministicRandom rng, DateTime firstDay, int days)
    {
        var months = MonthRanges(firstDay, days);
        var perMonth = MonthShares(count, rng, months, capacities: [.. months.Select(m => (m.End - m.Start) * SlotsPerDay)]);
        var slots = new List<int>(count);
        for (var m = 0; m < months.Count; m++)
        {
            var first = months[m].Start * SlotsPerDay;
            slots.AddRange(Shuffled(perMonth[m], rng, modulus: (months[m].End - months[m].Start) * SlotsPerDay).Select(i => first + i));
        }

        // The months were filled in order; shuffle so that the statuses handed out by position are not sorted by date.
        for (var i = slots.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (slots[i], slots[j]) = (slots[j], slots[i]);
        }

        return [.. slots];
    }

    /// <summary><paramref name="count"/> day offsets (0 to days - 1), spread over the calendar months with <see cref="MonthShares"/>; a day may repeat.</summary>
    private static List<int> DaysOverMonths(int count, DeterministicRandom rng, DateTime firstDay, int days)
    {
        var months = MonthRanges(firstDay, days);
        var perMonth = MonthShares(count, rng, months, capacities: null);
        var result = new List<int>(count);
        for (var m = 0; m < months.Count; m++)
        {
            for (var i = 0; i < perMonth[m]; i++)
            {
                result.Add(months[m].Start + rng.Next(months[m].End - months[m].Start));
            }
        }

        return result;
    }

    /// <summary>The calendar months that the days [0, days) from <paramref name="firstDay"/> fall in, each as a range of day offsets.</summary>
    private static List<(int Start, int End)> MonthRanges(DateTime firstDay, int days)
    {
        var months = new List<(int Start, int End)>();
        var start = 0;
        for (var d = 1; d <= days; d++)
        {
            if (d == days || firstDay.AddDays(d).Month != firstDay.AddDays(d - 1).Month)
            {
                months.Add((start, d));
                start = d;
            }
        }

        return months;
    }

    /// <summary>
    /// How many of <paramref name="total"/> rows fall in each month: proportional to the days the month has, times a deterministic factor between 0.65 and 1.35 for that month
    /// (the month-to-month variation) and a gentle rise towards the present (a clinic that is growing), but never fewer than a quarter of an even share, so no month is empty, and never
    /// more than its <paramref name="capacities"/> allow. The shares add up to exactly <paramref name="total"/>.
    /// </summary>
    private static int[] MonthShares(int total, DeterministicRandom rng, List<(int Start, int End)> months, int[]? capacities)
    {
        var weights = new double[months.Count];
        for (var m = 0; m < months.Count; m++)
        {
            var progress = months.Count == 1 ? 1.0 : m / (double)(months.Count - 1);
            weights[m] = (months[m].End - months[m].Start) * (rng.Between(65, 135) / 100.0) * (0.85 + (0.3 * progress));
        }

        var shares = new int[months.Count];
        var floor = total / (months.Count * 4);
        var remaining = total;
        for (var m = 0; m < months.Count; m++)
        {
            shares[m] = Math.Min(floor, capacities?[m] ?? int.MaxValue);
            remaining -= shares[m];
        }

        // Hand out the rest by weight, largest remainder first; a month that reaches its capacity closes and the surplus goes to the others.
        while (remaining > 0)
        {
            var open = Enumerable.Range(0, months.Count).Where(m => capacities is null || shares[m] < capacities[m]).ToList();
            if (open.Count == 0)
            {
                throw new InvalidOperationException("The demo history has no room for that many rows.");
            }

            var weightSum = open.Sum(m => weights[m]);
            var wanted = open.ToDictionary(m => m, m => remaining * weights[m] / weightSum);
            foreach (var m in open)
            {
                var whole = Math.Min((int)wanted[m], (capacities?[m] ?? int.MaxValue) - shares[m]);
                shares[m] += whole;
                remaining -= whole;
            }

            foreach (var m in open.OrderByDescending(m => wanted[m] - Math.Floor(wanted[m])).ThenBy(m => m))
            {
                if (remaining == 0)
                {
                    break;
                }

                if (capacities is null || shares[m] < capacities[m])
                {
                    shares[m]++;
                    remaining--;
                }
            }
        }

        return shares;
    }

    /// <summary>Size of the name grid to draw from: every first name with every last name, or more when more people are needed.</summary>
    private static int NameSpace(int people) => Math.Max(people, SyntheticVocabulary.FirstNames.Count * SyntheticVocabulary.LastNames.Count);

    /// <summary>
    /// <paramref name="count"/> distinct values from [0, modulus) in random order (a partial Fisher-Yates shuffle). Distinct means
    /// no two rows share a name or a clinic slot.
    /// </summary>
    private static int[] Shuffled(int count, DeterministicRandom rng, int? modulus = null)
    {
        var m = modulus ?? count;
        var values = new int[m];
        for (var i = 0; i < m; i++)
        {
            values[i] = i;
        }

        for (var i = 0; i < count; i++)
        {
            var j = i + rng.Next(m - i);
            (values[i], values[j]) = (values[j], values[i]);
        }

        return values[..count];
    }

    private static Gender PickGender(DeterministicRandom rng)
    {
        var roll = rng.Next(100);
        return roll < 48 ? Gender.Male : roll < 96 ? Gender.Female : Gender.Other;
    }

    private static BloodType PickBloodType(DeterministicRandom rng) => PickWeighted(rng, BloodTypeExtensions.All);

    private static BloodType PickWeighted(DeterministicRandom rng, IReadOnlyList<BloodType> among)
    {
        var total = among.Sum(t => TypeWeights[(int)t]);
        var roll = rng.Next(total);
        foreach (var type in among)
        {
            roll -= TypeWeights[(int)type];
            if (roll < 0)
            {
                return type;
            }
        }

        return among[^1];
    }
}
