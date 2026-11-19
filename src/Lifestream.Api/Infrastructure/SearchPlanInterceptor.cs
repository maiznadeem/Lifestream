using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lifestream.Api.Infrastructure;

/// <summary>
/// Appends <c>OPTION (RECOMPILE)</c> to queries tagged <see cref="Tag"/> (the name-search list queries). The same SQL text serves a
/// term that matches 3 donors and one that matches 1,200, and the plan that is right for one is a scan for the other; with a cached plan
/// the first term decides for everyone. Compiling for the actual term costs a few milliseconds and picks the seek plan for the narrow
/// term and the set-based plan for the broad one.
/// </summary>
public sealed class SearchPlanInterceptor : DbCommandInterceptor
{
    public const string Tag = "search-recompile";

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

    private static void Rewrite(DbCommand command)
    {
        if (command.CommandText.StartsWith("-- " + Tag, StringComparison.Ordinal) && !command.CommandText.Contains("OPTION (RECOMPILE)", StringComparison.Ordinal))
        {
            command.CommandText += "\nOPTION (RECOMPILE)";
        }
    }
}
