using Lifestream.Contracts.Appointments;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Dashboard;

/// <summary>
/// Which of today's appointments is "next": the one the dashboard marks "Next" and the one "Record donation" opens. One rule for both, so they
/// can never name two different people.
/// </summary>
public static class NextAppointment
{
    /// <summary>How long after its time a pending appointment is still the next one (the donor may be late); after that the following one is.</summary>
    public static TimeSpan Grace { get; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The earliest pending appointment that did not start more than <see cref="Grace"/> ago; when every pending one is older than that, the
    /// earliest of them (someone still has to be seen); null when nobody is pending.
    /// </summary>
    /// <param name="today">Today's appointments, in any order and with any status.</param>
    /// <param name="utcNow">The time now.</param>
    public static AppointmentListItem? Of(IEnumerable<AppointmentListItem> today, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(today);
        var pending = today.Where(a => a.Status == AppointmentStatus.Pending).OrderBy(a => a.SlotStartUtc).ToList();
        var earliest = utcNow - Grace;
        return pending.FirstOrDefault(a => a.SlotStartUtc >= earliest) ?? pending.FirstOrDefault();
    }
}
