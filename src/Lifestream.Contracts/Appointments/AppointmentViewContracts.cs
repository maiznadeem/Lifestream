namespace Lifestream.Contracts.Appointments;

/// <summary>
/// The Appointments screen's saved views (<c>?view=</c> on the list, with <c>utcOffsetMinutes</c> naming the caller's local calendar). Today is every appointment of the
/// local day that is not cancelled; Upcoming the pending ones after today; Past the ones before today that are not cancelled; Cancelled every cancelled one.
/// </summary>
public enum AppointmentView
{
    Today,
    Upcoming,
    Past,
    Cancelled,
}

/// <summary>Result of GET appointments/views: how many appointments each view holds, with the same search and filters as the list.</summary>
public sealed record AppointmentViewCounts(int Today, int Upcoming, int Past, int Cancelled);
