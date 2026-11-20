using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lifestream.Desktop.Services;

namespace Lifestream.Desktop.Tests.RealWindows;

/// <summary>
/// A real top-level window, for what an off-screen render cannot show: what Windows itself draws in the title bar and around the window, and
/// what the window answers when Windows asks where the pointer is. The window is shown outside every monitor, is never activated and has no
/// taskbar button; it is captured with <c>PrintWindow(PW_RENDERFULLCONTENT)</c>, which returns what the compositor holds for this one window.
/// Nothing here moves the pointer, sends keys, reads the screen or changes a setting, and a window is never maximized (Windows would put it on a
/// monitor).
/// <para>
/// Real windows are created only on an isolated desktop (<see cref="IsolatedDesktop"/>), where they are drawn in software; the rules below
/// were made for the person's own desktop and are kept on the isolated one as a second line.
/// </para>
/// <para>
/// The window can never take the keyboard from the person at the PC. <c>ShowActivated = false</c> and <c>WS_EX_NOACTIVATE</c> are not enough:
/// such a window still became the foreground window when a UI Automation pattern was called on it. So while a window is open, a hook on its
/// thread refuses every activation and every focus change (<see cref="ThreadGuard"/>), and every later position that would reach a monitor
/// (a maximize, a centring) is moved back off by the same hook. As the last line, the window is checked all the time it is open: if it is ever
/// the foreground window, or on a monitor, or not in the normal state, it is closed at once and the test fails.
/// </para>
/// </summary>
internal sealed class RealWindow : IDisposable
{
    public const int HitClient = 1;
    public const int HitCaption = 2;
    public const int HitMaximizeButton = 9;
    public const int HitLeft = 10;
    public const int HitRight = 11;
    public const int HitTop = 12;
    public const int HitTopLeft = 13;
    public const int HitBottom = 15;
    public const int HitBottomLeft = 16;
    public const int HitBottomRight = 17;

    /// <summary>DWMWA_NCRENDERING_ENABLED, DWMWA_WINDOW_CORNER_PREFERENCE and DWMWA_SYSTEMBACKDROP_TYPE, for <see cref="DwmAttribute"/>.</summary>
    public const int DwmNonClientRendering = 1;
    public const int DwmCornerPreference = 33;
    public const int DwmBackdropType = 38;

    /// <summary>How far outside the virtual screen the window is put (inside the 16-bit range of a hit-test message's coordinates).</summary>
    internal const int Away = 20000;

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const uint PwRenderFullContent = 0x2;
    private const int WmNcHitTest = 0x0084;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private static readonly Lazy<string?> Reason = new(WhyUnavailable);

    private readonly ThreadGuard _guard;
    private readonly DispatcherTimer _watch;
    private string? _broken;
    private bool _closed;

