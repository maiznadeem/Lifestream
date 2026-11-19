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

    /// <summary>
    /// True on an input (and, inherited, on every input inside it) that must show the error look: the Critical edge and the 2 px Critical bar of
    /// design-system 5.2. The field templates trigger on it and on their own <c>Validation.HasError</c>. A <see cref="FormField"/> sets it while it
    /// shows an error, and a composite input (the password field, the date field, the record picker) sets it from its own validation state, so the
    /// boxes inside look the same as a plain field in error. WPF's red adorner is switched off on every input (<c>Validation.ErrorTemplate</c>).
    /// </summary>
    public static readonly DependencyProperty HasErrorProperty =
        DependencyProperty.RegisterAttached("HasError", typeof(bool), typeof(InputAssist), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetHasError(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(HasErrorProperty);

    public static void SetHasError(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(HasErrorProperty, value);

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
