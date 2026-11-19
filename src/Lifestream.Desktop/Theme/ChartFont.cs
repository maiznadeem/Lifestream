using System.Runtime.CompilerServices;
using System.Windows;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;

namespace Lifestream.Desktop.Theme;

/// <summary>
/// The charts draw with SkiaSharp, not WPF text, so they do not see the theme's font. This loads the bundled Inter from the
/// embedded resource into an <see cref="SKTypeface"/> and makes it the LiveCharts default, so axis labels, axis names and the
/// legend use the one app font (SHELL-26).
/// </summary>
public static class ChartFont
{
    private static readonly Lazy<SKTypeface> InterTypeface = new(Load);

    /// <summary>The bundled Inter as a Skia typeface.</summary>
    public static SKTypeface Inter => InterTypeface.Value;

    /// <summary>Sets Inter as the default typeface of every LiveCharts text (idempotent).</summary>
    public static void Apply()
    {
        // The chart control configures LiveCharts for Skia in its static constructor, but only when nothing configured it
        // first; run that now so this call adds the typeface to the defaults instead of replacing them.
        RuntimeHelpers.RunClassConstructor(typeof(CartesianChart).TypeHandle);
        LiveCharts.Configure(settings => settings.HasTextSettings(new TextSettings { DefaultTypeface = Inter }));
    }

    private static SKTypeface Load()
    {
        _ = Application.Current; // registers the pack: scheme when no application exists yet (unit tests)
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Lifestream;component/Fonts/InterVariable.ttf", UriKind.Absolute))
            ?? throw new InvalidOperationException("The bundled font Fonts/InterVariable.ttf is not embedded in the executable.");
        using var stream = resource.Stream;
        return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException("Skia could not read the bundled font Fonts/InterVariable.ttf.");
    }
}
