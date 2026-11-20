using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lifestream.Client.Shell;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Shell;

/// <summary>
/// The command palette (Ctrl+K): a 640 DIP flyout under the title bar with a query field and grouped results. Up and Down move through the results
/// (the field keeps focus), Enter runs the selected one, Esc or a click outside closes it and focus goes back where it was. It opens with a 180 ms
/// fade and scale from 98 % (none when Windows animations are off).
/// </summary>
public partial class CommandPaletteView : UserControl
{
    private CommandPaletteViewModel? _model;
    private IInputElement? _returnFocus;

    public CommandPaletteView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>True while the palette is shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = e.NewValue as CommandPaletteViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen))
        {
            if (_model!.IsOpen)
            {
                Show();
            }
            else
            {
                Hide();
            }
        }
        else if (e.PropertyName == nameof(CommandPaletteViewModel.SelectedIndex) && Results.SelectedItem is { } item)
        {
            Results.ScrollIntoView(item);
        }
    }

    private void Show()
    {
        _returnFocus = Keyboard.FocusedElement;
        Visibility = Visibility.Visible;
        Hint.Visibility = Visibility.Visible;
        var duration = MotionSettings.Standard;
        Surface.RenderTransformOrigin = new Point(0.5, 0);
        Surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        OpenScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.98, 1, duration) { EasingFunction = ease });
        OpenScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.98, 1, duration) { EasingFunction = ease });
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
        });
    }

    private void Hide()
    {
        Visibility = Visibility.Collapsed;
        if (_returnFocus is UIElement { IsVisible: true } element)
        {
            element.Focus();
        }

        _returnFocus = null;
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Hint.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                _model.Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                _model.Move(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                _model.Move(5);
                e.Handled = true;
                break;
            case Key.PageUp:
                _model.Move(-5);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = _model.RunSelectedAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                _model.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (_model is not null && (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>() is { DataContext: PaletteItem item })
        {
            _ = _model.RunAsync(item);
        }
    }

    private void OnLayerMouseDown(object sender, MouseButtonEventArgs e) => _model?.Close();

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}

/// <summary>"Ctrl+D" to the keys ["Ctrl", "D"], for the shortcut keycaps in the palette.</summary>
public sealed class ShortcutKeysConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } text ? text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visual-tree helpers.</summary>
internal static class VisualTreeExtensions
{
    public static T? FindAncestor<T>(this DependencyObject element)
        where T : DependencyObject
    {
        for (var current = element; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is T found)
            {
                return found;
            }
        }

        return null;
    }
}
