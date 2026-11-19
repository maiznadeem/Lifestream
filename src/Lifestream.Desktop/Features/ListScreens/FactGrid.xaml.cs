using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// The "Details" block of a detail panel: label and value lines (<see cref="Lifestream.Client.Features.ListScreens.DetailFact"/>) in two columns. Without <see cref="Facts"/>
/// it shows the list view model's DetailFacts; the heading hides when there is nothing to show.
/// </summary>
public partial class FactGrid : UserControl
{
    public static readonly DependencyProperty FactsProperty =
        DependencyProperty.Register(nameof(Facts), typeof(IEnumerable), typeof(FactGrid), new PropertyMetadata(null, (d, _) => ((FactGrid)d).Rebind()));

    public static readonly DependencyProperty HeadingProperty =
        DependencyProperty.Register(nameof(Heading), typeof(string), typeof(FactGrid), new PropertyMetadata("Details"));

    public FactGrid()
    {
        InitializeComponent();
        Rebind();
        ((System.Collections.Specialized.INotifyCollectionChanged)List.Items).CollectionChanged += (_, _) => UpdateHeading();
        Loaded += (_, _) => Rebind();
    }

    /// <summary>The facts to show; null shows the data context's DetailFacts.</summary>
    public IEnumerable? Facts { get => (IEnumerable?)GetValue(FactsProperty); set => SetValue(FactsProperty, value); }

    /// <summary>The heading above the facts ("Details").</summary>
    public string Heading { get => (string)GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }

    private void Rebind()
    {
        if (ReadLocalValue(FactsProperty) == DependencyProperty.UnsetValue && BindingOperations.GetBindingExpressionBase(this, FactsProperty) is null)
        {
            List.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("DetailFacts"));
        }
        else
        {
            List.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(Facts)) { Source = this });
        }

        UpdateHeading();
    }

    private void UpdateHeading() => HeadingText.Visibility = List.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}
