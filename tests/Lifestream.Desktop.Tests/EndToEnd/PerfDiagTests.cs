using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using Lifestream.Desktop.Features.Dashboard;

namespace Lifestream.Desktop.Tests.EndToEnd;

/// <summary>
/// A diagnostic, not a budget: what the UI thread does during the first visits to every screen, for finding where a UI-thread gap goes. It visits the
/// screens as the gap test does (same rig, same 250 ms link, same order, three rounds), and writes every dispatcher operation of 1 ms or more, with
/// its priority and method, and each visit's gap, to the TSV file named by <c>LIFESTREAM_PERF_DIAG</c> (nothing is written to the results).
/// With <c>LIFESTREAM_PERF_DIAG_TIMER=1</c> the process runs on a 1 ms timer, so a sampling profiler (dotnet-trace) samples every millisecond. Driven by
/// <c>tests/perf/run-perf.ps1 -TestFilter PerfPhase=diag</c>; views are hosted off-screen as in <see cref="PerfTests"/>.
/// </summary>
[Collection("Wpf")]
[Trait("Category", "Perf")]
public sealed class PerfDiagTests
{
    private static readonly string[] Screens = ["Dashboard", "Donors", "Appointments", "Patients", "Requests", "Stock", "Receipts", "Staff", "Profile"];

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    private static readonly PropertyInfo? OperationName = typeof(DispatcherOperation).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>CPU cycles this thread has run (not counting time it waited for a processor), for comparing builds on a busy machine.</summary>
    private static ulong ThreadCycles() => QueryThreadCycleTime(GetCurrentThread(), out var cycles) ? cycles : 0;