    private RealWindow(Window window, ThreadGuard guard)
    {
        Window = window;
        _guard = guard;
        window.Closed += (_, _) => _closed = true;

        // Looked at all the time the window is open, also while a test waits on something else.
        _watch = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(30) };
        _watch.Tick += (_, _) => CloseIfItBrokeARule();
    }

    /// <summary>
    /// Why real windows cannot be created and captured here (the tests skip with this message), or null when they can. They run only on an
    /// isolated desktop (<see cref="IsolatedDesktop"/>: tools/test-fast.ps1 -IsolatedDesktop), never on the desktop of the person at the PC, and
    /// need an interactive window station with composition: not a GitHub runner (set <c>LIFESTREAM_REAL_WINDOW_TESTS=1</c> to try there anyway),
    /// not a service session. The display's state is not asked there (<see cref="DisplayIsOff"/> is for a run on the person's own desktop, which
    /// is no longer allowed). <c>LIFESTREAM_REAL_WINDOW_TESTS=0</c> turns them off.
    /// </summary>
    public static string? Unavailable => Reason.Value;

    /// <summary>
    /// What the tests skip with while the display is off. The display turns off after some minutes without input although the session stays
    /// unlocked, and the compositor then draws nothing: <c>PrintWindow</c> returns an empty window for as long as a test cares to wait (each of
    /// them used to fail after 60 s). The test runner of the release gate (tools/test-fast.ps1) counts the tests that carry this message and
    /// fails the gate unless it is told that a run without them is acceptable, so the checks are never lost silently.
    /// </summary>
    public const string DisplayIsOff = "display is off: real-window checks not run";

    /// <summary>How long <see cref="ConsoleDisplayState"/> waits for Windows to say what state the display is in (it answers in a few milliseconds).</summary>
    public const int DisplayStateWaitMilliseconds = 400;

    /// <summary>The skip message for a display in the given state (0 off, 1 on, 2 dimmed, null not known): only a display that is off skips.</summary>
    internal static string? DisplayReason(int? consoleDisplayState) => consoleDisplayState == 0 ? DisplayIsOff : null;

    /// <summary>
    /// The state of the display as Windows reports it (the power setting GUID_CONSOLE_DISPLAY_STATE): 0 off, 1 on, 2 dimmed; null when Windows
    /// did not say within <see cref="DisplayStateWaitMilliseconds"/>. Subscribing to the setting delivers its current value at once, on a thread
    /// of the system. It needs no window, reads nothing from the screen and sends no input.
    /// </summary>
    internal static int? ConsoleDisplayState()
    {
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        PowerSettingCallback callback = (_, _, setting) =>
        {
            // POWERBROADCAST_SETTING: the setting's GUID (16 bytes), the length of its data, the data (one DWORD for this setting).
            if (setting != IntPtr.Zero && Marshal.ReadInt32(setting, 16) >= sizeof(int))
            {
                answer.TrySetResult(Marshal.ReadInt32(setting, 20));
            }

            return 0;
        };
        var subscription = new PowerSettingSubscription { Callback = callback, Context = IntPtr.Zero };
        var consoleDisplayState = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
        if (PowerSettingRegisterNotification(ref consoleDisplayState, DeviceNotifyCallback, ref subscription, out var registration) != 0)
        {
            return null;
        }

        try
        {
            return answer.Task.Wait(DisplayStateWaitMilliseconds) ? answer.Task.Result : null;
        }
        finally
        {
            _ = PowerSettingUnregisterNotification(registration);
            GC.KeepAlive(callback);
        }
    }

    public Window Window { get; }

    public IntPtr Handle { get; private set; }

    /// <summary>Device pixels per DIP of the window.</summary>
    public double Scale => VisualTreeHelper.GetDpi(Window).DpiScaleX;

    /// <summary>The bounds of every monitor together, in device pixels.</summary>
    public static Int32Rect VirtualScreen =>
        new(GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen), GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));

    /// <summary>Activations and focus changes the guard has refused for this window's thread.</summary>
    public int ActivationsRefused => _guard.Refused;

    /// <summary>True when a window of this process is the foreground window (it must never be).</summary>
    public static bool HasTheForeground => GetForegroundWindow() is var foreground && foreground != IntPtr.Zero && IsOurs(foreground);

    /// <summary>
    /// Asks Windows to make the window its thread's active window, as a click or UI Automation would, and says whether it became active. This is
    /// the thread's own active window, not the foreground: even if the guard failed, the person's window would keep the keyboard.
    /// </summary>
    public bool AskToBeActive()
    {
        SetActiveWindow(Handle);
        return GetActiveWindow() == Handle;
    }

    /// <summary>The window's style bits (GWL_STYLE).</summary>
    public long Style => GetWindowLongPtr(Handle, GwlStyle).ToInt64();

    /// <summary>The number of items in the window's system menu (0 when it has none).</summary>
    public int SystemMenuItems => GetSystemMenu(Handle, false) is var menu && menu != IntPtr.Zero ? Math.Max(0, GetMenuItemCount(menu)) : 0;

    /// <summary>The window's rectangle on the virtual desktop, in device pixels.</summary>
    public Int32Rect Bounds
    {
        get
        {
            GetWindowRect(Handle, out var rect);
            return new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
    }

    /// <summary>Shows the window off every monitor, not activated and not in the taskbar.</summary>
    public static RealWindow Show(Window window)
    {
        if (Unavailable is { } why)
        {
            throw new InvalidOperationException("No real window is shown: " + why);
        }

        IsolatedDesktop.UseSoftwareRendering();
        var screen = VirtualScreen;
        var scale = GetDpiForSystem() / 96.0;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = (screen.X - Away) / scale;
        window.Top = (screen.Y - Away) / scale;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Topmost = false;
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() | WsExNoActivate));
        };

        // The hook is in place before the window exists, and stays until it is gone.
        var real = new RealWindow(window, new ThreadGuard(screen));
        try
        {
            real._watch.Start();
            window.Show();
            real.Handle = new WindowInteropHelper(window).Handle;
            real.CloseIfItBrokeARule();
            real.ThrowIfBroken();
            return real;
        }
        catch
        {
            real.Dispose();
            throw;
        }
    }

    /// <summary>Lets the dispatcher run for the given time.</summary>
    public void Settle(int milliseconds)
    {
        StaHost.Settle(milliseconds);
        CloseIfItBrokeARule();
        ThrowIfBroken();
    }

    /// <summary>An element's rectangle in a capture (device pixels from the window's top-left corner).</summary>
    public Int32Rect BoundsOf(FrameworkElement element)
    {
        var bounds = Bounds;
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Int32Rect((int)Math.Round(topLeft.X) - bounds.X, (int)Math.Round(topLeft.Y) - bounds.Y, (int)Math.Round(bottomRight.X - topLeft.X), (int)Math.Round(bottomRight.Y - topLeft.Y));
    }

    /// <summary>
    /// What the window answers to WM_NCHITTEST for a point given in device pixels from its top-left corner: what Windows asks before every click,
    /// drag and hover. The question is sent to the window itself; no pointer moves.
    /// </summary>
    public int HitTest(int x, int y)
    {
        ThrowIfBroken();
        var bounds = Bounds;
        var packed = (((bounds.Y + y) & 0xFFFF) << 16) | ((bounds.X + x) & 0xFFFF);
        return (int)SendMessage(Handle, WmNcHitTest, IntPtr.Zero, new IntPtr(packed)).ToInt64();
    }

    /// <summary>An integer attribute the Desktop Window Manager holds for the window, or null when it cannot be read.</summary>
    public int? DwmAttribute(int attribute)
    {
        var value = 0;
        return DwmGetWindowAttribute(Handle, attribute, ref value, sizeof(int)) == 0 ? value : null;
    }

    /// <summary>
    /// What the compositor draws for this window, the non-client area included. Waits until the content is drawn: for the first moments after a
    /// window is shown the compositor holds an empty frame. A window that shows little (its content hidden) may say how few colours count as
    /// drawn; it is then taken when two captures in a row are the same.
    /// </summary>
    public WindowCapture Capture(int timeoutMilliseconds = 60_000, int drawnColours = 24)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        WindowCapture? previous = null;
        while (true)
        {
            var capture = CaptureNow();
            var drawn = capture.DistinctColours(drawnColours) >= drawnColours;
            if (drawn && previous is not null && capture.Pixels.AsSpan().SequenceEqual(previous.Pixels))
            {
                return capture;
            }

            // Something that keeps moving (a spinner) never gives two equal frames: a drawn frame is then good enough.
            if (drawn && started.ElapsedMilliseconds > 5_000)
            {
                return capture;
            }

            if (started.ElapsedMilliseconds > timeoutMilliseconds)
            {
                throw new InvalidOperationException($"PrintWindow returned an empty window for {timeoutMilliseconds} ms ({capture.DistinctColours(24)} colours).");
            }

            previous = drawn ? capture : null;
            Settle(80);
        }
    }

    public void Dispose()
    {
        _watch.Stop();
        if (!_closed)
        {
            Window.Close();
            if (!Hosted.PumpUntil(() => _closed, 2000))
            {
                CloseNow();
                Hosted.PumpUntil(() => _closed, 2000);
            }
        }

        _guard.Dispose();
    }

    /// <summary>Closes the window whatever it would ask first (a form dialog holds its close back to ask about unsaved input).</summary>
    private void CloseNow()
    {
        if (_closed)
        {
            return;
        }

        Window.DataContext = null;
        Window.Close();
        if (!_closed && Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
        }
    }

    /// <summary>The rules a window must never break; a window that breaks one is closed before anything else happens.</summary>
    private void CloseIfItBrokeARule()
    {
        if (_closed || _broken is not null || Handle == IntPtr.Zero)
        {
            return;
        }

        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && (foreground == Handle || IsOurs(foreground)))
        {
            _broken = "A window of the test became the foreground window and was closed at once.";
        }
        else if (Window.WindowState != WindowState.Normal)
        {
            _broken = $"The window was {Window.WindowState} and was closed at once: a real window is never maximized or minimized.";
        }
        else if (IsWindowVisible(Handle) && OnAMonitor(Bounds) is { } where)
        {
            _broken = $"The window was on a monitor ({where}) and was closed at once.";
        }

        if (_broken is not null)
        {
            _watch.Stop();
            CloseNow();
        }
    }

    private void ThrowIfBroken()
    {
        if (_broken is not null)
        {
            throw new InvalidOperationException(_broken);
        }
    }

    private static bool IsOurs(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var process);
        return process == (uint)Environment.ProcessId; // no window of the test process may ever be the foreground window
    }

    private static string? OnAMonitor(Int32Rect bounds)
    {
        var screen = VirtualScreen;
        var apart = bounds.X + bounds.Width <= screen.X || bounds.Y + bounds.Height <= screen.Y || bounds.X >= screen.X + screen.Width || bounds.Y >= screen.Y + screen.Height;
        return apart ? null : $"{bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}";
    }

    private WindowCapture CaptureNow()
    {
        CloseIfItBrokeARule();
        ThrowIfBroken();
        var bounds = Bounds;
        var info = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = bounds.Width, Height = -bounds.Height, Planes = 1, BitCount = 32 };
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("No bitmap for the capture.");
            }

            var previous = SelectObject(dc, bitmap);
            var printed = PrintWindow(Handle, dc, PwRenderFullContent);
            GdiFlush();
            SelectObject(dc, previous);
            if (!printed)
            {
                throw new InvalidOperationException("PrintWindow failed.");
            }

            var pixels = new byte[bounds.Width * bounds.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new WindowCapture(bounds.Width, bounds.Height, pixels);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    private static string? WhyUnavailable()
    {
        const string need = "Real-window tests need an interactive desktop with the Desktop Window Manager compositing";
        var setting = Environment.GetEnvironmentVariable("LIFESTREAM_REAL_WINDOW_TESTS");
        if (setting == "0")
        {
            return "Real-window tests are turned off (LIFESTREAM_REAL_WINDOW_TESTS=0).";
        }

        // Never on the desktop of the person at the PC: only on an isolated desktop (IsolatedDesktop).
        if (IsolatedDesktop.WhyNot is { } notIsolated)
        {
            return "Real-window tests run only on an isolated desktop (tools/test-fast.ps1 -IsolatedDesktop), never on the desktop of the person at the PC: " + notIsolated + ".";
        }

        if (setting != "1" && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            return need + "; this is a GitHub Actions runner, where they were never proven. Set LIFESTREAM_REAL_WINDOW_TESTS=1 to run them anyway.";
        }

        if (!Environment.UserInteractive)
        {
            return need + "; this process has no interactive window station (a service or a build agent).";
        }

        if (DwmIsCompositionEnabled(out var composing) != 0 || !composing)
        {
            return need + "; composition is off here.";
        }

        var screen = VirtualScreen;
        if (screen.Width <= 0 || screen.Height <= 0)
        {
            return need + "; this session has no monitor.";
        }

        if (screen.X - Away < short.MinValue + 1000 || screen.Y - Away < short.MinValue + 1000)
        {
            return need + $"; the desktop starts at {screen.X},{screen.Y}, too far out for a window beside it.";
        }

        // The display's state (DisplayIsOff) is not asked: on the isolated desktop the windows are drawn in software into what PrintWindow returns,
        // whether the person's display is on or off (2026-11-21: the owner's display turns off at night and the checks must still run).
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    private const uint DeviceNotifyCallback = 2;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PowerSettingCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerSettingSubscription
    {
        public PowerSettingCallback Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref PowerSettingSubscription recipient, out IntPtr registration);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingUnregisterNotification(IntPtr registration);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}

/// <summary>
/// Hooks on the calling thread, for as long as a real window is open on it. The CBT hook refuses every activation and every focus change of the
/// thread's windows, so none of them can become the active or the foreground window whoever asks (Windows itself, UI Automation, the window's own
/// code). The call hook sees every position a top-level window of the thread is about to take, before any window procedure does (a subclass is
/// too late: WPF's own procedure handles the message first): it adds "do not activate" and moves a position that would reach a monitor back
/// off by the distance the windows are kept away, so a maximize or a centring cannot bring one on screen. The same mechanism as the real app's
/// off-screen mode.
/// </summary>
internal sealed class ThreadGuard : IDisposable
{
    private const int WhCallWndProc = 4;
    private const int WhCbt = 5;
    private const int HcbtActivate = 5;
    private const int HcbtSetFocus = 9;
    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint GaRoot = 2;

    private static readonly int XOffset = (int)Marshal.OffsetOf<WindowPos>(nameof(WindowPos.X));
    private static readonly int YOffset = (int)Marshal.OffsetOf<WindowPos>(nameof(WindowPos.Y));
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WindowPos>(nameof(WindowPos.Flags));

    private readonly HookProc _cbt;
    private readonly HookProc _call;
    private readonly Int32Rect _screen;
    private IntPtr _cbtHook;
    private IntPtr _callHook;

    public ThreadGuard(Int32Rect screen)
    {
        _screen = screen;
        _cbt = OnCbt;
        _call = OnCall;
        var thread = GetCurrentThreadId();
        _cbtHook = SetWindowsHookEx(WhCbt, _cbt, IntPtr.Zero, thread);
        _callHook = SetWindowsHookEx(WhCallWndProc, _call, IntPtr.Zero, thread);
        if (_cbtHook == IntPtr.Zero || _callHook == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException("The hooks that keep a real window from being activated could not be installed; no window is shown.");
        }
    }

    /// <summary>Activations and focus changes refused so far.</summary>
    public int Refused { get; private set; }

    public void Dispose()
    {
        if (_cbtHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_cbtHook);
            _cbtHook = IntPtr.Zero;
        }

        if (_callHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_callHook);
            _callHook = IntPtr.Zero;
        }

        GC.KeepAlive(_cbt);
        GC.KeepAlive(_call);
    }

    private IntPtr OnCbt(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code is HcbtActivate or HcbtSetFocus)
        {
            Refused++;
            return new IntPtr(1); // refused: the person's own window keeps the keyboard
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr OnCall(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var call = Marshal.PtrToStructure<CallWndProcStruct>(lParam);
            if (call.Message == WmWindowPosChanging && call.LParam != IntPtr.Zero && GetAncestor(call.Hwnd, GaRoot) == call.Hwnd)
            {
                var pos = Marshal.PtrToStructure<WindowPos>(call.LParam);
                var flags = pos.Flags | SwpNoActivate;
                if ((flags & SwpNoMove) == 0)
                {
                    var (width, height) = (pos.Cx, pos.Cy);
                    if ((flags & SwpNoSize) != 0 && GetWindowRect(call.Hwnd, out var now))
                    {
                        (width, height) = (now.Right - now.Left, now.Bottom - now.Top);
                    }

                    // Within reach of a monitor (its shadow included): the same place, as far off as the windows are kept.
                    const int margin = 200;
                    if (pos.X < _screen.X + _screen.Width + margin && pos.X + width > _screen.X - margin && pos.Y < _screen.Y + _screen.Height + margin && pos.Y + height > _screen.Y - margin)
                    {
                        Marshal.WriteInt32(call.LParam, XOffset, pos.X - _screen.Width - RealWindow.Away);
                        Marshal.WriteInt32(call.LParam, YOffset, pos.Y - _screen.Height - RealWindow.Away);
                    }
                }

                Marshal.WriteInt32(call.LParam, FlagsOffset, unchecked((int)flags));
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CallWndProcStruct
    {
        public IntPtr LParam;
        public IntPtr WParam;
        public int Message;
        public IntPtr Hwnd;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

/// <summary>The pixels of a captured window (BGRA, top row first). The alpha PrintWindow returns is not part of what is seen and is ignored.</summary>
internal sealed class WindowCapture(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Pixels { get; } = pixels;

    public (byte R, byte G, byte B) At(int x, int y)
    {
        var i = ((y * Width) + x) * 4;
        return (Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }

    /// <summary>The number of different colours among evenly spread pixels, counted up to the given limit.</summary>
    public int DistinctColours(int limit)
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < Pixels.Length && seen.Count < limit; i += 4 * 7)
        {
            seen.Add(BitConverter.ToInt32(Pixels, i) & 0xFFFFFF);
        }

        return seen.Count;
    }

    public WindowCapture Crop(Int32Rect area)
    {
        var x0 = Math.Clamp(area.X, 0, Width);
        var y0 = Math.Clamp(area.Y, 0, Height);
        var w = Math.Clamp(area.Width, 0, Width - x0);
        var h = Math.Clamp(area.Height, 0, Height - y0);
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            Buffer.BlockCopy(Pixels, (((y0 + y) * Width) + x0) * 4, result, y * w * 4, w * 4);
        }

        return new WindowCapture(w, h, result);
    }

    /// <summary>Saves the capture as an opaque PNG, optionally enlarged with square pixels.</summary>
    public void Save(string path, int zoom = 1)
    {
        var opaque = (byte[])Pixels.Clone();
        for (var i = 3; i < opaque.Length; i += 4)
        {
            opaque[i] = 255;
        }

        BitmapSource source = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, opaque, Width * 4);
        if (zoom > 1)
        {
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(source, new Rect(0, 0, Width * zoom, Height * zoom));
            }

            var target = new RenderTargetBitmap(Width * zoom, Height * zoom, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            source = target;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
