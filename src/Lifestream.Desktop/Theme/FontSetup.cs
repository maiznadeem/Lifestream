using System.Windows;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;
using System.Runtime.CompilerServices;

namespace Lifestream.Desktop.Theme;

/// <summary>
/// Loads the bundled font for both text engines at startup (SHELL-26) and keeps the app alive when it cannot (SHELL-01): the WPF text
/// falls back to the system UI font (<see cref="SystemFonts.MessageFontFamily"/>) and the charts to Skia's default typeface, each with a
/// logged warning, instead of an exception that ends the process before any window exists. The two loaders are properties so a test can inject
/// a failing one.
/// </summary>
public static class FontSetup
{
    private const string FontFolder = "pack://application:,,,/Lifestream;component/Fonts/";
    private const string FamilyName = "Google Sans Flex";
    private const string RegularFile = "GoogleSansFlex-Regular.ttf";

    // One Skia typeface per process, shared by every thread. LiveCharts latches its fallback typeface process-wide on the first label it draws
    // and disposes any other typeface when a paint is torn down, so a second object for the same font would be disposed under its users.
    private static readonly object TypefaceLock = new();
    private static SKTypeface? _bundledTypeface;
    private static SKTypeface? _fallbackTypeface;

    /// <summary>Loads the bundled font as a Skia typeface (the chart text). Throws when it cannot.</summary>
    public static Func<SKTypeface> ChartTypefaceLoader { get; set; } = LoadBundledChartTypeface;

    /// <summary>Says whether WPF can resolve the bundled font family.</summary>
    public static Func<bool> AppFontAvailable { get; set; } = IsBundledFamilyAvailable;

    /// <summary>Makes a typeface the default of every LiveCharts text. A property so a test can observe the choice without reconfiguring the process-wide LiveCharts settings.</summary>
    public static Action<SKTypeface> ApplyTypeface { get; set; } = static face => LiveCharts.Configure(settings => settings.HasTextSettings(new TextSettings { DefaultTypeface = face }));

    /// <summary>Where warnings go; the app points it at its logger once the host exists.</summary>
    public static Action<string> Warn { get; set; } = static message => System.Diagnostics.Debug.WriteLine(message);

    /// <summary>True when the chart font fell back to the default typeface.</summary>
    public static bool ChartFellBack { get; private set; }

    /// <summary>True when the app font fell back to the system UI font.</summary>
    public static bool AppFellBack { get; private set; }

    /// <summary>The Skia typeface the charts use: the bundled font, or the default typeface after a failed load.</summary>
    public static SKTypeface? ChartTypeface { get; private set; }

    /// <summary>
    /// Makes the bundled font the default of every LiveCharts text (idempotent). A load that fails is logged and replaced by the default typeface; if even that
    /// is unavailable the charts keep LiveCharts' own default.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The typeface lives for the whole process: LiveCharts keeps it as its default.")]
    public static void ApplyChartFont()
    {
        // The chart control configures LiveCharts for Skia in its static constructor, but only when nothing configured it
        // first; run that now so this call adds the typeface to the defaults instead of replacing them.
        RuntimeHelpers.RunClassConstructor(typeof(CartesianChart).TypeHandle);
        ChartFellBack = false;
        SKTypeface? face;
        try
        {
            face = ChartTypefaceLoader();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ChartFellBack = true;
            Warn($"The bundled chart font could not be loaded ({ex.GetType().Name}: {ex.Message}). Charts use the default font.");
            face = FallbackTypeface();
        }

        ChartTypeface = face;
        if (face is not null)
        {
            ApplyTypeface(face);
        }
    }

    /// <summary>Points <c>Font.Family</c> of the theme at the system UI font when the bundled family cannot be resolved.</summary>
    public static void ApplyAppFont(ResourceDictionary theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        AppFellBack = false;
        bool available;
        try
        {
            available = AppFontAvailable();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Warn($"The bundled font could not be checked ({ex.GetType().Name}: {ex.Message}).");
            available = false;
        }

        if (!available)
        {
            AppFellBack = true;
            Warn($"The bundled font {FamilyName} could not be loaded. The app uses the system UI font.");
            theme["Font.Family"] = SystemFonts.MessageFontFamily;
        }
    }

    /// <summary>The system UI font as a Skia typeface, never null: the UI font, else Skia's default, else a freshly created default. Null only when Skia itself is unusable.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The typeface lives for the whole process: LiveCharts keeps it as its default.")]
    private static SKTypeface? FallbackTypeface()
    {
        try
        {
            lock (TypefaceLock)
            {
                return _fallbackTypeface ??= SKTypeface.FromFamilyName(SystemFonts.MessageFontFamily.Source) ?? SKTypeface.Default ?? SKTypeface.CreateDefault();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Warn($"Skia's default typeface is not available either ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    /// <summary>Loads the bundled font once per process; every later call, from any thread, returns the same object. A failed load is not cached, so the next call retries.</summary>
    private static SKTypeface LoadBundledChartTypeface()
    {
        lock (TypefaceLock)
        {
            if (_bundledTypeface is not null)
            {
                return _bundledTypeface;
            }

            _ = Application.Current; // registers the pack: scheme when no application exists yet (unit tests)
            var resource = Application.GetResourceStream(new Uri(FontFolder + RegularFile, UriKind.Absolute))
                ?? throw new InvalidOperationException($"The bundled font Fonts/{RegularFile} is not embedded in the executable.");
            using var stream = resource.Stream;
            return _bundledTypeface = SKTypeface.FromStream(stream) ?? throw new InvalidOperationException($"Skia could not read the bundled font Fonts/{RegularFile}.");
        }
    }

    private static bool IsBundledFamilyAvailable()
    {
        _ = Application.Current;
        foreach (var family in Fonts.GetFontFamilies(new Uri(FontFolder, UriKind.Absolute)))
        {
            if (family.FamilyNames.Values.Any(name => string.Equals(name, FamilyName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
