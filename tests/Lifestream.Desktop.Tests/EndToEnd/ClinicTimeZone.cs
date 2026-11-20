using System.Reflection;

namespace Lifestream.Desktop.Tests.EndToEnd;

/// <summary>
/// Makes this process show times as a PC in the demo clinic's own time zone would. The demo clinic is in UTC (the API's <c>Clinic:TimeZone</c>, open 09:00 to 17:00);
/// the app shows every time in the PC's zone. Screenshots taken on a PC five hours east would show a clinic that works from 14:00 to 21:30, and would carry that PC's
/// zone in every time on them. Windows has no per-process time zone, so the runtime's cached local zone is replaced for the life of this scope; when the runtime's
/// internals are not what this expects, it throws rather than fall back to the PC's zone.
/// </summary>
internal sealed class ClinicTimeZone : IDisposable
{
    private ClinicTimeZone()
    {
    }

    /// <summary>The zone of the demo clinic (appsettings.json, Clinic:TimeZone).</summary>
    public static TimeZoneInfo Zone => TimeZoneInfo.Utc;

    public static ClinicTimeZone Use()
    {
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        // ClearCachedData puts a new cache object in place, so the cache is read after it.
        TimeZoneInfo.ClearCachedData();
        var cache = typeof(TimeZoneInfo).GetField("s_cachedData", Hidden)?.GetValue(null)
            ?? throw new InvalidOperationException("TimeZoneInfo has no s_cachedData field in this runtime: the capture cannot show the clinic's time zone.");
        var local = cache.GetType().GetField("_localTimeZone", Hidden)
            ?? throw new InvalidOperationException("TimeZoneInfo's cache has no _localTimeZone field in this runtime: the capture cannot show the clinic's time zone.");
        local.SetValue(cache, Zone);

        // DateTime.Now does not go through TimeZoneInfo.Local on Windows: it keeps this year's offset and rule, read from the operating system, in a field of its own.
        if (cache.GetType().GetField("_oneYearLocalFromUtc", Hidden) is { } thisYear)
        {
            thisYear.SetValue(cache, Activator.CreateInstance(thisYear.FieldType, DateTime.UtcNow.Year, Zone.BaseUtcOffset, null));
        }

        // Every way the app reads the local time must agree, or the images would mix two zones.
        var noon = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        if (!TimeZoneInfo.Local.Equals(Zone) || noon.ToLocalTime().Hour != 12 || DateTimeOffset.Now.Offset != TimeSpan.Zero
            || TimeProvider.System.GetLocalNow().Offset != TimeSpan.Zero || DateTime.Now.Hour != DateTime.UtcNow.Hour)
        {
            TimeZoneInfo.ClearCachedData();
            throw new InvalidOperationException("The local time zone of this process could not be set to the clinic's.");
        }

        return new ClinicTimeZone();
    }

    /// <summary>Back to the PC's own zone.</summary>
    public void Dispose() => TimeZoneInfo.ClearCachedData();
}
