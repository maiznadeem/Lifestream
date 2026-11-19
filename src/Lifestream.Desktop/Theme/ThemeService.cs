using System.Windows;
using System.Windows.Media;
using ThemeMode = Lifestream.Client.Common.ThemeMode;
using Microsoft.Win32;

namespace Lifestream.Desktop.Theme;

/// <summary>
/// Light and dark at runtime (docs/design-system.md, "Theme"). The theme (<see cref="ThemeLoader.Resources"/>) holds exactly one colour dictionary,
/// Colors.Light.xaml or Colors.Dark.xaml (the same keys); switching replaces it, and because every view refers to brushes with DynamicResource, every
/// open window repaints. <see cref="ThemeMode.System"/> follows the Windows app mode (AppsUseLightTheme) and changes with it. When Windows high
/// contrast is on, the brushes map to the system colours whatever the mode.
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly Uri LightUri = new("/Lifestream;component/Theme/Colors.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkUri = new("/Lifestream;component/Theme/Colors.Dark.xaml", UriKind.Relative);
    private static bool _watching;

    // Like the theme dictionary itself (ThemeLoader), the mode belongs to a UI thread: the app has one; tests render on many threads at once.
    [ThreadStatic]
    private static ThemeMode _mode;

    [ThreadStatic]
    private static bool _isDark;

    [ThreadStatic]
    private static EventHandler? _changed;

    /// <summary>The chosen mode (of this UI thread).</summary>
    public static ThemeMode Mode { get => _mode; private set => _mode = value; }

    /// <summary>True when the dark colours are in use (on this UI thread).</summary>
    public static bool IsDark { get => _isDark; private set => _isDark = value; }

    /// <summary>Tests set this to stand in for the Windows app mode; null reads the registry.</summary>
    public static bool? SystemIsDarkOverride { get; set; }

    /// <summary>Raised on the UI thread after the colours changed (charts, the taskbar badge and the window frame redraw). Handlers belong to the thread that added them.</summary>
    public static event EventHandler? Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    /// <summary>True when Windows apps are set to dark.</summary>
    public static bool SystemIsDark
    {
        get
        {
            if (SystemIsDarkOverride is { } value)
            {
                return value;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                return false;
            }
        }
    }

    /// <summary>The colour dictionary for a resolved theme (a new instance each call).</summary>
    public static ResourceDictionary CreateColors(bool dark)
    {
        var colors = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        if (SystemParameters.HighContrast)
        {
            ApplyHighContrast(colors);
        }

        return colors;
    }

    /// <summary>Uses the mode in this thread's theme and in every window that shares it, and remembers it for <see cref="ThemeMode.System"/> changes.</summary>
    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        var dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemIsDark,
        };
        var theme = ThemeLoader.Resources;
        if (dark == IsDark && !SystemParameters.HighContrast)
        {
            WatchSystem();
            return;
        }

        IsDark = dark;
        ThemeLoader.ReplaceColors(theme, CreateColors(dark));
        WatchSystem();
        _changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The current theme's colour for a token (for code that paints itself, such as the charts); transparent when missing.</summary>
    public static Color ColorOf(string key) => ThemeLoader.Resources[key] is Color color ? color : Colors.Transparent;

    /// <summary>Reads the resolved mode at load time, before any window exists (ThemeLoader).</summary>
    internal static bool ResolveDark() => Mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => SystemIsDark,
    };

    internal static void MarkLoaded(bool dark) => IsDark = dark;

    private static void WatchSystem()
    {
        if (_watching || Application.Current is null)
        {
            return;
        }

        _watching = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility)
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (Mode == ThemeMode.System || SystemParameters.HighContrast)
                    {
                        var dark = SystemIsDark;
                        IsDark = !dark; // force the swap even when only high contrast changed
                        Apply(Mode);
                    }
                });
            }
        };
    }

    /// <summary>Windows high contrast: every brush of the theme becomes the matching system colour, so the user's contrast theme wins.</summary>
    private static void ApplyHighContrast(ResourceDictionary colors)
    {
        var window = SystemColors.WindowColor;
        var text = SystemColors.WindowTextColor;
        var highlight = SystemColors.HighlightColor;
        var highlightText = SystemColors.HighlightTextColor;
        var gray = SystemColors.GrayTextColor;
        var control = SystemColors.ControlColor;
        foreach (var key in colors.Keys.OfType<string>().ToList())
        {
            var name = key[(key.IndexOf('.') + 1)..];
            Color mapped = name switch
            {
                "Primary" or "PrimaryHover" or "PrimaryPressed" or "SelectedBar" or "Focus" or "Blood" or "Critical" or "Success" or "Warning" or "Info" or "Chart1" or "Chart2" => highlight,
                "OnPrimary" or "OnPrimaryPressed" or "OnCloseHover" => highlightText,
                "Ink" or "TextPrimary" => text,
                "Ink2" or "Ink3" or "TextSecondary" or "TextMuted" or "TextDisabled" or "StrokeStrong" or "Stroke2" or "ChartText" => gray,
                "Stroke" or "BorderSubtle" or "ChartGrid" => text,
                "Hover" or "Selected" or "SurfaceHover" or "SurfacePressed" or "SurfaceSelected" => control,
                _ => window,
            };
            if (key.StartsWith("Color.", StringComparison.Ordinal))
            {
                colors[key] = mapped;
            }
            else if (key.StartsWith("Brush.", StringComparison.Ordinal))
            {
                var brush = new SolidColorBrush(mapped);
                brush.Freeze();
                colors[key] = brush;
            }
        }
    }
}
