using Lifestream.Api.Infrastructure;
using Lifestream.Data;
using Lifestream.Data.Seed;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Auth;

/// <summary>
/// Process-wide demo bookkeeping: which staff row is the demo account right now (ids change at every reset), when the last reset
/// happened (so older sessions get the X-Demo-Reset header) and the once-per-UTC-day reset guard. One replica, so memory is enough;
/// the database application lock (inside <see cref="DemoReset"/>) still makes the reset safe if there were two.
/// </summary>
public sealed class DemoDirectory : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _demoStaffId;
    private DateOnly? _checkedDay;

    public void Dispose() => _gate.Dispose();

    /// <summary>Set whenever this process performed a reset (daily or manual).</summary>
    public DateTime? LastResetUtc { get; private set; }

    public async Task<int> DemoStaffIdAsync(LifestreamDbContext db, CancellationToken ct)
    {
        var cached = Volatile.Read(ref _demoStaffId);
        if (cached != 0)
        {
            return cached;
        }

        var id = await db.Staff.Where(s => s.IsDemo).Select(s => s.Id).FirstOrDefaultAsync(ct);
        if (id == 0)
        {
            throw ApiException.Conflict("The demo account does not exist.");
        }

        Volatile.Write(ref _demoStaffId, id);
        return id;
    }

    /// <summary>Runs the daily reset once per UTC day. Cheap (no database call) after the first request of the day.</summary>
    public async Task EnsureDailyResetAsync(IServiceProvider services, DateOnly today, DateTime now, CancellationToken ct)
    {
        if (_checkedDay == today)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_checkedDay == today)
            {
                return;
            }

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LifestreamDbContext>();
            if (await DemoReset.RunIfDueAsync(db, today, ct))
            {
                AfterReset(now);
            }

            _checkedDay = today;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void AfterReset(DateTime now)
    {
        LastResetUtc = now;
        Volatile.Write(ref _demoStaffId, 0);
        RevocationCache.Clear();
    }
}
