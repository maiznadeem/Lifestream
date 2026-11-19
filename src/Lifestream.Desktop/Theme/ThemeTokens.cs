using System.Windows;

namespace Lifestream.Desktop.Theme;

/// <summary>
/// Reads spacing, radii and window metrics from the token file for code that builds elements itself (SHELL-25). Code never
/// carries a literal: a missing token is a bug, so it throws instead of falling back to a number.
/// </summary>
internal static class ThemeTokens
{
    public static double Space(string key) => Get<double>(key);

    public static Thickness Inset(string key) => Get<Thickness>(key);

    public static CornerRadius Radius(string key) => Get<CornerRadius>(key);

    private static T Get<T>(string key) =>
        ThemeLoader.Resources[key] is T value ? value : throw new InvalidOperationException($"Theme token '{key}' is missing or is not a {typeof(T).Name}.");
}