    [Fact]
    [Trait("PerfPhase", "diag")]
    public void Diag_UiThreadOperations_DuringTheFirstVisitsToEveryScreen()
    {
        var path = Environment.GetEnvironmentVariable("LIFESTREAM_PERF_DIAG");
        Assert.False(string.IsNullOrEmpty(path), "LIFESTREAM_PERF_DIAG names the file the diagnostic writes");
        if (Environment.GetEnvironmentVariable("LIFESTREAM_PERF_DIAG_TIMER") == "1")
        {
            _ = TimeBeginPeriod(1); // a 1 ms timer for this process, so a sampling profiler samples every millisecond instead of every 15.6
        }

        var rounds = int.TryParse(Environment.GetEnvironmentVariable("LIFESTREAM_PERF_DIAG_ROUNDS"), CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 3;
        // LIFESTREAM_PERF_DIAG_PREWARM=1: every screen's view is first built and laid out once on another STA thread (its own theme), which is then gone.
        // What that saves on this thread's first visits is the process-wide one-time cost (types, BAML, fonts), the part a background preload could take.
        var prewarmMs = 0.0;
        if (Environment.GetEnvironmentVariable("LIFESTREAM_PERF_DIAG_PREWARM") == "1")
        {
            var clock = Stopwatch.StartNew();
            StaHost.Run(() =>
            {
                foreach (var type in typeof(Lifestream.Desktop.Shell.ShellView).Assembly.GetTypes().Where(t => Screens.Any(s => t.Name == s + "View") && t.Namespace!.Contains(".Features.", StringComparison.Ordinal)))
                {
                    using var warm = Hosted.Create((System.Windows.FrameworkElement)Activator.CreateInstance(type)!, 1100, 680);
                }

                return 0;
            });
            prewarmMs = clock.Elapsed.TotalMilliseconds;
        }

        var lines = StaHost.Run(() =>
        {
            var output = new List<string> { "screen\tvisit\tat_ms\tms\tpriority\tmethod\tmcycles" };
            output.Add($"startup\t0\t0\t{prewarmMs:F1}\tPREWARM\tscreens built on another thread first");
            var started = Stopwatch.StartNew();
            PerfTests.Pump(ChartWarmUp.Start(Dispatcher.CurrentDispatcher), 10_000);
            output.Add($"startup\t0\t0\t{started.Elapsed.TotalMilliseconds:F1}\tWARMUP\tchart warm-up");
            if (Environment.GetEnvironmentVariable("LIFESTREAM_PERF_DIAG_PREWARM") == "2")
            {
                // The same, on this thread (the theme the shell will use): what is left of a first visit's extra cost after this is not per-thread.
                var same = Stopwatch.StartNew();
                foreach (var type in typeof(Lifestream.Desktop.Shell.ShellView).Assembly.GetTypes().Where(t => Screens.Any(s => t.Name == s + "View") && t.Namespace!.Contains(".Features.", StringComparison.Ordinal)))
                {
                    using var warm = Hosted.Create((System.Windows.FrameworkElement)Activator.CreateInstance(type)!, 1100, 680);
                }

                output.Add($"startup\t0\t0\t{same.Elapsed.TotalMilliseconds:F1}\tPREWARM2\tscreens built on this thread first");
            }
            var rig = RealRig.Create();
            var shellClock = Stopwatch.StartNew();
            var host = Hosted.Create(new Lifestream.Desktop.Shell.ShellView { DataContext = rig.Shell }, 1366, 736);
            output.Add($"startup\t0\t0\t{shellClock.Elapsed.TotalMilliseconds:F1}\tBUILD\tshell view hosted");
            host.Settle(100);
            using (rig)
            using (host)
            {
                rig.Probe.Latency = TimeSpan.FromMilliseconds(250);
                var dispatcher = Dispatcher.CurrentDispatcher;
                var screen = "";
                var visit = 0;
                long visitStart = 0;
                var open = new Dictionary<DispatcherOperation, (long At, ulong Cycles)>();
                dispatcher.Hooks.OperationStarted += (_, e) => open[e.Operation] = (Stopwatch.GetTimestamp(), ThreadCycles());
                dispatcher.Hooks.OperationCompleted += (_, e) =>
                {
                    if (!open.Remove(e.Operation, out var start))
                    {
                        return;
                    }

                    var ms = Stopwatch.GetElapsedTime(start.At).TotalMilliseconds;
                    var cycles = (ThreadCycles() - start.Cycles) / 1e6;
                    if (ms >= 1 && visit > 0)
                    {
                        var at = Stopwatch.GetElapsedTime(visitStart, start.At).TotalMilliseconds;
                        output.Add($"{screen}\t{visit}\t{at:F1}\t{ms:F1}\t{e.Operation.Priority}\t{OperationName?.GetValue(e.Operation)}\t{cycles:F1}");
                    }
                };

                for (var round = 1; round <= rounds; round++)
                {
                    foreach (var name in Screens)
                    {
                        screen = name;
                        visit = round;
                        var running = true;
                        var worst = 0.0;
                        var probe = new Thread(() =>
                        {
                            while (Volatile.Read(ref running))
                            {
                                var posted = Stopwatch.GetTimestamp();
                                dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                                {
                                    var waited = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                                    if (waited > worst)
                                    {
                                        worst = waited;
                                    }
                                });
                                Thread.Sleep(3);
                            }
                        })
                        { IsBackground = true };
                        probe.Start();
                        visitStart = Stopwatch.GetTimestamp();
                        var task = name == "Profile" ? rig.Shell.ProfileCommand.ExecuteAsync() : rig.Shell.NavigateCommand.ExecuteAsync(rig.Shell.Items.First(i => i.Title == name));
                        PerfTests.Pump(task);
                        PerfTests.PumpFor(300);
                        Volatile.Write(ref running, false);
                        probe.Join();
                        PerfTests.PumpFor(20);
                        output.Add($"{name}\t{round}\t0\t{worst:F1}\tGAP\tlargest wait of an input-priority post");
                    }
                }
            }

            return output;
        });

        File.WriteAllLines(path!, lines, Encoding.UTF8);
    }

}
