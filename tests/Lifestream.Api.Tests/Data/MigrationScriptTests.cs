using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Lifestream.Api.Tests.Data;

/// <summary>The committed idempotent script (OWN-04, DB-01): up to date, and it builds the same schema as the migrations.</summary>
public sealed partial class MigrationScriptTests(EmptyDatabaseFixture empty) : IClassFixture<EmptyDatabaseFixture>
{
    private const string ScriptPath = "src/Lifestream.Data/Migrations/idempotent.sql";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IdempotentScript_Committed_MatchesWhatTheMigrationsGenerate()
    {
        await using var db = empty.NewContext();
        var generated = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        var committed = await File.ReadAllTextAsync(SchemaTests.RepoFile(ScriptPath), Ct);

        Assert.Equal(Normalize(generated), Normalize(committed));
    }

    [Fact]
    public async Task IdempotentScript_AppliedToEmptyDatabaseTwice_GivesTheMigratedSchema_NoPendingChanges()
    {
        Assert.Equal(0, await empty.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables", Ct));
        var script = await File.ReadAllTextAsync(SchemaTests.RepoFile(ScriptPath), Ct);

        await RunScriptAsync(script);
        await RunScriptAsync(script); // idempotent: the second run changes nothing and does not fail

        await using var db = empty.NewContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.False(db.Database.HasPendingModelChanges(), "Applying the script left the database different from the model.");
        Assert.Equal(12 + 1, await empty.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables", Ct));
        Assert.Equal(
            db.Database.GetMigrations().Count(),
            await empty.ScalarAsync<int>("SELECT COUNT(*) FROM __EFMigrationsHistory", Ct));
    }

    private async Task RunScriptAsync(string script)
    {
        await using var connection = new SqlConnection(empty.ConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline)]
    private static partial Regex GoSeparator();
}
