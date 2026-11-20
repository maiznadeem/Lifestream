using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// The "Showing X-Y of N" line and the page buttons under a list. Its fade shows only while the table above has more rows below its view, so a row cut by the
/// footer reads as scrolled (design-qa-2 N19), and a list whose rows all fit ends on a clean last row.
/// </summary>
public partial class ListFooter : UserControl
{
    private ScrollViewer? _rows;

    public ListFooter()
    {
        InitializeComponent();
        Fade.Visibility = Visibility.Hidden;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        Detach();
        // The table is the footer's sibling in the list panel's grid (row 0); its rows scroll in the viewer of its template.
        if (Parent is not Panel panel || panel.Children.OfType<DataGrid>().FirstOrDefault() is not { } grid)
        {
            return;
        }

        grid.ApplyTemplate();
        _rows = FindViewer(grid);
        if (_rows is not null)
        {
            _rows.ScrollChanged += OnRowsScrolled;
            Update();
        }
    }

    private void Detach()
    {
        if (_rows is not null)
        {
            _rows.ScrollChanged -= OnRowsScrolled;
            _rows = null;
        }
    }

    private void OnRowsScrolled(object sender, ScrollChangedEventArgs e) => Update();

    private void Update()
    {
        var more = _rows is { } rows && rows.ExtentHeight - rows.VerticalOffset - rows.ViewportHeight > 0.5;
        Fade.Visibility = more ? Visibility.Visible : Visibility.Hidden;
    }

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
}
