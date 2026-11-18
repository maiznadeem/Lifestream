namespace Lifestream.Contracts;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class Paging
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
}
