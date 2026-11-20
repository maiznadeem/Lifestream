using System.Windows;
using System.Windows.Controls;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// Columns that fold away when the table gets narrow (direction A, "column priority"): set <c>lists:ListColumns.HideBelow="700"</c> on a column and
/// <c>lists:ListColumns.IsResponsive="True"</c> on its grid. The column is hidden while the grid is narrower than that many DIP (a compact window, or the
/// side panel taking its share), and shown again when it is wider.
/// The name column of a list is <c>Width="Auto"</c> with a cap of 320: it takes what its names need before the detail columns (star) share the rest, so a
/// name is not cut while another column has room to spare. If the columns on show then need more than the table has (long names in a narrow table), the
/// lowest-priority column still on show, the one with the highest <c>HideBelow</c>, folds away too (<see cref="Fit"/>): a column goes before a name is cut.
/// </summary>
public static class ListColumns
{
    public static readonly DependencyProperty HideBelowProperty =
        DependencyProperty.RegisterAttached("HideBelow", typeof(double), typeof(ListColumns), new PropertyMetadata(0.0));

    public static readonly DependencyProperty IsResponsiveProperty =
        DependencyProperty.RegisterAttached("IsResponsive", typeof(bool), typeof(ListColumns), new PropertyMetadata(false, OnIsResponsiveChanged));

    /// <summary>
    /// Whether the column belongs to what the screen shows now (bind it through a <see cref="DataProxy"/>: a column has no data context). A column that is not shown stays
    /// hidden whatever the width; one that is shown still folds away below its <see cref="HideBelowProperty"/>. True by default.
    /// </summary>
    public static readonly DependencyProperty IsShownProperty =
        DependencyProperty.RegisterAttached("IsShown", typeof(bool), typeof(ListColumns), new PropertyMetadata(true, OnIsShownChanged));

    public static bool GetIsShown(DependencyObject column) => (bool)(column ?? throw new ArgumentNullException(nameof(column))).GetValue(IsShownProperty);

    public static void SetIsShown(DependencyObject column, bool value) => (column ?? throw new ArgumentNullException(nameof(column))).SetValue(IsShownProperty, value);

    /// <summary>The grid of each column seen by <see cref="Apply"/>, so a change of <see cref="IsShownProperty"/> can lay the columns out again.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridColumn, DataGrid> Owners = new();

