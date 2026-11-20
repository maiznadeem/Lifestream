using System.Data.Common;
using Lifestream.Data.Queries;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lifestream.Api.Infrastructure;

/// <summary>
/// Appends the query hint a search needs to the statements that carry one (tagged by the search itself, <see cref="SearchQueries.PlanTag"/> or
/// <see cref="SearchQueries.SeekPlanTag"/>).
/// <para>
/// A search looks for a word inside names (<c>LIKE '%word%'</c>), and SQL Server cannot know how many names contain a word: it assumes 9 % of them,
/// for "zzzz" as for "a". With that guess and a page of 50 to fill ("a row goal"), it reads the big table in the list's order and looks every row's
/// person up until it has 50 matches. That is quick when most names match and reads the whole table, one lookup per row, when few do: 9,000 pages for
/// a last name, and the commonest search is for one person. Every search is therefore sent with <c>DISABLE_OPTIMIZER_ROWGOAL</c>: the statement
/// then starts from the people found and costs about the same for every word.
/// </para>
/// <para>
/// <see cref="SearchQueries.SeekPlanTag"/> adds <c>LOOP JOIN</c>: from the people found, one index seek per person into the big table, where the
/// 9 % guess would have SQL Server join the big table's whole index.
/// </para>
/// <para>
/// The statements used to be recompiled for each term instead (<c>OPTION (RECOMPILE)</c>), which chose well while a search was a prefix of the name,
/// because SQL Server can estimate a prefix from the index. It cannot estimate "contains", so a recompile would give the same plan every time and
/// only cost its 10 ms (50 ms and more for the receipt search).
/// </para>
/// </summary>
public sealed class SearchPlanInterceptor : DbCommandInterceptor
{
    private const string NoRowGoal = "USE HINT ('DISABLE_OPTIMIZER_ROWGOAL')";

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Rewrite(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Rewrite(command);
        return ValueTask.FromResult(result);
    }

    private const string SearchHint = "\nOPTION (" + NoRowGoal + ")";
    private const string SeekHint = "\nOPTION (LOOP JOIN, " + NoRowGoal + ")";

    /// <summary>The hint to append to a statement: one of two fixed texts when its first line is a search tag and it has no OPTION clause yet, else none.</summary>
    internal static string? HintFor(string sql)
    {
        var hint = IsTagged(sql, SearchQueries.SeekPlanTag) ? SeekHint : IsTagged(sql, SearchQueries.PlanTag) ? SearchHint : null;
        return hint is null || sql.Contains("\nOPTION (", StringComparison.Ordinal) ? null : hint;
    }

    // EF writes a tag as a comment line of its own in front of the statement.
    private static bool IsTagged(string sql, string tag) =>
        sql.Length > tag.Length + 3 && sql.StartsWith("-- ", StringComparison.Ordinal) && string.CompareOrdinal(sql, 3, tag, 0, tag.Length) == 0 && sql[tag.Length + 3] is '\r' or '\n';

    private static void Rewrite(DbCommand command)
    {
        if (HintFor(command.CommandText) is { } hint)
        {
            command.CommandText += hint;
        }
    }
}
