using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Lifestream.Desktop.Theme;

/// <summary>
/// How every text of the app is drawn on screen, in one place, so no part of a window can look like another typeface (Fonts/README.md, "Rendering"):
/// <list type="bullet">
/// <item><see cref="TextFormattingMode.Display"/>: glyphs sit on whole pixels and the fonts' hinting is used, which keeps 12 to 15 DIP text crisp at
/// 100 % scale. (Ideal formatting ignores hinting and draws small text soft.) Headings use it too: one mode for every size.</item>
/// <item><see cref="TextRenderingMode.Grayscale"/>: the only anti-aliasing WPF can draw on every surface. ClearType is not possible on Mica (the title
/// bar, the sidebar, the sign-in screen's right side), so with it the same text looked different from one side of a window to the other.</item>
/// <item>Kerning off: in Display formatting a kerning pair is rounded to a whole pixel, which closed up "2.1.0", "10:30" and "made-up" at 12 and
/// 13 DIP. The advances of the hinted fonts are already even.</item>
/// </list>
/// The three values are inherited, so they are set on the root of each visual tree: every window (<c>theme:TextDefaults.Apply="True"</c> on its root
/// element) and the two controls that open in a tree of their own without inheriting from the window, tool tips and context menus (in their
/// styles). (<see cref="TextOptions.TextHintingModeProperty"/> is not inherited, so it cannot be set this way; every text keeps its default, Auto,
/// which hints text for a fixed position unless it is being animated.) A printed receipt is not on screen: its document keeps WPF's defaults for
/// paper when it is printed, and takes these values from the window when it is previewed.
/// </summary>
public static class TextDefaults
{
    public const TextFormattingMode Formatting = TextFormattingMode.Display;
    public const TextRenderingMode Rendering = TextRenderingMode.Grayscale;
    public const bool Kerning = false;

    /// <summary>True on the root of a visual tree: the element, and everything in it, draws its text with the app's settings.</summary>
    public static readonly DependencyProperty ApplyProperty =
        DependencyProperty.RegisterAttached("Apply", typeof(bool), typeof(TextDefaults), new PropertyMetadata(false, OnApplyChanged));

    public static bool GetApply(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(ApplyProperty);
    }

    public static void SetApply(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ApplyProperty, value);
    }

    /// <summary>Sets the values on a root element (a window, or the root of an element hosted without one).</summary>
    public static void ApplyTo(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        TextOptions.SetTextFormattingMode(root, Formatting);
        TextOptions.SetTextRenderingMode(root, Rendering);
        Typography.SetKerning(root, Kerning);
    }

    private static void OnApplyChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            ApplyTo(element);
        }
    }
}
