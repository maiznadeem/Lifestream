using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// The keyboard of a list beside its side panel (direction A): set <c>lists:ListKeys.Panel="{Binding ElementName=Panel}"</c> on the table. Enter on a selected row moves the
/// focus into the open panel (its first control, the primary action), and Esc in the table runs the list's CloseDetailCommand, as Esc does inside the panel.
/// </summary>
public static class ListKeys
{
    public static readonly DependencyProperty PanelProperty =
        DependencyProperty.RegisterAttached("Panel", typeof(FrameworkElement), typeof(ListKeys), new PropertyMetadata(null, OnPanelChanged));

    public static FrameworkElement? GetPanel(DependencyObject element) => (FrameworkElement?)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(PanelProperty);

    public static void SetPanel(DependencyObject element, FrameworkElement? value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(PanelProperty, value);

    private static void OnPanelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is not null)
        {
            element.PreviewKeyDown += OnPreviewKeyDown;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement table || GetPanel(table) is not { } panel || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (e.Key is Key.Enter or Key.Return && table is DataGrid { SelectedItem: not null } && panel.IsVisible)
        {
            // The detail may still be loading: wait for the layout, then focus the first control the panel offers.
            // The panel's content first (its header row starts with the close button).
            var content = (panel as ContentControl)?.Content as DependencyObject ?? panel;
            panel.Dispatcher.BeginInvoke(() => (FirstFocusable(content) ?? FirstFocusable(panel))?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && panel.IsVisible && table.DataContext is { } model
                 && model.GetType().GetProperty("CloseDetailCommand")?.GetValue(model) is ICommand close && close.CanExecute(null))
        {
            close.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>The first visible, enabled tab stop inside an element, depth first in visual order (the panel's primary action).</summary>
    public static Control? FirstFocusable(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { IsVisible: false })
            {
                continue;
            }

            if (child is Control { Focusable: true, IsTabStop: true, IsEnabled: true } control)
            {
                return control;
            }

            if (FirstFocusable(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
