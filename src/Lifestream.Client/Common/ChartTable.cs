namespace Lifestream.Client.Common;

/// <summary>
/// The data of a chart as a table (SHELL-35): column titles and rows of text. Every chart is shown with an equivalent table behind a
/// "View as table" toggle, so the numbers are available to screen readers and to people who cannot read the chart.
/// </summary>
public sealed record ChartTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);
