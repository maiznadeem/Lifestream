using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lifestream.Desktop.Features;

/// <summary>
/// Makes a text box accept digits only: <c>feat:DigitsOnly.IsEnabled="True"</c>. Typing a letter, a space or a sign does nothing, and a paste keeps
/// only its digits. The view model refuses anything else too, so this is a convenience, not the only guard (salary and units boxes).
/// </summary>
public static class DigitsOnly
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(DigitsOnly), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(IsEnabledProperty, value);

    /// <summary>The digits of a text, in order.</summary>
    public static string Filter(string? text) => new([.. (text ?? string.Empty).Where(char.IsAsciiDigit)]);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.PreviewTextInput -= OnTextInput;
        box.PreviewKeyDown -= OnKeyDown;
        DataObject.RemovePastingHandler(box, OnPaste);
        if ((bool)e.NewValue)
        {
            box.PreviewTextInput += OnTextInput;
            box.PreviewKeyDown += OnKeyDown;
            DataObject.AddPastingHandler(box, OnPaste);
            InputMethod.SetIsInputMethodEnabled(box, false);
        }
    }

    private static void OnTextInput(object sender, TextCompositionEventArgs e) => e.Handled = Filter(e.Text).Length != e.Text.Length;

    private static void OnKeyDown(object sender, KeyEventArgs e) => e.Handled |= e.Key == Key.Space;

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text)
        {
            e.CancelCommand();
            return;
        }

        var digits = Filter(text);
        if (digits.Length == 0)
        {
            e.CancelCommand();
        }
        else if (digits != text)
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, digits);
            e.DataObject = data;
        }
    }
}
