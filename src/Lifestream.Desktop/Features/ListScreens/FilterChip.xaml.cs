using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// A filter chip of a list toolbar (direction A): dashed "+ Label" while the first choice ("All ...") is selected, "Label: value" on a filled chip otherwise. The choices
/// open under the chip; the selection is applied only when the user picks a choice (a click or Enter), so moving through the list with the arrow keys does not reload
/// the list on every step.
/// </summary>
public partial class FilterChip : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(FilterChip), new PropertyMetadata(string.Empty, (d, _) => ((FilterChip)d).UpdateLook()));

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(FilterChip), new PropertyMetadata(null, (d, _) => ((FilterChip)d).UpdateLook()));

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(FilterChip), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((FilterChip)d).UpdateLook()));

    public static readonly DependencyProperty DisplayMemberPathProperty =
        DependencyProperty.Register(nameof(DisplayMemberPath), typeof(string), typeof(FilterChip), new PropertyMetadata(null, (d, _) => ((FilterChip)d).UpdateLook()));

    private static readonly DependencyPropertyKey IsSetPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsSet), typeof(bool), typeof(FilterChip), new PropertyMetadata(false));

    /// <summary>True while a value other than the first ("All ...") is chosen.</summary>
    public static readonly DependencyProperty IsSetProperty = IsSetPropertyKey.DependencyProperty;

    public FilterChip()
    {
        InitializeComponent();
        UpdateLook();
    }

    /// <summary>What the chip filters by, in sentence case ("Blood group").</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>The choices; the first one means "no filter".</summary>
    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    /// <summary>The chosen value (two-way).</summary>
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }

    /// <summary>The property of a choice that is shown.</summary>
    public string? DisplayMemberPath { get => (string?)GetValue(DisplayMemberPathProperty); set => SetValue(DisplayMemberPathProperty, value); }

    public bool IsSet => (bool)GetValue(IsSetProperty);

    /// <summary>The chip button (for focus and tests).</summary>
    public Button Button => ChipButton;

    /// <summary>True while the choices are open.</summary>
    public bool IsOpen => ChoicesPopup.IsOpen;

    /// <summary>Opens the choices with the current one selected and focused.</summary>
    public void Open()
    {
        ChoicesList.DisplayMemberPath = DisplayMemberPath;
        ChoicesList.ItemsSource = ItemsSource;
        ChoicesList.SelectedItem = SelectedItem ?? First();
        ChoicesPopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            ChoicesList.UpdateLayout();
            var index = Math.Max(0, ChoicesList.SelectedIndex);
            (ChoicesList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
        }));
    }

    /// <summary>Applies a choice (as a click on it does) and closes the list.</summary>
    public void Pick(object? choice)
    {
        if (choice is not null)
        {
            SelectedItem = choice;
        }

        Close();
    }

    private object? First()
    {
        if (ItemsSource is null)
        {
            return null;
        }

        foreach (var item in ItemsSource)
        {
            return item;
        }

        return null;
    }

    private void Close()
    {
        ChoicesPopup.IsOpen = false;
        ChipButton.Focus();
    }

    private string DisplayOf(object? item)
    {
        if (item is null)
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(DisplayMemberPath))
        {
            return item.ToString() ?? string.Empty;
        }

        // Read the display member through a one-off binding, as an ItemsControl would.
        var probe = new TextBlock();
        BindingOperations.SetBinding(probe, TextBlock.TextProperty, new Binding(DisplayMemberPath) { Source = item, Mode = BindingMode.OneTime });
        return probe.Text;
    }

    private void UpdateLook()
    {
        if (ChipText is null)
        {
            return;
        }

        var first = First();
        var isSet = SelectedItem is not null && first is not null && !Equals(SelectedItem, first);
        SetValue(IsSetPropertyKey, isSet);
        ChipText.Text = isSet ? $"{Label}: {DisplayOf(SelectedItem)}" : Label;
        PlusIcon.Visibility = isSet ? Visibility.Collapsed : Visibility.Visible;
        ChevronIcon.Visibility = isSet ? Visibility.Visible : Visibility.Collapsed;
        ChipButton.SetResourceReference(StyleProperty, isSet ? "Button.Secondary" : "Button.Chip");
        AutomationProperties.SetName(ChipButton, isSet ? $"{Label} filter: {DisplayOf(SelectedItem)}" : $"{Label} filter: {DisplayOf(first)}");
        AutomationProperties.SetHelpText(ChipButton, "Opens the choices");
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (ChoicesPopup.IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    private void OnChipKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || e.SystemKey == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            Open();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            case Key.Space:
                Pick(ChoicesList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Escape:
            case Key.Tab:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ChoicesList, source) is ListBoxItem item)
        {
            Pick(item.DataContext);
            e.Handled = true;
        }
    }

    private void OnPopupClosed(object? sender, EventArgs e) => ChoicesList.ItemsSource = null;
}
