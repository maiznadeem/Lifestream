using Lifestream.Contracts.Appointments;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Appointments;

/// <summary>
/// The user's local calendar day, for the places outside the Appointments screen that show "today" (the dashboard's panel, the sidebar count, Ctrl+D).
/// <para>
/// The list's <c>fromUtc</c>/<c>toUtc</c> are whole UTC days on the server (from the start of the "from" day to the end of the "to" day, whatever the time), so
/// they alone return up to two days for anyone whose midnight is not UTC's. The query therefore also names the Today view with the user's UTC offset, which the
/// server cuts to the local day exactly; <c>fromUtc</c>/<c>toUtc</c> stay for a 2.0 server, which does not know the view, and <see cref="Holds"/> drops what such
/// a server returns outside the day.
/// </para>
/// </summary>
public readonly record struct LocalDay(DateTime StartUtc, DateTime EndUtc, int UtcOffsetMinutes)
{
    /// <summary>The local day that holds the clock's "now".</summary>
    public static LocalDay Of(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var local = clock.GetLocalNow();
        var start = new DateTimeOffset(local.Date, local.Offset).UtcDateTime;
        var end = new DateTimeOffset(local.Date.AddDays(1), local.Offset).UtcDateTime;
        return new LocalDay(start, end, (int)local.Offset.TotalMinutes);
    }

    /// <summary>The appointments of this day (cancelled ones included unless <paramref name="status"/> says otherwise).</summary>
    public AppointmentQuery Query(AppointmentStatus? status = null, int pageSize = 50, string? sort = null) =>
        new(Status: status, FromUtc: StartUtc, ToUtc: EndUtc, PageSize: pageSize, Sort: sort) { View = AppointmentView.Today, UtcOffsetMinutes = UtcOffsetMinutes };

    /// <summary>True when the appointment's slot starts inside this day.</summary>
    public bool Holds(AppointmentListItem appointment)
    {
        ArgumentNullException.ThrowIfNull(appointment);
        return appointment.SlotStartUtc >= StartUtc && appointment.SlotStartUtc < EndUtc;
    }
}
