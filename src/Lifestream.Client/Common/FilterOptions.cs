namespace Lifestream.Client.Common;

/// <summary>
/// One entry of a filter drop-down: the text shown and the typed value, which is null for the explicit "All" choice. A combo box is
/// bound to a list of these (never to bare strings), so "All" is a real, keyboard-reachable choice and not an empty selection.
/// </summary>
public sealed record FilterOption<T>(string Display, T? Value)
    where T : struct
{
    public bool IsAll => Value is null;

    public override string ToString() => Display;
}

public static class FilterOptions
{
    /// <summary>The text of the explicit choice that removes the filter.</summary>
    public const string AllText = "All";

    /// <summary>Builds the list for a filter: "All" first, then one typed option per value.</summary>
    public static IReadOnlyList<FilterOption<T>> WithAll<T>(IEnumerable<T> values, Func<T, string> display, string allText = AllText)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(display);
        return [new FilterOption<T>(allText, null), .. values.Select(v => new FilterOption<T>(display(v), v))];
    }
}
