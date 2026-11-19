using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lifestream.Client.Api;
using Lifestream.Contracts.Lookups;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// A list filter on one record (a donor), shown as a chip of the command bar: "+ Donor" opens the record search under it; once chosen, "Donor: name" with an x to remove
/// it. Bind <see cref="SelectedOption"/> two-way to the view model's filter.
/// </summary>
public partial class RecordFilterChip : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(RecordKind), typeof(RecordFilterChip), new PropertyMetadata(RecordKind.Donor, (d, _) => ((RecordFilterChip)d).OnKindChanged()));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(RecordFilterChip), new PropertyMetadata("Donor", (d, _) => ((RecordFilterChip)d).Refresh()));

    public static readonly DependencyProperty SelectedOptionProperty =
        DependencyProperty.Register(nameof(SelectedOption), typeof(RecordOption), typeof(RecordFilterChip),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((RecordFilterChip)d).OnSelectedChanged(e.NewValue as RecordOption)));

    private bool _syncing;

    public RecordFilterChip()
    {
        InitializeComponent();
        Picker.Kind = Kind;
        Picker.Label = Label;
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Controls.RecordPicker.SelectedOptionProperty, typeof(Controls.RecordPicker));
        descriptor.AddValueChanged(Picker, OnPickerChanged);
        Unloaded += (_, _) => SearchPopup.IsOpen = false;
        Refresh();
    }

    /// <summary>Which records the search finds.</summary>
    public RecordKind Kind { get => (RecordKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>The kind of record in words, for the chip ("Donor").</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>The chosen record, or null for no filter.</summary>
    public RecordOption? SelectedOption { get => (RecordOption?)GetValue(SelectedOptionProperty); set => SetValue(SelectedOptionProperty, value); }

    private void OnKindChanged() => Picker.Kind = Kind;

    private void OnSelectedChanged(RecordOption? option)
    {
        if (!_syncing && !Equals(Picker.SelectedOption, option))
        {
            _syncing = true;
            try
            {
                Picker.SelectedOption = option;
            }
            finally
            {
                _syncing = false;
            }
        }

        Refresh();
    }

    private void OnPickerChanged(object? sender, EventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            SelectedOption = Picker.SelectedOption;
        }
        finally
        {
            _syncing = false;
        }

        if (Picker.SelectedOption is not null)
        {
            SearchPopup.IsOpen = false;
            ChipButton.Focus();
        }

        Refresh();
    }

    private void Refresh()
    {
        if (ChipText is null)
        {
            return;
        }

        var chosen = SelectedOption;
        ChipText.Text = chosen is null ? Label : $"{Label}: {chosen.Title}";
        PlusIcon.Visibility = chosen is null ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = chosen is null ? Visibility.Collapsed : Visibility.Visible;
        PopupLabel.Text = $"Show one {Label.ToLowerInvariant()}'s records";
        Picker.Label = Label;
        AutomationProperties.SetName(ChipButton, chosen is null ? $"Filter by {Label.ToLowerInvariant()}" : $"{Label} filter: {chosen.Title}. Change");
        var clearName = $"Remove the {Label.ToLowerInvariant()} filter";
        AutomationProperties.SetName(ClearButton, clearName);
        ClearButton.ToolTip = clearName;
        if (chosen is null)
        {
            ChipButton.ClearValue(StyleProperty);
            ChipButton.SetResourceReference(StyleProperty, "Button.Chip");
        }
        else
        {
            // The "set" look of a chip: filled, as the blood group chip once a value is chosen.
            ChipButton.SetResourceReference(StyleProperty, "Button.Secondary");
        }
    }

    // Opens the search; with a record chosen it shows that record, and the picker's Change starts a new search (the filter stays until another is chosen).
    private void OnChipClick(object sender, RoutedEventArgs e) => SearchPopup.IsOpen = true;

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        SelectedOption = null;
        ChipButton.Focus();
    }

    private void OnPopupOpened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (Picker.FindName("SearchBox") is TextBox box)
            {
                box.Focus();
                Keyboard.Focus(box);
            }
        }, DispatcherPriority.Input);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Escape && SearchPopup.IsOpen)
        {
            SearchPopup.IsOpen = false;
            ChipButton.Focus();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
