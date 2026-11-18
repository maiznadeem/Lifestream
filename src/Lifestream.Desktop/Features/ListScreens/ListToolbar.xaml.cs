using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Common;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>The search and filter row of a list screen; Ctrl+F from the shell puts the keyboard focus in the search box (SHELL-34).</summary>
public partial class ListToolbar : UserControl
{
    private ViewModelBase? _model;

    public ListToolbar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

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
