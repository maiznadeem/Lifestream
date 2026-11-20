using Lifestream.Data.Entities;
using Lifestream.Domain;

namespace Lifestream.Data.Seed;

/// <summary>Row counts at scale factor 1. The 100x dataset is exactly 100 times these.</summary>
public static class SeedSizes
{
    public const int Staff = 11;
    // Enough donors for a year of the demo's donations at the rule's pace (Eligibility.MinDaysBetweenDonations): with 40, each would have had to give
    // blood more often than the rule allows, or the history would have had to be twice as long and half as busy.
    public const int Donors = 100;
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
/// <para>
/// Nobody breaks the rule the app enforces (<see cref="Eligibility.MinDaysBetweenDonations"/>): a donor's donations are at least that many days apart, and
/// every appointment, whatever became of it, is on a day the donor was (or will be) eligible, judged as the API judges a booking. See <see cref="DonationSlots"/>
/// and <see cref="AssignDonors"/>.
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

    /// <summary>The reason an appointment carries when its donor was not eligible on the day. Nothing the generator plans needs it (see <see cref="AssignDonors"/>).</summary>
    private const string EarlyDonationReason = "Physician approved an early donation.";

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
        BuildPeople(set, rng, today, factor, pastDays, futureDays);
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

    private static void BuildPeople(DemoDataSet set, DeterministicRandom rng, DateOnly today, int factor, int pastDays, int futureDays)
    {
        var donors = SeedSizes.Donors * factor;
        var donorOrder = Shuffled(donors, rng, NameSpace(donors));
        // A donor is within the age limits on every day of the dataset: old enough on the first day of the history, and not too old on the day of the
        // latest booking (two bookings ahead at most, each the rule's gap and the booking window after the one before: see BookAhead).
        var youngest = (Eligibility.MinAgeYears * 365) + 30 + pastDays;
        var oldest = Math.Min((Eligibility.MaxAgeYears - 1) * 365, (Eligibility.MaxAgeYears * 365) - (2 * (Eligibility.MinDaysBetweenDonations + futureDays)) - 2);
        for (var i = 0; i < donors; i++)
        {
            set.Donors.Add(new Donor
            {
                Name = SyntheticVocabulary.PersonName(donorOrder[i]),
                DateOfBirth = today.AddDays(-rng.Between(youngest, oldest)),
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
    /// Creates every appointment with a unique slot and returns the completed ones (each gets a donation receipt), oldest first. The history ends two days ago;
    /// yesterday, today and tomorrow are the full clinic days of <see cref="AroundToday"/>; the open bookings start the day after tomorrow. The rows of those
    /// three days come out of the same totals, so the dataset holds exactly <see cref="SeedSizes"/> times the factor of every status at any time of day.
    /// </summary>
    private static List<Appointment> BuildAppointments(DemoDataSet set, DeterministicRandom rng, DeterministicRandom aroundRng, DateTime nowUtc, int factor, int pastDays, int futureDays)
    {
        var todayStart = nowUtc.Date;
        var donors = DonorRoles.Assign(set.Donors, aroundRng);
        var around = AroundToday(aroundRng, nowUtc, donors);

        var completedInHistory = (SeedSizes.CompletedAppointments * factor) - around.Count(a => a.Status == AppointmentStatus.Completed);
        var noShowsInHistory = (SeedSizes.NoShowAppointments * factor) - around.Count(a => a.Status == AppointmentStatus.NoShow);
        var cancelled = SeedSizes.CancelledAppointments * factor;
        var historyStart = todayStart.AddDays(-pastDays);

        // The donations of the history: when, then who.
        var donationSlots = DonationSlots(completedInHistory, rng, historyStart, pastDays, donors);
        var completed = donationSlots.ConvertAll(slot => NewPastAppointment(rng, SlotTime(historyStart, slot), AppointmentStatus.Completed));
        AssignDonors(completed, rng, donors, todayStart);
        set.Appointments.AddRange(completed);
        completed.AddRange(around.Where(a => a.Status == AppointmentStatus.Completed));
        var donationDays = completed.GroupBy(a => a.Donor!).ToDictionary(g => g.Key, g => g.Select(a => a.SlotStartUtc.Date).Order().ToArray());

        // The appointments that were cancelled or missed, in the slots the donations left free: each was booked by a donor who was eligible on its day.
        var otherSlots = SlotsOverMonths(cancelled + noShowsInHistory, rng, historyStart, pastDays - 1, [.. donationSlots]);
        for (var i = 0; i < otherSlots.Length; i++)
        {
            var appointment = NewPastAppointment(rng, SlotTime(historyStart, otherSlots[i]), i < cancelled ? AppointmentStatus.Cancelled : AppointmentStatus.NoShow);
            appointment.Donor = DonorEligibleOn(appointment.SlotStartUtc.Date, rng, set.Donors, donors, donationDays);
            set.Appointments.Add(appointment);
        }

        set.Appointments.AddRange(around);

        BookAhead(set, rng, nowUtc, (SeedSizes.PendingAppointments * factor) - around.Count(a => a.Status == AppointmentStatus.Pending), futureDays, donationDays, around);

        foreach (var appointment in set.Appointments)
        {
            appointment.DonorBloodType = appointment.Donor!.BloodType;
        }

        return completed;
    }

    /// <summary>
    /// The open bookings after tomorrow, <paramref name="count"/> of them. A booking is for a day on which the donor is eligible, as the API judges a booking, and
    /// it keeps the rule's distance from the donor's other open booking too, so recording one donation never makes another booking of that donor a day the rule forbids.
    /// <para>
    /// Nobody books twice while somebody has not booked (an appointment still to come today or tomorrow is a booking). The donors who can come soonest book,
    /// three in every four of them, so some donors who are eligible now have no appointment yet: from the day after tomorrow for a donor who is eligible now,
    /// otherwise from the day the rule allows, in both cases within the booking window after it. Should there be more bookings than donors, the rest are
    /// second ones, <see cref="Eligibility.MinDaysBetweenDonations"/> days or more after the donor's first.
    /// </para>
    /// </summary>
    private static void BookAhead(DemoDataSet set, DeterministicRandom rng, DateTime nowUtc, int count, int futureDays, Dictionary<Donor, DateTime[]> donationDays, List<Appointment> around)
    {
        var gap = Eligibility.MinDaysBetweenDonations;
        var todayStart = nowUtc.Date;
        var firstOpenDay = todayStart.AddDays(2);
        var taken = new HashSet<DateTime>();

        // Each donor's latest day that a new booking has to keep its distance from: the last donation, or the booking still open.
        var latest = donationDays.ToDictionary(d => d.Key, d => d.Value[^1]);
        var booked = new HashSet<Donor>();
        foreach (var appointment in around.Where(a => a.Status == AppointmentStatus.Pending))
        {
            latest[appointment.Donor!] = appointment.SlotStartUtc.Date;
            booked.Add(appointment.Donor!);
        }

        // Whoever can come soonest first (a stable sort: donors who can come on the same day stay in their drawn order), every fourth passed over.
        var everybody = Shuffled(set.Donors.Count, rng).Select(i => set.Donors[i]).ToList();
        var round = everybody.Where(d => !booked.Contains(d)).OrderBy(d => latest.GetValueOrDefault(d)).Where((_, i) => i % 4 != 3).ToList();
        while (count > 0)
        {
            foreach (var donor in round.Take(count))
            {
                var from = latest.TryGetValue(donor, out var day) && day.AddDays(gap) > firstOpenDay ? day.AddDays(gap) : firstOpenDay;
                var slot = FreeSlot(from.AddDays(rng.Next(futureDays)), rng, taken);
                latest[donor] = slot.Date;
                set.Appointments.Add(new Appointment
                {
                    Donor = donor,
                    SlotStartUtc = slot,
                    PlacedAtUtc = NotAfter(DuringTheWorkingDay(todayStart.AddDays(-rng.Between(0, 6)), rng.Between(0, WorkingMinutes - 1)), nowUtc),
                    Status = AppointmentStatus.Pending,
                });
            }

            count -= Math.Min(count, round.Count);
            // The next round: everybody, whoever comes soonest first.
            round = [.. everybody.OrderBy(d => latest.GetValueOrDefault(d))];
        }
    }

    /// <summary>An appointment of the history, booked one to fourteen days before its slot. The donor is set by the caller.</summary>
    private static Appointment NewPastAppointment(DeterministicRandom rng, DateTime slot, AppointmentStatus status) => new()
    {
        SlotStartUtc = slot,
        PlacedAtUtc = slot.AddDays(-rng.Between(1, 14)).AddMinutes(rng.Between(-120, 120)),
        Status = status,
    };

    /// <summary>
    /// The slots of the history's donations, as slot indexes from <paramref name="historyStart"/>, oldest first. They are placed so that there is always a donor
    /// who may give blood: in any <see cref="Eligibility.MinDaysBetweenDonations"/> consecutive days there are fewer donations than donors who can donate.
    /// <para>
    /// Each calendar month holds at most its share of one donation per donor and gap, and a month's donations are spread over its days (one in each equal
    /// part of the month), so no run of days is busier than its months. The months that reach into the last days before yesterday (as many days as the
    /// rule's gap, less one) are tighter: whoever comes in yesterday, today or tomorrow gives no blood in those days, so the limit there counts only the
    /// other donors. Within those limits the months vary as every other row of the history does (<see cref="MonthShares"/>).
    /// </para>
    /// </summary>
    private static List<int> DonationSlots(int count, DeterministicRandom rng, DateTime historyStart, int pastDays, DonorRoles donors)
    {
        var gap = Eligibility.MinDaysBetweenDonations;
        var firstRecentDay = pastDays - gap;
        // Donations a day that the donors can keep up for ever, with two to spare for the ends of a run of days that cuts two months.
        var perDay = (donors.Donating - 2) / (double)gap;
        var perRecentDay = Math.Max(0, Math.Min(perDay, (donors.Donating - donors.AroundToday - 2) / (double)(gap - 1)));

        var months = MonthRanges(historyStart, pastDays - 1);
        var perMonth = MonthShares(count, rng, months, capacities: [.. months.Select(m =>
            Math.Min((m.End - m.Start) * SlotsPerDay, (int)((m.End > firstRecentDay ? perRecentDay : perDay) * (m.End - m.Start))))]);
        var slots = new List<int>(count);
        for (var m = 0; m < months.Count; m++)
        {
            slots.AddRange(SpreadEvenly(perMonth[m], rng, (months[m].End - months[m].Start) * SlotsPerDay).Select(i => (months[m].Start * SlotsPerDay) + i));
        }

        return slots;
    }

    /// <summary><paramref name="count"/> values from [0, range), ascending: the range is cut into that many equal parts and one value is drawn from each.</summary>
    private static IEnumerable<int> SpreadEvenly(int count, DeterministicRandom rng, int range)
    {
        for (var i = 0; i < count; i++)
        {
            var from = (int)((long)i * range / count);
            var to = (int)((long)(i + 1) * range / count);
            yield return from + rng.Next(to - from);
        }
    }

    /// <summary>
    /// Gives each donation of the history (oldest first in <paramref name="donations"/>) its donor, working back from the newest: a donor is free for a
    /// donation when their next one (or their appointment of yesterday, today or tomorrow) is <see cref="Eligibility.MinDaysBetweenDonations"/> days or more
    /// after it, and one of the free donors is drawn. So no donor gives blood twice within the gap, and whoever comes in around today is eligible on that
    /// day. Donors who come for the first time are never drawn.
    /// <para>
    /// <see cref="DonationSlots"/> leaves a free donor for every donation. Should a change of the sizes break that, the donor who has waited longest gives
    /// blood early and the appointment carries the reason, as the API would have recorded it; the dataset stays whole.
    /// </para>
    /// </summary>
    private static void AssignDonors(List<Appointment> donations, DeterministicRandom rng, DonorRoles donors, DateTime todayStart)
    {
        var gap = Eligibility.MinDaysBetweenDonations;
        var free = new List<Donor>(donors.Others);
        // Donors with a donation ahead of them, the one furthest ahead first: they become free again in that order.
        var waiting = new Queue<(Donor Donor, DateTime Next)>();
        foreach (var (group, day) in new[] { (donors.Tomorrow, 1), (donors.Today, 0), (donors.Yesterday, -1) })
        {
            foreach (var donor in group)
            {
                waiting.Enqueue((donor, todayStart.AddDays(day)));
            }
        }

        for (var i = donations.Count - 1; i >= 0; i--)
        {
            var day = donations[i].SlotStartUtc.Date;
            while (waiting.Count > 0 && waiting.Peek().Next >= day.AddDays(gap))
            {
                free.Add(waiting.Dequeue().Donor);
            }

            Donor chosen;
            if (free.Count > 0)
            {
                var pick = rng.Next(free.Count);
                chosen = free[pick];
                free[pick] = free[^1];
                free.RemoveAt(free.Count - 1);
            }
            else
            {
                chosen = waiting.Dequeue().Donor;
                donations[i].OverrideReason = EarlyDonationReason;
            }

            donations[i].Donor = chosen;
            waiting.Enqueue((chosen, day));
        }
    }

    /// <summary>
    /// A donor who was eligible on <paramref name="day"/> by the rule the API applies to a booking: no donation in the <see cref="Eligibility.MinDaysBetweenDonations"/>
    /// days up to it. Drawn at random; a donor who has never donated is always one.
    /// </summary>
    private static Donor DonorEligibleOn(DateTime day, DeterministicRandom rng, List<Donor> all, DonorRoles donors, Dictionary<Donor, DateTime[]> donationDays)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var donor = rng.Pick(all);
            if (!donationDays.TryGetValue(donor, out var days))
            {
                return donor;
            }

            // The donor's last donation on or before the day.
            var after = Array.BinarySearch(days, day);
            after = after >= 0 ? after + 1 : ~after;
            if (after == 0 || days[after - 1].AddDays(Eligibility.MinDaysBetweenDonations) <= day)
            {
                return donor;
            }
        }

        return rng.Pick(donors.FirstTime);
    }

    /// <summary>A free slot on <paramref name="day"/>, or the next free one after it when the day is full; the slot is then <paramref name="taken"/>.</summary>
    private static DateTime FreeSlot(DateTime day, DeterministicRandom rng, HashSet<DateTime> taken)
    {
        var index = rng.Next(SlotsPerDay);
        while (!taken.Add(SlotTime(day, index)))
        {
            index++;
        }

        return SlotTime(day, index);
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
    /// Nobody comes twice in the three days.
    /// </para>
    /// </summary>
    private static List<Appointment> AroundToday(DeterministicRandom rng, DateTime nowUtc, DonorRoles donors)
    {
        var appointments = new List<Appointment>(3 * AppointmentsPerClinicDay);
        var todayStart = nowUtc.Date;
        for (var day = -1; day <= 1; day++)
        {
            // One of the day's appointments (never the first, never one of the last two) is a no-show once its time has passed.
            var noShow = rng.Between(1, AppointmentsPerClinicDay - 3);
            for (var hour = 0; hour < AppointmentsPerClinicDay; hour++)
            {
                var slot = todayStart.AddDays(day).AddHours(OpeningHourUtc + hour).AddMinutes(30 * rng.Next(2));
                var isOver = slot.AddMinutes(DonationMinutes) <= nowUtc;
                appointments.Add(new Appointment
                {
                    Donor = day < 0 ? donors.Yesterday[hour]
                        : hour == AppointmentsPerClinicDay - 1 ? donors.FirstTime[day]
                        : day == 0 ? donors.Today[hour] : donors.Tomorrow[hour],
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
    /// Who comes in around today. <see cref="FirstTime"/> (the last appointment of today and of tomorrow) have never donated; <see cref="Today"/> and
    /// <see cref="Tomorrow"/> are those days' other donors and <see cref="Yesterday"/> that day's eight; <see cref="Others"/> is everybody else. The first
    /// eight donors (one per blood group) are never first-time donors or today's or tomorrow's, so every group keeps a donor with a history.
    /// </summary>
    private sealed record DonorRoles(List<Donor> FirstTime, List<Donor> Today, List<Donor> Tomorrow, List<Donor> Yesterday, List<Donor> Others)
    {
        /// <summary>The donors who give blood at all: everybody but the first-time donors.</summary>
        public int Donating => Today.Count + Tomorrow.Count + Yesterday.Count + Others.Count;

        /// <summary>The donors with an appointment yesterday, today or tomorrow who have donated before.</summary>
        public int AroundToday => Today.Count + Tomorrow.Count + Yesterday.Count;

        public static DonorRoles Assign(List<Donor> all, DeterministicRandom rng)
        {
            var groups = BloodTypeExtensions.All.Count;
            var perDay = AppointmentsPerClinicDay - 1;
            var order = Shuffled(FirstTimeDonors + (2 * perDay), rng, all.Count - groups).Select(i => all[groups + i]).ToList();
            var firstTime = order[..FirstTimeDonors];
            var today = order[FirstTimeDonors..(FirstTimeDonors + perDay)];
            var tomorrow = order[(FirstTimeDonors + perDay)..];
            var rest = all.Except(order).ToList();
            var yesterday = Shuffled(AppointmentsPerClinicDay, rng, rest.Count).Select(i => rest[i]).ToList();
            return new DonorRoles(firstTime, today, tomorrow, yesterday, [.. rest.Except(yesterday)]);
        }
    }

    /// <summary>The clinic's working day in minutes (09:00 to 17:00 UTC): bookings and requests are made in it, not in the night.</summary>
    private const int WorkingMinutes = SlotsPerDay * 30;

    private static DateTime DuringTheWorkingDay(DateTime day, int minute) => day.AddHours(OpeningHourUtc).AddMinutes(minute);

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

        // The stock as it stands at a moment: every bag collected by then, less the ones that have expired.
        List<BloodType> InStockAt(DateTime moment)
        {
            while (next < bags.Count && bags[next].CollectedAtUtc <= moment)
            {
                queues[bags[next].BloodType].Enqueue(bags[next]);
                next++;
            }

            var date = DateOnly.FromDateTime(moment);
            foreach (var queue in queues.Values)
            {
                while (queue.Count > 0 && queue.Peek().ExpiresOn < date)
                {
                    queue.Dequeue();
                }
            }

            return [.. queues.Where(q => q.Value.Count > 0).Select(q => q.Key)];
        }

        var lastIssue = DateTime.MinValue;
        foreach (var planned in issueDays)
        {
            // Issues stay in time order (one that had to wait pushes the ones planned before its new time back to it).
            var issuedAt = planned < lastIssue ? lastIssue : planned;
            var candidates = InStockAt(issuedAt);
            if (candidates.Count == 0 && next < bags.Count && bags[next].CollectedAtUtc.AddMinutes(30) <= nowUtc)
            {
                // No stock at all at that moment (it happens on a few dates a year): the request is fulfilled half an hour after the next donation comes in,
                // as a clinic would do it. Without this the row would stay a pending request, and the dataset would hold a receipt fewer than it says.
                issuedAt = bags[next].CollectedAtUtc.AddMinutes(30);
                candidates = InStockAt(issuedAt);
            }

            if (candidates.Count == 0)
            {
                // No stock and no donation left to wait for: the request stays Pending and never reaches the receipt count.
                set.Requests.Add(NewRequest(rng, rng.Pick(set.Patients), issuedAt.AddDays(-1), RequestStatus.Pending));
                continue;
            }

            lastIssue = issuedAt;

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
            set.Requests.Add(NewRequest(rng, rng.Pick(set.Patients), NotAfter(DuringTheWorkingDay(todayStart.AddDays(-rng.Between(0, 10)), rng.Between(0, WorkingMinutes - 1)), nowUtc), RequestStatus.Pending, today));
        }

        for (var i = 0; i < SeedSizes.CancelledRequests * factor; i++)
        {
            set.Requests.Add(NewRequest(rng, rng.Pick(set.Patients), DuringTheWorkingDay(todayStart.AddDays(-rng.Between(5, pastDays - IssueFreeDays)), i * 37 % WorkingMinutes), RequestStatus.Cancelled));
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
    /// calendar months with <see cref="MonthShares"/>, none of them one of the <paramref name="taken"/> slots: a month never holds more slots than it has free, and no two rows share a slot.
    /// </summary>
    private static int[] SlotsOverMonths(int count, DeterministicRandom rng, DateTime firstDay, int days, HashSet<int> taken)
    {
        var months = MonthRanges(firstDay, days);
        var free = months.ConvertAll(m => Enumerable.Range(m.Start * SlotsPerDay, (m.End - m.Start) * SlotsPerDay).Where(slot => !taken.Contains(slot)).ToArray());
        var perMonth = MonthShares(count, rng, months, capacities: [.. free.Select(f => f.Length)]);
        var slots = new List<int>(count);
        for (var m = 0; m < months.Count; m++)
        {
            slots.AddRange(Shuffled(perMonth[m], rng, modulus: free[m].Length).Select(i => free[m][i]));
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
