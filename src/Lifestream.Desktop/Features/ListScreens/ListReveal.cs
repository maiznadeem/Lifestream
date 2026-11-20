using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Lifestream.Client.Features.ListScreens;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// Brings the selected row of a list into view when its view model asks (<see cref="IRevealsSelection"/>): set <c>lists:ListReveal.IsEnabled="True"</c> on the table.
/// A record another screen opened by id, or one just saved, is selected by the view model; its row may be far down the page, and a selection made from code does
/// not scroll the table. A row the user chooses is under the pointer or the keyboard already, so nothing is asked then.
/// </summary>
public static class ListReveal
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(ListReveal), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached("Source", typeof(Subscription), typeof(ListReveal), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        grid.DataContextChanged -= OnDataContextChanged;
        Follow(grid, null);
        if ((bool)e.NewValue)
        {
            grid.DataContextChanged += OnDataContextChanged;
            Follow(grid, grid.DataContext as IRevealsSelection);
        }
    }

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Follow((DataGrid)sender, e.NewValue as IRevealsSelection);

    private static void Follow(DataGrid grid, IRevealsSelection? source)
    {
        (grid.GetValue(SourceProperty) as Subscription)?.Stop();
        grid.SetValue(SourceProperty, source is null ? null : new Subscription(grid, source));
    }

    /// <summary>One table listening to one view model; the handler holds the table weakly, so a view model that outlives its view does not keep it.</summary>
    private sealed class Subscription
    {
        private readonly WeakReference<DataGrid> _grid;
        private readonly IRevealsSelection _source;

        public Subscription(DataGrid grid, IRevealsSelection source)
        {
            _grid = new WeakReference<DataGrid>(grid);
            _source = source;
            _source.RevealSelectionRequested += OnRequested;
        }

        public void Stop() => _source.RevealSelectionRequested -= OnRequested;

        private void OnRequested(object? sender, EventArgs e)
        {
            if (!_grid.TryGetTarget(out var grid))
            {
                Stop();
                return;
            }

            // After the rows of the page have been laid out (and after the table has put back a remembered scroll position): the reveal is the newer wish.
            grid.Dispatcher.BeginInvoke(() => Reveal(grid), DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>
    /// Brings the selected row into view, clear of the footer's fade. ScrollIntoView stops with a row from further down on the list's bottom edge, which is
    /// where the footer draws its 20 px fade while more rows lie below (<see cref="ListFooter"/>): after a booking the new row read as half hidden
    /// (night-iso's real-app walk; measured: row 786-830, viewport to 830, fade over its last 20 px). The list then scrolls on by the overlap.
    /// </summary>
    internal static void Reveal(DataGrid grid)
    {
        if (grid.SelectedItem is not { } item)
        {
            return;
        }

        grid.UpdateLayout();
        grid.ScrollIntoView(item);
        for (var step = 0; step < 5; step++)
        {
            grid.UpdateLayout();
            if (Descendants(grid).OfType<ScrollViewer>().FirstOrDefault(v => ReferenceEquals(v.TemplatedParent, grid)) is not { } viewer
                || Descendants(viewer).OfType<ScrollContentPresenter>().FirstOrDefault() is not { } viewport
                || Descendants(grid).OfType<DataGridRow>().FirstOrDefault(r => r.IsVisible && ReferenceEquals(r.Item, item)) is not { } row)
            {
                return;
            }

            var bounds = row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
            var moreBelow = viewer.ExtentHeight - viewer.VerticalOffset - viewer.ViewportHeight > 0.5;
            var overlap = bounds.Bottom - (viewport.ActualHeight - ListFooter.FadeHeight);
            if (!moreBelow || overlap <= 0.5 || bounds.Top - overlap < 0)
            {
                return; // clear of the fade, no fade (the end of the list), or a row too tall to lift without cutting its top
            }

            if (Math.Abs(viewer.ViewportHeight - viewport.ActualHeight) < 1)
            {
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset + overlap); // the list scrolls by pixels: by exactly the overlap
            }
            else
            {
                viewer.LineDown(); // by items: a line at a time until the row is clear
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var inner in Descendants(child))
            {
                yield return inner;
            }
        }
    }
}
