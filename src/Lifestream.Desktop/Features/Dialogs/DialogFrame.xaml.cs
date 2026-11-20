using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lifestream.Client.Validation;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Features.Dialogs;

/// <summary>The frame of a form dialog. A field's message moves focus to the field when the view model asks for it, and a field is validated when the user leaves it.</summary>
public partial class DialogFrame : UserControl
{
    public static readonly DependencyProperty FieldsProperty = DependencyProperty.Register(nameof(Fields), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

    public static readonly DependencyProperty FooterStartProperty = DependencyProperty.Register(nameof(FooterStart), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

    public static readonly DependencyProperty FooterEndProperty = DependencyProperty.Register(nameof(FooterEnd), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

    public DialogFrame()
    {
        InitializeComponent();
        AddHandler(LostFocusEvent, new RoutedEventHandler(OnFieldLostFocus));
        FormFocus.SetIsEnabled(this, true);
    }

    /// <summary>The dialog's fields (placed in a scroll area above the actions).</summary>
    public object? Fields { get => GetValue(FieldsProperty); set => SetValue(FieldsProperty, value); }

    /// <summary>Extra footer actions before Cancel (a viewer's "Save as PDF").</summary>
    public object? FooterStart { get => GetValue(FooterStartProperty); set => SetValue(FooterStartProperty, value); }

    /// <summary>A viewer's own primary action, last in the footer where the primary always sits (the receipt's Print); for a dialog without <c>PrimaryText</c>.</summary>
    public object? FooterEnd { get => GetValue(FooterEndProperty); set => SetValue(FooterEndProperty, value); }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FormViewModelBase form || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        for (DependencyObject? node = source; node is not null; node = (node is Visual ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is FormField field && FormFocus.GetFieldName(field) is { Length: > 0 } name)
            {
                form.ValidateOnBlur(name);
                return;
            }
        }
    }
}
