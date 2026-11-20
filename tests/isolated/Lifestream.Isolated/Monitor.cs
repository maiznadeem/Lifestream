using System.Diagnostics;
using System.Runtime.InteropServices;
using static Lifestream.Isolated.Native;

namespace Lifestream.Isolated;

/// <summary>
/// Watches the person's input desktop while processes run on the isolated one, and kills every one of them at once if any of them reaches it.
/// It only reads: the foreground window, the focus, active, caret, capture and menu windows of the foreground thread (GetGUIThreadInfo), the
/// cursor shape and every top-level window on the input desktop, every 10 ms; and, as they happen, the foreground, focus, create, show, menu,
/// capture and dialog events of that desktop (an out-of-context WinEvent hook, which sees events of the desktop its thread is on). A window,
/// event or focus that belongs to a process of the isolation job, to the tool itself or to one of its exec clients is a violation: the job is
/// terminated before anything is written, and the run fails.
/// </summary>
internal sealed unsafe class Monitor(IntPtr job, string isolatedDesktop, string stateDir, StreamWriter log)
{
    private readonly object _gate = new();
    private readonly HashSet<uint> _everInJob = [];
    private readonly Dictionary<uint, long> _clients = []; // pid -> creation time: a pid is reused once its process has ended
    private readonly Dictionary<uint, string> _names = [];
    private readonly Dictionary<string, (long Total, long Ours)> _events = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private WinEventProc? _proc;
    private volatile bool _stop;
    private bool _armed;
    private uint _eventThread;

    public string? Violation { get; private set; }

    public long Ticks { get; private set; }

    public double MaxGapMs { get; private set; }

    public long GapsOver30Ms { get; private set; }

    public long StateChanges { get; private set; }

    public long ForegroundChanges { get; private set; }

    public long FocusChanges { get; private set; }

    public long CaretChanges { get; private set; }

    public long NullForegroundTicks { get; private set; }

    public long NewWindows { get; private set; }

    public long NewVisibleWindows { get; private set; }

    public long InputDesktopChecks { get; private set; }

    public long InputDesktopUnavailable { get; private set; }

    public long BusyCursorEpisodes { get; private set; }

    public int WindowsOnInputDesktop { get; private set; }

    public IReadOnlyCollection<uint> JobProcesses => _everInJob;

    public IReadOnlyDictionary<string, (long Total, long Ours)> Events => _events;

    /// <summary>For the self-test only: the image name of a process that is treated as ours.</summary>
    public string? Canary { get; init; }

    /// <summary>For the self-test only: when the canary starts to count.</summary>
    public TimeSpan CanaryAfter { get; init; }

    private static string DescribeProcess(uint pid) => ProcessName(pid);

    public string NameOf(uint pid) => _names.TryGetValue(pid, out var n) ? n : $"pid{pid}"; // for the summary of job processes only

