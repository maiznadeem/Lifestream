using System.Windows;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Theme;

/// <summary>Loads the theme once per UI thread and hands the same dictionary to every window and to the application.</summary>
public static class ThemeLoader
{
    [ThreadStatic]
    private static ResourceDictionary? _resources;

    /// <summary>The whole theme: tokens, motion, typography, controls, chrome and shell styles.</summary>
    public static ResourceDictionary Resources => _resources ??= Load();

    /// <summary>Forgets the loaded theme so the next access reloads it (tests change the motion setting between loads).</summary>
    public static void Reset() => _resources = null;

    private static ResourceDictionary Load()
    {
        // Touching Application runs its static constructor, which registers the pack: scheme even when no Application exists yet (unit tests).
        _ = Application.Current;
        FocusAssist.Register();
        FontSetup.ApplyChartFont();
        var theme = new ResourceDictionary { Source = new Uri("/Lifestream;component/Theme/Theme.xaml", UriKind.Relative) };
        FontSetup.ApplyAppFont(theme);
        return theme;
    }
}
