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
            grid.Dispatcher.BeginInvoke(
                () =>
                {
                    if (grid.SelectedItem is { } item)
                    {
                        grid.UpdateLayout();
                        grid.ScrollIntoView(item);
                    }
                },
                DispatcherPriority.ContextIdle);
        }
    }
}
