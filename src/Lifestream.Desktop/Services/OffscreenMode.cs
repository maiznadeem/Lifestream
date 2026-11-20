using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Lifestream.Desktop.Services;

/// <summary>
/// Test-only switch for driving the real app without touching the desktop it runs on (run/briefs/real-window-capture.md). With
/// <c>LIFESTREAM_OFFSCREEN=1</c> in the environment, every top-level window the UI thread creates (the sign-in and main windows, dialogs,
/// popups, menus, combo-box drop-downs, tooltips and common dialogs) lives outside every monitor and is never activated:
/// <list type="bullet">
/// <item>a thread CBT hook moves each new top-level window off the desktop before it exists on screen and refuses every activation;</item>
/// <item>a thread WH_CALLWNDPROC hook rewrites, before any window procedure sees it, every later position of a top-level window that would touch a
/// monitor (centring, a popup kept inside the nearest monitor, maximizing) by the same offset, so sizes and the maximized layout are real and
/// nothing is drawn on a monitor (a subclass is not enough: WPF's own window procedure runs first and handles the message);</item>
/// <item>the sign-in and main windows are also placed off the desktop by WPF itself (manual start-up location), as the launch probe does;</item>
/// <item>a WinEvent hook on the whole process is the safety net: a window of another thread that shows on a monitor is moved off at once and
/// logged as <c>ONSCREEN</c> in the file named by <c>LIFESTREAM_OFFSCREEN_LOG</c>.</item>
/// </list>
/// The windows are captured with PrintWindow and driven through UI Automation from another process (tests/realapp). With
/// <c>LIFESTREAM_PREFERENCES_PATH</c> the preferences file is read from that path instead of the user's own, so a test never changes the
/// theme or remembered username of the person whose PC it runs on. Without the switch this class does nothing.
/// </summary>
internal static partial class OffscreenMode
{
    public const string Variable = "LIFESTREAM_OFFSCREEN";

    private const int WhCbt = 5;
    private const int WhCallWndProc = 4;
    private const int WhCallWndProcRet = 12;
    private const int HcbtCreateWnd = 3;
    private const int HcbtActivate = 5;
    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const int WsChild = 0x40000000;
    private const int CwUseDefault = unchecked((int)0x80000000);
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x800B;
    private const int GaRoot = 2;

    private static readonly HookProc CbtProc = OnCbt;
    private static readonly HookProc CallProc = OnCallWndProc;
    private static readonly HookProc CallRetProc = OnCallWndProcRet;
    private static readonly WinEventProc ShowProc = OnWinEvent;
    private static IntPtr _cbtHook;
    private static IntPtr _callHook;
    private static IntPtr _callRetHook;
    private static IntPtr _eventHook;
    private static IntPtr _locationHook;
    private static Rect32 _desktop;
    private static string? _log;

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable(Variable) == "1";

    /// <summary>The preferences file to use instead of the user's own, or null.</summary>
    public static string? PreferencesPath => Enabled ? Environment.GetEnvironmentVariable("LIFESTREAM_PREFERENCES_PATH") : null;

    /// <summary>How far a window is moved: every monitor and then some, so nothing of it, its shadow included, can reach one.</summary>
    private static int OffsetX => -(_desktop.Right - _desktop.Left) - _desktop.Left - 8000;

    private static int OffsetY => -(_desktop.Bottom - _desktop.Top) - _desktop.Top - 8000;

