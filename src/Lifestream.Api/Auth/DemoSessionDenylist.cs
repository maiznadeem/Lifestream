using System.Data;
using System.Runtime.CompilerServices;
using Lifestream.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Lifestream.Api.Auth;

/// <summary>
/// Server-side revocation for demo sessions. A demo session has no refresh-token row (a daily reset must never log a visitor out), so
/// what ended is remembered in the <c>DemoRevocation</c> table instead: a session (family) that signed out or whose rotated-away
/// refresh token came back, and the ids of refresh tokens already used. Every row expires when the longest token it could still affect
/// would have, and expired rows are purged whenever the table is used (no timer). The rows survive a restart and are not touched by the
/// demo reset. A "not revoked" answer is remembered for a few seconds (cleared at once by <see cref="RevocationCache.Clear"/>).
/// </summary>
public sealed class DemoSessionDenylist(LifestreamDbContext db, TimeProvider time, IMemoryCache cache)
{
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromMinutes(5);
    private const int PurgeBatch = 500;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static string FamilyKey(Guid family) => "f:" + family.ToString("N");

    private static string TokenKey(Guid tokenId) => "t:" + tokenId.ToString("N");

    /// <summary>True when the session signed out (or was ended by a reused refresh token): its access and refresh tokens are no longer accepted.</summary>
    public async Task<bool> IsRevokedAsync(Guid family, CancellationToken ct)
    {
        var cacheKey = (Volatile.Read(ref RevocationCache.VersionOf(cache).Value), "demo-revoked", family);
        if (cache.TryGetValue(cacheKey, out bool cached))
        {
            return cached;
        }

        var now = Now;
        await PurgeAsync(now, ct);
        var key = FamilyKey(family);
        var revoked = await db.DemoRevocations.AsNoTracking().AnyAsync(r => r.Key == key && r.ExpiresAtUtc > now, ct);
        if (!revoked)
        {
            // Only "not revoked" is remembered: a revoked session is asked about rarely, and a revocation clears the memory at once.
            cache.Set(cacheKey, false, TimeSpan.FromSeconds(20));
        }

        return revoked;
    }

    /// <summary>Ends the session until <paramref name="until"/> (when its last token would have expired anyway).</summary>
    public async Task RevokeFamilyAsync(Guid family, DateTime until, CancellationToken ct)
    {
        await PurgeAsync(Now, ct);
        await db.Database.ExecuteSqlRawAsync(
            "MERGE [DemoRevocation] WITH (HOLDLOCK) AS t USING (SELECT @k AS [Key], @u AS [ExpiresAtUtc]) AS s ON t.[Key] = s.[Key] " +
            "WHEN MATCHED AND t.[ExpiresAtUtc] < s.[ExpiresAtUtc] THEN UPDATE SET [ExpiresAtUtc] = s.[ExpiresAtUtc] " +
            "WHEN NOT MATCHED THEN INSERT ([Key], [ExpiresAtUtc]) VALUES (s.[Key], s.[ExpiresAtUtc]);",
            [KeyParameter(FamilyKey(family)), TimeParameter("@u", until)],
            ct);
        RevocationCache.Clear(cache);
    }

    /// <summary>Marks a refresh token as used. False when it was already used (a replay of a rotated-away token); two callers at once cannot both win.</summary>
    public async Task<bool> TryUseAsync(Guid tokenId, DateTime until, CancellationToken ct)
    {
        var now = Now;
        await PurgeAsync(now, ct);
        var key = TokenKey(tokenId);

        // An expired entry for the same id is replaced (the update only matches an expired row, so two racers cannot both win)...
        if (await db.DemoRevocations.Where(r => r.Key == key && r.ExpiresAtUtc <= now).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAtUtc, until), ct) == 1)
        {
            return true;
        }

        // ...otherwise the first caller inserts the row and every later one finds it.
        var inserted = await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO [DemoRevocation] ([Key], [ExpiresAtUtc]) SELECT @k, @u WHERE NOT EXISTS (SELECT 1 FROM [DemoRevocation] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = @k);",
            [KeyParameter(key), TimeParameter("@u", until)],
            ct);
        return inserted == 1;
    }

    private async Task PurgeAsync(DateTime now, CancellationToken ct)
    {
        // When this process last purged (kept in the process-wide cache object, so two hosts in one process never share it); a restart simply purges again on first use.
        var gate = cache.GetOrCreate("lifestream.demo-purge", _ => new StrongBox<long>())!;
        var last = Interlocked.Read(ref gate.Value);
        if (last != 0 && Math.Abs(now.Ticks - last) < PurgeEvery.Ticks || Interlocked.CompareExchange(ref gate.Value, now.Ticks, last) != last)
        {
            return;
        }

        var expired = await db.DemoRevocations.Where(r => r.ExpiresAtUtc <= now).OrderBy(r => r.ExpiresAtUtc).Select(r => r.Key).Take(PurgeBatch).ToListAsync(ct);
        if (expired.Count > 0)
        {
            await db.DemoRevocations.Where(r => expired.Contains(r.Key) && r.ExpiresAtUtc <= now).ExecuteDeleteAsync(ct);
        }
    }

    private static SqlParameter KeyParameter(string key) => new("@k", SqlDbType.VarChar, 40) { Value = key };

    private static SqlParameter TimeParameter(string name, DateTime value) => new(name, SqlDbType.DateTime2) { Scale = 3, Value = DateTime.SpecifyKind(value, DateTimeKind.Utc) };
}
