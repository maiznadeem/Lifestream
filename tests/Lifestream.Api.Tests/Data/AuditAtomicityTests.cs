using System.Data.Common;
using Lifestream.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lifestream.Api.Tests.Data;

/// <summary>
/// DB-16 in depth: the audit row carries the real generated id, the change and its audit rows commit together, every
/// SaveChanges overload is right (including <c>acceptAllChangesOnSuccess: false</c> and execution-strategy retries), and
/// bulk deletes go through a helper that audits. All against a real SQL Server database.
/// </summary>
public sealed class AuditAtomicityTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Tag() => "AT" + Guid.NewGuid().ToString("N")[..10];

    private Task<int> DonorRows(string name) => fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM Donor WHERE Name = '{name}'", Ct);

    private Task<int> AuditRows(string entity, int id, string action) =>
        fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM AuditLog WHERE Entity = '{entity}' AND EntityId = {id} AND Action = '{action}'", Ct);

    [Fact]
    public async Task RanAgainstARealSqlServer() =>
        Assert.Contains("Microsoft SQL Server", await fixture.ScalarAsync<string>("SELECT @@VERSION", Ct), StringComparison.Ordinal);

    [Fact]
    public async Task SaveChangesAsync_AcceptFalse_ThenAcceptAllChanges_InsertsOnce_AndAuditsTheRealId()
    {
        var name = Tag();
        await using var db = fixture.NewContext(null);
        var donor = TestRows.Donor(name);
        db.Donors.Add(donor);

        await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct);
        db.ChangeTracker.AcceptAllChanges();

        Assert.Equal(1, await DonorRows(name));
        Assert.True(donor.Id > 0);
        Assert.Equal(1, await AuditRows("Donor", donor.Id, "Insert"));
        Assert.Equal(0, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM AuditLog WHERE EntityId = 0 AND Entity = 'Donor'", Ct));
    }

    [Fact]
    public async Task SaveChanges_Sync_AcceptFalse_ThenAcceptAllChanges_InsertsOnce_AndAuditsTheRealId()
    {
        var name = Tag();
        await using var db = fixture.NewContext(null);
        var donor = TestRows.Donor(name);
        db.Donors.Add(donor);

        db.SaveChanges(acceptAllChangesOnSuccess: false);
        db.ChangeTracker.AcceptAllChanges();

        Assert.Equal(1, await DonorRows(name));
        Assert.Equal(1, await AuditRows("Donor", donor.Id, "Insert"));
    }

    [Fact]
    public async Task SaveChanges_Sync_Default_InsertsOnce_AndAuditsTheRealId()
    {
        var name = Tag();
        await using var db = fixture.NewContext(null);
        var donor = TestRows.Donor(name);
        db.Donors.Add(donor);

        db.SaveChanges();

        Assert.Equal(1, await DonorRows(name));
        Assert.Equal(1, await AuditRows("Donor", donor.Id, "Insert"));
    }

    [Fact]
    public async Task SaveChangesAsync_NewParentAndChild_AuditBothWithRealIds()
    {
        var name = Tag();
        await using var db = fixture.NewContext(null);
        var donor = TestRows.Donor(name);
        var appointment = TestRows.Appointment(donor);
        db.Appointments.Add(appointment);

        await db.SaveChangesAsync(Ct);

        Assert.True(donor.Id > 0 && appointment.Id > 0);
        Assert.Equal(1, await AuditRows("Donor", donor.Id, "Insert"));
        Assert.Equal(1, await AuditRows("Appointment", appointment.Id, "Insert"));
    }

    [Fact]
    public async Task Save_AuditInsertFails_BusinessChangeIsNotKept_NoTransactionOpenedByTheCaller()
    {
        var name = Tag();
        var failAudit = new FailOnAuditInsert();
        await using var db = fixture.NewContext(null, failAudit);
        db.Donors.Add(TestRows.Donor(name));

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));

        Assert.True(failAudit.Fired, "The audit insert was never attempted, so the test proved nothing.");
        Assert.Equal(0, await DonorRows(name));
    }

    [Fact]
    public async Task Save_Sync_AuditInsertFails_BusinessChangeIsNotKept()
    {
        var name = Tag();
        var failAudit = new FailOnAuditInsert();
        await using var db = fixture.NewContext(null, failAudit);
        db.Donors.Add(TestRows.Donor(name));

        Assert.ThrowsAny<Exception>(() => db.SaveChanges());

        Assert.True(failAudit.Fired);
        Assert.Equal(0, await DonorRows(name));
    }

    [Fact]
    public async Task Save_InsideCallersTransaction_RolledBack_LeavesNeitherRowNorAudit()
    {
        var name = Tag();
        await using var db = fixture.NewContext(null);
        var donor = TestRows.Donor(name);
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Donors.Add(donor);
            await db.SaveChangesAsync(Ct);
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(0, await DonorRows(name));
        Assert.Equal(0, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM AuditLog WHERE Entity = 'Donor' AND Action = 'Insert' AND EntityId = {donor.Id}", Ct));
    }

    [Fact]
    public async Task Save_InsideCallersTransaction_Committed_KeepsRowAndAudit()
    {
        var name = Tag();
        await using var db = fixture.NewContext(null);
        var donor = TestRows.Donor(name);
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Donors.Add(donor);
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(1, await DonorRows(name));
        Assert.Equal(1, await AuditRows("Donor", donor.Id, "Insert"));
    }

    [Fact]
    public async Task Save_WithExecutionStrategyRetry_InsertsOnce_AndAuditsTheRealId()
    {
        // The first attempt fails after the business row is written and while the audit row is being written; the
        // execution strategy retries the whole save. One donor, one audit row, carrying the id the donor really has.
        var name = Tag();
        var failOnce = new FailOnAuditInsert(failTimes: 1, () => new TransientTestException());
        await using var db = NewRetryingContext(failOnce);
        var donor = TestRows.Donor(name);
        db.Donors.Add(donor);

        await db.SaveChangesAsync(Ct);

        Assert.Equal(1, failOnce.Attempts - 1);
        Assert.Equal(1, await DonorRows(name));
        Assert.True(donor.Id > 0);
        Assert.Equal(1, await AuditRows("Donor", donor.Id, "Insert"));
        Assert.Equal(1, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM AuditLog a JOIN Donor d ON d.Id = a.EntityId WHERE a.Entity = 'Donor' AND d.Name = '{name}'", Ct));
    }

    [Fact]
    public async Task Save_WithExecutionStrategyRetry_NewParentAndChild_InsertOnce()
    {
        var name = Tag();
        var failOnce = new FailOnAuditInsert(failTimes: 1, () => new TransientTestException());
        await using var db = NewRetryingContext(failOnce);
        var donor = TestRows.Donor(name);
        var appointment = TestRows.Appointment(donor);
        db.Appointments.Add(appointment);

        await db.SaveChangesAsync(Ct);

        Assert.Equal(1, await DonorRows(name));
        Assert.Equal(1, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM Appointment WHERE DonorId = {donor.Id}", Ct));
        Assert.Equal(1, await AuditRows("Appointment", appointment.Id, "Insert"));
    }

    [Fact]
    public async Task Save_UpdateAndDelete_AuditInsideTheSameTransaction()
    {
        var name = Tag();
        await using var db = fixture.NewContext(null);
        var donor = TestRows.Donor(name);
        db.Donors.Add(donor);
        await db.SaveChangesAsync(Ct);

        donor.Name = name + "x";
        await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct);
        db.ChangeTracker.AcceptAllChanges();
        db.Donors.Remove(donor);
        await db.SaveChangesAsync(Ct);

        Assert.Equal(1, await AuditRows("Donor", donor.Id, "Insert"));
        Assert.Equal(1, await AuditRows("Donor", donor.Id, "Update"));
        Assert.Equal(1, await AuditRows("Donor", donor.Id, "Delete"));
    }

    // ---- DB-16(d): bulk work that bypasses the change tracker must go through an auditing helper ----

    [Fact]
    public async Task BulkDeleteAsync_DeletesRows_AndWritesOneAuditRowPerDeletedId()
    {
        var name = Tag();
        var actor = await NewActorAsync();
        await using var setup = fixture.NewContext(actor);
        var donors = Enumerable.Range(0, 3).Select(i => TestRows.Donor($"{name} {i}")).ToList();
        setup.Donors.AddRange(donors);
        await setup.SaveChangesAsync(Ct);

        await using var db = fixture.NewContext(actor);
        var deleted = await db.BulkDeleteAsync(db.Donors.Where(d => d.Name.StartsWith(name)), Ct);

        Assert.Equal(3, deleted);
        Assert.Equal(0, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM Donor WHERE Name LIKE '{name}%'", Ct));
        foreach (var donor in donors)
        {
            var audit = (await fixture.RowsAsync($"SELECT StaffId FROM AuditLog WHERE Entity = 'Donor' AND EntityId = {donor.Id} AND Action = 'Delete'", Ct)).Single();
            Assert.Equal(actor, (int)audit[0]!);
        }
    }

    [Fact]
    public async Task BulkDeleteAsync_WhenTheAuditWriteFails_TheRowsAreStillThere()
    {
        var name = Tag();
        await using var setup = fixture.NewContext(null);
        setup.Donors.Add(TestRows.Donor(name));
        await setup.SaveChangesAsync(Ct);

        var failAudit = new FailOnAuditInsert();
        await using var db = fixture.NewContext(null, failAudit);
        await Assert.ThrowsAnyAsync<Exception>(() => db.BulkDeleteAsync(db.Donors.Where(d => d.Name == name), Ct));

        Assert.True(failAudit.Fired);
        Assert.Equal(1, await DonorRows(name));
    }

    [Fact]
    public async Task BulkUpdateAsync_UpdatesRows_AndWritesAuditRows()
    {
        var name = Tag();
        await using var setup = fixture.NewContext(null);
        var donor = TestRows.Donor(name);
        setup.Donors.Add(donor);
        await setup.SaveChangesAsync(Ct);

        await using var db = fixture.NewContext(null);
        var updated = await db.BulkUpdateAsync(db.Donors.Where(d => d.Id == donor.Id), s => s.SetProperty(d => d.Address, "Bulk Street"), Ct);

        Assert.Equal(1, updated);
        Assert.Equal(1, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM Donor WHERE Id = {donor.Id} AND Address = 'Bulk Street'", Ct));
        Assert.Equal(1, await AuditRows("Donor", donor.Id, "Update"));
    }

    [Fact]
    public async Task BulkDeleteAsync_NothingMatches_DeletesAndAuditsNothing()
    {
        await using var db = fixture.NewContext(null);
        var before = await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM AuditLog", Ct);

        var deleted = await db.BulkDeleteAsync(db.Donors.Where(d => d.Name == "no such donor " + Guid.NewGuid()), Ct);

        Assert.Equal(0, deleted);
        Assert.Equal(before, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM AuditLog", Ct));
    }

    [Fact]
    public void BareExecuteDelete_IsDocumentedAsBypassingAudit()
    {
        // The guard is the helper above plus this documented rule; a bare ExecuteDelete cannot be intercepted by EF.
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "database.md"));
        Assert.Contains("ExecuteDelete", doc, StringComparison.Ordinal);
        Assert.Contains("BulkDeleteAsync", doc, StringComparison.Ordinal);
    }

    private async Task<int> NewActorAsync()
    {
        await using var db = fixture.NewContext(null);
        var actor = TestRows.Staff(Tag());
        db.Staff.Add(actor);
        await db.SaveChangesAsync(Ct);
        return actor.Id;
    }

    private LifestreamDbContext NewRetryingContext(IInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<LifestreamDbContext>()
            .UseSqlServer(fixture.ConnectionString, o => o.ExecutionStrategy(d => new TestRetryingStrategy(d)))
            .AddInterceptors(interceptor)
            .Options;
        return new LifestreamDbContext(options);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lifestream.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Lifestream.slnx not found above " + AppContext.BaseDirectory);
    }

    private sealed class TransientTestException : Exception;

    private sealed class TestRetryingStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestException;
    }

    /// <summary>Throws when the audit row insert is about to run, the first <c>failTimes</c> times.</summary>
    private sealed class FailOnAuditInsert(int failTimes = int.MaxValue, Func<Exception>? error = null) : DbCommandInterceptor
    {
        public bool Fired => Attempts > 0;

        public int Attempts { get; private set; }

        private void Check(DbCommand command)
        {
            if (!command.CommandText.Contains("INSERT INTO [AuditLog]", StringComparison.Ordinal))
            {
                return;
            }

            Attempts++;
            if (Attempts <= failTimes)
            {
                throw error?.Invoke() ?? new InvalidOperationException("audit insert failed (test)");
            }
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Check(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Check(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Check(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Check(command);
            return ValueTask.FromResult(result);
        }
    }
}
