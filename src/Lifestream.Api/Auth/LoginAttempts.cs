using Lifestream.Api.Infrastructure;

namespace Lifestream.Api.Auth;

/// <summary>
/// Failure counting and lockout for usernames that have no staff row (or belong to the demo account), so a locked real account and
/// an unknown name behave the same: both count, both lock after the same number of wrong guesses, both answer 429 with the
/// same Retry-After. Held in memory; there is one replica, and the counter of a name that does not exist is not worth a table.
/// </summary>
public sealed class UnknownLoginTracker(TimeProvider time)
{
    private const int MaxEntries = 50_000;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public static string KeyOf(string username) => username.Trim().ToLowerInvariant();

    /// <summary>Counts one guess atomically. Returns how long the name is locked for, or null when the guess may be checked.</summary>
    public TimeSpan? Reserve(string username, int maxAttempts, int lockoutMinutes)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            var key = KeyOf(username);
            if (!_entries.TryGetValue(key, out var entry))
            {
                MakeRoom(now);
                _entries[key] = entry = new Entry();
            }

            entry.LastSeenUtc = now;
            if (entry.LockoutEndUtc is { } until && until > now)
            {
                return until - now;
            }

            entry.Failures++;
            if (entry.Failures >= maxAttempts)
            {
                entry.LockoutEndUtc = now.AddMinutes(lockoutMinutes);
                entry.Failures = 0;
            }

            return null;
        }
    }

    private void MakeRoom(DateTime now)
    {
        if (_entries.Count < MaxEntries)
        {
            return;
        }

        foreach (var stale in _entries.Where(e => e.Value.LastSeenUtc < now - Retention).Select(e => e.Key).ToList())
        {
            _entries.Remove(stale);
        }

        if (_entries.Count >= MaxEntries)
        {
            // Still full (a flood of names): forget the least recently seen tenth.
            foreach (var oldest in _entries.OrderBy(e => e.Value.LastSeenUtc).Take(MaxEntries / 10).Select(e => e.Key).ToList())
            {
                _entries.Remove(oldest);
            }
        }
    }

    private sealed class Entry
    {
        public int Failures { get; set; }
        public DateTime? LockoutEndUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }
}

/// <summary>The outcome of counting one guess against a real account.</summary>
public sealed record AttemptReservation(bool LockedNow);

public static class LoginProblems
{
    public static ApiException Locked(TimeSpan retryAfter) =>
        new(StatusCodes.Status429TooManyRequests, Lifestream.Contracts.ProblemCodes.LockedOut, "Too many failed attempts. Try again later.", retryAfter: retryAfter);
}
