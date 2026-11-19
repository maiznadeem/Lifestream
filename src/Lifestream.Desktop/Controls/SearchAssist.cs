using System.Windows;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// The shortcut hint of a search field (<c>TextBox.Search</c> in Inputs.xaml): <c>controls:SearchAssist.Shortcut="Ctrl F"</c> shows two small "kbd" keys at the right
/// of the field while it is empty. The string is split on spaces into one key each; the template reads the result from <see cref="KeysProperty"/>.
/// </summary>
public static class SearchAssist
{
    private static readonly DependencyPropertyKey KeysPropertyKey =
        DependencyProperty.RegisterAttachedReadOnly("Keys", typeof(string[]), typeof(SearchAssist), new FrameworkPropertyMetadata(Array.Empty<string>()));

    /// <summary>The keys of <see cref="ShortcutProperty"/>, one per word (read-only, for templates).</summary>
    public static readonly DependencyProperty KeysProperty = KeysPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ShortcutProperty =
        DependencyProperty.RegisterAttached("Shortcut", typeof(string), typeof(SearchAssist), new FrameworkPropertyMetadata(string.Empty, OnShortcutChanged));

    public static string GetShortcut(DependencyObject element) => (string)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(ShortcutProperty);

    public static void SetShortcut(DependencyObject element, string value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(ShortcutProperty, value);

    public static string[] GetKeys(DependencyObject element) => (string[])(element ?? throw new ArgumentNullException(nameof(element))).GetValue(KeysProperty);

    private static void OnShortcutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var text = e.NewValue as string ?? string.Empty;
        d.SetValue(KeysPropertyKey, text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
