using SkiaSharp;

namespace Lifestream.Desktop.Theme;

/// <summary>
/// The charts draw with SkiaSharp, not WPF text, so they do not see the theme's font. This is the bundled Inter as an <see cref="SKTypeface"/>;
/// <see cref="FontSetup"/> makes it the LiveCharts default at startup, so axis labels, axis names and the legend use the one app font (SHELL-26).
/// </summary>
public static class ChartFont
{
    /// <summary>The bundled Inter as a Skia typeface (throws when the font cannot be loaded).</summary>
    public static SKTypeface Inter => FontSetup.ChartTypeface ?? FontSetup.ChartTypefaceLoader();

    /// <summary>Sets Inter as the default typeface of every LiveCharts text (idempotent); falls back with a warning when it cannot be loaded.</summary>
    public static void Apply() => FontSetup.ApplyChartFont();
}
