using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Common;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>The command bar of a list screen; Ctrl+F from the shell puts the keyboard focus in the search box (SHELL-34).</summary>
public partial class ListToolbar : UserControl
{
    public static readonly DependencyProperty SearchPlaceholderProperty =
        DependencyProperty.Register(nameof(SearchPlaceholder), typeof(string), typeof(ListToolbar), new PropertyMetadata("Search"));

    public static readonly DependencyProperty SearchWidthProperty =
        DependencyProperty.Register(nameof(SearchWidth), typeof(double), typeof(ListToolbar), new PropertyMetadata(320.0));

    public static readonly DependencyProperty ShowBloodTypeFilterProperty =
        DependencyProperty.Register(nameof(ShowBloodTypeFilter), typeof(bool), typeof(ListToolbar), new PropertyMetadata(true));

    public static readonly DependencyProperty FiltersProperty =
        DependencyProperty.Register(nameof(Filters), typeof(object), typeof(ListToolbar), new PropertyMetadata(null));

    public static readonly DependencyProperty TrailingProperty =
        DependencyProperty.Register(nameof(Trailing), typeof(object), typeof(ListToolbar), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowAddButtonProperty =
        DependencyProperty.Register(nameof(ShowAddButton), typeof(bool), typeof(ListToolbar), new PropertyMetadata(false));

    private ViewModelBase? _model;

    public ListToolbar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    /// <summary>The search field's placeholder, naming what it searches ("Search name, phone or donor ID").</summary>
    public string SearchPlaceholder { get => (string)GetValue(SearchPlaceholderProperty); set => SetValue(SearchPlaceholderProperty, value); }

    /// <summary>The search field's width in DIP.</summary>
    public double SearchWidth { get => (double)GetValue(SearchWidthProperty); set => SetValue(SearchWidthProperty, value); }

    /// <summary>Shows the "+ Blood group" chip (bound to the list's BloodTypeFilter); true by default.</summary>
    public bool ShowBloodTypeFilter { get => (bool)GetValue(ShowBloodTypeFilterProperty); set => SetValue(ShowBloodTypeFilterProperty, value); }

    /// <summary>The screen's own filter chips, after the blood group chip.</summary>
    public object? Filters { get => GetValue(FiltersProperty); set => SetValue(FiltersProperty, value); }

    /// <summary>Content at the right end of the bar.</summary>
    public object? Trailing { get => GetValue(TrailingProperty); set => SetValue(TrailingProperty, value); }

    /// <summary>Shows the primary "Add" button at the right end (for a screen without a page header); false by default.</summary>
    public bool ShowAddButton { get => (bool)GetValue(ShowAddButtonProperty); set => SetValue(ShowAddButtonProperty, value); }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _model = e.NewValue as ViewModelBase;
        if (_model is not null)
        {
            _model.FocusSearchRequested += OnFocusSearchRequested;
        }
    }

    private void Detach()
    {
        if (_model is not null)
        {
            _model.FocusSearchRequested -= OnFocusSearchRequested;
            _model = null;
        }
    }

    private void OnFocusSearchRequested(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
