using System.Windows;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Theme;

/// <summary>
/// Loads the theme once per UI thread and hands the same dictionary to every window and to the application. The theme is Theme.xaml (shared tokens,
/// type, motion, control templates) plus one colour dictionary in front of it, which <see cref="ThemeService"/> replaces to switch light and dark.
/// </summary>
public static class ThemeLoader
{
    [ThreadStatic]
    private static ResourceDictionary? _resources;

    /// <summary>The whole theme: colours, tokens, motion, typography, controls, chrome and shell styles.</summary>
    public static ResourceDictionary Resources => _resources ??= Load();

    /// <summary>Forgets the loaded theme so the next access reloads it (tests change the motion setting or the theme between loads).</summary>
    public static void Reset() => _resources = null;

    /// <summary>The colour dictionary currently merged into a theme.</summary>
    internal static ResourceDictionary? ColorsOf(ResourceDictionary theme) =>
        theme.MergedDictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Theme/Colors.", StringComparison.Ordinal) == true);

    /// <summary>Puts a colour dictionary in place of the current one; DynamicResource references in every window that merges the theme update.</summary>
    internal static void ReplaceColors(ResourceDictionary theme, ResourceDictionary colors)
    {
        var current = ColorsOf(theme);
        if (current is null)
        {
            theme.MergedDictionaries.Insert(0, colors);
            return;
        }

        theme.MergedDictionaries[theme.MergedDictionaries.IndexOf(current)] = colors;
    }

    private static ResourceDictionary Load()
    {
        // Touching Application runs its static constructor, which registers the pack: scheme even when no Application exists yet (unit tests).
        _ = Application.Current;
        FocusAssist.Register();
        FontSetup.ApplyChartFont();
        var theme = new ResourceDictionary { Source = new Uri("/Lifestream;component/Theme/Theme.xaml", UriKind.Relative) };
        var dark = ThemeService.ResolveDark();
        theme.MergedDictionaries.Insert(0, ThemeService.CreateColors(dark));
        ThemeService.MarkLoaded(dark);
        FontSetup.ApplyAppFont(theme);
        return theme;
    }
}
