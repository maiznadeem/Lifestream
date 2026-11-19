using System.Globalization;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts.Appointments;

namespace Lifestream.Client.Features.Appointments;

/// <summary>
/// Reads and writes the time of an appointment with exact formats, never a guess (FEAT-12). "12:00 AM" is 00:00, "12:00 PM" is 12:00 and
/// "1:00 PM" is 13:00; a 24-hour "13:00" works too. Anything else is refused.
/// </summary>
public static class SlotTime
{
    private static readonly string[] Formats = ["h:mm tt", "hh:mm tt", "H:mm", "HH:mm"];

    /// <summary>The time shown on a slot: 24-hour, "13:00", as in the app's date and time format.</summary>
    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out TimeOnly time)
    {
        var trimmed = text?.Trim();
        if (!TimeOnly.TryParseExact(trimmed, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
        {
            return false;
        }

        // A 12-hour time has an hour from 1 to 12: "0:00 PM" and "13:00 PM" are refused, not guessed.
        if (trimmed!.EndsWith("M", StringComparison.OrdinalIgnoreCase))
        {
            var hour = int.Parse(trimmed[..trimmed.IndexOf(':', StringComparison.Ordinal)], CultureInfo.InvariantCulture);
            if (hour is < 1 or > 12)
            {
                time = default;
                return false;
            }
        }

        return true;
    }
}

/// <summary>One time of the slot picker: shown in the user's local time, and disabled when it is taken or already past (FEAT-11).</summary>
public sealed record SlotChoice(DateTime StartUtc, TimeOnly Local, bool IsTaken, bool IsPast)
{
    public string Display => SlotTime.Format(Local);

    public bool IsAvailable => !IsTaken && !IsPast;

    /// <summary>"13:00", "13:00 (taken)" or "13:00 (past)": the state is in words, not only in a colour.</summary>
    public string Label => IsTaken ? $"{Display} (taken)" : IsPast ? $"{Display} (past)" : Display;

    public static SlotChoice From(SlotInfo slot, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var utc = DateTime.SpecifyKind(slot.StartUtc, DateTimeKind.Utc);
        return new SlotChoice(utc, RecordFormatting.LocalTime(utc, zone), slot.IsTaken, utc <= nowUtc);
    }
}
