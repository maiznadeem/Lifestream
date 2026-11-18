using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lifestream.Data;

/// <summary>
/// Every timestamp column is UTC (DB-09). Writing a <see cref="DateTime"/> whose Kind is Local or Unspecified throws, so a
/// client's local clock can never be stored as if it were UTC; values read back have Kind Utc.
/// </summary>
public sealed class UtcDateTimeConverter()
    : ValueConverter<DateTime, DateTime>(v => RequireUtc(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
{
    /// <summary>Returns the value when its Kind is Utc; otherwise throws <see cref="ArgumentException"/>.</summary>
    public static DateTime RequireUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : throw new ArgumentException(
                $"A DateTime written to the database must have Kind=Utc, but this one is {value.Kind} ({value:O}). Convert it with ToUniversalTime() or build it as UTC.",
                nameof(value));
}

/// <summary>The nullable counterpart of <see cref="UtcDateTimeConverter"/>.</summary>
public sealed class NullableUtcDateTimeConverter()
    : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? UtcDateTimeConverter.RequireUtc(v.Value) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
