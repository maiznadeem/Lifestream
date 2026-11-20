using System.Windows;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// What a page tells the shell. A page rebuilt to direction A draws its own page header (Controls/PageHeader: date line, title, count, primary
/// actions) and sets <c>controls:ShellPage.OwnsHeader="True"</c> on its root, so the shell hides its plain title for it. Screens not yet rebuilt leave
/// it unset and keep the shell's title.
/// </summary>
public static class ShellPage
{
    public static readonly DependencyProperty OwnsHeaderProperty =
        DependencyProperty.RegisterAttached("OwnsHeader", typeof(bool), typeof(ShellPage), new FrameworkPropertyMetadata(false));

    public static bool GetOwnsHeader(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(OwnsHeaderProperty);

    public static void SetOwnsHeader(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(OwnsHeaderProperty, value);
}
