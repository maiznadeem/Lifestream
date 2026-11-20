using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lifestream.Api.Infrastructure;

/// <summary>Classifies database failures: resuming from auto-pause, or paused for the rest of the month.</summary>
public static class DatabaseErrors
{
    /// <summary>The free monthly allowance is used up and the database is paused until the 1st (Azure error 42119).</summary>
    public const int FreeAllowanceExhausted = 42119;

    // 40613 = database not currently available (resuming), 40197/40501/4922x = service busy or reconfiguring,
    // -2 = connect timeout, the rest are dropped connections while the database starts.
    private static readonly int[] WakingList = [40613, 40197, 40501, 49918, 49919, 49920, -2, 10060, 10054, 10053, 64, 233, 20];
    private static readonly HashSet<int> WakingNumbers = [.. WakingList];

    /// <summary>The error numbers the retrying strategy treats as "the database is still starting".</summary>
    public static IReadOnlyList<int> RetryNumbers => WakingList;

    public static bool IsResting(Exception exception) => Find(exception).Any(e => e.Number == FreeAllowanceExhausted);

    public static bool IsWaking(Exception exception) =>
        exception is RetryLimitExceededException || Find(exception).Any(e => WakingNumbers.Contains(e.Number));

    private static IEnumerable<SqlException> Find(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql)
            {
                yield return sql;
            }

            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Find))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>The SQL error number behind an exception, when there is one (for logging, never the message).</summary>
    public static int? NumberOf(Exception exception) => Find(exception).Select(e => (int?)e.Number).FirstOrDefault();
}

/// <summary>
/// Retries transient failures for up to <see cref="DatabaseSettings.WakeBudgetSeconds"/> in total (a paused serverless database
/// takes about a minute to resume) and never waits longer than <see cref="DatabaseSettings.MaxRetryDelaySeconds"/> between two tries;
/// after the budget EF throws and the API answers 503. A database whose free allowance is used up (42119) is not retried at all.
/// </summary>
public sealed class WakeAwareExecutionStrategy(ExecutionStrategyDependencies dependencies, DatabaseSettings settings)
    : SqlServerRetryingExecutionStrategy(dependencies, int.MaxValue, TimeSpan.FromSeconds(settings.MaxRetryDelaySeconds), [.. DatabaseErrors.RetryNumbers])
{
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly TimeSpan _budget = TimeSpan.FromSeconds(settings.WakeBudgetSeconds);

    protected override TimeSpan? GetNextDelay(Exception lastException)
    {
        var elapsed = DateTime.UtcNow - _started;
        if (elapsed >= _budget)
        {
            return null;
        }

        var delay = base.GetNextDelay(lastException);
        if (delay is null)
        {
            return null;
        }

        // EF's own wait after "database not available" (40613) starts at 5 seconds and doubles; MaxRetryDelaySeconds is the real ceiling,
        // so while a database resumes the tries come at that steady interval and the first one that can succeed is at most that late.
        var ceiling = TimeSpan.FromSeconds(settings.MaxRetryDelaySeconds);
        var left = _budget - elapsed;
        var wait = delay.Value < ceiling ? delay.Value : ceiling;
        return wait < left ? wait : left;
    }
}