    public void Stop()
    {
        _stop = true;
        if (_eventThread != 0)
        {
            PostThreadMessageW(_eventThread, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Log(string line)
    {
        lock (_gate)
        {
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
            log.Flush();
        }
    }

    private void Violate(string what)
    {
        lock (_gate)
        {
            if (Violation is null)
            {
                TerminateJobObject(job, 0xDEAD); // first: nothing of ours may keep running once one of them reached the person's desktop
                Violation = what;
                File.WriteAllText(Path.Combine(stateDir, "violation.txt"), $"{DateTime.Now:O} {what}{Environment.NewLine}");
            }
        }

        Log("VIOLATION " + what + " -> every process of the job was terminated");
    }

    private void RefreshJob()
    {
        lock (_gate)
        {
            RefreshJobLocked();
        }
    }

    private void RefreshJobLocked()
    {
        var buffer = stackalloc byte[8 + (8 * 2048)];
        if (QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, 8 + (8 * 2048), out _))
        {
            var count = *(uint*)(buffer + 4);
            var ids = (ulong*)(buffer + 8);
            for (var i = 0; i < count; i++)
            {
                var pid = (uint)ids[i];
                if (_everInJob.Add(pid))
                {
                    _names[pid] = ProcessName(pid);
                    Log($"job process {pid} {_names[pid]}");
                }
            }
        }

        var clients = Path.Combine(stateDir, "clients");
        if (Directory.Exists(clients))
        {
            foreach (var file in Directory.EnumerateFiles(clients))
            {
                if (uint.TryParse(Path.GetFileName(file), out var pid) && !_clients.ContainsKey(pid))
                {
                    _clients[pid] = CreationTime(pid);
                }
            }
        }
    }

    /// <summary>
    /// True for the tool itself, its exec clients (the same process: same id and start time) and every live process of the job. Process ids are
    /// reused soon after a process ends (2026-11-21: a console program of someone else got the id of a driver that had ended a minute before,
    /// and its windows were taken for ours), so a remembered id alone never counts: a window or event of the person's desktop belongs to a live
    /// process, which is asked whether it is in the job.
    /// </summary>
    private bool IsOurs(uint pid)
    {
        lock (_gate)
        {
            return IsOursLocked(pid);
        }
    }

    private bool IsOursLocked(uint pid)
    {
        if (pid == 0)
        {
            return false;
        }

        if (pid == (uint)Environment.ProcessId)
        {
            return true;
        }

        if (_clients.TryGetValue(pid, out var started) && started != 0 && CreationTime(pid) == started)
        {
            return true;
        }

        // Self-test of the kill switch: a process of the person's (named by --self-test-canary) counts as ours, so that its ordinary
        // windows and focus changes must trigger the kill without anything of ours ever touching the input desktop.
        if (Canary is not null && _clock.Elapsed >= CanaryAfter && string.Equals(DescribeProcess(pid), Canary, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (IsProcessInJob(process, job, out var inJob) && inJob)
            {
                _everInJob.Add(pid);
                return true;
            }

            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private string Describe(IntPtr hwnd)
    {
        lock (_gate)
        {
            return DescribeLocked(hwnd);
        }
    }

    private static string DescribeLocked(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "0";
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return $"0x{hwnd:X}({ProcessName(pid)}:{pid} {ClassName(hwnd)})"; // the name now: a remembered one may be of an ended process
    }

    /// <summary>
    /// A Lifestream process that is not ours (another run's tests or app on the person's desktop) is not killed, but what it does there is
    /// written down with its window's title and place and counted, so that it is never taken for silence.
    /// </summary>
    private void NoteOutsider(string what, IntPtr hwnd, uint pid)
    {
        var name = ProcessName(pid);
        if (!name.StartsWith("Lifestream", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_gate)
        {
            OutsideLifestream++;
        }

        var title = new System.Text.StringBuilder(256);
        _ = GetWindowTextW(GetAncestor(hwnd, 2), title, title.Capacity);
        GetWindowRect(GetAncestor(hwnd, 2), out var r);
        Log($"OUTSIDE (a Lifestream process not on the isolated desktop): {what} {name}:{pid} hwnd 0x{hwnd:X} title \"{title}\" rect {r.Left},{r.Top},{r.Right},{r.Bottom} visible={IsWindowVisible(GetAncestor(hwnd, 2))}");
    }

    /// <summary>What Lifestream processes outside the isolation did on the person's desktop (foreground, focus, windows): not ours, but seen.</summary>
    public long OutsideLifestream { get; private set; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    private static long CreationTime(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return GetProcessTimes(process, out var creation, out _, out _, out _) ? creation : 0;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static uint PidOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>The 10 ms poll, on the calling thread (which must be on the input desktop) until <see cref="Stop"/>.</summary>
    public void Poll()
    {
        var input = OpenInputDesktop(0, false, DesktopReadObjects | DesktopEnumerate);
        var inputName = input != IntPtr.Zero ? ObjectName(input) : "(none)";
        Log($"poll thread on desktop '{ThreadDesktopName()}', input desktop '{inputName}', isolated desktop '{isolatedDesktop}'");
        if (input == IntPtr.Zero || !string.Equals(ThreadDesktopName(), inputName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The monitor must run on the input desktop it watches.");
        }

        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003);
        var appStarting = LoadCursorW(IntPtr.Zero, new IntPtr(32650));
        var wait = LoadCursorW(IntPtr.Zero, new IntPtr(32514));
        var known = new HashSet<IntPtr>();
        var current = new HashSet<IntPtr>();
        EnumWindowsProc collect = (hwnd, _) =>
        {
            current.Add(hwnd);
            return true;
        };
        EnumDesktopWindows(input, collect, IntPtr.Zero);
        known.UnionWith(current);
        WindowsOnInputDesktop = known.Count;
        Log($"{known.Count} top-level windows on the input desktop at the start");

        (IntPtr Fg, IntPtr Active, IntPtr Focus, IntPtr Caret, IntPtr Capture, IntPtr Menu, IntPtr Move) previous = default;
        var busy = false;
        var last = _clock.Elapsed.TotalMilliseconds;
        while (!_stop)
        {
            var due = -100_000L; // 10 ms
            if (timer != IntPtr.Zero && SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(timer, 1000);
            }
            else
            {
                Thread.Sleep(10);
            }

            var now = _clock.Elapsed.TotalMilliseconds;
            var gap = now - last;
            last = now;
            if (Ticks > 0)
            {
                MaxGapMs = Math.Max(MaxGapMs, gap);
                if (gap > 30)
                {
                    GapsOver30Ms++;
                }
            }

            Ticks++;
            if (Ticks % 10 == 1)
            {
                RefreshJob();
            }

            // The foreground and what its thread holds.
            var fg = GetForegroundWindow();
            var thread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(thread, ref info))
            {
                info = new GuiThreadInfo();
            }

            if (fg == IntPtr.Zero)
            {
                NullForegroundTicks++;
            }

            var state = (fg, info.Active, info.Focus, info.Caret, info.Capture, info.MenuOwner, info.MoveSize);
            if (Canary is not null && !_armed && _clock.Elapsed >= CanaryAfter)
            {
                _armed = true;
                previous = default; // the self-test looks at the state as it is when the canary starts to count
                Log("SELF-TEST: canary armed");
            }

            if (state != previous)
            {
                StateChanges++;
                if (state.fg != previous.Fg)
                {
                    ForegroundChanges++;
                }

                if (state.Focus != previous.Focus)
                {
                    FocusChanges++;
                }

                if (state.Caret != previous.Caret)
                {
                    CaretChanges++;
                }

                Log($"state fg={Describe(fg)} active={Describe(info.Active)} focus={Describe(info.Focus)} caret={Describe(info.Caret)} capture={Describe(info.Capture)} menu={Describe(info.MenuOwner)} move={Describe(info.MoveSize)}");
                foreach (var hwnd in new[] { fg, info.Active, info.Focus, info.Caret, info.Capture, info.MenuOwner, info.MoveSize })
                {
                    if (hwnd != IntPtr.Zero && IsOurs(PidOf(hwnd)))
                    {
                        Violate($"the person's foreground/focus state holds our window {Describe(hwnd)}");
                    }
                }

                if (fg != previous.Fg && fg != IntPtr.Zero)
                {
                    NoteOutsider("took the foreground", fg, PidOf(fg));
                }

                previous = state;
            }

            // Every top-level window of the input desktop.
            current.Clear();
            EnumDesktopWindows(input, collect, IntPtr.Zero);
            foreach (var hwnd in current)
            {
                if (known.Add(hwnd))
                {
                    NewWindows++;
                    var pid = PidOf(hwnd);
                    var visible = IsWindowVisible(hwnd);
                    if (visible)
                    {
                        NewVisibleWindows++;
                    }

                    if (IsOurs(pid))
                    {
                        Violate($"our window {Describe(hwnd)} visible={visible} is on the input desktop");
                    }
                    else
                    {
                        if (visible)
                        {
                            Log($"new visible window {Describe(hwnd)}");
                        }

                        NoteOutsider($"made a top-level window (visible={visible})", hwnd, pid);
                    }
                }
            }

            known.IntersectWith(current);
            WindowsOnInputDesktop = known.Count;

            // The cursor: a busy or app-starting cursor while processes start would be seen by the person.
            var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            if (GetCursorInfo(ref cursor))
            {
                var isBusy = cursor.Cursor == appStarting || cursor.Cursor == wait;
                if (isBusy && !busy)
                {
                    BusyCursorEpisodes++;
                    Log($"cursor busy ({(cursor.Cursor == appStarting ? "app starting" : "wait")})");
                }
                else if (!isBusy && busy)
                {
                    Log("cursor normal");
                }

                busy = isBusy;
            }

            // The input desktop itself: never ours.
            if (Ticks % 25 == 0)
            {
                InputDesktopChecks++;
                var name = InputDesktopName();
                if (name is null)
                {
                    InputDesktopUnavailable++;
                }
                else if (string.Equals(name, isolatedDesktop, StringComparison.OrdinalIgnoreCase))
                {
                    Violate($"the isolated desktop '{name}' became the input desktop");
                }
                else if (!string.Equals(name, inputName, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"input desktop is now '{name}'");
                }
            }
        }

        GC.KeepAlive(collect);
        CloseDesktop(input);
        if (timer != IntPtr.Zero)
        {
            CloseHandle(timer);
        }
    }

    /// <summary>The WinEvent hook, on the calling thread (on the input desktop) until <see cref="Stop"/>.</summary>
    public void WatchEvents()
    {
        _eventThread = GetCurrentThreadId();
        _proc = OnEvent;
        var hooks = new[]
        {
            SetWinEventHook(0x0003, 0x0017, IntPtr.Zero, _proc, 0, 0, 0x0002), // foreground, menus, capture, move/size, dialogs, switch, minimize
            SetWinEventHook(0x8000, 0x8000, IntPtr.Zero, _proc, 0, 0, 0x0002), // create
            SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, _proc, 0, 0, 0x0002), // show
            SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, _proc, 0, 0, 0x0002), // focus
        };
        Log($"event thread on desktop '{ThreadDesktopName()}', hooks {string.Join(",", hooks.Select(h => h != IntPtr.Zero))}");
        if (hooks.Any(h => h == IntPtr.Zero))
        {
            throw new InvalidOperationException("A WinEvent hook could not be installed.");
        }

        while (!_stop && GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            DispatchMessageW(ref msg);
        }

        foreach (var hook in hooks)
        {
            UnhookWinEvent(hook);
        }
    }

    private void OnEvent(IntPtr hook, uint @event, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var name = @event switch
        {
            0x0003 => "foreground",
            0x0004 => "menustart",
            0x0006 => "menupopupstart",
            0x0008 => "capturestart",
            0x000A => "movesizestart",
            0x0010 => "dialogstart",
            0x0014 => "switchstart",
            0x0016 => "minimizestart",
            0x8000 => "create",
            0x8002 => "show",
            0x8005 => "focus",
            _ => null,
        };
        if (name is null || ((@event is 0x8000 or 0x8002) && idObject != 0))
        {
            return; // only window objects for create and show; every focus and system event
        }

        var pid = PidOf(hwnd);
        if (pid == 0 && thread != 0)
        {
            pid = ProcessOfThread(thread);
        }

        var ours = IsOurs(pid);
        lock (_gate)
        {
            _events.TryGetValue(name, out var count);
            _events[name] = (count.Total + 1, count.Ours + (ours ? 1 : 0));
        }

        if (ours)
        {
            Violate($"event {name} from our process {NameOf(pid)}:{pid} on the input desktop (hwnd 0x{hwnd:X})");
        }
        else if (@event is 0x0003 or 0x0010 or 0x0006)
        {
            Log($"event {name} {Describe(hwnd)}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint thread);

    [DllImport("kernel32.dll")]
    private static extern uint GetProcessIdOfThread(IntPtr thread);

    private static uint ProcessOfThread(uint thread)
    {
        var handle = OpenThread(0x0800 /* THREAD_QUERY_LIMITED_INFORMATION */, false, thread);
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return GetProcessIdOfThread(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
