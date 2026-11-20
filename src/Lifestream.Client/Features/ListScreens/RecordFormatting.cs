using Lifestream.Client.Common;
using Lifestream.Domain;

namespace Lifestream.Client.Features.ListScreens;

/// <summary>Text shared by the appointments, requests and receipts screens (FEAT-14, FEAT-17): names first, ids as secondary text, times only when present.</summary>
public static class RecordFormatting
{
    /// <summary>
    /// A UTC instant in local time: "05 Mar 2022 14:30", or only "05 Mar 2022" when the local time of day is exactly midnight (a date with
    /// no time, which would otherwise read as a made-up "00:00"). A missing value is empty.
    /// </summary>
    public static string When(DateTime? utc, TimeZoneInfo? zone = null)
    {
        if (utc is not { } value)
        {
            return string.Empty;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local);
        return local.TimeOfDay == TimeSpan.Zero ? Formatting.Date(DateOnly.FromDateTime(local)) : Formatting.DateTime(value, zone);
    }

    /// <summary>"Thu 19 Nov at 11:00": a UTC instant as a day and a time of the local calendar, for a sentence (a confirmation's body).</summary>
    public static string DayAndTime(DateTime utc, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local);
        return local.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture) + " at " + local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>"Name (D-0175)": a name with its record code (<see cref="Receipts.RecordCodes"/>) in one line, for the detail pane.</summary>
    public static string NameWithCode(string name, string code) => $"{name} ({code})";

    /// <summary>The local calendar date of a UTC instant.</summary>
    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo? zone = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local));

    /// <summary>The local time of day of a UTC instant.</summary>
    public static TimeOnly LocalTime(DateTime utc, TimeZoneInfo? zone = null) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local));

    public static string Units(int units) => units == 1 ? "1 unit" : $"{units} units";

    public static string Kind(ReceiptKind kind) => kind == ReceiptKind.Donation ? "Donation" : "Issue";
}
