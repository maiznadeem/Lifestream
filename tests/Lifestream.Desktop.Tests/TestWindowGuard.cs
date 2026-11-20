using System.Runtime.InteropServices;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// No window of a test thread can become the active or the foreground window, whoever asks. On 2026-11-20/21 the hidden "LifestreamHosted"
/// window of <see cref="Hosted"/> became the person's foreground window, with the keyboard focus and the caret, whenever a test focused an
/// element in it: WPF asks Windows for the focus of the element's window (SetFocus), Windows activates that window to give it, and while
/// nobody had typed for a while (or by the rules that let a process take the foreground) the activation reached the desktop the person was
/// typing on, so their keys went to an invisible window. A thread hook (WH_CBT) refuses every activation and every Win32 focus change on
/// the thread from its first hosted element on; WPF keeps its own keyboard focus inside the element tree, which is what the tests look at.
/// Every test process also runs on an isolated desktop (tools/test-fast.ps1); this guard is for a run that does not.
/// </summary>
internal static class TestWindowGuard
{
    private const int WhCbt = 5;
    private const int HcbtActivate = 5;
    private const int HcbtSetFocus = 9;

    private static readonly HookProc Refuse = static (code, wParam, lParam) =>
        code is HcbtActivate or HcbtSetFocus ? new IntPtr(1) : CallNextHookEx(IntPtr.Zero, code, wParam, lParam); // 1: it does not happen

    /// <summary>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: never activated by a click or by Windows, never in Alt+Tab or the taskbar.</summary>
    public const int NoActivate = 0x08000000 | 0x00000080;

    /// <summary>Where a test window is put: outside any monitor layout (and inside the 16-bit range of window messages' coordinates).</summary>
    public const int OffMonitors = -30000;

    [ThreadStatic]
    private static IntPtr _hook;

    /// <summary>Installs the guard on the calling thread (once; it stays until the thread ends). Throws when it cannot.</summary>
    public static void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWindowsHookExW(WhCbt, Refuse, IntPtr.Zero, GetCurrentThreadId());
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("The hook that keeps test windows from being activated could not be installed; no window is created.");
        }
    }

    /// <summary>True when the guard is in place on the calling thread.</summary>
    public static bool Installed => _hook != IntPtr.Zero;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
