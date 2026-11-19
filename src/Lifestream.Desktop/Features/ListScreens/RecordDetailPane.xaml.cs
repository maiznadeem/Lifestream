using System.Windows;
using System.Windows.Controls;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>The detail pane beside a list: facts of the selected record with Edit and Delete, and the screen's related action when it has one.</summary>
public partial class RecordDetailPane : UserControl
{
    public static readonly DependencyProperty RelatedActionsProperty = DependencyProperty.Register(nameof(RelatedActions), typeof(object), typeof(RecordDetailPane), new PropertyMetadata(null));

    public RecordDetailPane() => InitializeComponent();

    /// <summary>A related action next to Edit and Delete (for example "Book appointment" on a donor).</summary>
    public object? RelatedActions { get => GetValue(RelatedActionsProperty); set => SetValue(RelatedActionsProperty, value); }
}
