using System.Windows;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// The placeholder of a text input: a hint shown in the field while it is empty (for example "Enter your username"). It is not part of the value
/// and screen readers get it as the field's help text. The input templates draw it; the label still lives above the field (FormField).
/// </summary>
public static class InputAssist
{
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(InputAssist), new FrameworkPropertyMetadata(string.Empty, OnPlaceholderChanged));

    public static string GetPlaceholder(DependencyObject element) => (string)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(PlaceholderProperty, value);

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetHelpText(d)))
        {
            System.Windows.Automation.AutomationProperties.SetHelpText(d, e.NewValue as string ?? string.Empty);
        }
    }
}
