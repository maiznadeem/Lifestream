using System.Data.Common;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lifestream.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lifestream.Api.Tests.Data;

/// <summary>One executed statement: its actual plan (with the real parameter values) and the pages it read.</summary>
internal sealed record ExecutedPlan(string Sql, XDocument Plan, int LogicalReads)
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>Every operator in the plan as (physical operator, logical operator, table).</summary>
    public IEnumerable<(string Physical, string Logical, string? Table)> Operators() =>
        Plan.Descendants(Ns + "RelOp").Select(op => (
            (string)op.Attribute("PhysicalOp")!,
            (string)op.Attribute("LogicalOp")!,
            op.Elements().SelectMany(e => e.Elements(Ns + "Object")).FirstOrDefault() is { } o ? (string?)o.Attribute("Table") : null));

    /// <summary>Operators that read a whole table: a heap scan or a scan of the clustered index.</summary>
    public IEnumerable<string> TableScans() =>
        Operators().Where(o => o.Physical is "Table Scan" or "Clustered Index Scan").Select(o => o.Table ?? "?");

    public bool HasJoin() => Operators().Any(o => o.Logical.Contains("Join", StringComparison.Ordinal));
}

/// <summary>Replays the statements a context sent, with the same parameters, under SET STATISTICS XML and IO.</summary>
internal static class PlanHelper
{
    public static async Task<List<ExecutedPlan>> ExecuteAndCaptureAsync(
        SqlServerDatabaseFixture fixture,
        Func<LifestreamDbContext, Task> query,
        CancellationToken ct)
    {
        var capture = new ParameterCapture();
        await using (var db = fixture.NewContext(null, capture))
        {
            await query(db);
        }

        var plans = new List<ExecutedPlan>();
        foreach (var (sql, parameters) in capture.Statements)
        {
            plans.Add(await ReplayAsync(fixture.ConnectionString, sql, parameters, ct));
        }

        return plans;
    }

    private static async Task<ExecutedPlan> ReplayAsync(string connectionString, string sql, List<SqlParameter> parameters, CancellationToken ct)
    {
        var reads = 0;
        await using var connection = new SqlConnection(connectionString);
        connection.InfoMessage += (_, e) =>
        {
            foreach (Match m in LogicalReadsRegex.Matches(e.Message))
            {
                reads += int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        };
        await connection.OpenAsync(ct);
        await using (var set = new SqlCommand("SET STATISTICS IO ON; SET STATISTICS XML ON;", connection))
        {
            await set.ExecuteNonQueryAsync(ct);
        }

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());
        string? xml = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            do
            {
                var isPlan = reader.FieldCount == 1 && reader.GetName(0).Contains("XML Showplan", StringComparison.Ordinal);
                while (await reader.ReadAsync(ct))
                {
                    if (isPlan)
                    {
                        xml = reader.GetString(0);
                    }
                }
            }
            while (await reader.NextResultAsync(ct));
        }

        return new ExecutedPlan(sql, XDocument.Parse(xml ?? throw new InvalidOperationException("No plan returned for: " + sql)), reads);
    }

    private static readonly Regex LogicalReadsRegex = new(@"(?<!lob )logical reads (\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class ParameterCapture : DbCommandInterceptor
    {
        public List<(string Sql, List<SqlParameter> Parameters)> Statements { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command) =>
            Statements.Add((command.CommandText, command.Parameters.Cast<SqlParameter>().Select(p => (SqlParameter)((ICloneable)p).Clone()).ToList()));
    }
}
