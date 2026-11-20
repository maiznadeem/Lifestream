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
    /// <summary>The narrowest a column that sizes to its content or shares the room may get. A column declared with a width in DIP has that width, also below this.</summary>
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

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        KeepDeclaredWidths(); // before the grid works out its columns for the first time
    }

    /// <summary>
    /// A column declared with a width in DIP keeps it: the width is also its minimum, so the grid cannot take width from it. WPF's DataGrid takes width from
    /// fixed columns, down to their minimum, whenever it believes the table is too narrow, and gives it back only to star columns. A grouped grid believes
    /// that until its first row exists (it finds its scroll viewer, and so its own width, through a row), so the Time column of Appointments was shown at
    /// the grid's minimum of 64 and "09:00" ended in an ellipsis whenever the rows arrived after the first layout, as they do from a real server.
    /// </summary>
    private void KeepDeclaredWidths()
    {
        foreach (var column in Columns)
        {
            // Set on the column itself, so it also holds below the grid's minimum (a 60 DIP column was shown at 64 while the layout counted it as 60).
            if (column.Width.IsAbsolute && column.Width.Value <= column.MaxWidth
                && (column.ReadLocalValue(DataGridColumn.MinWidthProperty) is not double set || set < column.Width.Value))
            {
                column.MinWidth = column.Width.Value;
            }
        }
    }

    private void ApplyColumnDefaults()
    {
        if (!_defaultsApplied)
        {
            KeepDeclaredWidths(); // a grid built in code gets its columns after it is initialised
        }

        _defaultsApplied = Columns.Count > 0;
        foreach (var column in Columns)
        {
            if (column.MinWidth < MinimumColumnWidth && !column.Width.IsAbsolute)
            {
                column.MinWidth = MinimumColumnWidth;
            }

            if (column is DataGridTextColumn text && text.ReadLocalValue(DataGridBoundColumn.ElementStyleProperty) == DependencyProperty.UnsetValue && TryFindResource("DataGrid.TextCell") is Style style)
            {
                text.ElementStyle = style;
            }

            column.CanUserSort = !string.IsNullOrEmpty(column.SortMemberPath);

            // A header that sits over right-aligned numbers: the grid's own header style with the alignment changed (ColumnAssist.HeaderAlignment).
            var alignment = ColumnAssist.GetHeaderAlignment(column);
            if (alignment != HorizontalAlignment.Left && column.ReadLocalValue(DataGridColumn.HeaderStyleProperty) == DependencyProperty.UnsetValue && ColumnHeaderStyle is { } headerStyle)
            {
                var aligned = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), headerStyle);
                aligned.Setters.Add(new Setter(HorizontalContentAlignmentProperty, alignment));
                column.HeaderStyle = aligned;
            }
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
/// <c>controls:TruncationTooltip.IsEnabled="True"</c> on a TextBlock with <c>TextTrimming</c>. The property is inherited and set on the data grid cell style, so every text block in a cell has it.
/// </summary>
public static class TruncationTooltip
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(TruncationTooltip), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnChanged));

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

        // The same text in a block of its own, with all the width it wants and everything that decides its width taken from the block: the font, the
        // formatting mode, and the typography (kerning is off in the app, Theme/TextDefaults.cs; a numeric column has tabular figures). FormattedText
        // cannot be given a typography, so it measured kerned text against a block that draws it unkerned.
        var whole = new TextBlock
        {
            Text = block.Text,
            FontFamily = block.FontFamily,
            FontStyle = block.FontStyle,
            FontWeight = block.FontWeight,
            FontStretch = block.FontStretch,
            FontSize = block.FontSize,
            FlowDirection = block.FlowDirection,
            TextWrapping = TextWrapping.NoWrap,
        };
        System.Windows.Media.TextOptions.SetTextFormattingMode(whole, System.Windows.Media.TextOptions.GetTextFormattingMode(block));
        System.Windows.Documents.Typography.SetKerning(whole, System.Windows.Documents.Typography.GetKerning(block));
        System.Windows.Documents.Typography.SetNumeralAlignment(whole, System.Windows.Documents.Typography.GetNumeralAlignment(block));
        System.Windows.Media.VisualTreeHelper.SetRootDpi(whole, System.Windows.Media.VisualTreeHelper.GetDpi(block));
        whole.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return whole.DesiredSize.Width > block.ActualWidth + 0.5;
    }

    /// <summary>The whole text of a block, also when it sits in inlines (a Hyperlink in a name cell), which the Text property does not report to a binding.</summary>
    public static string FullText(TextBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text;
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.ToolTipOpening -= OnToolTipOpening;
        block.Loaded -= OnRefresh;
        block.DataContextChanged -= OnDataContextChanged;
        if ((bool)e.NewValue)
        {
            block.ToolTipOpening += OnToolTipOpening;
            block.Loaded += OnRefresh;
            block.DataContextChanged += OnDataContextChanged;
            Refresh(block);
        }
        else if (block.ReadLocalValue(FrameworkElement.ToolTipProperty) is string)
        {
            block.ClearValue(FrameworkElement.ToolTipProperty);
        }
    }

    private static void OnRefresh(object sender, RoutedEventArgs e) => Refresh((TextBlock)sender);

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Refresh((TextBlock)sender);

    private static void Refresh(TextBlock block)
    {
        var local = block.ReadLocalValue(FrameworkElement.ToolTipProperty);
        if (local == DependencyProperty.UnsetValue || local is string)
        {
            block.ToolTip = FullText(block); // a tooltip someone set on purpose is left alone
        }
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not TextBlock block)
        {
            return;
        }

        Refresh(block); // the text may have changed since the last refresh (a bound Run does not notify the block)
        if (!IsTrimmed(block))
        {
            e.Handled = true; // text fits: no tooltip
        }
    }
}

/// <summary>
/// Per-column settings of the shared data grid. <c>controls:ColumnAssist.HeaderAlignment="Right"</c> on a column puts its header over right-aligned
/// numbers; the grid applies it when it loads (a column has no style of its own to base one on, and a view cannot name the grid's header style
/// before it is in a window).
/// </summary>
public static class ColumnAssist
{
    public static readonly DependencyProperty HeaderAlignmentProperty =
        DependencyProperty.RegisterAttached("HeaderAlignment", typeof(HorizontalAlignment), typeof(ColumnAssist), new PropertyMetadata(HorizontalAlignment.Left));

    public static HorizontalAlignment GetHeaderAlignment(DependencyObject column) => (HorizontalAlignment)(column ?? throw new ArgumentNullException(nameof(column))).GetValue(HeaderAlignmentProperty);

    public static void SetHeaderAlignment(DependencyObject column, HorizontalAlignment value) => (column ?? throw new ArgumentNullException(nameof(column))).SetValue(HeaderAlignmentProperty, value);
}
