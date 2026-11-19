using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lifestream.Client.Validation;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// The frame of a record form. It validates a field when the user leaves it (the field is named with <c>FormFocus.FieldName</c>) and
/// lets the view model move focus to the first invalid field on submit.
/// </summary>
public partial class RecordEditorChrome : UserControl
{
    public static readonly DependencyProperty FieldsProperty = DependencyProperty.Register(nameof(Fields), typeof(object), typeof(RecordEditorChrome), new PropertyMetadata(null));

    public RecordEditorChrome()
    {
        InitializeComponent();
        AddHandler(LostFocusEvent, new RoutedEventHandler(OnFieldLostFocus));
        FormFocus.SetIsEnabled(this, true);
    }

    /// <summary>The form's fields (placed in a scroll area above the actions).</summary>
    public object? Fields { get => GetValue(FieldsProperty); set => SetValue(FieldsProperty, value); }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FormViewModelBase form || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        for (DependencyObject? node = source; node is not null; node = (node as Visual is not null ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is FormField field && FormFocus.GetFieldName(field) is { Length: > 0 } name)
            {
                form.ValidateOnBlur(name);
                return;
            }
        }
    }
}
