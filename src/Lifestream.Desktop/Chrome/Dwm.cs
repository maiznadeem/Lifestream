using System.Runtime.InteropServices;
using System.Windows;

namespace Lifestream.Desktop.Chrome;

/// <summary>
/// The Desktop Window Manager calls the window chrome needs (Windows 11): the Mica backdrop, the dark frame for the dark theme, round corners, and
/// no frame at all for a window that draws its own surface. Every call is best effort: on Windows 10, or when DWM refuses, it returns false and
/// the window keeps its solid fallback background.
/// </summary>
internal static partial class Dwm
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int BorderColor = 34;
    private const int SystemBackdropType = 38;
    private const int CornerDoNotRound = 1;
    private const int CornerRound = 2;
    private const int BackdropMainWindow = 2; // Mica
    private const int ColorNone = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE: no window border
    private const int SizeFrameX = 32; // SM_CXSIZEFRAME
    private const int SizeFrameY = 33; // SM_CYSIZEFRAME
    private const int PaddedBorder = 92; // SM_CXPADDEDBORDER

    /// <summary>Windows 11 22H2 (build 22621) or later: the first build with the documented system backdrop attribute.</summary>
    public static bool SupportsMica => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>Windows 11 (build 22000) or later: DWM draws the window's rounded corners, border and shadow.</summary>
    public static bool DrawsRoundCorners => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>Tests and screenshots turn Mica off so the window paints its fallback brush (Mica cannot be captured off-screen).</summary>
    public static bool MicaDisabled { get; set; } = Environment.GetEnvironmentVariable("LIFESTREAM_NO_MICA") == "1";

    public static bool SetDarkFrame(IntPtr hwnd, bool dark) => Set(hwnd, UseImmersiveDarkMode, dark ? 1 : 0);

    public static bool SetRoundCorners(IntPtr hwnd) => DrawsRoundCorners && Set(hwnd, WindowCornerPreference, CornerRound);

    /// <summary>
    /// For a transparent window that draws its own surface, corners and shadow (the dialogs): DWM is told not to round it and not to draw a border.
    /// A window that asks for round corners gets DWM's rounded 1 px border around its whole rectangle, shadow room included.
    /// </summary>
    public static bool SetNoFrame(IntPtr hwnd)
    {
        if (!DrawsRoundCorners)
        {
            return false;
        }

        var notRounded = Set(hwnd, WindowCornerPreference, CornerDoNotRound);
        return Set(hwnd, BorderColor, ColorNone) && notRounded;
    }

    /// <summary>
    /// Asks for Mica behind the window. It shows wherever the frame is extended under transparent pixels; the caller extends the frame
    /// (the window chrome's glass frame, <c>Chrome.GlassFrameMica</c>).
    /// </summary>
    public static bool SetMica(IntPtr hwnd) => SupportsMica && !MicaDisabled && Set(hwnd, SystemBackdropType, BackdropMainWindow);

    /// <summary>
    /// How far Windows pushes a maximized window past its monitor's work area on each side, in DIPs at the given DPI: the resize frame plus the
    /// padded border (SM_CXSIZEFRAME + SM_CXPADDEDBORDER). <c>SystemParameters.WindowResizeBorderThickness</c> is the resize frame alone, half of it.
    /// </summary>
    public static Thickness MaximizedOverhang(double dpi)
    {
        var device = (uint)Math.Round(dpi);
        var scale = dpi / 96;
        var x = (GetSystemMetricsForDpi(SizeFrameX, device) + GetSystemMetricsForDpi(PaddedBorder, device)) / scale;
        var y = (GetSystemMetricsForDpi(SizeFrameY, device) + GetSystemMetricsForDpi(PaddedBorder, device)) / scale;
        return new Thickness(x, y, x, y);
    }

    /// <summary>The DPI of the primary monitor (96 at 100 %).</summary>
    public static double SystemDpi => GetDpiForSystem();

    private static bool Set(IntPtr hwnd, int attribute, int value)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();
}
