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
    private static readonly HashSet<int> WakingNumbers = [40613, 40197, 40501, 49918, 49919, 49920, -2, 10060, 10054, 10053, 64, 233, 20];

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
/// takes up to about a minute to resume) with a capped exponential back-off; after that EF throws and the API answers 503.
/// </summary>
public sealed class WakeAwareExecutionStrategy(ExecutionStrategyDependencies dependencies, DatabaseSettings settings)
    : SqlServerRetryingExecutionStrategy(dependencies, int.MaxValue, TimeSpan.FromSeconds(settings.MaxRetryDelaySeconds), [40613, 40197, 40501, 49918, 49919, 49920])
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

        var left = _budget - elapsed;
        return delay.Value < left ? delay : left;
    }
}
