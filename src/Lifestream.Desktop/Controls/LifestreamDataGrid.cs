using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lifestream.Client.Common;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// The data grid every list screen uses (SHELL-38, SHELL-39). Rows are virtualised and recycled, so 10,000 items create a
/// screenful of <see cref="DataGridRow"/>s. Columns size to their content with a minimum width, truncated text shows a tooltip,
/// sorting is requested from the server through <see cref="SortCommand"/> (the grid shows the indicator but does not reorder
/// the loaded rows), and the sort stays across filtering and paging because it is a property of the view model, not of the items.
/// </summary>
public sealed class LifestreamDataGrid : DataGrid
{
    /// <summary>The narrowest a column may get.</summary>
    public const double MinimumColumnWidth = 64;

    public static readonly DependencyProperty SortCommandProperty =
        DependencyProperty.Register(nameof(SortCommand), typeof(ICommand), typeof(LifestreamDataGrid), new FrameworkPropertyMetadata(null));

    /// <summary>The <c>SortMemberPath</c> of the sorted column, or null.</summary>
    public static readonly DependencyProperty SortColumnProperty =
        DependencyProperty.Register(nameof(SortColumn), typeof(string), typeof(LifestreamDataGrid), new FrameworkPropertyMetadata(null, (d, _) => ((LifestreamDataGrid)d).ShowSortIndicator()));

    public static readonly DependencyProperty SortDescendingProperty =
        DependencyProperty.Register(nameof(SortDescending), typeof(bool), typeof(LifestreamDataGrid), new FrameworkPropertyMetadata(false, (d, _) => ((LifestreamDataGrid)d).ShowSortIndicator()));

    public LifestreamDataGrid()
    {
        AutoGenerateColumns = false;
        CanUserAddRows = false;
        CanUserDeleteRows = false;
        CanUserReorderColumns = false;
        CanUserResizeRows = false;
        IsReadOnly = true;
        SelectionMode = DataGridSelectionMode.Single;
        SelectionUnit = DataGridSelectionUnit.FullRow;
        HeadersVisibility = DataGridHeadersVisibility.Column;
        GridLinesVisibility = DataGridGridLinesVisibility.None;
        RowHeaderWidth = 0;
        EnableRowVirtualization = true;
        EnableColumnVirtualization = true;
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(this, true);
        Loaded += (_, _) =>
        {
            ApplyColumnDefaults();
            BeginScrollRestore();
        };
        DataContextChanged += (_, _) => BeginScrollRestore();
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        // Once the user scrolls themselves, a pending restore is abandoned.
        PreviewMouseWheel += (_, _) => _restoreTarget = null;
        PreviewKeyDown += (_, _) => _restoreTarget = null;
        PreviewMouseLeftButtonDown += (_, _) => _restoreTarget = null;
        Sorting += (_, e) =>
        {
            // Server-side sort: remember the request, show the indicator, and let the view model fetch the sorted page.
            e.Handled = true;
            var path = e.Column.SortMemberPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var descending = SortColumn == path && !SortDescending;
            SortColumn = path;
            SortDescending = descending;
            var expression = descending ? "-" + path : path;
            if (SortCommand?.CanExecute(expression) == true)
            {
                SortCommand.Execute(expression);
            }
        };
    }

    public ICommand? SortCommand { get => (ICommand?)GetValue(SortCommandProperty); set => SetValue(SortCommandProperty, value); }

    public string? SortColumn { get => (string?)GetValue(SortColumnProperty); set => SetValue(SortColumnProperty, value); }

    public bool SortDescending { get => (bool)GetValue(SortDescendingProperty); set => SetValue(SortDescendingProperty, value); }

    protected override void OnLoadingRow(DataGridRowEventArgs e)
    {
        base.OnLoadingRow(e);
        // Before the first row builds its cells, so no cell is built with the default text style.
        if (!_defaultsApplied)
        {
            ApplyColumnDefaults();
        }
    }

    private bool _defaultsApplied;
    private double? _restoreTarget;
    private ScrollViewer? _viewer;

    /// <summary>A list screen whose view model remembers the scroll position (<see cref="IScrollMemory"/>) gets it back here (SHELL-24).</summary>
    private void BeginScrollRestore()
    {
        _restoreTarget = DataContext is IScrollMemory { ScrollOffset: > 0 } memory ? memory.ScrollOffset : null;
        TryRestoreScroll();
    }

    private ScrollViewer? Viewer => _viewer ??= FindViewer(this);

    private static ScrollViewer? FindViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindViewer(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void TryRestoreScroll()
    {
        if (_restoreTarget is { } target && Viewer is { } viewer && viewer.ScrollableHeight >= target)
        {
            _restoreTarget = null;
            viewer.ScrollToVerticalOffset(target);
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer || !ReferenceEquals(viewer, Viewer))
        {
            return;
        }

        if (_restoreTarget is not null)
        {
            // The rows of this visit are still arriving: wait until the old position exists instead of recording the top.
            TryRestoreScroll();
            return;
        }

        if (DataContext is IScrollMemory memory)
        {
            memory.ScrollOffset = viewer.VerticalOffset;
        }
    }

    private void ApplyColumnDefaults()
    {
        _defaultsApplied = Columns.Count > 0;
        foreach (var column in Columns)
        {
            if (column.MinWidth < MinimumColumnWidth)
            {
                column.MinWidth = MinimumColumnWidth;
            }

            if (column is DataGridTextColumn text && text.ReadLocalValue(DataGridBoundColumn.ElementStyleProperty) == DependencyProperty.UnsetValue && TryFindResource("DataGrid.TextCell") is Style style)
            {
                text.ElementStyle = style;
            }

            column.CanUserSort = !string.IsNullOrEmpty(column.SortMemberPath);
        }

        ShowSortIndicator();
    }

    private void ShowSortIndicator()
    {
        foreach (var column in Columns)
        {
            column.SortDirection = column.SortMemberPath == SortColumn && SortColumn is not null
                ? SortDescending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending
                : null;
        }
    }

    protected override void OnItemsSourceChanged(System.Collections.IEnumerable oldValue, System.Collections.IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);
        ShowSortIndicator(); // the indicator belongs to the view model's sort, so a new page or filter keeps it
    }
}

/// <summary>
/// Shows the full text of a trimmed (truncated) cell as a tooltip and nothing otherwise (SHELL-39). Set
/// <c>controls:TruncationTooltip.IsEnabled="True"</c> on a TextBlock with <c>TextTrimming</c>.
/// </summary>
public static class TruncationTooltip
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(TruncationTooltip), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(IsEnabledProperty, value);

    /// <summary>True when the text of the block does not fit its width and is shown with an ellipsis.</summary>
    public static bool IsTrimmed(TextBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.TextTrimming == TextTrimming.None || block.ActualWidth <= 0)
        {
            return false;
        }

        var formatted = new System.Windows.Media.FormattedText(
            block.Text,
            System.Globalization.CultureInfo.CurrentUICulture,
            block.FlowDirection,
            new System.Windows.Media.Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize,
            System.Windows.Media.Brushes.Black,
            System.Windows.Media.VisualTreeHelper.GetDpi(block).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace > block.ActualWidth + 0.5;
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.ToolTipOpening -= OnToolTipOpening;
        if ((bool)e.NewValue)
        {
            block.ToolTipOpening += OnToolTipOpening;
            block.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = block });
        }
        else
        {
            block.ClearValue(FrameworkElement.ToolTipProperty);
        }
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is TextBlock block && !IsTrimmed(block))
        {
            e.Handled = true; // text fits: no tooltip
        }
    }
}
