using System.Data;
using System.Linq.Expressions;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lifestream.Api.Common;

/// <summary>One row of a page together with the total row count, so a page and its total come back from one SQL command.</summary>
public sealed record ListRow<T>(T Item, int Total);

public static class Lists
{
    /// <summary>Page 1 or later; size defaults to 50 and is clamped to 200 so no list can ever return more.</summary>
    public static (int Page, int Size) Normalize(int page, int pageSize)
    {
        if (page < 1)
        {
            throw ApiException.Validation("page", "must be 1 or more");
        }

        var size = pageSize <= 0 ? Paging.DefaultPageSize : Math.Min(pageSize, Paging.MaxPageSize);
        return (page, size);
    }

    /// <summary>Builds the response. The total comes from the first row; only an empty result needs a fallback (0, or a count for a page past the end).</summary>
    public static async Task<PagedResult<T>> BuildAsync<T>(IReadOnlyList<ListRow<T>> rows, int page, int size, Func<Task<int>> countWhenEmpty)
    {
        var total = rows.Count > 0 ? rows[0].Total : page == 1 ? 0 : await countWhenEmpty();
        return new PagedResult<T>([.. rows.Select(r => r.Item)], total, page, size);
    }

    /// <summary>The prefix pattern ("term%", wildcards escaped) from the data module, so every name search seeks the name index.</summary>
    public static string PrefixPattern(string term) => Lifestream.Data.Queries.SearchQueries.PrefixPattern(term);

    public static string? CleanSearch(string? search)
    {
        var term = search?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            return null;
        }

        return term.Length > 100 ? throw ApiException.Validation("search", "must be at most 100 characters") : term;
    }

    /// <summary>Day-based range: from the start of the "from" day to the end of the "to" day, whatever its time (half-open, never BETWEEN).</summary>
    public static (DateTime? Start, DateTime? EndExclusive) DayRange(DateTime? from, DateTime? to)
    {
        var start = from is { } f ? Dates.Utc(f).Date : (DateTime?)null;
        var end = to is { } t ? DateTime.SpecifyKind(Dates.Utc(t).Date.AddDays(1), DateTimeKind.Utc) : (DateTime?)null;
        if (start is { } s)
        {
            start = DateTime.SpecifyKind(s, DateTimeKind.Utc);
        }

        return (start, end);
    }
}

/// <summary>Whitelisted sort keys: "name" ascending, "-name" descending. Always ends with the id so paging is stable.</summary>
public sealed class SortMap<T>
{
    private readonly Dictionary<string, Func<IQueryable<T>, bool, IOrderedQueryable<T>>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Expression<Func<T, int>> _id;
    private readonly string _default;
    private readonly bool _defaultDescending;

    public SortMap(Expression<Func<T, int>> id, string defaultKey, bool defaultDescending = false)
    {
        _id = id;
        _default = defaultKey;
        _defaultDescending = defaultDescending;
    }

    public SortMap<T> Add<TKey>(string name, Expression<Func<T, TKey>> key)
    {
        _keys[name] = (q, desc) => desc ? q.OrderByDescending(key) : q.OrderBy(key);
        return this;
    }

    public IQueryable<T> Apply(IQueryable<T> query, string? sort)
    {
        var name = string.IsNullOrWhiteSpace(sort) ? _default : sort.Trim();
        var descending = string.IsNullOrWhiteSpace(sort) ? _defaultDescending : name.StartsWith('-');
        name = name.TrimStart('-', '+');
        if (!_keys.TryGetValue(name, out var apply))
        {
            throw ApiException.Validation("sort", $"must be one of: {string.Join(", ", _keys.Keys)}");
        }

        return descending ? apply(query, true).ThenByDescending(_id) : apply(query, false).ThenBy(_id);
    }
}

public static class Transactions
{
    /// <summary>
    /// Runs a multi-step write in ONE explicit transaction inside the retrying execution strategy (so a database that is still
    /// waking is retried as a whole). The audit rows written by a second save are part of it. The work must load what it needs
    /// itself, because every retry starts from a clean change tracker.
    /// </summary>
    public static Task<T> RunAsync<T>(this Lifestream.Data.LifestreamDbContext db, Func<CancellationToken, Task<T>> work, CancellationToken ct, IsolationLevel isolation = IsolationLevel.ReadCommitted) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(isolation, ct);
            var result = await work(ct);
            await transaction.CommitAsync(ct);
            return result;
        });

    /// <summary>Takes a named exclusive application lock for the rest of the current transaction (serialises, for example, two bookings of one slot).</summary>
    public static async Task LockAsync(this Lifestream.Data.LifestreamDbContext db, string name, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("LockAsync needs an open transaction.");
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "sp_getapplock";
        AddParameter(command, "@Resource", name);
        AddParameter(command, "@LockMode", "Exclusive");
        AddParameter(command, "@LockOwner", "Transaction");
        AddParameter(command, "@LockTimeout", 15_000);
        var result = command.CreateParameter();
        result.ParameterName = "@result";
        result.DbType = DbType.Int32;
        result.Direction = ParameterDirection.ReturnValue;
        command.Parameters.Add(result);
        await command.ExecuteNonQueryAsync(ct);
        if ((int)result.Value! < 0)
        {
            throw ApiException.Conflict("The system is busy. Try again.");
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
