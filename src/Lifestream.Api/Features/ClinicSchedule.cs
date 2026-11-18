using Lifestream.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Features;

/// <summary>Clinic opening hours and slot grid (configuration keys Clinic:Hours, Clinic:SlotMinutes, Clinic:TimeZone). Slots are stored in UTC.</summary>
public sealed class ClinicSchedule(IOptions<ClinicSettings> options)
{
    private readonly ClinicSettings _settings = options.Value;

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(_settings.TimeZone);

    public (TimeOnly Open, TimeOnly Close) Hours
    {
        get
        {
            var parts = _settings.Hours.Split('-', 2);
            return (TimeOnly.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), TimeOnly.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public int SlotMinutes => Math.Max(5, _settings.SlotMinutes);

    /// <summary>Null when the instant is a bookable slot start (on the grid, inside opening hours); otherwise the reason it is not.</summary>
    public string? WhyNotBookable(DateTime slotUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(slotUtc, Zone);
        var (open, close) = Hours;

        // TimeSpan arithmetic on purpose: TimeOnly subtraction wraps around midnight and would accept 08:00 for a 09:00 opening.
        var sinceOpen = local.TimeOfDay - open.ToTimeSpan();
        var end = local.TimeOfDay + TimeSpan.FromMinutes(SlotMinutes);
        if (local.Second != 0 || local.Millisecond != 0 || sinceOpen < TimeSpan.Zero || sinceOpen.TotalMinutes % SlotMinutes != 0)
        {
            return $"must start on a {SlotMinutes}-minute slot between {open:HH:mm} and {close:HH:mm}";
        }

        return end > close.ToTimeSpan() ? $"must end by {close:HH:mm}" : null;
    }

    /// <summary>Every slot start (UTC) on a clinic-local calendar day.</summary>
    public IEnumerable<DateTime> SlotsOn(DateOnly localDate)
    {
        var (open, close) = Hours;
        var zone = Zone;
        for (var t = open.ToTimeSpan(); t + TimeSpan.FromMinutes(SlotMinutes) <= close.ToTimeSpan(); t += TimeSpan.FromMinutes(SlotMinutes))
        {
            var local = DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue) + t, DateTimeKind.Unspecified);
            if (!zone.IsInvalidTime(local))
            {
                yield return TimeZoneInfo.ConvertTimeToUtc(local, zone);
            }
        }
    }
}
