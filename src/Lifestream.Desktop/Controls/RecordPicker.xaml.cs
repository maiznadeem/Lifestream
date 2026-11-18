using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Lifestream.Client.Api;
using Lifestream.Client.Lookups;
using Lifestream.Contracts.Lookups;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// A searchable async combo for donor, patient, staff, appointment and request (SHELL-37): <c>&lt;controls:RecordPicker Kind="Donor"
/// Label="Donor" SelectedId="{Binding DonorId, Mode=TwoWay}"/&gt;</c>. It replaces every free-typed id field. All behaviour is in
/// <see cref="RecordPickerViewModel"/>; this class only connects it to the dependency properties.
/// </summary>
public partial class RecordPicker : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(RecordKind), typeof(RecordPicker), new FrameworkPropertyMetadata(RecordKind.Donor));

    public static readonly DependencyProperty SelectedIdProperty =
        DependencyProperty.Register(nameof(SelectedId), typeof(int?), typeof(RecordPicker), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIdChanged));

    public static readonly DependencyProperty SelectedOptionProperty =
        DependencyProperty.Register(nameof(SelectedOption), typeof(RecordOption), typeof(RecordPicker), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedOptionChanged));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(RecordPicker), new FrameworkPropertyMetadata(string.Empty));

    private RecordPickerViewModel? _viewModel;
    private bool _syncing;

    public RecordPicker()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ShowResultsInline();
            EnsureViewModel();
        };
        Unloaded += (_, _) => _viewModel?.Dispose();
    }

    /// <summary>Builds the view model for a kind. The app sets it from DI at startup; tests set it to a fake-backed factory.</summary>
    public static Func<RecordKind, RecordPickerViewModel>? ViewModelFactory { get; set; }

    public RecordKind Kind { get => (RecordKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public int? SelectedId { get => (int?)GetValue(SelectedIdProperty); set => SetValue(SelectedIdProperty, value); }

    public RecordOption? SelectedOption { get => (RecordOption?)GetValue(SelectedOptionProperty); set => SetValue(SelectedOptionProperty, value); }

    /// <summary>The visible label; also the accessible name of the search box.</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>
    /// Shows the list of matches inside the control instead of in a popup window. Off-screen tests set it so that no real window
    /// is ever opened; the content, bindings and states are the same.
    /// </summary>
    public static bool InlineResults { get; set; }

    private void ShowResultsInline()
    {
        if (!InlineResults || ResultsPopup.Child is not { } child)
        {
            return;
        }

        ResultsPopup.Child = null;
        ResultsPopup.IsOpen = false;
        if (child is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Top;
            element.Margin = new Thickness(0, 44, 0, 0);
            element.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(RecordPickerViewModel.IsOpen)) { Converter = new BooleanToVisibilityConverter() });
        }

        SearchMode.Children.Add(child);
    }

    /// <summary>The view model behind this picker (null until the control has loaded).</summary>
    public RecordPickerViewModel? ViewModel => _viewModel;

    private void EnsureViewModel()
    {
        if (_viewModel is not null || ViewModelFactory is null)
        {
            return;
        }

        _viewModel = ViewModelFactory(Kind);
        _viewModel.Label = Label;
        _viewModel.PropertyChanged += OnViewModelChanged;
        Root.DataContext = _viewModel;
        if (SelectedOption is { } option)
        {
            _viewModel.Select(option);
        }
        else if (SelectedId is { } id)
        {
            _viewModel.Select(new RecordOption(id, $"Record {id}", null));
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecordPickerViewModel.Selected) || _syncing || _viewModel is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            SelectedOption = _viewModel.Selected;
            SelectedId = _viewModel.SelectedId;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void OnSelectedIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (RecordPicker)d;
        if (picker._syncing || picker._viewModel is null)
        {
            return;
        }

        picker._syncing = true;
        try
        {
            if (e.NewValue is int id)
            {
                if (picker._viewModel.SelectedId != id)
                {
                    picker._viewModel.Select(new RecordOption(id, $"Record {id}", null));
                }
            }
            else
            {
                picker._viewModel.Selected = null;
            }
        }
        finally
        {
            picker._syncing = false;
        }
    }

    private static void OnSelectedOptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (RecordPicker)d;
        if (picker._syncing || picker._viewModel is null)
        {
            return;
        }

        picker._syncing = true;
        try
        {
            if (e.NewValue is RecordOption option)
            {
                picker._viewModel.Select(option);
                picker.SelectedId = option.Id;
            }
            else
            {
                picker._viewModel.Selected = null;
                picker.SelectedId = null;
            }
        }
        finally
        {
            picker._syncing = false;
        }
    }

    private void OnSearchGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_viewModel is { HasSelection: false } vm && vm.Options.Count == 0 && !vm.IsSearching)
        {
            _ = vm.SearchNowAsync(); // show the first page of options as soon as the box is entered
            vm.IsOpen = true;
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when ResultsList.Items.Count > 0:
                _viewModel!.IsOpen = true;
                ResultsList.SelectedIndex = Math.Max(ResultsList.SelectedIndex, 0);
                (ResultsList.ItemContainerGenerator.ContainerFromIndex(ResultsList.SelectedIndex) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Enter when ResultsList.Items.Count > 0:
                Choose(ResultsList.SelectedItem as RecordOption ?? ResultsList.Items[0] as RecordOption);
                e.Handled = true;
                break;
            case Key.Escape when _viewModel is { IsOpen: true }:
                _viewModel.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Choose(ResultsList.SelectedItem as RecordOption);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel!.IsOpen = false;
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void OnListChosen(object sender, MouseButtonEventArgs e) => Choose(ResultsList.SelectedItem as RecordOption);

    private void Choose(RecordOption? option)
    {
        if (option is not null)
        {
            _viewModel!.Select(option);
        }
    }
}

/// <summary>Shows an element only when a length is zero (the hint of an empty search box).</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int length && length == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
