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
        var time = TimeOnly.FromDateTime(local);
        var minutesFromOpen = (time - open).TotalMinutes;
        if (local.Second != 0 || local.Millisecond != 0 || minutesFromOpen < 0 || minutesFromOpen % SlotMinutes != 0)
        {
            return $"must start on a {SlotMinutes}-minute slot between {open:HH:mm} and {close:HH:mm}";
        }

        return time.AddMinutes(SlotMinutes) > close && time.AddMinutes(SlotMinutes) != TimeOnly.MinValue ? $"must end by {close:HH:mm}" : null;
    }

    /// <summary>Every slot start (UTC) on a clinic-local calendar day.</summary>
    public IEnumerable<DateTime> SlotsOn(DateOnly localDate)
    {
        var (open, close) = Hours;
        var zone = Zone;
        for (var t = open; t.AddMinutes(SlotMinutes) <= close && t >= open; t = t.AddMinutes(SlotMinutes))
        {
            var local = localDate.ToDateTime(t, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
            {
                continue;
            }

            yield return TimeZoneInfo.ConvertTimeToUtc(local, zone);
            if (t.AddMinutes(SlotMinutes) <= t)
            {
                yield break;
            }
        }
    }
}
