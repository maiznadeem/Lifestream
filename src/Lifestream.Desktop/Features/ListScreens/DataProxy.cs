using System.Windows;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// Carries a view's data context to places outside its visual tree, such as a <see cref="System.Windows.Controls.DataGridColumn"/>'s Visibility: put one in the view's
/// resources with <c>Data="{Binding}"</c> and bind with <c>Source={StaticResource ...}</c>, <c>Path=Data.X</c>.
/// </summary>
public sealed class DataProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(object), typeof(DataProxy), new UIPropertyMetadata(null));

    public object? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    protected override Freezable CreateInstanceCore() => new DataProxy();
}
