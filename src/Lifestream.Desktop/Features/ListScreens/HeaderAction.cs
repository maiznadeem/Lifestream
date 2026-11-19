using System.Windows;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// One filled primary per screen (design system 1 and 7.6): the page header's action is <c>Button.Primary</c> while no side panel is open, and <c>Button.Secondary</c>
/// while one is, so the panel's main action is the only filled button. Set <c>lists:HeaderAction.IsDemoted="{Binding IsDetailOpen}"</c> on the header's button.
/// </summary>
public static class HeaderAction
{
    public static readonly DependencyProperty IsDemotedProperty =
        DependencyProperty.RegisterAttached("IsDemoted", typeof(bool), typeof(HeaderAction), new PropertyMetadata(false, OnIsDemotedChanged));

    public static bool GetIsDemoted(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(IsDemotedProperty);

    public static void SetIsDemoted(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(IsDemotedProperty, value);

    private static void OnIsDemotedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element)
        {
            element.SetResourceReference(FrameworkElement.StyleProperty, (bool)e.NewValue ? "Button.Secondary" : "Button.Primary");
        }
    }
}
