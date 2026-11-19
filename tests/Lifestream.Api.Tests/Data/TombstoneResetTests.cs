using Lifestream.Data.Seed;

namespace Lifestream.Api.Tests.Data;

/// <summary>FEAT-06 (real SQL Server): the demo reset wipes every kept copy of a deleted record, so none lingers.</summary>
public sealed class TombstoneResetTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DemoReset_LeavesZeroDeletedRecordCopies()
    {
        await fixture.ClearAllAsync(Ct);
        await fixture.ExecuteAsync("INSERT INTO DeletedRecord (Entity, EntityId, Json) VALUES ('Donor', 1, '{\"name\":\"Someone\"}'), ('Patient', 2, '{\"name\":\"Else\"}')", Ct);
        Assert.Equal(2, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM DeletedRecord", Ct));

        await using (var db = fixture.NewContext())
        {
            Assert.True(await DemoReset.RunIfDueAsync(db, new DateOnly(2026, 11, 19), Ct));
        }

        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM DeletedRecord", Ct));
    }
}
