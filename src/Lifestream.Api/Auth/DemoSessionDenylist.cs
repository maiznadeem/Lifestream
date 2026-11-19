using System.Collections.Concurrent;

namespace Lifestream.Api.Auth;

/// <summary>
/// Server-side revocation for demo sessions. A demo session has no database row (a daily reset must never log a visitor out), so
/// what ended is remembered here instead: a session (family) that signed out or whose rotated-away refresh token came back, and the
/// ids of refresh tokens already used. Every entry expires when the longest token it could still affect would have, so the list
/// stays small. Held in memory, like the rest of the demo bookkeeping (one replica); a restart forgets it, and the signed tokens
/// then expire on their own within the demo refresh lifetime.
/// </summary>
public sealed class DemoSessionDenylist(TimeProvider time)
{
    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTime> _entries = new(StringComparer.Ordinal);
    private long _lastPruneTicks;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static string FamilyKey(Guid family) => "f:" + family.ToString("N");

    private static string TokenKey(Guid tokenId) => "t:" + tokenId.ToString("N");

    /// <summary>True when the session signed out (or was ended by a reused refresh token): its access and refresh tokens are no longer accepted.</summary>
    public bool IsRevoked(Guid family) => _entries.TryGetValue(FamilyKey(family), out var until) && until > Now;

    /// <summary>Ends the session until <paramref name="until"/> (when its last token would have expired anyway).</summary>
    public void RevokeFamily(Guid family, DateTime until)
    {
        Prune();
        _entries.AddOrUpdate(FamilyKey(family), until, (_, existing) => existing > until ? existing : until);
    }

    /// <summary>Marks a refresh token as used. False when it was already used (a replay of a rotated-away token).</summary>
    public bool TryUse(Guid tokenId, DateTime until)
    {
        Prune();
        var key = TokenKey(tokenId);
        var now = Now;
        while (true)
        {
            if (_entries.TryAdd(key, until))
            {
                return true;
            }

            if (_entries.TryGetValue(key, out var existing) && existing > now)
            {
                return false;
            }

            // An expired entry for the same id: replace it (compared against what was read, so two racers cannot both win).
            if (_entries.TryUpdate(key, until, existing))
            {
                return true;
            }
        }
    }

    private void Prune()
    {
        var now = Now;
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now.Ticks - last < PruneEvery.Ticks || Interlocked.CompareExchange(ref _lastPruneTicks, now.Ticks, last) != last)
        {
            return;
        }

        foreach (var expired in _entries.Where(e => e.Value <= now).Select(e => e.Key).ToList())
        {
            _entries.TryRemove(expired, out _);
        }
    }
}