    /// <summary>Installs the hooks on the calling (UI) thread; call before the first window is created.</summary>
    public static void Install()
    {
        if (!Enabled || _cbtHook != IntPtr.Zero)
        {
            return;
        }

        _log = Environment.GetEnvironmentVariable("LIFESTREAM_OFFSCREEN_LOG");
        _desktop = new Rect32(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(76) + GetSystemMetrics(78), GetSystemMetrics(77) + GetSystemMetrics(79));
        _cbtHook = SetWindowsHookExW(WhCbt, CbtProc, IntPtr.Zero, GetCurrentThreadId());
        _callHook = SetWindowsHookExW(WhCallWndProc, CallProc, IntPtr.Zero, GetCurrentThreadId());
        _callRetHook = SetWindowsHookExW(WhCallWndProcRet, CallRetProc, IntPtr.Zero, GetCurrentThreadId());
        var pid = (uint)Environment.ProcessId;
        _eventHook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, ShowProc, pid, 0, 0);
        _locationHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, ShowProc, pid, 0, 0);
        Log($"installed desktop={_desktop.Left},{_desktop.Top},{_desktop.Right},{_desktop.Bottom} offset={OffsetX},{OffsetY} uiThread={GetCurrentThreadId()} cbt={_cbtHook != IntPtr.Zero} call={_callHook != IntPtr.Zero} ret={_callRetHook != IntPtr.Zero} event={_eventHook != IntPtr.Zero}");
        if (_cbtHook == IntPtr.Zero || _callHook == IntPtr.Zero || _callRetHook == IntPtr.Zero)
        {
            throw new InvalidOperationException("LIFESTREAM_OFFSCREEN: the window hook could not be installed; refusing to show windows on the desktop.");
        }
    }

    /// <summary>The sign-in and main windows: no taskbar button, never activated.</summary>
    public static void Prepare(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = OffsetX - 2000;
        window.Top = OffsetY - 2000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Topmost = false;
    }

    private static bool TouchesDesktop(int x, int y, int width, int height) =>
        x < _desktop.Right + 200 && x + width > _desktop.Left - 200 && y < _desktop.Bottom + 200 && y + height > _desktop.Top - 200;

    private static unsafe IntPtr OnCbt(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HcbtActivate)
        {
            return 1; // never activated: the person's foreground window keeps the focus
        }

        if (code == HcbtCreateWnd)
        {
            var create = ((CbtCreateWnd*)lParam)->CreateStruct;
            if ((create->Style & WsChild) == 0 && create->Parent != new IntPtr(-3))
            {
                if (create->X == CwUseDefault || create->Y == CwUseDefault)
                {
                    create->X = OffsetX;
                    create->Y = OffsetY;
                }
                else if (TouchesDesktop(create->X, create->Y, Math.Max(create->Cx, 1), Math.Max(create->Cy, 1)))
                {
                    Log($"create 0x{wParam:X} at {create->X},{create->Y} {create->Cx}x{create->Cy} -> moved off");
                    create->X += OffsetX;
                    create->Y += OffsetY;
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static unsafe IntPtr OnCallWndProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var call = (CallWndProcStruct*)lParam;
            if (call->Message == WmWindowPosChanging)
            {
                Rewrite(call->Hwnd, (WindowPos*)call->LParam, "move");
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>
    /// After the window procedure: WPF's own WM_WINDOWPOSCHANGING handling can put back a position on the desktop (a dialog sized to its content
    /// was set to 0,0 that way, 2026-11-20), and Windows applies the WINDOWPOS only after this returns.
    /// </summary>
    private static unsafe IntPtr OnCallWndProcRet(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var call = (CallWndProcRetStruct*)lParam;
            if (call->Message == WmWindowPosChanging)
            {
                Rewrite(call->Hwnd, (WindowPos*)call->LParam, "after-wndproc");
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static unsafe void Rewrite(IntPtr hwnd, WindowPos* pos, string stage)
    {
        if (GetAncestor(hwnd, GaRoot) != hwnd)
        {
            return;
        }

        pos->Flags |= SwpNoActivate;
        if ((pos->Flags & SwpNoMove) != 0)
        {
            return;
        }

        var width = pos->Cx;
        var height = pos->Cy;
        if ((pos->Flags & SwpNoSize) != 0 && GetWindowRect(hwnd, out var current))
        {
            width = current.Right - current.Left;
            height = current.Bottom - current.Top;
        }

        if (TouchesDesktop(pos->X, pos->Y, Math.Max(width, 1), Math.Max(height, 1)))
        {
            Log($"{stage} 0x{hwnd:X} to {pos->X},{pos->Y} {width}x{height} -> moved off");
            pos->X += OffsetX;
            pos->Y += OffsetY;
        }
    }

    private static void OnWinEvent(IntPtr hook, uint @event, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero || GetAncestor(hwnd, GaRoot) != hwnd || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var r))
        {
            return;
        }

        if (TouchesDesktop(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) && r.Right - r.Left > 0 && r.Bottom - r.Top > 0)
        {
            SetWindowPos(hwnd, IntPtr.Zero, r.Left + OffsetX, r.Top + OffsetY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            Log($"ONSCREEN hwnd=0x{hwnd:X} thread={thread} rect={r.Left},{r.Top},{r.Right},{r.Bottom} moved off");
        }
    }

    private static void Log(string line)
    {
        if (_log is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(_log, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // the log is a diagnostic; a locked file must not stop the app
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private delegate void WinEventProc(IntPtr hook, uint @event, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallWndProcStruct
    {
        public IntPtr LParam;
        public IntPtr WParam;
        public int Message;
        public IntPtr Hwnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallWndProcRetStruct
    {
        public IntPtr LResult;
        public IntPtr LParam;
        public IntPtr WParam;
        public int Message;
        public IntPtr Hwnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CbtCreateWnd
    {
        public CreateStructW* CreateStruct;
        public IntPtr InsertAfter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateStructW
    {
        public IntPtr CreateParams;
        public IntPtr Instance;
        public IntPtr Menu;
        public IntPtr Parent;
        public int Cy;
        public int Cx;
        public int Y;
        public int X;
        public int Style;
        public IntPtr Name;
        public IntPtr Class;
        public uint ExStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetWindowsHookExW(int idHook, HookProc proc, IntPtr module, uint threadId);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, int flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
