using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>A record's header, the screen's own actions, and the record's facts (direction A).</summary>
public partial class ActionDetailPane : UserControl
{
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(ActionDetailPane), new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(object), typeof(ActionDetailPane), new PropertyMetadata(null));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(nameof(Body), typeof(object), typeof(ActionDetailPane), new PropertyMetadata(null));

    public static readonly DependencyProperty ExtraProperty = DependencyProperty.Register(nameof(Extra), typeof(object), typeof(ActionDetailPane), new PropertyMetadata(null));

    public static readonly DependencyProperty FactsSourceProperty =
        DependencyProperty.Register(nameof(FactsSource), typeof(IEnumerable), typeof(ActionDetailPane), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowFrameProperty =
        DependencyProperty.Register(nameof(ShowFrame), typeof(bool), typeof(ActionDetailPane), new PropertyMetadata(true, (d, _) => ((ActionDetailPane)d).UpdateFrame()));

    public ActionDetailPane()
    {
        InitializeComponent();
        UpdateFrame();
    }

    /// <summary>The buttons of the record's actions: one Button.Primary, then secondary ones.</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    /// <summary>The header (avatar, name, tag); the record's name when null.</summary>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <summary>Content between the actions and the facts (a status card).</summary>
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    /// <summary>Content after the facts.</summary>
    public object? Extra { get => GetValue(ExtraProperty); set => SetValue(ExtraProperty, value); }

    /// <summary>The facts to show; the view model's DetailFacts when not set.</summary>
    public IEnumerable? FactsSource { get => (IEnumerable?)GetValue(FactsSourceProperty); set => SetValue(FactsSourceProperty, value); }

    /// <summary>Draws the pane as its own panel (on by default); off inside a side panel, which is the surface.</summary>
    public bool ShowFrame { get => (bool)GetValue(ShowFrameProperty); set => SetValue(ShowFrameProperty, value); }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FactsSourceProperty && Facts is not null && BindingOperations.GetBindingExpressionBase(Facts, FactGrid.FactsProperty) is null)
        {
            Facts.SetBinding(FactGrid.FactsProperty, new Binding(nameof(FactsSource)) { Source = this });
        }
    }

    private void UpdateFrame() => DetailPaneFrame.Apply(Frame, ShowFrame && !DetailPaneFrame.GetIsInPanel(this));

    internal void RefreshFrame() => UpdateFrame();
}
