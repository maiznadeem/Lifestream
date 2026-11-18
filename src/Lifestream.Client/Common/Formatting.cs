using System.Globalization;

namespace Lifestream.Client.Common;

/// <summary>The one place dates are turned into text (SHELL-36): "05 Mar 2022" and "05 Mar 2022 14:30", never ambiguous.</summary>
public static class Formatting
{
    public const string DateFormat = "dd MMM yyyy";
    public const string DateTimeFormat = "dd MMM yyyy HH:mm";

    public static string Date(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string Date(DateOnly? date, string whenMissing = "") => date is { } d ? Date(d) : whenMissing;

    /// <summary>Formats a UTC timestamp in the user's local time zone (or the given one).</summary>
    public static string DateTime(DateTime utc, TimeZoneInfo? zone = null)
    {
        var asUtc = System.DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(asUtc, zone ?? TimeZoneInfo.Local);
        return local.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
    }

    public static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text?.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
