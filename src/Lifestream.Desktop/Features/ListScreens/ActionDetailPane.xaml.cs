using System.Windows;
using System.Windows.Controls;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>A record's facts with the screen's own actions above them.</summary>
public partial class ActionDetailPane : UserControl
{
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(ActionDetailPane), new PropertyMetadata(null));

    public ActionDetailPane() => InitializeComponent();

    /// <summary>The buttons of the record's actions.</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
}
