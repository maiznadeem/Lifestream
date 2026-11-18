using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Common;

namespace Lifestream.Desktop.Features.Stock;

/// <summary>The Stock screen: one tile per blood type.</summary>
public partial class StockView : UserControl
{
    private ViewModelBase? _model;

    public StockView()
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

    /// <summary>Ctrl+F: the stock screen has no search box, so the first filter takes focus.</summary>
    private void OnFocusSearchRequested(object? sender, EventArgs e) => BloodTypeBox.Focus();
}
