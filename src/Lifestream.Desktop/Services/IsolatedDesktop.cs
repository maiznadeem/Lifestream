using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lifestream.Desktop.Services;

/// <summary>
/// Test-only: real windows of tests and of the real-app harness are created only on a desktop of their own, never on the desktop of the
/// person at the PC (tests/isolated/README.md). On 2026-11-20 windows shown "off-screen, never activated" in the owner's session still took
/// the keyboard. tests/isolated/Lifestream.Isolated creates a <c>LifestreamIso-*</c> desktop that is never switched to, starts the processes on
/// it with <c>LIFESTREAM_ISOLATED_DESKTOP</c> naming it, and watches the person's desktop the whole time. Only the input desktop gets input,
/// the foreground and the focus. The compositor does not capture what hardware rendering presents on another desktop (the client area comes
/// back empty), so windows there are drawn in software.
/// </summary>
internal static partial class IsolatedDesktop
{
    public const string Variable = "LIFESTREAM_ISOLATED_DESKTOP";
    public const string Prefix = "LifestreamIso-";

    private static readonly Lazy<string?> Reason = new(() => Check(Environment.GetEnvironmentVariable(Variable), ThreadDesktopName(), InputDesktopName()));

    /// <summary>Why this process may not create real windows, or null when it runs on the isolated desktop it was told about.</summary>
    public static string? WhyNot => Reason.Value;

    /// <summary>
    /// Why a process on <paramref name="threadDesktop"/> may not create real windows when it was told its isolated desktop is
    /// <paramref name="expected"/> and the desktop receiving input is <paramref name="inputDesktop"/> (null: not known); null when it may.
    /// </summary>
    internal static string? Check(string? expected, string threadDesktop, string? inputDesktop)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return $"{Variable} is not set: this process runs on the desktop '{threadDesktop}'";
        }

        if (!expected.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return $"{Variable} names '{expected}', which is not an isolated {Prefix}* desktop";
        }

        if (!string.Equals(threadDesktop, expected, StringComparison.OrdinalIgnoreCase))
        {
            return $"this process runs on the desktop '{threadDesktop}', not on '{expected}'";
        }

        if (string.Equals(inputDesktop, expected, StringComparison.OrdinalIgnoreCase))
        {
            return $"the desktop '{expected}' receives input: it is not isolated";
        }

        return null;
    }

    /// <summary>On the isolated desktop: every window of this process is drawn in software, which the compositor there does capture.</summary>
    public static void UseSoftwareRendering()
    {
        if (WhyNot is null && RenderOptions.ProcessRenderMode != RenderMode.SoftwareOnly)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
    }

    /// <summary>The name of the desktop the calling thread is on.</summary>
    public static string ThreadDesktopName() => ObjectName(GetThreadDesktop(GetCurrentThreadId()));

    /// <summary>The name of the desktop that receives input, or null when this process may not open it (a locked session, a UAC prompt).</summary>
    public static string? InputDesktopName()
    {
        var input = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (input == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ObjectName(input);
        }
        finally
        {
            CloseDesktop(input);
        }
    }

    private static string ObjectName(IntPtr handle)
    {
        var buffer = new byte[512];
        return GetUserObjectInformationW(handle, 2 /* UOI_NAME */, buffer, buffer.Length, out var needed)
            ? Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2))
            : string.Empty;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr desktop);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetThreadDesktop(uint thread);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformationW(IntPtr handle, int index, [Out] byte[] buffer, int length, out int needed);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
