using System.Linq.Expressions;

namespace Lifestream.Data.Queries;

/// <summary>
/// Date-range filtering for every range query (dashboard counts, chart data, receipts, requests, appointments).
/// A range is half-open: <c>&gt;= start of the first day</c> and <c>&lt; start of the day after the last day</c>, so a row
/// at 23:59 on the end date is inside it. Never use <c>BETWEEN</c> or <c>&lt;= endDate</c> on a datetime column.
/// </summary>
public static class DateRanges
{
    public static (DateTime StartUtc, DateTime EndExclusiveUtc) HalfOpen(DateOnly firstDay, DateOnly lastDayInclusive) =>
        (firstDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), lastDayInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    /// <summary>Keeps rows whose timestamp is on or after <paramref name="firstDay"/> and before the day after <paramref name="lastDayInclusive"/>.</summary>
    public static IQueryable<T> InDayRange<T>(this IQueryable<T> source, Expression<Func<T, DateTime>> timestamp, DateOnly firstDay, DateOnly lastDayInclusive)
    {
        var (start, end) = HalfOpen(firstDay, lastDayInclusive);
        return source.InRange(timestamp, start, end);
    }

    /// <summary>Same, for timestamps the caller already has (for example from <c>FromUtc</c>/<c>ToUtc</c> query parameters): <c>&gt;= startUtc</c> and <c>&lt; endExclusiveUtc</c>.</summary>
    public static IQueryable<T> InRange<T>(this IQueryable<T> source, Expression<Func<T, DateTime>> timestamp, DateTime startUtc, DateTime endExclusiveUtc)
    {
        var parameter = timestamp.Parameters[0];
        var body = Expression.AndAlso(
            Expression.GreaterThanOrEqual(timestamp.Body, Bound(startUtc)),
            Expression.LessThan(timestamp.Body, Bound(endExclusiveUtc)));
        return source.Where(Expression.Lambda<Func<T, bool>>(body, parameter));
    }

    // A property read on a captured object, so EF sends the value as a parameter (one cached plan for every range).
    private static MemberExpression Bound(DateTime value) => Expression.Property(Expression.Constant(new Box(value)), nameof(Box.Value));

    private sealed record Box(DateTime Value);
}
