using System.Windows;
using System.Windows.Controls;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// Columns that fold away when the table gets narrow (direction A, "column priority"): set <c>lists:ListColumns.HideBelow="700"</c> on a column and
/// <c>lists:ListColumns.IsResponsive="True"</c> on its grid. The column is hidden while the grid is narrower than that many DIP (a compact window, or the
/// side panel taking its share), and shown again when it is wider.
/// </summary>
public static class ListColumns
{
    public static readonly DependencyProperty HideBelowProperty =
        DependencyProperty.RegisterAttached("HideBelow", typeof(double), typeof(ListColumns), new PropertyMetadata(0.0));

    public static readonly DependencyProperty IsResponsiveProperty =
        DependencyProperty.RegisterAttached("IsResponsive", typeof(bool), typeof(ListColumns), new PropertyMetadata(false, OnIsResponsiveChanged));

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
            var below = GetHideBelow(column);
            if (below <= 0)
            {
                continue;
            }

            var visibility = width < below ? Visibility.Collapsed : Visibility.Visible;
            if (column.Visibility != visibility)
            {
                column.Visibility = visibility;
            }
        }
    }

    private static void OnIsResponsiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        grid.SizeChanged -= OnSizeChanged;
        if ((bool)e.NewValue)
        {
            grid.SizeChanged += OnSizeChanged;
            Apply(grid, grid.ActualWidth);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            Apply((DataGrid)sender, e.NewSize.Width);
        }
    }
}
