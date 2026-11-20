// Runs windows where the person at the PC can never meet them: on a desktop of its own (CreateDesktop), never the input desktop.
//
//   hold <stateDir> [--minutes N] [--affinity 0xHEX] [--self-test-canary <image.exe> [--self-test-after SECONDS]]
//       Creates the desktop "LifestreamIso-<random>" and a job object for everything started on it (killed with the job; at most the given
//       processors, BelowNormal; no clipboard, no system parameters, no display settings, no desktop switching, no log-off, no USER handles
//       of processes outside the job), then watches the person's input desktop (Monitor) until 'stop', the time limit or a violation.
//       Exit code: 0 clean, 3 violation (every process of the job was terminated at once), 4 the watcher itself failed.
//   exec <stateDir> [--env K=V]... [--cwd DIR] [--timeout SECONDS] -- <exe> [args...]
//       Starts a process on that desktop, inside the job, and relays its output; exits with its exit code (124 on timeout, 3 on a violation).
//   stop <stateDir>
//       Ends the hold and prints its summary.
//
// Nothing here creates a window, sends input or changes a setting. See tests/isolated/README.md.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using static Lifestream.Isolated.Native;

namespace Lifestream.Isolated;

internal static unsafe class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return args switch
            {
                ["hold", var state, .. var rest] => Hold(Path.GetFullPath(state), rest),
                ["exec", var state, .. var rest] => Exec(Path.GetFullPath(state), rest),
                ["stop", var state] => StopHold(Path.GetFullPath(state)),
                _ => Usage(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Lifestream.Isolated: {e.Message}");
            return 4;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: hold <stateDir> [--minutes N] [--affinity 0xHEX] | exec <stateDir> [--env K=V]... [--cwd DIR] [--timeout S] -- <exe> [args...] | stop <stateDir>");
        return 2;
    }

    private static string Option(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private static int Hold(string stateDir, string[] args)
    {
        var minutes = double.Parse(Option(args, "--minutes", "120"), CultureInfo.InvariantCulture);
        var affinity = Convert.ToUInt64(Option(args, "--affinity", "0x3"), 16);
        Directory.CreateDirectory(stateDir);
        foreach (var stale in new[] { "desktop.txt", "ready", "violation.txt", "summary.txt", "hold.pid" })
        {
            File.Delete(Path.Combine(stateDir, stale));
        }

        if (Directory.Exists(Path.Combine(stateDir, "clients")))
        {
            Directory.Delete(Path.Combine(stateDir, "clients"), true);
        }

        // The tool watches the desktop it runs on: that must be the person's input desktop.
        var mine = ThreadDesktopName();
        var input = InputDesktopName();
        if (input is null || !string.Equals(mine, input, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"hold must run on the input desktop (this thread: '{mine}', input: '{input ?? "not available"}').");
        }

        // A new desktop with a name no one else uses; never switched to.
        string name;
        IntPtr desktop;
        while (true)
        {
            name = "LifestreamIso-" + Convert.ToHexString(Guid.NewGuid().ToByteArray()[..6]);
            var existing = OpenDesktopW(name, 0, false, DesktopReadObjects);
            if (existing != IntPtr.Zero)
            {
                CloseDesktop(existing);
                continue;
            }

            desktop = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero); // not inheritable (lpsa null)
            if (desktop == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateDesktop failed ({Marshal.GetLastWin32Error()}).");
            }

            break;
        }

        if (!string.Equals(ThreadDesktopName(), mine, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CreateDesktop moved the calling thread; refusing to go on.");
        }

        var job = CreateJobObjectW(IntPtr.Zero, @"Local\" + name);
        if (job == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateJobObject failed ({Marshal.GetLastWin32Error()}).");
        }

        var limit = new ExtendedLimit();
        limit.Basic.LimitFlags = JobLimitKillOnJobClose | JobLimitAffinity | JobLimitPriorityClass;
        limit.Basic.Affinity = new UIntPtr(affinity);
        limit.Basic.PriorityClass = BelowNormalPriorityClass;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limit, Marshal.SizeOf<ExtendedLimit>()))
        {
            throw new InvalidOperationException($"The job's limits could not be set ({Marshal.GetLastWin32Error()}).");
        }

        var ui = UiLimitHandles | UiLimitReadClipboard | UiLimitWriteClipboard | UiLimitSystemParameters | UiLimitDisplaySettings | UiLimitDesktop | UiLimitExitWindows;
        if (!SetInformationJobObject(job, JobObjectBasicUiRestrictions, &ui, sizeof(uint)))
        {
            throw new InvalidOperationException($"The job's UI restrictions could not be set ({Marshal.GetLastWin32Error()}).");
        }

        var stop = CreateEventW(IntPtr.Zero, true, false, @"Local\" + name + "-stop");
        using var log = new StreamWriter(Path.Combine(stateDir, "monitor.log"), append: false, Encoding.UTF8);
        var canary = Option(args, "--self-test-canary", string.Empty);
        var monitor = new Monitor(job, name, stateDir, log) { Canary = canary.Length > 0 ? canary : null, CanaryAfter = TimeSpan.FromSeconds(double.Parse(Option(args, "--self-test-after", "0"), CultureInfo.InvariantCulture)) };
        if (monitor.Canary is not null)
        {
            monitor.Log($"SELF-TEST: windows and focus of '{canary}' count as ours; the kill switch is expected to fire");
        }

        monitor.Log($"hold pid {Environment.ProcessId} on '{mine}'; isolated desktop '{name}' created; job limits: affinity 0x{affinity:X}, BelowNormal, UI 0x{ui:X}; up to {minutes} min");

        Exception? broken = null;
        var poll = new Thread(() => Guard(monitor.Poll, ref broken, monitor)) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "poll" };
        var events = new Thread(() => Guard(monitor.WatchEvents, ref broken, monitor)) { IsBackground = true, Name = "events" };
        poll.Start();
        events.Start();
        Thread.Sleep(300);
        File.WriteAllText(Path.Combine(stateDir, "hold.pid"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(stateDir, "desktop.txt"), name);
        File.WriteAllText(Path.Combine(stateDir, "ready"), DateTime.Now.ToString("O"));
        Console.WriteLine($"isolated desktop {name} ready; monitor log {Path.Combine(stateDir, "monitor.log")}");

        var started = Stopwatch.StartNew();
        var ended = "stop";
        while (true)
        {
            if (WaitForSingleObject(stop, 100) == 0)
            {
                break;
            }

            if (monitor.Violation is not null)
            {
                ended = "violation";
                break;
            }

            if (broken is not null)
            {
                ended = "watcher broken: " + broken.Message;
                break;
            }

            if (started.Elapsed.TotalMinutes > minutes)
            {
                ended = "time limit";
                break;
            }
        }

        TerminateJobObject(job, 0xEE); // whatever is still running on the desktop ends with the hold
        monitor.Stop();
        poll.Join(2000);
        events.Join(2000);
        var summary = Summary(monitor, name, started.Elapsed, ended);
        monitor.Log("summary:" + Environment.NewLine + summary);
        File.WriteAllText(Path.Combine(stateDir, "summary.txt"), summary);
        Console.WriteLine(summary);
        CloseHandle(job);
        CloseDesktop(desktop);
        CloseHandle(stop);
        return monitor.Violation is not null ? 3 : broken is not null ? 4 : 0;
    }

    private static void Guard(Action action, ref Exception? broken, Monitor monitor)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            broken = e;
            monitor.Log("WATCHER BROKEN " + e);
        }
    }

    private static string Summary(Monitor m, string name, TimeSpan duration, string ended)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"outcome: {(m.Violation is not null ? "VIOLATION: " + m.Violation : ended)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"isolated desktop: {name}; watched for {duration.TotalSeconds:F1} s");
        text.AppendLine(CultureInfo.InvariantCulture, $"poll: {m.Ticks} ticks (10 ms), largest gap {m.MaxGapMs:F1} ms, gaps over 30 ms {m.GapsOver30Ms}");
        text.AppendLine(CultureInfo.InvariantCulture, $"input desktop state changes: {m.StateChanges} (foreground {m.ForegroundChanges}, focus {m.FocusChanges}, caret {m.CaretChanges}); ticks with no foreground {m.NullForegroundTicks}");
        text.AppendLine(CultureInfo.InvariantCulture, $"new top-level windows on the input desktop: {m.NewWindows} ({m.NewVisibleWindows} visible), none of ours unless a VIOLATION says so");
        text.AppendLine(CultureInfo.InvariantCulture, $"input desktop checks: {m.InputDesktopChecks} ({m.InputDesktopUnavailable} not available: locked or a UAC prompt)");
        text.AppendLine(CultureInfo.InvariantCulture, $"busy/app-starting cursor episodes on the input desktop: {m.BusyCursorEpisodes}");
        text.AppendLine(CultureInfo.InvariantCulture, $"input desktop events (total/ours): {string.Join(", ", m.Events.OrderBy(e => e.Key).Select(e => $"{e.Key} {e.Value.Total}/{e.Value.Ours}"))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"processes that ran in the job: {m.JobProcesses.Count} ({string.Join(", ", m.JobProcesses.Select(m.NameOf).GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}"))})");
        return text.ToString();
    }

    private static int StopHold(string stateDir)
    {
        var name = File.ReadAllText(Path.Combine(stateDir, "desktop.txt")).Trim();
        var stop = OpenEventW(0x0002 | Synchronize, false, @"Local\" + name + "-stop");
        if (stop == IntPtr.Zero)
        {
            Console.Error.WriteLine("The hold is not running.");
            return 1;
        }

        SetEvent(stop);
        CloseHandle(stop);
        var summary = Path.Combine(stateDir, "summary.txt");
        for (var i = 0; i < 100 && !File.Exists(summary); i++)
        {
            Thread.Sleep(100);
        }

        Console.Write(File.Exists(summary) ? File.ReadAllText(summary) : "no summary yet");
        return File.Exists(Path.Combine(stateDir, "violation.txt")) ? 3 : 0;
    }

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return arg;
        }

        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            text.Append('\\', c == '"' ? (slashes * 2) + 1 : slashes);
            slashes = 0;
            text.Append(c);
        }

        text.Append('\\', slashes * 2).Append('"');
        return text.ToString();
    }

    private static int Exec(string stateDir, string[] args)
    {
        var split = Array.IndexOf(args, "--");
        if (split < 0 || split == args.Length - 1)
        {
            return Usage();
        }

        var options = args[..split];
        var command = args[(split + 1)..];
        var timeout = double.Parse(Option(options, "--timeout", "3600"), CultureInfo.InvariantCulture);
        var cwd = Option(options, "--cwd", Environment.CurrentDirectory);
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            env[(string)e.Key] = (string?)e.Value ?? string.Empty;
        }

        for (var i = 0; i < options.Length - 1; i++)
        {
            if (options[i] == "--env")
            {
                var kv = options[i + 1].Split('=', 2);
                env[kv[0]] = kv.Length > 1 ? kv[1] : string.Empty;
            }
        }

        // The hold must be running, unviolated, with its desktop and job.
        var name = File.ReadAllText(Path.Combine(stateDir, "desktop.txt")).Trim();
        if (File.Exists(Path.Combine(stateDir, "violation.txt")))
        {
            Console.Error.WriteLine("The isolated desktop had a violation; nothing more is started on it: " + File.ReadAllText(Path.Combine(stateDir, "violation.txt")));
            return 3;
        }

        var holdPid = int.Parse(File.ReadAllText(Path.Combine(stateDir, "hold.pid")), CultureInfo.InvariantCulture);
        using (var hold = Process.GetProcessById(holdPid))
        {
            if (hold.HasExited || !hold.ProcessName.Contains("Lifestream.Isolated", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("The hold (the monitor) is not running; nothing is started without it.");
                return 4;
            }
        }

        var job = OpenJobObjectW(JobObjectAssignProcess | JobObjectQuery | JobObjectTerminate, false, @"Local\" + name);
        if (job == IntPtr.Zero)
        {
            Console.Error.WriteLine($"The job of the isolated desktop cannot be opened ({Marshal.GetLastWin32Error()}).");
            return 4;
        }

        Directory.CreateDirectory(Path.Combine(stateDir, "clients"));
        File.WriteAllText(Path.Combine(stateDir, "clients", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)), string.Empty);

        var inherit = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        CreatePipe(out var outRead, out var outWrite, ref inherit, 0);
        CreatePipe(out var errRead, out var errWrite, ref inherit, 0);
        CreatePipe(out var inRead, out var inWrite, ref inherit, 0);
        SetHandleInformation(outRead, HandleFlagInherit, 0);
        SetHandleInformation(errRead, HandleFlagInherit, 0);
        SetHandleInformation(inWrite, HandleFlagInherit, 0);

        env["LIFESTREAM_ISOLATED_DESKTOP"] = name; // what the app, the driver and the real-window tests check before they create a window
        var block = new StringBuilder();
        foreach (var (k, v) in env)
        {
            block.Append(k).Append('=').Append(v).Append('\0');
        }

        block.Append('\0');
        var desktopPath = Marshal.StringToHGlobalUni(@"WinSta0\" + name);
        var environment = Marshal.StringToHGlobalUni(block.ToString());
        try
        {
            var startup = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfo>(),
                Desktop = desktopPath,
                Flags = StartfUseStdHandles | StartfForceOffFeedback,
                StdInput = inRead,
                StdOutput = outWrite,
                StdError = errWrite,
            };
            var line = new StringBuilder(string.Join(' ', command.Select(Quote)));
            if (!CreateProcessW(null, line, IntPtr.Zero, IntPtr.Zero, true, CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow, environment, cwd, ref startup, out var pi))
            {
                Console.Error.WriteLine($"CreateProcess failed ({Marshal.GetLastWin32Error()}): {line}");
                return 4;
            }

            // Into the job before it runs a single instruction; never run outside it.
            if (!AssignProcessToJobObject(job, pi.Process) || !IsProcessInJob(pi.Process, job, out var inJob) || !inJob)
            {
                var error = Marshal.GetLastWin32Error();
                TerminateProcess(pi.Process, 0xDEAD);
                Console.Error.WriteLine($"The process could not be put in the isolation job ({error}); it was ended before it ran.");
                return 4;
            }

            ResumeThread(pi.Thread);
            CloseHandle(pi.Thread);
            CloseHandle(outWrite);
            CloseHandle(errWrite);
            CloseHandle(inRead);
            CloseHandle(inWrite);
            var relays = new[] { Relay(outRead, Console.OpenStandardOutput()), Relay(errRead, Console.OpenStandardError()) };
            var code = WaitForSingleObject(pi.Process, timeout * 1000 > uint.MaxValue - 1 ? Infinite : (uint)(timeout * 1000));
            int result;
            if (code == WaitTimeout)
            {
                TerminateProcess(pi.Process, 124);
                Console.Error.WriteLine($"Lifestream.Isolated exec: ended after {timeout} s");
                result = 124;
            }
            else
            {
                GetExitCodeProcess(pi.Process, out var exit);
                result = unchecked((int)exit);
            }

            foreach (var relay in relays)
            {
                relay.Join(3000);
            }

            CloseHandle(pi.Process);
            if (File.Exists(Path.Combine(stateDir, "violation.txt")))
            {
                Console.Error.WriteLine("VIOLATION on the isolated desktop: " + File.ReadAllText(Path.Combine(stateDir, "violation.txt")));
                return 3;
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(desktopPath);
            Marshal.FreeHGlobal(environment);
            CloseHandle(job);
        }
    }

    private static Thread Relay(IntPtr pipe, Stream to)
    {
        var thread = new Thread(() =>
        {
            using var from = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(pipe, true), FileAccess.Read, 1);
            var buffer = new byte[4096];
            int read;
            try
            {
                while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    to.Write(buffer, 0, read);
                    to.Flush();
                }
            }
            catch (IOException)
            {
                // the child ended
            }
        })
        { IsBackground = true };
        thread.Start();
        return thread;
    }
}