    private static void OnIsShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGridColumn column)
        {
            return;
        }

        if (Owners.TryGetValue(column, out var grid) && grid.ActualWidth > 0)
        {
            Apply(grid, grid.ActualWidth);
            FitLater(grid, shareAgain: true);
        }
        else
        {
            column.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public static double GetHideBelow(DependencyObject column) => (double)(column ?? throw new ArgumentNullException(nameof(column))).GetValue(HideBelowProperty);

    public static void SetHideBelow(DependencyObject column, double value) => (column ?? throw new ArgumentNullException(nameof(column))).SetValue(HideBelowProperty, value);

    public static bool GetIsResponsive(DependencyObject grid) => (bool)(grid ?? throw new ArgumentNullException(nameof(grid))).GetValue(IsResponsiveProperty);

    public static void SetIsResponsive(DependencyObject grid, bool value) => (grid ?? throw new ArgumentNullException(nameof(grid))).SetValue(IsResponsiveProperty, value);

    /// <summary>Shows or hides each column for a grid of this width.</summary>
    public static void Apply(DataGrid grid, double width)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (width <= 0)
        {
            return;
        }

        foreach (var column in grid.Columns)
        {
            Owners.AddOrUpdate(column, grid);
            var below = GetHideBelow(column);
            var governed = column.ReadLocalValue(IsShownProperty) != DependencyProperty.UnsetValue;
            if (below <= 0 && !governed)
            {
                continue;
            }

            var visibility = !GetIsShown(column) || (below > 0 && width < below) ? Visibility.Collapsed : Visibility.Visible;
            if (column.Visibility != visibility)
            {
                column.Visibility = visibility;
            }
        }
    }

    /// <summary>
    /// WPF's DataGrid takes width from fixed columns, down to their minimum, while the table is too narrow for them (a passing layout, the side panel opening), and
    /// gives it back only to star columns. Once the table is laid out, each fixed column gets the width it was declared with back.
    /// </summary>
    public static void RestoreWidths(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        foreach (var column in grid.Columns)
        {
            if (column.ReadLocalValue(DeclaredWidthProperty) is not double declared)
            {
                declared = column.Width.IsAbsolute ? column.Width.Value : double.NaN;
                column.SetValue(DeclaredWidthProperty, declared);
            }

            if (!double.IsNaN(declared) && column.Visibility == Visibility.Visible && Math.Abs(column.ActualWidth - declared) > 0.5)
            {
                column.Width = new DataGridLength(declared);
            }
        }
    }

    /// <summary>The width a fixed column was declared with (NaN for star and auto columns), read once.</summary>
    private static readonly DependencyProperty DeclaredWidthProperty =
        DependencyProperty.RegisterAttached("DeclaredWidth", typeof(double), typeof(ListColumns), new PropertyMetadata(double.NaN));

    private static void OnIsResponsiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        grid.SizeChanged -= OnSizeChanged;
        grid.LoadingRow -= OnLoadingRow;
        if ((bool)e.NewValue)
        {
            grid.SizeChanged += OnSizeChanged;
            grid.LoadingRow += OnLoadingRow; // a row with a longer name can widen the name column
            Apply(grid, grid.ActualWidth);
            RestoreWidths(grid);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            var grid = (DataGrid)sender;
            Apply(grid, e.NewSize.Width);
            // After the grid has shared out the new width among its columns (it does so in its own layout pass, after this event).
            grid.Dispatcher.BeginInvoke(() => RestoreWidths(grid), System.Windows.Threading.DispatcherPriority.Loaded);
            FitLater(grid, shareAgain: true);
        }
    }

    private static void OnLoadingRow(object? sender, DataGridRowEventArgs e) => FitLater((DataGrid)sender!, shareAgain: false);

    /// <summary>True while a <see cref="Fit"/> is queued for the grid (rows arrive many at a time; one pass serves them all).</summary>
    private static readonly DependencyProperty FitQueuedProperty =
        DependencyProperty.RegisterAttached("FitQueued", typeof(bool), typeof(ListColumns), new PropertyMetadata(false));

    /// <summary>True when the queued <see cref="Fit"/> follows a change of the columns on show, so the star columns must share the room again.</summary>
    private static readonly DependencyProperty ShareAgainProperty =
        DependencyProperty.RegisterAttached("ShareAgain", typeof(bool), typeof(ListColumns), new PropertyMetadata(false));

    private static void FitLater(DataGrid grid, bool shareAgain)
    {
        if (shareAgain)
        {
            grid.SetValue(ShareAgainProperty, true);
        }

        if ((bool)grid.GetValue(FitQueuedProperty))
        {
            return;
        }

        grid.SetValue(FitQueuedProperty, true);
        grid.Dispatcher.BeginInvoke(
            () =>
            {
                var share = (bool)grid.GetValue(ShareAgainProperty);
                grid.ClearValue(FitQueuedProperty);
                grid.ClearValue(ShareAgainProperty);
                Fit(grid, share);
            },
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// While the columns on show need more than the table has (the name column what its names need, up to its cap; a detail column its minimum; a fixed column
    /// its width), hides the lowest-priority column still on show, the one with the highest <see cref="HideBelowProperty"/>. Then a name column that the grid
    /// had squeezed gets its width back. (WPF's DataGrid never overflows on its own: it takes the missing width out of the auto column, which is how names
    /// were cut while the table still looked full.) Columns come back when the table's width changes: <see cref="Apply"/> shows what the width allows, then
    /// this runs again.
    /// </summary>
    /// <param name="grid">The table.</param>
    /// <param name="shareAgain">
    /// True after the columns on show changed (a width change, a column bound to what the screen shows): the detail (star) columns then share the room afresh.
    /// WPF leaves them as they were when a column comes back: the returning column at its minimum and one neighbour paying for all of it.
    /// </param>
    public static void Fit(DataGrid grid, bool shareAgain = true)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (!grid.IsLoaded || grid.ActualWidth <= 0)
        {
            return;
        }

        var room = grid.ActualWidth - grid.BorderThickness.Left - grid.BorderThickness.Right - grid.Padding.Left - grid.Padding.Right;
        for (var pass = 0; pass < grid.Columns.Count; pass++)
        {
            var shown = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
            if (shown.Sum(Need) <= room + 0.5)
            {
                break;
            }

            var lowest = shown.Where(c => GetHideBelow(c) > 0).OrderByDescending(GetHideBelow).FirstOrDefault();
            if (lowest is null)
            {
                break; // nothing left that may fold away
            }

            lowest.Visibility = Visibility.Collapsed;
            shareAgain = true;
            grid.UpdateLayout();
        }

        // The grid hands freed width to the star columns only: an auto column it had squeezed is sized to its content again.
        foreach (var column in grid.Columns)
        {
            if (column.Visibility == Visibility.Visible && column.Width.IsAuto && column.ActualWidth + 0.5 < Need(column))
            {
                column.Width = DataGridLength.Auto;
                shareAgain = true;
            }
        }

        // With no detail column on show to take the spare width (a narrow table: only fixed columns are left beside the name), the name column takes it,
        // up to its cap, instead of leaving a blank strip at the table's right edge. It sizes to its names again as soon as a detail column is back.
        var detailOnShow = grid.Columns.Any(c => c.Visibility == Visibility.Visible && c.Width.IsStar && !IsStretched(c));
        foreach (var column in grid.Columns)
        {
            if (column.Visibility != Visibility.Visible)
            {
                continue;
            }

            if (!detailOnShow && column.Width.IsAuto)
            {
                column.SetValue(StretchedNeedProperty, Need(column));
                column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            }
            else if (detailOnShow && IsStretched(column))
            {
                column.ClearValue(StretchedNeedProperty);
                column.Width = DataGridLength.Auto;
                shareAgain = true;
            }
        }

        if (shareAgain)
        {
            foreach (var column in grid.Columns)
            {
                if (column.Visibility == Visibility.Visible && column.Width.IsStar && !IsStretched(column))
                {
                    column.Width = new DataGridLength(column.Width.Value, DataGridLengthUnitType.Star); // a fresh star width: the grid works the shares out again
                }
            }
        }
    }

    /// <summary>On a name column that is filling the table's spare width for now: what its names needed when it started to (NaN otherwise).</summary>
    private static readonly DependencyProperty StretchedNeedProperty =
        DependencyProperty.RegisterAttached("StretchedNeed", typeof(double), typeof(ListColumns), new PropertyMetadata(double.NaN));

    private static bool IsStretched(DataGridColumn column) => !double.IsNaN((double)column.GetValue(StretchedNeedProperty));

    /// <summary>The least width a column can be shown at without cutting what it is for.</summary>
    private static double Need(DataGridColumn column)
    {
        if (IsStretched(column))
        {
            return (double)column.GetValue(StretchedNeedProperty);
        }

        if (column.Width.IsStar)
        {
            return column.MinWidth;
        }

        if (column.Width.IsAbsolute)
        {
            return column.ReadLocalValue(DeclaredWidthProperty) is double declared && !double.IsNaN(declared) ? declared : column.Width.Value;
        }

        // Auto (or size to cells or header): what its content asked for, within the column's own limits.
        var desired = double.IsNaN(column.Width.DesiredValue) ? column.ActualWidth : column.Width.DesiredValue;
        return Math.Clamp(desired, column.MinWidth, double.IsInfinity(column.MaxWidth) ? double.MaxValue : column.MaxWidth);
    }
}
