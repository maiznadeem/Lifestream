using System.Windows;
using System.Windows.Controls;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>The name, date of birth, gender, blood type, phone and address fields shared by the donor and patient forms; <see cref="Extra"/> is one more field of the form.</summary>
public partial class PersonFormFields : UserControl
{
    public static readonly DependencyProperty ExtraProperty = DependencyProperty.Register(nameof(Extra), typeof(object), typeof(PersonFormFields), new PropertyMetadata(null));

    public PersonFormFields() => InitializeComponent();

    /// <summary>An extra field placed beside the blood type (the donor's frequency).</summary>
    public object? Extra { get => GetValue(ExtraProperty); set => SetValue(ExtraProperty, value); }
}
