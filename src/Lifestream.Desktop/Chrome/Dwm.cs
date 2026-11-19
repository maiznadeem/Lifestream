using System.Runtime.InteropServices;

namespace Lifestream.Desktop.Chrome;

/// <summary>
/// The Desktop Window Manager calls the window chrome needs (Windows 11): the Mica backdrop, the dark frame for the dark theme and round corners.
/// Every call is best effort: on Windows 10, or when DWM refuses, it returns false and the window keeps its solid fallback background.
/// </summary>
internal static partial class Dwm
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int SystemBackdropType = 38;
    private const int CornerRound = 2;
    private const int BackdropMainWindow = 2; // Mica
    private const int BackdropNone = 1;

    /// <summary>Windows 11 22H2 (build 22621) or later: the first build with the documented system backdrop attribute.</summary>
    public static bool SupportsMica => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>Windows 11 (build 22000) or later: DWM draws the window's rounded corners, border and shadow.</summary>
    public static bool DrawsRoundCorners => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>Tests and screenshots turn Mica off so the window paints its fallback brush (Mica cannot be captured off-screen).</summary>
    public static bool MicaDisabled { get; set; } = Environment.GetEnvironmentVariable("LIFESTREAM_NO_MICA") == "1";

    public static bool SetDarkFrame(IntPtr hwnd, bool dark) => Set(hwnd, UseImmersiveDarkMode, dark ? 1 : 0);

    public static bool SetRoundCorners(IntPtr hwnd) => DrawsRoundCorners && Set(hwnd, WindowCornerPreference, CornerRound);

    /// <summary>Extends the frame over the whole client area and asks for Mica behind it.</summary>
    public static bool SetMica(IntPtr hwnd, bool on)
    {
        if (!SupportsMica || MicaDisabled)
        {
            return false;
        }

        var margins = on ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : default;
        if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0)
        {
            return false;
        }

        return Set(hwnd, SystemBackdropType, on ? BackdropMainWindow : BackdropNone);
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
