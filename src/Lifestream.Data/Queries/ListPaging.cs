using Lifestream.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Data.Queries;

/// <summary>
/// Paging for the list screens (DB-05), in two steps so that no page, however deep, reads a table:
/// <list type="number">
/// <item><see cref="PageIdsAsync"/> reads only the ids of the page, from the narrow index that holds the filter and sort
/// columns (index key + <c>Id</c> is the keyset order: every nonclustered index ends in the clustered key).</item>
/// <item><see cref="WithIds"/> then loads those at most <c>pageSize</c> rows (and joins their display names) by primary key.</item>
/// </list>
/// Selecting the page's columns in one statement lets the optimizer pick a scan of the clustered index instead (it is cheaper
/// than fifty key lookups at a few hundred pages); asking for ids first removes that choice. The caller re-sorts the loaded
/// rows into the id order.
/// </summary>
public static class ListPaging
{
    /// <summary>The ids of page <paramref name="page"/> (1-based) of <paramref name="ordered"/>. The ordering must end in <c>Id</c>.</summary>
    public static Task<List<int>> PageIdsAsync<T>(IOrderedQueryable<T> ordered, int page, int pageSize, CancellationToken cancellationToken = default)
        where T : class, IEntity =>
        ordered.Select(e => e.Id).Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

    /// <summary>Rows with the given ids (a primary-key seek per id).</summary>
    public static IQueryable<T> WithIds<T>(this IQueryable<T> source, IReadOnlyCollection<int> ids)
        where T : class, IEntity =>
        source.Where(e => ids.Contains(e.Id));
}
