namespace Lifestream.Client.Common;

/// <summary>What a list screen remembers for the session: search, filters, sort and scroll position (SHELL-24).</summary>
public sealed class ListViewState
{
    public string? Search { get; set; }

    public Dictionary<string, string?> Filters { get; } = [];

    public string? SortColumn { get; set; }

    public bool SortDescending { get; set; }

    public double ScrollOffset { get; set; }

    /// <summary>The server-side sort expression, such as "name" or "-name"; null when unsorted. The sort survives filtering and paging.</summary>
    public string? SortExpression => SortColumn is null ? null : SortDescending ? "-" + SortColumn : SortColumn;
}

public interface IListViewStateStore
{
    /// <summary>Returns the same state object every time for the key, for as long as the user is signed in.</summary>
    ListViewState GetState(string key);

    void Clear();
}

public sealed class ListViewStateStore : IListViewStateStore
{
    private readonly Dictionary<string, ListViewState> _states = [];

    public ListViewState GetState(string key)
    {
        if (!_states.TryGetValue(key, out var state))
        {
            state = new ListViewState();
            _states[key] = state;
        }

        return state;
    }

    public void Clear() => _states.Clear();
}
